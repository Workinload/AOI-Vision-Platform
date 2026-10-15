// A10 synthetic 8200x15000 x8-bit OpenCV 5.0 memory stress. NOT an Itek SDK/device capture.
#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#include <QCoreApplication>
#include <QCryptographicHash>
#include <QByteArray>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace {
constexpr int kWidth=8200;
constexpr int kHeight=15000;
constexpr uint64_t kExpectedBytes=123000000ull;
std::vector<long long> rssSamples;
long long rssKb(const std::string& property) {
    std::ifstream stat("/proc/self/status");
    std::string s;
    while (std::getline(stat,s)) {
        if (s.rfind(property+":",0)==0) {
            const size_t at=s.find_first_of("0123456789");
            return at==std::string::npos? -1:std::stoll(s.substr(at));
        }
    }
    return -1;
}
void sample() {rssSamples.push_back(rssKb("VmRSS"));}
std::string sha(const cv::Mat& mat) {
    QCryptographicHash h(QCryptographicHash::Sha256);
    if (!mat.isContinuous()) throw std::runtime_error("noncontiguous frame hash requires row streaming");
    h.addData(reinterpret_cast<const char*>(mat.ptr()),
              static_cast<qsizetype>(mat.total()*mat.elemSize()));
    return h.result().toHex().toStdString();
}
void need(bool ok,const char* msg) {
    if (!ok)throw std::runtime_error(msg);
}
}
int main(int argc,char** argv) {
    QCoreApplication app(argc,argv);
    try {
        auto started=std::chrono::steady_clock::now();
        sample();
        cv::Mat physical(kHeight,kWidth,CV_8UC1);
        need(physical.isContinuous(),"raw buffer not continuous");
        need(static_cast<uint64_t>(physical.total()*physical.elemSize())==kExpectedBytes,
             "source not 8200x15000x8");
        for (int row=0;row<kHeight;row++) {
            uint8_t* p=physical.ptr<uint8_t>(row);
            for (int x=0;x<kWidth;x++) p[x]=static_cast<uint8_t>((row*13 +x*7 +(row>>3))&255);
            if (row%1500==0)sample();
        }
        sample();
        cv::Mat oddPhysicalRows((kHeight+1)/2,kWidth,CV_8UC1);
        cv::Mat evenPhysicalRows(kHeight/2,kWidth,CV_8UC1);
        // 1-based [1::2] first/third physical scanline and [2::2] second/fourth.
        for (int row=0;row<kHeight;row++) {
            uint8_t* dest=(row%2==0?oddPhysicalRows:evenPhysicalRows).ptr<uint8_t>(row/2);
            std::memcpy(dest,physical.ptr<uint8_t>(row),kWidth);
            if (row%1500==0)sample();
        }
        sample();
        need(oddPhysicalRows.rows==7500 && evenPhysicalRows.rows==7500,"split wrong row count");
        need(oddPhysicalRows.total()*oddPhysicalRows.elemSize()==61500000ull,"split one bytes mismatch");
        need(evenPhysicalRows.total()*evenPhysicalRows.elemSize()==61500000ull,"split two bytes mismatch");
        cv::Mat merged(kHeight,kWidth,CV_8UC1);
        for (int row=0;row<kHeight;row++) {
            const uint8_t* src=(row%2==0?oddPhysicalRows:evenPhysicalRows).ptr<uint8_t>(row/2);
            std::memcpy(merged.ptr<uint8_t>(row),src,kWidth);
            if(row%1500==0)sample();
        }
        need(std::memcmp(physical.ptr(),merged.ptr(),kExpectedBytes)==0,
             "split/interleave roundtrip not byte exact");
        sample();
        const std::string parentHash=sha(physical);
        need(parentHash==sha(merged),"decoded SHA roundtrip mismatch");
        const std::string firstHash=sha(oddPhysicalRows);
        const std::string secondHash=sha(evenPhysicalRows);
        need(firstHash!=secondHash,"odd/even scanline channels unexpectedly identical");
        sample();
        int caught=0;
        oddPhysicalRows.ptr<uint8_t>(0)[0]^=1;
        cv::Mat tampered=physical.clone(); // independent negative check parent bytes remain unmodified
        std::memcpy(tampered.ptr<uint8_t>(0),oddPhysicalRows.ptr<uint8_t>(0),kWidth);
        if (std::memcmp(tampered.ptr(),physical.ptr(),kExpectedBytes)!=0) ++caught;
        oddPhysicalRows.ptr<uint8_t>(0)[0]^=1;
        const cv::Mat malformedWidth=oddPhysicalRows(cv::Rect(0,0,kWidth-1,oddPhysicalRows.rows));
        if (malformedWidth.cols!=kWidth || malformedWidth.rows!=7500) ++caught;
        const cv::Mat missingLastRow=oddPhysicalRows(cv::Rect(0,0,kWidth,oddPhysicalRows.rows-1));
        if (missingLastRow.rows!=7500 || missingLastRow.cols!=kWidth) ++caught;
        need(caught==3,"failure to detect stress negative fixtures");
        cv::Mat roi=physical(cv::Rect(0,0,64,64));
        cv::Mat mirrored;
        cv::flip(roi,mirrored,1); // actual OpenCV5 imgproc not stub pixels
        for (int y=0;y<64;y++)
            for (int x=0;x<64;x++)
                need(mirrored.at<uint8_t>(y,x)==roi.at<uint8_t>(y,63-x),
                     "OpenCV imgproc mirror not exact");
        sample();
        std::vector<long long> sorted=rssSamples;
        std::sort(sorted.begin(),sorted.end());
        auto index=static_cast<size_t>(std::ceil(0.95*sorted.size())-1);
        long long p95=sorted.at(index);
        long long peak=rssKb("VmHWM");
        double ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-started).count();
        std::cout << "{\"status\":\"PASS\",\"source\":\"ACTUAL_OPENCV5_SYNTHETIC_123MB\","
                  << "\"width\":8200,\"height\":15000,\"depth_bits\":8,"
                  << "\"raw_parent_assets\":1,\"raw_bytes\":"<<kExpectedBytes
                  << ",\"odd_rows_1_based\":7500,\"even_rows_1_based\":7500,"
                  << "\"first_channel_bytes\":61500000,\"second_channel_bytes\":61500000,"
                  << "\"parent_sha256\":\""<<parentHash<<"\","
                  << "\"odd_sha256\":\""<<firstHash<<"\","
                  << "\"even_sha256\":\""<<secondHash<<"\","
                  << "\"mutation_negatives\":"<<caught<<",\"mutations_total\":3,"
                  << "\"rss_sample_count\":"<<sorted.size()<<","
                  << "\"rss_p95_kb\":"<<p95<<",\"rss_peak_kb\":"<<peak<<","
                  << "\"duration_ms\":"<<ms<<","
                  << "\"hardware_status\":\"NOT_CONNECTED\","
                  << "\"raw_asset_persisted\":false,\"wbs_verified\":false}"
                  << std::endl;
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "LINE_SCAN_123MB_FAIL_CLOSED: "<<e.what()<<std::endl;
        return 2;
    }
}
