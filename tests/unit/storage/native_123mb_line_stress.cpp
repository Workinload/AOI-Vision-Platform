// Standalone POSIX SIM_ONLY line-scan 8200x15000=123MB real byte persistence stress.
#include "aoi/assets/lossless_store.hpp"
#include <openssl/evp.h>
#include <array>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <sys/resource.h>
#include <unistd.h>
namespace fs=std::filesystem;
using namespace aoi::agent03;
int main(int argc,char** argv){
try{
 if(argc!=2)throw std::invalid_argument("migration SQL path required");
 std::string pattern="/tmp/aoi-123mb-XXXXXX";auto* temp=mkdtemp(pattern.data());if(!temp)throw std::runtime_error("mkdtemp failed");
 fs::path root(temp);struct Cleanup{fs::path dir;~Cleanup(){std::error_code ec;fs::remove_all(dir,ec);}} clean{root};
 constexpr int width=8200,height=15000;constexpr std::uintmax_t raw_size=static_cast<std::uintmax_t>(width)*height;
 auto source=root/"input.raw8";
 const auto start=std::chrono::steady_clock::now();
 EVP_MD_CTX* ctx=EVP_MD_CTX_new();if(!ctx)throw std::runtime_error("ctx allocation failed");
 if(EVP_DigestInit_ex(ctx,EVP_sha256(),nullptr)!=1)throw std::runtime_error("sha init failed");
 {
  std::ofstream out(source,std::ios::binary);if(!out)throw std::runtime_error("source open failed");
  std::array<std::uint8_t,width> row;
  for(int y=0;y<height;++y){
   for(int x=0;x<width;++x)row[x]=static_cast<std::uint8_t>((x*13+y*7)&0xff);
   out.write(reinterpret_cast<const char*>(row.data()),width);
   if(EVP_DigestUpdate(ctx,row.data(),width)!=1)throw std::runtime_error("sha stream failed");
  }
  out.flush();if(!out)throw std::runtime_error("source write failed");
 }
 unsigned char sha[EVP_MAX_MD_SIZE];unsigned int bytes=0;
 if(EVP_DigestFinal_ex(ctx,sha,&bytes)!=1||bytes!=32)throw std::runtime_error("sha finalize failed");EVP_MD_CTX_free(ctx);
 constexpr char hex[]="0123456789abcdef";std::string checksum;
 for(unsigned int i=0;i<bytes;++i){checksum.push_back(hex[sha[i]>>4]);checksum.push_back(hex[sha[i]&15]);}
 if(fs::file_size(source)!=raw_size)throw std::runtime_error("raw size not 123000000");
 WalStore db(root,"MASTER",argv[1]);db.create_piece("stress-wp",1,"stress-rel","SIMULATED","2026-10-16T00:00:00Z");
 RawParent parent;parent.asset_id="stress-parent";parent.workpiece_id="stress-wp";parent.host_id="MASTER";
 parent.group_id="stress-line";parent.device_id="SIM-LS-1";parent.capture_id="stress-cap";parent.attempt_no=1;
 parent.pixel_format="GRAY8";parent.width=width;parent.height=height;parent.stride=width;parent.expected_pixel_sha=checksum;
 db.reserve_raw(parent,"2026-10-16T00:00:00Z");
 db.link_raw({"stress-wp","CH08","stress-line","stress-parent",1});db.link_raw({"stress-wp","CH09","stress-line","stress-parent",1});
 LosslessStore assets(db,root/"MASTER"/"assets");assets.write_raw_file(parent.asset_id,"stress/raw-frame.raw8",source);
 if(!assets.verify_raw(parent.asset_id))throw std::runtime_error("real raw bytes verification failed");
 const auto stored=db.raw(parent.asset_id);
 if(!stored||stored->state!="AVAILABLE"||stored->file_bytes!=static_cast<std::int64_t>(raw_size)||stored->file_sha!=checksum)throw std::runtime_error("WAL asset metadata wrong");
 if(db.count_for_test("raw_assets")!=1||db.count_for_test("channel_asset_links")!=2)throw std::runtime_error("shared parent count wrong");
 const auto ms=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-start).count();
 rusage usage{};getrusage(RUSAGE_SELF,&usage);
 std::cout<<"PASS RAW_FRAME_BYTES="<<raw_size<<" HOST=MASTER CHANNELS=CH08,CH09 SHARED_PARENT=1\n";
 std::cout<<"RAW_FILE_SHA256="<<checksum<<" DECODED_PIXEL_SHA256="<<stored->pixel_sha<<"\n";
 std::cout<<"WALL_MS="<<ms<<" PEAK_RSS_KIB="<<usage.ru_maxrss<<" EXIT=0\n";
 return 0;
}catch(const std::exception& e){std::cerr<<"STRESS_FAILED: "<<e.what()<<'\n';return 1;}
}
