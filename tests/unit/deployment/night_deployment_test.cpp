#include "../../../libs/deployment/night_deployment.hpp"
#include <cassert>
#include <iostream>
using namespace aoi::a7::night;
int main(){
 MachineProfile p{"machine","reg",{}};
 for(int i=1;i<=7;++i)p.device_role.emplace("CAM"+std::to_string(i),i<=4?"MASTER":"SLAVE");
 p.device_role.emplace("LINE8","SLAVE");p.device_role.emplace("LINE9","MASTER");
 assert(p.valid());Registry r(p);const std::string bytes="synthetic model";assert(r.model("model",bytes,digest(bytes))==Result::OK);
 Draft d{"D","A","",p.sha256(),{},1};
 for(int i=1;i<=11;++i){char c[5];std::snprintf(c,sizeof(c),"CH%02d",i);
 auto device=i>=10?"LINE9":i>=8?"LINE8":"CAM"+std::to_string(i);
 auto host=i>=10?"MASTER":i>=8?"SLAVE":i<=4?"MASTER":"SLAVE";
 d.stations.emplace(c,Station{c,device,host,"model",digest(bytes),std::string(64,'a'),"ROI",.5,1});}
 assert(r.draft(d)==Result::OK);assert(r.freeze("D","R")==Result::OK);
 assert(r.freeze("D","R")==Result::DUPLICATE);
 assert(r.active("A").empty());auto copy=r.get_draft("D")->stations.at("CH02");copy.threshold=.8;
 assert(r.patch("D","CH02",1,copy,"")==Result::OK);assert(r.patch("D","CH02",1,copy,"")==Result::CONFLICT);
 assert(r.release("R")->stations.at("CH02").threshold==.5);
 Deployment dep(r,"TX","R",2,"/tmp/a7-c10-night-journal");
 Receipt m{"TX","MASTER",dep.expected_hash(),dep.expected_hash(),2,true,true,true},s=m;s.host="SLAVE";
 assert(dep.begin());assert(dep.device(m));assert(dep.phase()!=Phase::PREPARED_BOTH);
 assert(dep.device(s));assert(dep.virtual_plc(m));assert(dep.phase()!=Phase::PREPARED_BOTH);
 assert(dep.virtual_plc(s));assert(dep.phase()==Phase::PREPARED_BOTH);
 assert(dep.commit());assert(dep.active(m));assert(dep.phase()!=Phase::ACTIVE_SIMULATED);
 assert(dep.active(s));assert(dep.phase()==Phase::ACTIVE_SIMULATED);assert(!dep.production_ready());
 assert(r.active("A")=="R");assert(Deployment::requires_operator_hold_on_restart("/tmp/a7-c10-night-journal"));
 Deployment bad(r,"TX2","R",3,"/tmp/a7-c10-night-journal-bad");assert(bad.begin());assert(!bad.device(m));
 assert(bad.phase()==Phase::FAILED_SAFE);assert(!bad.production_ready());
 std::cout<<"A7_NIGHT_SIM_ONLY_PREVIEW_PASS\n";
}
