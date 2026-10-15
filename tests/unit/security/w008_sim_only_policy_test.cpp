// W008: executable adversarial tests; NO network libraries or real IO.
#include "../../../libs/auth/sim_only_policy.hpp"
#include <algorithm>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

using namespace aoi::auth::w008;
namespace {
void require(bool ok, const std::string& info) { if (!ok) throw std::runtime_error(info); }
void expect(Reason got, Reason want) {
    require(got == want, "expected=" + std::string(name(want)) + " got=" + std::string(name(got)));
}
struct MemIo final : SimSink { int writes{0}; void writeMock() noexcept override { ++writes; } };
// Non-cryptographic chain is ONLY a corruption/fault-injection fixture, not W019 audit.
struct MockAudit final : AuditSink {
    bool writable{true};
    std::vector<AuditEvent> entries;
    std::vector<std::uint64_t> chain;
    static std::uint64_t digest(const AuditEvent& e, std::uint64_t previous) {
        std::uint64_t v = previous;
        const std::string s = std::to_string(static_cast<int>(e.operation)) + ":" +
            std::to_string(static_cast<int>(e.reason)) + ":" + (e.allowed ? "A" : "D") +
            ":" + e.actor + ":" + e.target + ":" + e.trace;
        for (unsigned char c : s) { v ^= c; v *= 1099511628211ULL; }
        return v;
    }
    bool verify() const noexcept {
        std::uint64_t last = 14695981039346656037ULL;
        if (entries.size() != chain.size()) return false;
        for (std::size_t i=0; i<entries.size(); ++i) {
            last = digest(entries[i], last);
            if (last != chain[i]) return false;
        }
        return true;
    }
    bool append(const AuditEvent& event) noexcept override {
        if (!writable || !verify()) return false;
        const std::uint64_t prev = chain.empty() ? 14695981039346656037ULL : chain.back();
        entries.push_back(event);
        chain.push_back(digest(event, prev));
        return true;
    }
};
struct Fixture {
    MockAudit audit;
    MemIo io;
    Policy gate{audit, io, 7};
};
Request fresh(Operation op = Operation::WriteSim) {
    Request r;
    r.operation = op;
    r.endpointKind = EndpointKind::VirtualPlc;
    r.endpointId = std::string(Policy::kSimId);
    r.ipLiteral = std::string(Policy::kSimIpv4);
    r.port = Policy::kSimPort;
    r.trustedSession = Session{true, false, Role::Engineer, 200, true, true, true};
    r.claimedRole = "ENGINEER";
    r.monotonicNow = 100;
    r.epoch = 7;
    r.readbackAgeMs = 10;
    r.coreConnected = true;
    r.deviceConnected = true;
    r.messageId = "message-1";
    r.payloadHash = std::string(64, 'a');
    r.nonce = "nonce-1";
    r.traceId = "trace-1";
    return r;
}
void next(Request& r) {
    r.messageId += "-next";
    r.nonce += "-next";
}
void denied(Request r, Reason why) {
    Fixture f;
    expect(f.gate.apply(r), why);
    require(f.io.writes == 0, "denied command reached simulator");
    require(f.audit.entries.size() == 1, "denial not attempted in mock audit");
    require(!f.audit.entries.front().allowed, "denial logged as allowed");
    require(f.audit.verify(), "mock audit unexpectedly corrupt");
}
using Fn = std::function<void()>;
struct Case { std::string label; Fn fn; };
}
int main() {
    std::vector<Case> tests;
    auto add = [&](std::string label, Fn f) { tests.push_back({std::move(label), std::move(f)}); };

    add("default_request_refused", [] { denied(Request{}, Reason::UnknownOperation); });
    add("virtual_write_positive_memory_only", [] {
        Fixture f; expect(f.gate.apply(fresh()), Reason::Allowed);
        require(f.io.writes == 1, "mock-only sink must receive one write");
        require(f.audit.entries.size() == 1 && f.audit.entries[0].allowed, "allow not audited");
    });
    add("unrecognized_plc_id", [] { auto r=fresh();r.endpointId="legacy-plc";denied(r,Reason::EndpointRejected); });
    add("wrong_plc_port", [] { auto r=fresh();r.port=9600;denied(r,Reason::EndpointRejected); });
    add("unknown_plc_kind", [] { auto r=fresh();r.endpointKind=EndpointKind::Unknown;denied(r,Reason::EndpointRejected); });
    for (const std::string bad : {"localhost", "127.0.0.2", "127.1", "2130706433",
             "::1", "::ffff:127.0.0.1", "0.0.0.0", "192.168.1.20", "PLC-DNS.invalid", "127.0.0.1@evil"}) {
        add("endpoint_reject_"+bad, [bad] {auto r=fresh();r.ipLiteral=bad;denied(r,Reason::EndpointRejected);});
    }
    for (const std::string ambiguous : {"D1000", "D1013", "D1030", "D2000", "D2099", "D1016"}) {
        add("legacy_alias_rejected_"+ambiguous, [ambiguous] {
            auto r=fresh();r.endpointId=ambiguous;denied(r,Reason::EndpointRejected);
        });
    }
    add("deterministic_endpoint_mutation_fuzz", [] {
        Fixture f;
        auto r=fresh();
        for (int i=0;i<500;++i) {
            r.endpointId="W008_MEMORY_VPLC-"+std::to_string(i);
            r.messageId="message-"+std::to_string(i);
            r.nonce="nonce-"+std::to_string(i);
            expect(f.gate.apply(r),Reason::EndpointRejected);
        }
        require(f.io.writes==0,"mutated endpoint escaped allowlist");
        require(f.audit.entries.size()==500 && f.audit.verify(),"audit chain for mutations incomplete");
    });
    add("physical_endpoint_even_loopback", [] { auto r=fresh();r.endpointKind=EndpointKind::PhysicalPlc;denied(r,Reason::ProductionIoBlocked); });
    add("physical_camera_endpoint", [] { auto r=fresh();r.endpointKind=EndpointKind::Camera;denied(r,Reason::ProductionIoBlocked); });
    add("physical_write_opcode", [] {auto r=fresh(Operation::WritePhysical);denied(r,Reason::ProductionIoBlocked);});
    add("admin_cannot_write_physical", [] {
        auto r=fresh(Operation::WritePhysical);r.trustedSession->role=Role::Admin;
        r.trustedSession->simWriteCapability=true;denied(r,Reason::ProductionIoBlocked);
    });
    add("admin_cannot_claim_site_signoff", [] {auto r=fresh();r.trustedSession->role=Role::Admin;
        r.sitePermitClaimed=true;denied(r,Reason::OverrideBlocked);});
    add("maintenance_override_denied", [] {auto r=fresh();r.maintenanceBypass=true;denied(r,Reason::OverrideBlocked);});
    add("anonymous", [] {auto r=fresh();r.trustedSession.reset();denied(r,Reason::Anonymous);});
    add("unauthenticated_session", [] {auto r=fresh();r.trustedSession->authenticated=false;denied(r,Reason::Anonymous);});
    add("revoked", [] {auto r=fresh();r.trustedSession->revoked=true;denied(r,Reason::Revoked);});
    add("expired", [] {auto r=fresh();r.trustedSession->expiresAt=99;denied(r,Reason::Expired);});
    add("exact_expiry_boundary", [] {auto r=fresh();r.trustedSession->expiresAt=100;denied(r,Reason::Expired);});
    add("unknown_role", [] {auto r=fresh();r.trustedSession->role=Role::Unknown;denied(r,Reason::InvalidRole);});
    add("viewer_forged_admin_role", [] {auto r=fresh();r.trustedSession->role=Role::Viewer;
        r.claimedRole="ADMIN";denied(r,Reason::Unauthorized);});
    add("operator_cannot_write_sim", [] {auto r=fresh();r.trustedSession->role=Role::Operator;denied(r,Reason::Unauthorized);});
    add("admin_needs_explicit_sim_capability", [] {auto r=fresh();r.trustedSession->role=Role::Admin;
        r.trustedSession->simWriteCapability=false;denied(r,Reason::Unauthorized);});
    add("admin_sim_write_allowed_only_mock", [] {Fixture f;auto r=fresh();r.trustedSession->role=Role::Admin;
        expect(f.gate.apply(r),Reason::Allowed);require(f.io.writes==1,"no mock dispatch");});
    add("core_disconnected", [] {auto r=fresh();r.coreConnected=false;denied(r,Reason::Offline);});
    add("device_disconnected", [] {auto r=fresh();r.deviceConnected=false;denied(r,Reason::Offline);});
    add("stale_epoch", [] {auto r=fresh();r.epoch=6;denied(r,Reason::WrongEpoch);});
    add("future_epoch", [] {auto r=fresh();r.epoch=8;denied(r,Reason::WrongEpoch);});
    add("unknown_command", [] {auto r=fresh(Operation::Unknown);denied(r,Reason::UnknownOperation);});
    add("missing_nonce", [] {auto r=fresh();r.nonce.clear();denied(r,Reason::BadMessage);});
    add("newline_injected_trace", [] {auto r=fresh();r.traceId="trace\nALLOW";denied(r,Reason::BadMessage);});
    add("nonhex_payload_digest", [] {auto r=fresh();r.payloadHash=std::string(64,'z');denied(r,Reason::BadMessage);});
    add("short_payload_digest", [] {auto r=fresh();r.payloadHash="a1";denied(r,Reason::BadMessage);});
    add("expired_readback", [] {auto r=fresh();r.readbackAgeMs=2001;denied(r,Reason::StaleReadback);});
    add("exact_freshness_boundary", [] {Fixture f;auto r=fresh();r.readbackAgeMs=2000;
        expect(f.gate.apply(r),Reason::Allowed);require(f.io.writes==1,"fresh readback rejected");});
    add("replay_same_message_same_digest", [] {Fixture f;auto r=fresh();
        expect(f.gate.apply(r),Reason::Allowed);expect(f.gate.apply(r),Reason::Replay);
        require(f.io.writes==1,"replay reached mock sink twice");});
    add("replay_same_message_modified_digest", [] {Fixture f;auto r=fresh();
        expect(f.gate.apply(r),Reason::Allowed);r.payloadHash=std::string(64,'b');
        expect(f.gate.apply(r),Reason::ReplayConflict);require(f.io.writes==1,"hash conflict replayed write");});
    add("replay_nonce_different_message", [] {Fixture f;auto r=fresh();
        expect(f.gate.apply(r),Reason::Allowed);r.messageId="message-2";
        expect(f.gate.apply(r),Reason::Replay);require(f.io.writes==1,"nonce replayed write");});
    add("different_message_and_nonce_allowed", [] {Fixture f;auto r=fresh();
        expect(f.gate.apply(r),Reason::Allowed);next(r);expect(f.gate.apply(r),Reason::Allowed);
        require(f.io.writes==2,"distinct simulated writes lost");});
    add("audit_io_failure_denies_write", [] {Fixture f;f.audit.writable=false;
        expect(f.gate.apply(fresh()),Reason::AuditUnavailable);require(f.io.writes==0,"write escaped audit failure");});
    add("audit_io_failure_denies_read", [] {Fixture f;f.audit.writable=false;
        expect(f.gate.apply(fresh(Operation::ReadSim)),Reason::AuditUnavailable);
        require(f.io.writes==0,"read failure unexpectedly mutated");});
    add("audit_corruption_blocks_next_write", [] {Fixture f;auto r=fresh();
        expect(f.gate.apply(r),Reason::Allowed);f.audit.entries[0].target="TAMPERED";
        require(!f.audit.verify(),"tamper detector failed");next(r);
        expect(f.gate.apply(r),Reason::AuditUnavailable);require(f.io.writes==1,"tampered audit allowed new write");});
    add("denial_is_audited_not_applied", [] {Fixture f;auto r=fresh();r.endpointId="UNKNOWN";
        expect(f.gate.apply(r),Reason::EndpointRejected);require(f.io.writes==0,"unsafe write");
        require(f.audit.entries.size()==1 && !f.audit.entries[0].allowed,"denial audit missing");});
    add("audit_failure_before_alarm_ack", [] {Fixture f;f.audit.writable=false;Alarm a;
        expect(f.gate.apply(fresh(Operation::AlarmAck),&a),Reason::AuditUnavailable);
        require(!a.acknowledged && a.active,"failed audit changed alarm");});
    add("ack_does_not_clear_active_fault", [] {Fixture f;Alarm a;
        expect(f.gate.apply(fresh(Operation::AlarmAck),&a),Reason::Allowed);
        require(a.active && a.acknowledged && !a.cleared,"ACK silently cleared fault");});
    add("viewer_cannot_ack", [] {Fixture f;Alarm a;auto r=fresh(Operation::AlarmAck);
        r.trustedSession->role=Role::Viewer;expect(f.gate.apply(r,&a),Reason::Unauthorized);
        require(!a.acknowledged,"viewer ACK changed alarm");});
    add("ack_requires_capability", [] {Fixture f;Alarm a;auto r=fresh(Operation::AlarmAck);
        r.trustedSession->ackCapability=false;expect(f.gate.apply(r,&a),Reason::Unauthorized);});
    add("ack_cannot_be_replayed", [] {Fixture f;Alarm a;auto r=fresh(Operation::AlarmAck);
        expect(f.gate.apply(r,&a),Reason::Allowed);expect(f.gate.apply(r,&a),Reason::Replay);
        require(a.active && a.acknowledged && !a.cleared,"repeated ACK changed active status");});
    add("ack_offline_refused", [] {Fixture f;Alarm a;auto r=fresh(Operation::AlarmAck);
        r.coreConnected=false;expect(f.gate.apply(r,&a),Reason::Offline);require(!a.acknowledged,"offline ACK accepted");});
    add("clear_active_fault_rejected", [] {Fixture f;Alarm a;
        auto r=fresh(Operation::AlarmClear);expect(f.gate.apply(r,&a),Reason::AlarmGuardBlocked);
        require(!a.cleared,"CLEAR during active fault");});
    add("clear_without_ack_rejected", [] {Fixture f;Alarm a;a.active=false;a.sourceRestored=true;
        expect(f.gate.apply(fresh(Operation::AlarmClear),&a),Reason::AlarmGuardBlocked);});
    add("clear_without_readback_rejected", [] {Fixture f;Alarm a;a.active=false;a.acknowledged=true;
        expect(f.gate.apply(fresh(Operation::AlarmClear),&a),Reason::AlarmGuardBlocked);});
    add("clear_stale_readback_rejected", [] {Fixture f;Alarm a;a.active=false;a.sourceRestored=true;a.acknowledged=true;
        auto r=fresh(Operation::AlarmClear);r.readbackAgeMs=2001;
        expect(f.gate.apply(r,&a),Reason::AlarmGuardBlocked);});
    add("clear_denied_without_capability", [] {Fixture f;Alarm a;a.active=false;a.sourceRestored=true;a.acknowledged=true;
        auto r=fresh(Operation::AlarmClear);r.trustedSession->clearCapability=false;
        expect(f.gate.apply(r,&a),Reason::Unauthorized);require(!a.cleared,"unauthorized CLEAR");});
    add("clear_requires_audit", [] {Fixture f;f.audit.writable=false;Alarm a;
        a.active=false;a.sourceRestored=true;a.acknowledged=true;
        expect(f.gate.apply(fresh(Operation::AlarmClear),&a),Reason::AuditUnavailable);
        require(!a.cleared,"CLEAR bypassed audit");});
    add("clear_after_ack_and_verified_restore", [] {Fixture f;Alarm a;
        auto ack=fresh(Operation::AlarmAck);expect(f.gate.apply(ack,&a),Reason::Allowed);
        a.active=false;a.sourceRestored=true;auto clear=fresh(Operation::AlarmClear);next(clear);
        expect(f.gate.apply(clear,&a),Reason::Allowed);require(a.cleared,"restored alarm not cleared");});
    add("clear_cannot_repeat_with_new_nonce", [] {Fixture f;Alarm a;
        a.active=false;a.sourceRestored=true;a.acknowledged=true;
        auto r=fresh(Operation::AlarmClear);expect(f.gate.apply(r,&a),Reason::Allowed);
        next(r);expect(f.gate.apply(r,&a),Reason::AlarmGuardBlocked);});
    add("clear_without_alarm_identity_rejected", [] {Fixture f;
        expect(f.gate.apply(fresh(Operation::AlarmClear)),Reason::AlarmGuardBlocked);});
    add("forged_role_never_logged_as_actor", [] {Fixture f;auto r=fresh();r.claimedRole="\nADMIN";
        expect(f.gate.apply(r),Reason::Allowed);require(f.audit.entries[0].actor=="MOCK_AUTHENTICATED_ACTOR",
        "untrusted role contaminated audit actor");});
    add("read_unknown_endpoint_denied", [] {auto r=fresh(Operation::ReadSim);
        r.endpointId="unknown-read-target";denied(r,Reason::EndpointRejected);});
    add("user_forged_clock_cannot_unexpire", [] {auto r=fresh();r.monotonicNow=0;
        r.trustedSession->expiresAt=99;denied(r,Reason::Expired);});
    add("full_read_requires_live_authenticated_session", [] {Fixture f;auto r=fresh(Operation::ReadSim);
        expect(f.gate.apply(r),Reason::Allowed);require(f.io.writes==0,"read reached write mock");});

    std::size_t failed = 0;
    for (const auto& c : tests) {
        try { c.fn();std::cout<<"CASE PASS "<<c.label<<'\n'; }
        catch(const std::exception& e){++failed;std::cout<<"CASE FAIL "<<c.label<<" -- "<<e.what()<<'\n';}
    }
    std::cout<<"W008_RESULT total="<<tests.size()<<" passed="<<(tests.size()-failed)
             <<" failed="<<failed<<" backend=MEMORY_ONLY external_io=NONE"<<'\n';
    return failed==0 ? 0 : 1;
}
