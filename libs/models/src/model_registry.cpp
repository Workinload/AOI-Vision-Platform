#include "aoi/models/model_registry.hpp"
#include <openssl/evp.h>
#include <array>
#include <fstream>
#include <set>
#include <stdexcept>
namespace aoi::agent03::models {
namespace {
void need(bool allowed,const char* reason){if(!allowed)throw std::invalid_argument(reason);}
}
std::string file_sha256(const std::filesystem::path& file){
  std::ifstream stream(file,std::ios::binary);need(static_cast<bool>(stream),"model file absent");
  auto* context=EVP_MD_CTX_new();if(!context)throw std::runtime_error("SHA alloc failed");
  struct Cleanup{EVP_MD_CTX* ptr;~Cleanup(){EVP_MD_CTX_free(ptr);}} cleanup{context};
  need(EVP_DigestInit_ex(context,EVP_sha256(),nullptr)==1,"hash init failure");
  std::array<char,1<<16> chunk;
  while(stream){stream.read(chunk.data(),static_cast<std::streamsize>(chunk.size()));const auto n=stream.gcount();
    if(n>0)need(EVP_DigestUpdate(context,chunk.data(),static_cast<std::size_t>(n))==1,"hash update failure");}
  need(stream.eof(),"model stream read error");
  unsigned char hash[EVP_MAX_MD_SIZE];unsigned int n=0;
  need(EVP_DigestFinal_ex(context,hash,&n)==1&&n==32,"hash final failure");
  constexpr char chars[]="0123456789abcdef";std::string digest;digest.reserve(64);
  for(unsigned i=0;i<n;++i){digest.push_back(chars[hash[i]>>4]);digest.push_back(chars[hash[i]&15]);}return digest;
}
void ModelRegistry::register_model(const ModelManifest& manifest){
  need(!manifest.id.empty()&&!manifest.version.empty(),"missing model identity");
  need(manifest.format=="ONNX"||manifest.format=="OPENVINO_IR","unsupported model format");
  need(manifest.batch==1&&manifest.channels>=1&&manifest.channels<=4&&manifest.height>0&&manifest.width>0,"model shape invalid");
  need(!manifest.classes.empty(),"model classes missing");
  std::set<std::string> labels;
  for(const auto& name:manifest.classes){need(!name.empty(),"blank class label");need(labels.insert(name).second,"duplicate class label");}
  need(manifest.expected_sha256.size()==64,"model hash length missing");
  if(manifest.format=="ONNX")need(manifest.local_model.extension()==".onnx","ONNX model extension mismatch");
  if(manifest.format=="OPENVINO_IR"){
    need(manifest.local_model.extension()==".xml","OpenVINO IR needs XML");
    auto weights=manifest.local_model;weights.replace_extension(".bin");
    need(std::filesystem::is_regular_file(weights),"OpenVINO IR weights absent");
  }
  need(file_sha256(manifest.local_model)==manifest.expected_sha256,"model SHA256 mismatch");
  need(registry_.emplace(manifest.id+"\x1f"+manifest.version,manifest).second,"model version already exists");
}
const ModelManifest& ModelRegistry::lookup(const std::string& id,const std::string& version)const{
  const auto i=registry_.find(id+"\x1f"+version);
  if(i==registry_.end())throw std::out_of_range("unknown model cannot infer OK");return i->second;
}
}
