#include "../../../libs/auth/sim_only_policy.hpp"
#include <iostream>
#include <string>
using namespace aoi::auth::w008;
struct Audit final:AuditSink { bool available=true;int records=0;bool append(const AuditEvent&) noexcept override {if(!available)return false;++records;return true;} };
struct Memory final:SimSink { int writes=0;void writeMock() noexcept override {++writes;} };
Request good(Operation op=Operation::WriteSim){Request r;r.operation=op;r.endpointKind=EndpointKind::VirtualPlc;r.endpointId="W008_MEMORY_VPLC";r.ipLiteral="127.0.0.1";r.port=29731;r.trustedSession=Session{true,false,Role::Engineer,200,true,true,true};r.coreConnected=true;r.deviceConnected=true;r.epoch=7;r.readbackAgeMs=1;r.messageId="m1";r.payloadHash=std::string(64,'a');r.nonce="n1";r.traceId="t1";return r;}
int main(){int checks=0,fail=0;auto run=[&](const char* label,auto alter,Reason want){Audit a;Memory io;Policy p(a,io);Request r=good();alter(r,a);Reason got=p.apply(r);++checks;if(got!=want || io.writes!=(want==Reason::Allowed ? 1:0)){++fail;std::cout<<"FAIL "<<label<<" got="<<name(got)<<'\n';}else std::cout<<"PASS "<<label<<'\n';};
run("sim-positive",[](auto&,auto&){},Reason::Allowed);
run("unknown-plc",[](auto&r,auto&){r.endpointId="D1013";},Reason::EndpointRejected);
run("dns-deny",[](auto&r,auto&){r.ipLiteral="localhost";},Reason::EndpointRejected);
run("ipv6-deny",[](auto&r,auto&){r.ipLiteral="::1";},Reason::EndpointRejected);
run("public-ip-deny",[](auto&r,auto&){r.ipLiteral="192.168.1.2";},Reason::EndpointRejected);
run("wrong-port",[](auto&r,auto&){r.port=9600;},Reason::EndpointRejected);
run("physical-plc",[](auto&r,auto&){r.endpointKind=EndpointKind::PhysicalPlc;},Reason::ProductionIoBlocked);
run("physical-camera",[](auto&r,auto&){r.endpointKind=EndpointKind::Camera;},Reason::ProductionIoBlocked);
run("physical-opcode",[](auto&r,auto&){r.operation=Operation::WritePhysical;r.trustedSession->role=Role::Admin;},Reason::ProductionIoBlocked);
run("admin-site-bypass",[](auto&r,auto&){r.sitePermitClaimed=true;r.trustedSession->role=Role::Admin;},Reason::OverrideBlocked);
run("maintenance-bypass",[](auto&r,auto&){r.maintenanceBypass=true;},Reason::OverrideBlocked);
run("anonymous",[](auto&r,auto&){r.trustedSession.reset();},Reason::Anonymous);
run("revoked",[](auto&r,auto&){r.trustedSession->revoked=true;},Reason::Revoked);
run("expired",[](auto&r,auto&){r.trustedSession->expiresAt=100;r.monotonicNow=0;},Reason::Expired);
run("viewer-forged-role",[](auto&r,auto&){r.trustedSession->role=Role::Viewer;r.claimedRole="ADMIN";},Reason::Unauthorized);
run("admin-missing-capability",[](auto&r,auto&){r.trustedSession->role=Role::Admin;r.trustedSession->simWriteCapability=false;},Reason::Unauthorized);
run("core-disconnected",[](auto&r,auto&){r.coreConnected=false;},Reason::Offline);
run("device-disconnected",[](auto&r,auto&){r.deviceConnected=false;},Reason::Offline);
run("stale-epoch",[](auto&r,auto&){r.epoch=6;},Reason::WrongEpoch);
run("bad-hash",[](auto&r,auto&){r.payloadHash="invalid";},Reason::BadMessage);
run("stale-readback",[](auto&r,auto&){r.readbackAgeMs=2001;},Reason::StaleReadback);
run("audit-down",[](auto&,auto&a){a.available=false;},Reason::AuditUnavailable);
{Audit a;Memory io;Policy p(a,io);auto r=good();bool ok=p.apply(r)==Reason::Allowed && p.apply(r)==Reason::Replay && io.writes==1;++checks;if(!ok){++fail;std::cout<<"FAIL replay\n";}else std::cout<<"PASS replay\n";}
{Audit a;Memory io;Policy p(a,io);Alarm alarm;auto r=good(Operation::AlarmAck);bool ok=p.apply(r,&alarm)==Reason::Allowed && alarm.active && !alarm.cleared;r.messageId="m2";r.nonce="n2";r.operation=Operation::AlarmClear;ok=ok && p.apply(r,&alarm)==Reason::AlarmGuardBlocked && !alarm.cleared;++checks;if(!ok){++fail;std::cout<<"FAIL ack-clear\n";}else std::cout<<"PASS ack-clear\n";}
std::cout<<"W008_SMOKE total="<<checks<<" failed="<<fail<<" MEMORY_ONLY\n";return fail?1:0;}
