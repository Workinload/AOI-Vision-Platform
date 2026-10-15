#include "aoi/assets/lossless_store.hpp"
#include <openssl/evp.h>
#include <array>
#include <cerrno>
#include <cstring>
#include <fcntl.h>
#include <fstream>
#include <stdexcept>
#include <sys/stat.h>
#include <unistd.h>
#include <utility>
namespace aoi::agent03 {
namespace fs=std::filesystem;
namespace {
void need(bool good,const std::string& error){if(!good)throw std::runtime_error(error);}
void verify_hex(const std::string& h){need(h.size()==64,"bad SHA256 length");for(char c:h)need((c>='0'&&c<='9')||(c>='a'&&c<='f'),"bad SHA256 characters");}
void write_all(int fd,const void* data,std::size_t length){
  const auto* bytes=static_cast<const unsigned char*>(data);
  while(length){auto n=::write(fd,bytes,length);if(n<0&&errno==EINTR)continue;need(n>0,"asset write error");bytes+=n;length-=static_cast<std::size_t>(n);}
}
class File {
 public:explicit File(const fs::path& path):fd_(::open(path.c_str(),O_WRONLY|O_CREAT|O_EXCL|O_NOFOLLOW,0600)){need(fd_>=0,"temp file creation failed");}
  ~File(){if(fd_>=0)::close(fd_);}
  File(const File&)=delete;int handle()const{return fd_;}
  void flush(){need(::fsync(fd_)==0,"asset fsync failed");}
 private:int fd_;
};
std::string file_hash(const fs::path& path){
  std::ifstream in(path,std::ios::binary);need(static_cast<bool>(in),"cannot open source/file bytes");
  EVP_MD_CTX* raw=EVP_MD_CTX_new();need(raw!=nullptr,"sha context allocation failed");
  struct Cleanup {EVP_MD_CTX* p;~Cleanup(){EVP_MD_CTX_free(p);}} cleanup{raw};
  need(EVP_DigestInit_ex(raw,EVP_sha256(),nullptr)==1,"sha init failed");
  std::array<char,1<<20> chunk;
  while(in){in.read(chunk.data(),static_cast<std::streamsize>(chunk.size()));auto n=in.gcount();if(n>0)need(EVP_DigestUpdate(raw,chunk.data(),static_cast<std::size_t>(n))==1,"sha update failed");}
  need(in.eof(),"source file read failed");
  unsigned char digest[EVP_MAX_MD_SIZE];unsigned int n=0;
  need(EVP_DigestFinal_ex(raw,digest,&n)==1&&n==32,"sha finalize failed");
  static constexpr char alphabet[]="0123456789abcdef";std::string s;s.reserve(64);
  for(unsigned int i=0;i<n;++i){s.push_back(alphabet[digest[i]>>4]);s.push_back(alphabet[digest[i]&15]);}
  return s;
}
void fsync_parent(const fs::path& path){
  const auto parent=path.parent_path();int fd=::open(parent.c_str(),O_RDONLY|O_DIRECTORY|O_NOFOLLOW);
  need(fd>=0,"asset parent dir invalid");const int ok=::fsync(fd);::close(fd);need(ok==0,"dir sync failed");
}
void flush_and_rename(const fs::path& temp,const fs::path& dest){
  need(!fs::exists(dest),"immutable destination exists");
  // Race-free deployment will require renameat2 RENAME_NOREPLACE + dirfd root confinement.
  fs::rename(temp,dest);fsync_parent(dest);
}
}
LosslessStore::LosslessStore(WalStore& data,const fs::path& root):data_(data),root_(fs::absolute(root).lexically_normal()){
  fs::create_directories(root_);root_=fs::canonical(root_);
}
fs::path LosslessStore::resolve_relative(const std::string& uri)const{
  need(!uri.empty()&&uri.front()!='/'&&uri.front()!='\\',"absolute/empty asset URI");
  need(uri.find(':')==std::string::npos&&uri.find('\\')==std::string::npos&&uri.find('\0')==std::string::npos,"nonportable URI");
  fs::path p(uri);need(!p.is_absolute(),"absolute URI forbidden");
  for(const auto& part:p){need(part!=".."&&part!="."&&!part.empty(),"URI traversal or dot component");}
  fs::path result=(root_/p).lexically_normal();
  need(result!=root_,"not a filename");
  // Reject pre-existing symlink components; only immutable local files under root allowed.
  fs::path partial=root_;
  for(const auto& part:p){partial/=part;std::error_code ec;
    auto status=fs::symlink_status(partial,ec);
    if(!ec&&fs::is_symlink(status))throw std::runtime_error("symlink asset path rejected");}
  auto canonical_parent=fs::weakly_canonical(result.parent_path());
  auto rel=canonical_parent.lexically_relative(root_);
  need(!rel.empty()&&*rel.begin()!="..","resolved asset escapes root");
  return result;
}
void LosslessStore::validate_raw(const RawParent& p,std::uintmax_t length,const std::string& hash)const{
  need(p.state=="PENDING"&&p.host_id==data_.host(),"raw wrong state/host");
  need(p.pixel_format=="GRAY8","only true GRAY8 pixel sha supported");
  need(p.stride>=p.width&&p.width>0&&p.height>0,"invalid stride/shape");
  auto expected=static_cast<std::uintmax_t>(p.stride)*static_cast<std::uintmax_t>(p.height);
  need(length==expected&&length>0,"original pixel byte length mismatch");
  verify_hex(p.expected_pixel_sha);need(hash==p.expected_pixel_sha,"original pixel SHA mismatch");
}
void LosslessStore::finish_stage(const RawParent& parent,const std::string& uri,const fs::path& temp,bool inject){
  fs::path target=resolve_relative(uri);
  flush_and_rename(temp,target);
  if(inject)throw std::runtime_error("SIMULATED_CRASH_AFTER_RENAME");
  need(fs::file_size(target)>0,"empty persisted file");
  auto sha=file_hash(target);need(sha==parent.expected_pixel_sha,"post-rename bytes changed");
  // GRAY8 raw stream has no lossy transform: decoded pixel bytes == persisted bytes.
  data_.raw_available(parent.asset_id,sha,sha,static_cast<std::int64_t>(fs::file_size(target)));
}
void LosslessStore::write_raw_bytes(const std::string& id,const std::string& uri,std::span<const std::uint8_t> pixels,bool inject){
  auto parent=data_.raw(id);need(parent.has_value(),"unknown asset");
  validate_raw(*parent,pixels.size(),sha256_hex(pixels.data(),pixels.size()));
  const auto target=resolve_relative(uri);fs::create_directories(target.parent_path());
  need(!fs::exists(target),"raw immutability violation");
  data_.raw_writing(id,uri);
  const fs::path temporary=target.string()+".part."+id;
  {File out(temporary);write_all(out.handle(),pixels.data(),pixels.size());out.flush();}
  finish_stage(*parent,uri,temporary,inject);
}
void LosslessStore::write_raw_file(const std::string& id,const std::string& uri,const fs::path& source,bool inject){
  auto parent=data_.raw(id);need(parent.has_value(),"unknown asset");
  need(fs::is_regular_file(source),"source must be a regular local file");
  const auto length=fs::file_size(source);
  validate_raw(*parent,length,file_hash(source));
  auto target=resolve_relative(uri);fs::create_directories(target.parent_path());need(!fs::exists(target),"raw immutability violation");
  data_.raw_writing(id,uri);
  auto temporary=fs::path(target.string()+".part."+id);
  {File out(temporary);std::ifstream input(source,std::ios::binary);need(static_cast<bool>(input),"input stream failed");
   std::array<char,1<<20> chunk;
   while(input){input.read(chunk.data(),static_cast<std::streamsize>(chunk.size()));auto n=input.gcount();if(n>0)write_all(out.handle(),chunk.data(),static_cast<std::size_t>(n));}
   need(input.eof(),"source stream read failed");out.flush();}
  finish_stage(*parent,uri,temporary,inject);
}
bool LosslessStore::verify_raw(const std::string& id){
  const auto p=data_.raw(id);if(!p||p->state!="AVAILABLE")return false;
  try {
    const auto full=resolve_relative(p->uri);
    const auto size=fs::file_size(full);
    bool same=size>0&&size==static_cast<std::uintmax_t>(p->file_bytes)&&size==static_cast<std::uintmax_t>(p->stride)*static_cast<std::uintmax_t>(p->height);
    same=same&&(file_hash(full)==p->file_sha)&&(p->file_sha==p->pixel_sha)&&(p->pixel_sha==p->expected_pixel_sha);
    if(same)return true;
  }catch(const std::exception&){}
  data_.raw_fail(id,"CORRUPT");return false;
}
RecoveryReport LosslessStore::reconcile(){
  RecoveryReport report;
  for(const auto& p:data_.unfinished_raw()){
    if(p.state=="PENDING"){++report.pending;continue;}
    try {
      auto full=resolve_relative(p.uri);
      const auto expected=static_cast<std::uintmax_t>(p.stride)*static_cast<std::uintmax_t>(p.height);
      if(fs::is_regular_file(full)&&fs::file_size(full)==expected&&expected>0){
        const auto digest=file_hash(full);
        if(digest==p.expected_pixel_sha){data_.raw_available(p.asset_id,digest,digest,static_cast<std::int64_t>(expected));++report.promoted;continue;}
      }
    }catch(const std::exception&){}
    data_.raw_fail(p.asset_id,"FAILED");++report.failed;
  }
  return report;
}
} // namespace aoi::agent03
