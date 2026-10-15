// W006 owner-scoped standalone C++20 contract tests: no PLC hardware or UDP.
#include "aoi/plc/locked_signal_registry.hpp"
#include <array>
#include <fstream>
#include <iostream>
#include <iterator>
#include <set>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

using namespace aoi::plc::w006;
namespace {
int passed=0, failed=0;
void check(bool ok,std::string_view label) {
    if (ok){std::cout<<"PASS "<<label<<'\n';++passed;}
    else {std::cerr<<"FAIL "<<label<<'\n';++failed;}
}
std::string read(const char* p) {
    std::ifstream f(p,std::ios::binary);
    if (!f) throw std::runtime_error(std::string("test fixture file missing: ")+p);
    return {std::istreambuf_iterator<char>(f),std::istreambuf_iterator<char>()};
}
void replace_once(std::string& v, std::string_view before, std::string_view after) {
    auto pos=v.find(before);
    if (pos==std::string::npos) throw std::runtime_error("missing fixture mutation needle");
    v.replace(pos,before.size(),after);
}
void assert_rejected(std::string_view s,std::string_view g,std::string_view why) {
    const auto attempt=LockedSignalRegistry::from_locked_bytes(s,g);
    check(!attempt.accepted() && attempt.status==ImportStatus::ChecksumMismatch,why);
}
}
int main(int argc,char** argv) {
    if(argc!=3){std::cerr<<"USAGE: w006_locked_registry_test signal.csv gates.csv\n";return 2;}
    try {
        const auto raw_signals=read(argv[1]),raw_gates=read(argv[2]);
        const auto imported=LockedSignalRegistry::from_locked_bytes(raw_signals,raw_gates);
        check(imported.accepted(),"original byte-locked PLC/FG reference accepted");
        const auto file_import=LockedSignalRegistry::from_locked_files(argv[1],argv[2]);
        check(file_import.accepted(),"bounded read-only file import accepted");
        if (!imported.accepted()) return 3;
        const auto& reg=*imported.registry;
        check(reg.signals().size()==45U,"exact original 45 signal facts");
        check(reg.gates().size()==14U,"exact original 14 field gates");
        check(reg.blocked_gates()==7U && reg.open_gates()==7U,"seven BLOCKED + seven OPEN");
        check(!reg.is_live_plc_readback() && !reg.has_field_production_permission(),
              "snapshot != PLC readback and field grant always absent");
        std::set<std::string_view> ids;
        bool read_only=true, not_field_verified=true, no_data_loss=true;
        for(const auto& x:reg.signals()) {
            ids.insert(x.id());
            read_only = read_only && x.at(SignalField::ProductionWriteEnabled)=="FALSE" &&
                        x.at(SignalField::MonitorReadOnlyDefault)=="TRUE" &&
                        !x.production_write_allowed();
            not_field_verified=not_field_verified && !x.field_meaning_verified();
            no_data_loss=no_data_loss && !x.at(SignalField::RiskNote).empty() &&
                         !x.at(SignalField::EvidenceSource).empty() &&
                         !x.at(SignalField::ValidationState).empty();
        }
        check(ids.size()==45U,"all SignalId unique; none inferred by address");
        check(read_only,"45/45 read-only, 45/45 production write FALSE");
        check(not_field_verified,"45/45 historical field meaning remains UNKNOWN");
        check(no_data_loss,"45/45 evidence and risk notes carried through");
        bool gates_denied=true, gates_unsigned=true;
        std::set<std::string_view> gateids;
        for(const auto& g:reg.gates()) {
            gateids.insert(g.id());
            gates_denied=gates_denied && g.at(GateField::ProductionWriteAllowed)=="FALSE" &&
                         !g.production_write_allowed();
            gates_unsigned=gates_unsigned && g.at(GateField::FieldSignoff).empty() &&
                           !g.signed_by_field_engineer();
        }
        check(gateids.size()==14U,"14/14 distinct gate identities");
        check(gates_denied && gates_unsigned,"14/14 FG unsigned and never grants writing");
        check(reg.find_signal("SIG_CAM_REAR_LINE")!=nullptr,"source SignalId lookup succeeds");
        check(reg.find_signal("SIG_UNKNOWN_TO_REJECT")==nullptr,"unknown SignalId lookup returns no fact");
        check(reg.find_gate("FG-01")!=nullptr && reg.find_gate("FG-14")!=nullptr,
              "original FG-01 through FG-14 present");
        check(reg.find_gate("FG-15")==nullptr,"unknown FG is absent, not auto OPEN");
        check(reg.find_gate("FG-01")->at(GateField::Status)=="BLOCKED",
              "XYU physical site gate remains BLOCKED");
        check(reg.find_gate("FG-08")->at(GateField::Status)=="OPEN" &&
              !reg.find_gate("FG-08")->production_write_allowed(),
              "OPEN never implies production activation");
        check(reg.find_by_address("D1013").size()==2U,"D1013 dual-use alias retained");
        check(reg.find_by_address("D1000").size()==2U,"D1000 coordinate/alarm alias retained");
        check(reg.find_by_address("D1030").size()==1U &&
              reg.find_by_address("D1000")[0]->has_conflict(),
              "D1030 not equated to D1000; coordinate conflict UNKNOWN");
        check(reg.find_signal("SIG_BARCODE_WORDS")->at(SignalField::EffectiveWords)=="20" &&
              reg.find_signal("SIG_BARCODE_WORDS")->has_conflict(),
              "20WORD read window retained; 10WORD PLC refresh mismatch UNKNOWN");
        check(reg.find_signal("SIG_FINAL_QUALITY")->at(SignalField::PLCAddress)=="D1016" &&
              reg.find_signal("SIG_DOWNSTREAM_REQUEST")->at(SignalField::PLCAddress)=="D2099",
              "D1016 quality and D2099 output distinct, physical disposition UNKNOWN");
        check(reg.find_signal("SIG_CAM_REAR_LINE")->at(SignalField::Direction)=="PLC_REQUEST/PC_REPLY",
              "legacy source direction preserved without enabling handshake");
        check(reg.find_signal("SIG_DIM_M_L")->at(SignalField::DataType)=="FLOAT32_BADC" &&
              reg.find_signal("SIG_DIM_M_L")->at(SignalField::EffectiveWords)=="2",
              "static 2WORD BADC preserved, not active encoding proof");
        check(reg.find_signal("SIG_RECIPE_NAME")->at(SignalField::DataType)=="BA_STRING",
              "static BA_STRING preserved without guessing barcode endianness");
        check(reg.find_signal("SIG_TURNTABLE")->at(SignalField::Direction)=="PLC_TO_PC",
              "D1015 is PLC source, never CamDone");
        check(LockedSignalRegistry::signal_source_sha256()==
              "97ff0003f2416a1dacf86fecea18ef80ec45fd5f016526333725e0c977e3fee8" &&
              LockedSignalRegistry::gate_source_sha256()==
              "c56d84b46176ca86afcb11088398005f6af86b7fc85d5ce0f9bf7a295871b6de",
              "immutable v2 source SHA256 pinned to official BASELINE_LOCK");

        // Independent negative inputs always pass through the real checksum-locked importer.
        { auto s=raw_signals;s[12]^=0x01;assert_rejected(s,raw_gates,"bit-flip checksum rejected"); }
        { auto s=raw_signals;replace_once(s,"SIG_HEARTBEAT_M","SIG_HEARTBEAT_S");
          assert_rejected(s,raw_gates,"duplicate SignalId rejected"); }
        { auto s=raw_signals;replace_once(s,"SIG_HEARTBEAT_M","SIG_UNKNOWN_NEW");
          assert_rejected(s,raw_gates,"unknown injected SignalId rejected"); }
        { auto s=raw_signals;replace_once(s,"SIG_HEARTBEAT_M","SIG");
          assert_rejected(s,raw_gates,"missing/truncated SignalId rejected"); }
        { auto s=raw_signals;replace_once(s,"D1013","D1014");
          assert_rejected(s,raw_gates,"disputed address silently rewritten rejected"); }
        { auto s=raw_signals;replace_once(s,"FLOAT32_BADC","FLOAT32_BA");
          assert_rejected(s,raw_gates,"invalid datatype/word order rejected"); }
        { auto s=raw_signals;replace_once(s,",FALSE,\r\n",",TRUE,\r\n");
          assert_rejected(s,raw_gates,"production writable mutation rejected"); }
        { auto s=raw_signals;replace_once(s,"G-XYU-1000"," ");
          assert_rejected(s,raw_gates,"conflict group clearing rejected"); }
        { auto s=raw_signals;replace_once(s,"SIG_BARCODE_WORDS","SIG_BARCODE_WORDS_");
          assert_rejected(s,raw_gates,"unverified barcode length change rejected"); }
        { auto g=raw_gates;replace_once(g,",BLOCKED,",",OPEN,");
          assert_rejected(raw_signals,g,"BLOCKED->OPEN silent gate escalation rejected"); }
        { auto g=raw_gates;replace_once(g,",FALSE,",",TRUE,");
          assert_rejected(raw_signals,g,"FG production unlock rejected"); }
        { auto g=raw_gates;g.append("FG-15,test,OPEN,none,none,none,FALSE,\r\n");
          assert_rejected(raw_signals,g,"extra/unapproved FG rejected"); }
        { auto g=raw_gates;g.pop_back();
          assert_rejected(raw_signals,g,"truncated source bytes rejected"); }
        { auto s=raw_signals;replace_once(s,"SIG_DIM_M_L","SIG_DIM_M_L\"bad");
          assert_rejected(s,raw_gates,"broken CSV quoting rejected"); }
        { const std::string too_large(1000001U,'X');
          check(!LockedSignalRegistry::from_locked_bytes(too_large,raw_gates).accepted(),
              "oversized source rejected before hashing/allocation"); }
        { std::string empty;check(!LockedSignalRegistry::from_locked_bytes(empty,raw_gates).accepted(),
              "missing signal CSV fails closed"); }
        { check(!LockedSignalRegistry::from_locked_files("/definitely/not/a/w006/file.csv",argv[2]).accepted(),
              "missing file fails closed"); }
        { auto g=raw_gates;replace_once(g,"FG-01","FG-02");
          assert_rejected(raw_signals,g,"duplicated/missing FG rejected"); }
        { auto g=raw_gates;replace_once(g,"FG-01","FG-99");
          assert_rejected(raw_signals,g,"unknown FG identifier rejected"); }
        { auto g=raw_gates;replace_once(g,",FALSE,\r\n",",FALSE,FAKE_SIGNOFF\r\n");
          assert_rejected(raw_signals,g,"forged field signature rejected"); }

        check(!WritePolicy::production_writes_possible(),"write capability compile-time disabled");
        check(!WritePolicy::assess(nullptr,{"SIG_CAM_POS","127.0.0.1","ADMIN",true,true,0}).allowed &&
              WritePolicy::assess(nullptr,{"SIG_CAM_POS","127.0.0.1","ADMIN",true,true,0}).reason==DenialReason::MissingRegistry,
              "missing registry can never permit write");
        const std::array<std::string_view,10> roles{{"ADMIN","ENGINEER","PLC_TECH","OPERATOR","ANONYMOUS",
              "SUPERVISOR","ROOT","SERVICE","PRODUCTION","QUALITY"}};
        for (const auto role:roles) {
            const auto result=WritePolicy::assess(&reg,{"SIG_CAM_POS","127.0.0.1",role,true,true,0});
            check(!result.allowed && result.reason==DenialReason::ProductionWriteDisabled,
                "ADMIN/root/fake approval cannot write even to simulated endpoint");
        }
        const std::array<std::string_view,8> endpoints{{"192.168.1.1","10.0.0.7","localhost","127.0.0.2",
              "0.0.0.0","127.0.0.1:9600","[fe80::1]",""}};
        for(const auto ep:endpoints) {
            const auto result=WritePolicy::assess(&reg,{"SIG_CAM_POS",ep,"ADMIN",true,true,0});
            check(!result.allowed && result.reason==DenialReason::NonLoopbackTarget,
                "physical, ambiguous or DNS target denied by pure policy");
        }
        for(auto ep:{"127.0.0.1","::1","[::1]"}) {
            const auto result=WritePolicy::assess(&reg,{"SIG_CAM_POS",ep,"ADMIN",true,true,0});
            check(!result.allowed && result.reason==DenialReason::ProductionWriteDisabled,
                  "approved-looking loopback still has NO W006 write endpoint");
        }
        {auto r=WritePolicy::assess(&reg,{"SIG_DOES_NOT_EXIST","127.0.0.1","ADMIN",true,true,0});
            check(!r.allowed && r.reason==DenialReason::UnknownSignal,"unknown signal write rejected");}
        {auto r=WritePolicy::assess(&reg,{"SIG_CAM_REAR_LINE","127.0.0.1","ADMIN",true,true,0});
            check(!r.allowed && r.reason==DenialReason::UnknownOrConflictedMapping,"D1013 alias write rejected");}
        {auto r=WritePolicy::assess(&reg,{"SIG_OLD_UI_X","127.0.0.1","ADMIN",true,true,0});
            check(!r.allowed && r.reason==DenialReason::UnknownOrConflictedMapping,"XYU ambiguous write rejected");}
        {auto r=WritePolicy::assess(&reg,{"SIG_FINAL_QUALITY","127.0.0.1","ADMIN",true,true,0});
            check(!r.allowed && r.reason==DenialReason::UnknownOrConflictedMapping,"D1016 physical NG routing UNKNOWN");}
        {auto r=WritePolicy::assess(&reg,{"SIG_DOWNSTREAM_REQUEST","127.0.0.1","ADMIN",true,true,0});
            check(!r.allowed && r.reason==DenialReason::UnknownOrConflictedMapping,"D2099 physical downstream UNKNOWN");}
        {auto r=WritePolicy::assess(&reg,{"SIG_CAM_POS","127.0.0.1","ADMIN",true,false,0});
            check(!r.allowed && r.reason==DenialReason::MissingOrExpiredApproval,"no approval rejected");}
        {auto r=WritePolicy::assess(&reg,{"SIG_CAM_POS","127.0.0.1","ADMIN",true,true,300001});
            check(!r.allowed && r.reason==DenialReason::MissingOrExpiredApproval,"expired approval rejected");}
        {auto r=WritePolicy::assess(&reg,{"SIG_CAM_POS","127.0.0.1","ADMIN",true,true,300000});
            check(!r.allowed,"even boundary-age approval grants no write");}
        {auto r=WritePolicy::assess(&reg,{"SIG_BARCODE_WORDS","::1","ADMIN",true,true,0});
            check(!r.allowed && r.reason==DenialReason::UnknownOrConflictedMapping,"barcode 10-vs-20 word not resolved");}
        check(reg.find_signal("SIG_CAM_POS")->at(SignalField::PLCAddress)=="D1006",
              "no mutation of registry fact after all write attempts");
        std::cout<<"SUMMARY pass="<<passed<<" fail="<<failed<<" MODE=SIM_ONLY W006_STATIC_ONLY\n";
        return failed==0 ? 0 : 1;
    }catch(const std::exception& e){std::cerr<<"HARNESS_EXCEPTION: "<<e.what()<<'\n';return 4;}
}
