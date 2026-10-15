#include "aoi/vision/pipeline.hpp"
#include "aoi/models/model_registry.hpp"
#include "aoi/replay/lab.hpp"
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <unistd.h>
using namespace aoi::agent03;
namespace fs=std::filesystem;
static int asserts=0;
void ok(bool good,const char* msg){if(!good)throw std::runtime_error(msg);++asserts;std::cout<<"PASS "<<msg<<'\n';}
void bad(const std::function<void()>& cb,const char* msg){bool failed=false;try{cb();}catch(const std::exception&){failed=true;}ok(failed,msg);}
int main(int argc,char** argv){
try{
 if(argc!=2)throw std::runtime_error("migration path required");
 std::string s="/tmp/aoi-agent03-vision-XXXXXX";auto* name=mkdtemp(s.data());if(!name)throw std::runtime_error("temp failed");
 fs::path root(name);
 struct Clean{fs::path root;~Clean(){std::error_code ec;fs::remove_all(root,ec);}} clean{root};
 cv::Mat sensor(120,160,CV_8UC1,cv::Scalar(0));cv::rectangle(sensor,cv::Rect(95,95,8,8),cv::Scalar(255),cv::FILLED);
 cv::rectangle(sensor,cv::Rect(5,5,8,8),cv::Scalar(255),cv::FILLED);
 auto d=vision::threshold_defects(sensor,{0,0,160,120},200,4);
 ok(d.processing=="SUCCEEDED"&&d.quality=="NG"&&d.defects.size()==2,"OpenCV5 finds two actual bright regions");
 auto cropped=vision::render_overlay(sensor,d,{0,0,32,32});
 ok(cropped.cols==32&&d.defects.size()==2&&d.quality=="NG","display crop cannot hide global NG");
 auto detection=vision::threshold_defects(sensor,{0,0,32,32},200,4);
 ok(detection.defects.size()==1&&detection.quality=="NG","detection ROI affects detections differently");
 bad([&](){vision::threshold_defects(sensor,{150,100,100,30},200,4);},"out of range detection ROI fails");
 bad([&](){vision::sensor_crop(sensor,{0,0,0,40});},"empty SensorROI fails");
 auto png=vision::lossless_png_roundtrip(sensor);
 ok(!png.encoded.empty()&&png.file_sha!=png.decoded_pixel_sha&&png.decoded_pixel_sha==vision::pixel_sha256(sensor),"actual OpenCV PNG encoded hash and decoded pixel SHA checked");
 auto mock=vision::deterministic_mock(sensor,49);
 ok(mock.origin=="SIMULATED"&&mock.decoded_pixel_sha256==vision::pixel_sha256(sensor),"deterministic synthetic provider correctly labelled");
 ok(mock.quality==vision::deterministic_mock(sensor,49).quality,"repeat MockInference deterministic");
 ok(vision::deterministic_mock(cv::Mat(),49).quality=="UNDECIDED","missing simulated frame cannot OK");
 ok(vision::deterministic_mock(sensor,49,true).processing=="ERROR","injected model error cannot masquerade as OK");
 vision::Transform t; t.sensor_to_detection=cv::Matx33d(1,0,-10, 0,1,-20, 0,0,1);
 t.detection_to_display=cv::Matx33d(0,-1,120, 1,0,0, 0,0,1);
 auto orig=cv::Point2d(25,37);auto inverse=t.point_to_sensor(t.point_to_display(orig));
 ok(std::abs(inverse.x-orig.x)<1e-9&&std::abs(inverse.y-orig.y)<1e-9,"two-stage affine inverse round trip");
 ok(t.sha256()==t.sha256(),"TransformChain SHA stable");
 cv::Mat scan(5,3,CV_8UC1);for(int y=0;y<5;++y)for(int x=0;x<3;++x)scan.at<std::uint8_t>(y,x)=static_cast<std::uint8_t>(10*y+x);
 auto line=vision::split_lines(scan,false);
 ok(line.first.rows==2&&line.second.rows==2&&line.first.at<std::uint8_t>(1,2)==scan.at<std::uint8_t>(1,0),"real OpenCV split and front rotation");
 auto rear=vision::split_lines(scan,true);
 ok(rear.first.rows==2&&rear.second.rows==2,"rear mirror and 180 view generated");
 models::ModelRegistry models;
 auto sample=root/"demo.onnx";{std::ofstream f(sample,std::ios::binary);f<<"SIMULATED MODEL FILE; NOT A VALID ONNX";}
 auto sha=models::file_sha256(sample);
 models::ModelManifest manifest;manifest.id="sim-model";manifest.version="v1";manifest.local_model=sample;manifest.format="ONNX";manifest.expected_sha256=sha;manifest.height=160;manifest.width=120;manifest.classes={"good","defect"};
 models.register_model(manifest);
 ok(models.lookup("sim-model","v1").expected_sha256==sha,"actual model bytes SHA and manifest shape gate");
 bad([&](){auto wrong=manifest;wrong.version="v2";wrong.expected_sha256=std::string(64,'0');models.register_model(wrong);},"model hash mismatch denied");
 bad([&](){auto wrong=manifest;wrong.version="v2";wrong.classes.clear();models.register_model(wrong);},"missing class labels denied");
 bad([&](){models.lookup("missing","v3");},"unknown model denied");
 auto labroot=root/"sandbox";
 replay::LabRunner lab(labroot,argv[1]);
 auto input=labroot/"lab_only"/"inputs"/"CH08.png";
 ok(fs::exists(input.parent_path()),"offline lab input directory independent");
 ok(cv::imwrite(input.string(),sensor),"actual PNG fixture written for replay");
 replay::Recipe a;a.name="recipeA";a.detection_roi={0,0,160,120};a.gray_threshold=200;a.min_area=4;
 replay::Recipe b=a;b.name="recipeB";b.gray_threshold=255;
 auto first=lab.single({"CH08","CH08.png"},a);
 ok(first.quality=="NG"&&first.defect_count==2&&first.raw_pixel_sha==vision::pixel_sha256(sensor),"offline single-channel real PNG reprocess");
 auto ab=lab.ab({"CH08","CH08.png"},a,b);
 ok(ab.changed_quality&&ab.changed_defect_count==-2,"offline A/B comparator executes distinct real recipe thresholds");
 auto batch=lab.batch({{"CH08","CH08.png"},{"CH09","missing.png"}},a);
 ok(batch.size()==2&&batch[1].processing=="MISSING"&&batch[1].quality=="UNDECIDED","batch missing PNG stays MISSING not OK");
 std::vector<replay::Sample> eleven;for(int i=1;i<=11;++i){char buf[5];std::snprintf(buf,sizeof(buf),"CH%02d",i);eleven.push_back({buf,"CH08.png"});}
 auto results=lab.piece11(eleven,a);
 ok(results.size()==11&&results.front().quality=="NG","same-piece 11-view offline execution");
 bad([&](){lab.piece11({{"CH01","CH08.png"}},a);},"incomplete 11 channel declaration rejected");
 bad([&](){lab.single({"CH08","../escapes.png"},a);},"offline input traversal rejected");
 ok(lab.db_path().string().find("lab_only")!=std::string::npos,"offline records written to separate WAL DB");
 std::cout<<"VISION_LAB_ASSERTIONS="<<asserts<<" EXIT=0\n";return 0;
}catch(const std::exception& e){std::cerr<<"VISION_TEST_FAILED: "<<e.what()<<'\n';return 1;}
}
