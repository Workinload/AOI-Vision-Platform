#pragma once
// AOI Agent03 W052-W055 branch-dev-only OpenCV5 pixel pipeline.
// No A0 C01 DTO or real PLC/camera access. SIMULATED detection is NOT accuracy.
#include <opencv2/core.hpp>
#include <cstdint>
#include <filesystem>
#include <span>
#include <string>
#include <vector>
namespace aoi::agent03::vision {
struct Roi {int x=0,y=0,width=0,height=0;};
struct Defect {int cls=2;Roi sensor_box;double score=1.0;};
struct Transform {
  cv::Matx33d sensor_to_detection = cv::Matx33d::eye();
  cv::Matx33d detection_to_display = cv::Matx33d::eye();
  cv::Point2d point_to_display(const cv::Point2d& sensor)const;
  cv::Point2d point_to_sensor(const cv::Point2d& display)const;
  std::string sha256()const;
};
struct Detection {
  std::string origin = "SIMULATED";
  std::string processing = "SUCCEEDED", quality = "UNDECIDED";
  std::string decoded_pixel_sha256;
  std::vector<Defect> defects;
};
struct SplitPair {cv::Mat first,second;};
void validate_gray8(const cv::Mat& image);
cv::Rect checked_roi(const Roi& r,const cv::Size& frame);
cv::Mat sensor_crop(const cv::Mat& original,const Roi& sensor_roi);
cv::Mat detection_crop(const cv::Mat& sensor,const Roi& detection_roi);
// A3 must independently sign rotation/odd-even semantics before merger.
SplitPair split_lines(const cv::Mat& source,bool slave_rear);
std::string pixel_sha256(const cv::Mat& pixels);
Detection deterministic_mock(const cv::Mat& image,std::uint64_t seed,bool inject_error=false);
Detection threshold_defects(const cv::Mat& sensor,const Roi& detection_roi,int gray_threshold,
                            int min_area);
// DisplayROI clips presentation pixels only, never mutates Detection status/defects.
cv::Mat render_overlay(const cv::Mat& sensor,const Detection& result,
                       const Roi& display_roi);
std::string overlay_json(const Detection& result);
struct PngImage {std::vector<std::uint8_t> encoded;std::string file_sha;std::string decoded_pixel_sha;};
PngImage lossless_png_roundtrip(const cv::Mat& sensor);
} // namespace aoi::agent03::vision
