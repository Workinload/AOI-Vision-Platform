#include "aoi/vision/pipeline.hpp"
#include "aoi/data/wal_store.hpp"
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <openssl/evp.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#if CV_VERSION_MAJOR != 5 || CV_VERSION_MINOR != 0
#error "Agent03 vision requires exact OpenCV 5.0.x API; older ABI cannot count as validated."
#endif
namespace aoi::agent03::vision {
namespace {
void demand(bool ok,const char* msg){if(!ok)throw std::invalid_argument(msg);}
std::string digest_bytes(const void* ptr,std::size_t n){return aoi::agent03::sha256_hex(ptr,n);}
cv::Matx33d inverse(const cv::Matx33d& x){auto determinant=cv::determinant(cv::Mat(x));demand(std::abs(determinant)>1e-12,"singular TransformChain");return x.inv();}
cv::Point2d apply(const cv::Matx33d& a,const cv::Point2d& p){
  auto x=a(0,0)*p.x+a(0,1)*p.y+a(0,2);
  auto y=a(1,0)*p.x+a(1,1)*p.y+a(1,2);
  auto w=a(2,0)*p.x+a(2,1)*p.y+a(2,2);
  demand(std::isfinite(x)&&std::isfinite(y)&&std::isfinite(w)&&std::abs(w)>1e-12,"bad projected point");
  return {x/w,y/w};
}
}
cv::Point2d Transform::point_to_display(const cv::Point2d& sensor)const{return apply(detection_to_display*sensor_to_detection,sensor);}
cv::Point2d Transform::point_to_sensor(const cv::Point2d& display)const{return apply(inverse(detection_to_display*sensor_to_detection),display);}
std::string Transform::sha256()const{
  std::ostringstream out;out<<std::fixed<<std::setprecision(12);
  for(const auto& matrix:{sensor_to_detection,detection_to_display})
    for(int y=0;y<3;++y)for(int x=0;x<3;++x)out<<matrix(y,x)<<';';
  const auto stable=out.str();return digest_bytes(stable.data(),stable.size());
}
void validate_gray8(const cv::Mat& image){demand(!image.empty()&&image.type()==CV_8UC1,"expected nonempty CV_8UC1 pixel surface");demand(image.cols>0&&image.rows>0,"invalid frame dimensions");}
cv::Rect checked_roi(const Roi& r,const cv::Size& frame){
  demand(r.x>=0&&r.y>=0&&r.width>0&&r.height>0,"ROI invalid origin/size");
  demand(frame.width>0&&frame.height>0&&r.x<=frame.width&&r.y<=frame.height&&r.width<=frame.width-r.x&&r.height<=frame.height-r.y,"ROI escapes source frame");
  return {r.x,r.y,r.width,r.height};
}
cv::Mat sensor_crop(const cv::Mat& source,const Roi& roi){validate_gray8(source);return source(checked_roi(roi,source.size())).clone();}
cv::Mat detection_crop(const cv::Mat& sensor,const Roi& roi){validate_gray8(sensor);return sensor(checked_roi(roi,sensor.size())).clone();}
SplitPair split_lines(const cv::Mat& original,bool slave_rear){
  validate_gray8(original);demand(original.rows>=2,"need two scan rows");
  // 0-index even row == 1-index odd sample; preserve source pixel values.
  cv::Mat image;
  if(slave_rear)cv::flip(original,image,1);else image=original;
  cv::Mat first((image.rows+1)/2,image.cols,CV_8UC1);
  cv::Mat second(image.rows/2,image.cols,CV_8UC1);
  for(int row=0;row<image.rows;++row){
    cv::Mat& target=(row%2==0)?first:second;
    std::memcpy(target.ptr<std::uint8_t>(row/2),image.ptr<std::uint8_t>(row),static_cast<std::size_t>(image.cols));
  }
  if(slave_rear){cv::flip(first,first,-1);cv::flip(second,second,-1);}
  else cv::flip(first,first,-1); // master/front split 1 rotated 180, split 2 unchanged
  return {std::move(first),std::move(second)};
}
std::string pixel_sha256(const cv::Mat& source){
  validate_gray8(source);
  EVP_MD_CTX* context=EVP_MD_CTX_new();if(!context)throw std::runtime_error("EVP allocate failed");
  struct Finalizer {EVP_MD_CTX* p;~Finalizer(){EVP_MD_CTX_free(p);}} guard{context};
  if(EVP_DigestInit_ex(context,EVP_sha256(),nullptr)!=1)throw std::runtime_error("SHA init failed");
  for(int y=0;y<source.rows;++y)
    if(EVP_DigestUpdate(context,source.ptr(y),static_cast<std::size_t>(source.cols))!=1)throw std::runtime_error("SHA row failed");
  unsigned char digest[EVP_MAX_MD_SIZE];unsigned int n=0;
  if(EVP_DigestFinal_ex(context,digest,&n)!=1||n!=32)throw std::runtime_error("SHA final failed");
  static constexpr char hex[]="0123456789abcdef";std::string result;result.reserve(64);
  for(unsigned int i=0;i<n;++i){result.push_back(hex[digest[i]>>4]);result.push_back(hex[digest[i]&15]);}
  return result;
}
Detection deterministic_mock(const cv::Mat& image,std::uint64_t seed,bool inject_error){
  Detection r;r.origin="SIMULATED";
  if(inject_error){r.processing="ERROR";return r;}
  if(image.empty()){r.processing="MISSING";return r;}
  validate_gray8(image);r.decoded_pixel_sha256=pixel_sha256(image);
  // Stable seeded pixel observation. This is synthetic fixture behavior, not AI accuracy.
  std::uint64_t fingerprint=seed;
  for(int y=0;y<image.rows;++y){const auto* row=image.ptr<std::uint8_t>(y);
    for(int x=0;x<image.cols;++x)fingerprint=(fingerprint*1099511628211ULL)^row[x];}
  if(fingerprint%3==0){r.defects.push_back({2,{static_cast<int>(fingerprint%static_cast<std::uint64_t>(image.cols)),
                                         static_cast<int>((fingerprint/7)%static_cast<std::uint64_t>(image.rows)),1,1},1.0});r.quality="NG";}
  // Can report OK only when fully valid synthetic image was accepted; tag SIMULATED.
  else r.quality="OK";
  return r;
}
Detection threshold_defects(const cv::Mat& sensor,const Roi& detection_roi,int threshold,int min_area){
  validate_gray8(sensor);demand(threshold>=0&&threshold<=255&&min_area>0,"invalid processing parameters");
  auto rect=checked_roi(detection_roi,sensor.size());
  cv::Mat binary;cv::threshold(sensor(rect),binary,static_cast<double>(threshold),255.0,cv::THRESH_BINARY);
  cv::Mat labels,stats,centroids;
  const int components=cv::connectedComponentsWithStats(binary,labels,stats,centroids,8,CV_32S);
  Detection result;result.origin="OPENCV5_ALGORITHM_NOT_MODEL";result.decoded_pixel_sha256=pixel_sha256(sensor);
  for(int component=1;component<components;++component){
    if(stats.at<int>(component,cv::CC_STAT_AREA)<min_area)continue;
    const int x=stats.at<int>(component,cv::CC_STAT_LEFT)+rect.x;
    const int y=stats.at<int>(component,cv::CC_STAT_TOP)+rect.y;
    result.defects.push_back(Defect{2,Roi{x,y,stats.at<int>(component,cv::CC_STAT_WIDTH),stats.at<int>(component,cv::CC_STAT_HEIGHT)},1.0});
  }
  result.quality=result.defects.empty()?"OK":"NG";return result;
}
cv::Mat render_overlay(const cv::Mat& sensor,const Detection& result,const Roi& display_roi){
  validate_gray8(sensor);
  cv::Mat bgr;cv::cvtColor(sensor,bgr,cv::COLOR_GRAY2BGR);
  for(const auto& defect:result.defects){
    auto rect=checked_roi(defect.sensor_box,sensor.size());
    cv::rectangle(bgr,rect,cv::Scalar(0,0,255),1,cv::LINE_8);
  }
  // Crop only rendered view; never mutate result's defects or quality.
  return bgr(checked_roi(display_roi,sensor.size())).clone();
}
std::string overlay_json(const Detection& result){
  std::ostringstream out;out<<"[";bool first=true;
  for(const auto& d:result.defects){if(!first)out<<',';first=false;
    out<<"{\"cls\":"<<d.cls<<",\"x\":"<<d.sensor_box.x<<",\"y\":"<<d.sensor_box.y
       <<",\"w\":"<<d.sensor_box.width<<",\"h\":"<<d.sensor_box.height
       <<",\"space\":\"SENSOR_PIXEL\"}";}
  return out.str()+"]";
}
PngImage lossless_png_roundtrip(const cv::Mat& image){
  validate_gray8(image);
  PngImage p;auto original=pixel_sha256(image);
  if(!cv::imencode(".png",image,p.encoded,{cv::IMWRITE_PNG_COMPRESSION,3}))throw std::runtime_error("PNG encode failed");
  demand(!p.encoded.empty(),"PNG encoder returned no bytes");
  auto decoded=cv::imdecode(p.encoded,cv::IMREAD_UNCHANGED);
  validate_gray8(decoded);
  demand(decoded.size()==image.size(),"PNG decoded image dimensions altered");
  p.decoded_pixel_sha=pixel_sha256(decoded);
  demand(p.decoded_pixel_sha==original,"PNG pixel hash mismatch after decoder");
  p.file_sha=sha256_hex(p.encoded.data(),p.encoded.size());return p;
}
} // namespace aoi::agent03::vision
