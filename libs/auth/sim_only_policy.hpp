#pragma once
// W008 / Issue #37 -- isolated SIM_ONLY security policy PROTOTYPE.
// No socket, PLC driver, camera SDK, production write, or C01 wire ABI.
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>

namespace aoi::auth::w008 {

enum class Operation { Unknown, ReadSim, WriteSim, WritePhysical, AlarmAck, AlarmClear };
enum class EndpointKind { Unknown, VirtualPlc, PhysicalPlc, Camera };
enum class Role { Unknown, Viewer, Operator, Engineer, Admin };
enum class Reason {
    Allowed, ProductionIoBlocked, OverrideBlocked, UnknownOperation,
    Offline, Anonymous, Revoked, Expired, InvalidRole, WrongEpoch,
    Unauthorized, EndpointRejected, BadMessage, Replay, ReplayConflict,
    StaleReadback, AlarmGuardBlocked, AuditUnavailable
};
inline std::string_view name(Reason r) noexcept {
    switch (r) {
        case Reason::Allowed: return "ALLOWED";
        case Reason::ProductionIoBlocked: return "PRODUCTION_IO_BLOCKED";
        case Reason::OverrideBlocked: return "OVERRIDE_BLOCKED";
        case Reason::UnknownOperation: return "UNKNOWN_OPERATION";
        case Reason::Offline: return "OFFLINE";
        case Reason::Anonymous: return "ANONYMOUS";
        case Reason::Revoked: return "REVOKED";
        case Reason::Expired: return "EXPIRED";
        case Reason::InvalidRole: return "INVALID_ROLE";
        case Reason::WrongEpoch: return "WRONG_EPOCH";
        case Reason::Unauthorized: return "UNAUTHORIZED";
        case Reason::EndpointRejected: return "ENDPOINT_REJECTED";
        case Reason::BadMessage: return "BAD_MESSAGE";
        case Reason::Replay: return "REPLAY";
        case Reason::ReplayConflict: return "REPLAY_CONFLICT";
        case Reason::StaleReadback: return "STALE_READBACK";
        case Reason::AlarmGuardBlocked: return "ALARM_GUARD_BLOCKED";
        case Reason::AuditUnavailable: return "AUDIT_UNAVAILABLE";
    }
    return "UNKNOWN_REASON";
}
struct Session { // Trusted session is INJECTED by test; never deserialize role from Qt IPC.
    bool authenticated{false};
    bool revoked{false};
    Role role{Role::Unknown};
    std::uint64_t expiresAt{0};
    bool simWriteCapability{false};
    bool ackCapability{false};
    bool clearCapability{false};
};
struct Request { // Defaults reject. All endpoints are opaque mock descriptors.
    Operation operation{Operation::Unknown};
    EndpointKind endpointKind{EndpointKind::Unknown};
    std::string endpointId{};
    std::string ipLiteral{};
    std::uint16_t port{0};
    std::optional<Session> trustedSession{};
    std::string claimedRole{}; // Untrusted UI claim is deliberately ignored.
    std::uint64_t monotonicNow{0};
    std::uint64_t epoch{0};
    std::uint64_t readbackAgeMs{UINT64_MAX};
    bool coreConnected{false};
    bool deviceConnected{false};
    bool maintenanceBypass{false};
    bool sitePermitClaimed{false}; // Unsigned 14 FG cannot grant authorization.
    std::string messageId{}, payloadHash{}, nonce{}, traceId{};
};
struct Alarm { // Simulated authoritative source state, NEVER supplied by QML.
    bool active{true};
    bool sourceRestored{false};
    bool acknowledged{false};
    bool cleared{false};
};
struct AuditEvent {
    Operation operation{};
    Reason reason{};
    bool allowed{};
    std::string actor{}, target{}, trace{};
};
struct AuditSink {
    virtual ~AuditSink() = default;
    virtual bool append(const AuditEvent&) noexcept = 0;
};
struct SimSink {
    virtual ~SimSink() = default;
    virtual void writeMock() noexcept = 0;
};

class Policy final {
public:
    // Fixed, TEST-ONLY endpoint. No caller-provided networking permitted.
    static constexpr std::string_view kSimId{"W008_MEMORY_VPLC"};
    static constexpr std::string_view kSimIpv4{"127.0.0.1"};
    static constexpr std::uint16_t kSimPort{29731};
    explicit Policy(AuditSink& audit, SimSink& simulator, std::uint64_t epoch = 7,
                    std::uint64_t trustedNow = 100)
        : audit_(audit), simulator_(simulator), epoch_(epoch), trustedNow_(trustedNow) {}

    Reason apply(const Request& r, Alarm* alarm = nullptr) {
        const auto reason = check(r, alarm);
        AuditEvent event{r.operation, reason, reason == Reason::Allowed,
                         r.trustedSession ? "MOCK_AUTHENTICATED_ACTOR" : "ANONYMOUS",
                         "REDACTED_TARGET", validToken(r.traceId) ? r.traceId : "INVALID_TRACE"};
        // Write-ahead audit is a test sink, NOT a durable W019 implementation.
        if (!audit_.append(event)) return Reason::AuditUnavailable;
        if (reason != Reason::Allowed) return reason;
        if (isMutation(r.operation)) {
            seenMessages_.emplace(r.messageId, r.payloadHash);
            seenNonces_.insert(r.nonce);
        }
        if (r.operation == Operation::WriteSim) simulator_.writeMock();
        if (r.operation == Operation::AlarmAck) alarm->acknowledged = true;
        if (r.operation == Operation::AlarmClear) alarm->cleared = true;
        return Reason::Allowed;
    }
private:
    static bool isMutation(Operation action) noexcept {
        return action == Operation::WriteSim || action == Operation::AlarmAck ||
               action == Operation::AlarmClear;
    }
    static bool validToken(const std::string& value) noexcept {
        if (value.empty() || value.size() > 96) return false;
        for (unsigned char c : value)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                  (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')) return false;
        return true;
    }
    static bool trustedSimEndpoint(const Request& r) noexcept {
        return r.endpointKind == EndpointKind::VirtualPlc && r.endpointId == kSimId &&
               r.ipLiteral == kSimIpv4 && r.port == kSimPort;
    }
    static bool validDigest(const std::string& value) noexcept {
        if (value.size() != 64) return false;
        for (char c : value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
    Reason check(const Request& r, const Alarm* alarm) const {
        // Production I/O has NO allow path, even if admin/sitePermit is forged.
        if (r.operation == Operation::WritePhysical ||
            r.endpointKind == EndpointKind::PhysicalPlc ||
            r.endpointKind == EndpointKind::Camera) return Reason::ProductionIoBlocked;
        if (r.maintenanceBypass || r.sitePermitClaimed) return Reason::OverrideBlocked;
        if (r.operation == Operation::Unknown) return Reason::UnknownOperation;
        if (!r.coreConnected || !r.deviceConnected) return Reason::Offline;
        if (!r.trustedSession || !r.trustedSession->authenticated) return Reason::Anonymous;
        const Session& s = *r.trustedSession;
        if (s.revoked) return Reason::Revoked;
        // Caller-supplied clock is ignored: expiration uses the trusted gate clock.
        if (trustedNow_ >= s.expiresAt) return Reason::Expired;
        if (s.role == Role::Unknown) return Reason::InvalidRole;
        if (r.epoch != epoch_) return Reason::WrongEpoch;
        if (r.operation == Operation::ReadSim)
            return trustedSimEndpoint(r) ? Reason::Allowed : Reason::EndpointRejected;
        if (!isMutation(r.operation)) return Reason::UnknownOperation;
        if (!validToken(r.messageId) || !validToken(r.nonce) ||
            !validToken(r.traceId) || !validDigest(r.payloadHash)) return Reason::BadMessage;
        if (const auto it = seenMessages_.find(r.messageId); it != seenMessages_.end())
            return it->second == r.payloadHash ? Reason::Replay : Reason::ReplayConflict;
        if (seenNonces_.contains(r.nonce)) return Reason::Replay;
        if (r.operation == Operation::WriteSim) {
            if ((s.role != Role::Engineer && s.role != Role::Admin) || !s.simWriteCapability)
                return Reason::Unauthorized;
            if (!trustedSimEndpoint(r)) return Reason::EndpointRejected;
            if (r.readbackAgeMs > 2000) return Reason::StaleReadback;
            return Reason::Allowed;
        }
        if (!alarm) return Reason::AlarmGuardBlocked;
        if (r.operation == Operation::AlarmAck) {
            if (s.role == Role::Viewer || !s.ackCapability) return Reason::Unauthorized;
            return alarm->cleared ? Reason::AlarmGuardBlocked : Reason::Allowed;
        }
        if (r.operation == Operation::AlarmClear) {
            if ((s.role != Role::Engineer && s.role != Role::Admin) || !s.clearCapability)
                return Reason::Unauthorized;
            if (alarm->active || !alarm->sourceRestored || !alarm->acknowledged ||
                alarm->cleared || r.readbackAgeMs > 2000) return Reason::AlarmGuardBlocked;
            return Reason::Allowed;
        }
        return Reason::UnknownOperation;
    }
    AuditSink& audit_;
    SimSink& simulator_;
    const std::uint64_t epoch_;
    const std::uint64_t trustedNow_;
    std::unordered_map<std::string, std::string> seenMessages_;
    std::unordered_set<std::string> seenNonces_;
};
} // namespace aoi::auth::w008