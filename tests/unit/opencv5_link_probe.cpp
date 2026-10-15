// Actual dynamically-linked OpenCV 5.0.0 smoke, not a production vision pipeline.
#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#include <cassert>
#include <iostream>
int main() {
    const std::string version=cv::getVersionString();
    assert(version=="5.0.0" && "AOI must not silently link OpenCV 4.x/other OpenCV version");
    cv::Mat gray(7,11,CV_8UC1,cv::Scalar(127));
    cv::Mat rgb;
    cv::cvtColor(gray,rgb,cv::COLOR_GRAY2BGR);
    assert(rgb.rows==7 && rgb.cols==11 && rgb.channels()==3);
    assert(rgb.at<cv::Vec3b>(0,0)[1]==127);
    std::cout<<"PASS: OpenCV "<<version<<" linked, grayscale->BGR real pixel operation\n";
}
