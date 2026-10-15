#pragma once
// C01 review-only A0 domain primitives. NOT a frozen wire/ABI contract.
// Zero device/network I/O; no production PLC write entry point.
#include <cstdint>
#include <limits>
#include <string>
#include <string_view>
#include <utility>
#include <type_traits>

namespace aoi::domain::c01_review {

template<class Tag> class Id final {
public:
    explicit Id(std::string value) : value_(std::move(value)) {}
    [[nodiscard]] bool valid() const noexcept {
        if (value_.empty() || value_.size() > 160) return false;
        for (unsigned char c : value_) {
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                  (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ':' || c == '-')) return false;
        }
        return (value_[0] >= 'A' && value_[0] <= 'Z') ||
               (value_[0] >= 'a' && value_[0] <= 'z') ||
               (value_[0] >= '0' && value_[0] <= '9');
    }
    [[nodiscard]] const std::string& value() const noexcept { return value_; }
    friend bool operator==(const Id&, const Id&) = default;
private:
    std::string value_;
};
struct WorkpieceTag {}; struct ReleaseTag {}; struct CaptureTag {};
struct ParentCaptureTag {}; struct CaptureGroupTag {}; struct ChannelTag {};
struct PhysicalDeviceTag {}; struct MessageTag {}; struct EventTag {};
struct TraceTag {}; struct HostTag {};
using WorkpieceId = Id<WorkpieceTag>;
using ReleaseId = Id<ReleaseTag>;
using CaptureId = Id<CaptureTag>;
using ParentCaptureId = Id<ParentCaptureTag>;
using CaptureGroupId = Id<CaptureGroupTag>;
using ChannelId = Id<ChannelTag>;
using PhysicalDeviceId = Id<PhysicalDeviceTag>;
using MessageId = Id<MessageTag>;
using EventId = Id<EventTag>;
using TraceId = Id<TraceTag>;
using HostId = Id<HostTag>;

struct Epoch final {
    std::uint64_t value{};
    [[nodiscard]] bool valid() const noexcept { return value != 0; }
};
struct AttemptNo final {
    std::uint32_t value{};
    [[nodiscard]] bool valid() const noexcept { return value != 0; }
};
struct SequenceNo final {
    std::uint64_t value{};
    [[nodiscard]] bool valid() const noexcept { return value != 0; }
};

enum class ProcessingStatus { Pending, Running, Succeeded, Error, Cancelled, Timeout };
enum class QualityStatus { Undecided, Ok, Ng, Missing, SkippedByVerifiedCondition };
enum class EventKind { WorkpieceResult, AlarmTransition, Deployment, Asset, UiSnapshot, AuthAudit };
enum class SafetyDecision { Accept, RejectIdentity, RejectStatePair, RejectForeignHost,
                            RejectStaleEpoch, RejectConflictingReplay, RejectUnknownVersion };

[[nodiscard]] inline bool valid_channel(const ChannelId& id) noexcept {
    const auto& s = id.value();
    return s.size() == 4 && s[0] == 'C' && s[1] == 'H' &&
           ((s[2] == '0' && s[3] >= '1' && s[3] <= '9') ||
            (s[2] == '1' && s[3] >= '0' && s[3] <= '1'));
}
[[nodiscard]] inline bool quality_pair_allowed(ProcessingStatus processing, QualityStatus quality) noexcept {
    if (quality == QualityStatus::Ok || quality == QualityStatus::Ng)
        return processing == ProcessingStatus::Succeeded;
    if (processing != ProcessingStatus::Succeeded)
        return quality != QualityStatus::Ok && quality != QualityStatus::Ng;
    return quality != QualityStatus::Undecided;
}
struct WorkpieceResultIdentity final {
    WorkpieceId workpiece;
    Epoch epoch;
    ReleaseId release;
    ChannelId channel;
    CaptureId capture;
    AttemptNo attempt;
    PhysicalDeviceId physical_device;
    CaptureGroupId capture_group;
    [[nodiscard]] bool valid() const noexcept {
        return workpiece.valid() && epoch.valid() && release.valid() &&
               valid_channel(channel) && capture.valid() && attempt.valid() &&
               physical_device.valid() && capture_group.valid();
    }
};
struct ParentFrameIdentity final {
    HostId host;
    CaptureGroupId capture_group;
    PhysicalDeviceId physical_device;
    AttemptNo attempt;
    [[nodiscard]] bool valid() const noexcept {
        return (host.value() == "MASTER" || host.value() == "SLAVE") &&
               capture_group.valid() && physical_device.valid() && attempt.valid();
    }
};
struct MessageIdentity final {
    MessageId message;
    EventId event;
    TraceId trace;
    HostId host;
    Epoch epoch;
    SequenceNo sequence;
    std::string schema_version;
    std::string payload_sha256; // Encoding-only validation; hash of actual bytes must be checked by caller.
    [[nodiscard]] bool shape_valid() const noexcept {
        if (!message.valid() || !event.valid() || !trace.valid() || !epoch.valid() || !sequence.valid())
            return false;
        if (host.value() != "MASTER" && host.value() != "SLAVE") return false;
        if (schema_version != "C01-REVIEW-0.2" || payload_sha256.size() != 64) return false;
        for (char c : payload_sha256)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
};
[[nodiscard]] inline SafetyDecision validate_result(
    const WorkpieceResultIdentity& identity, ProcessingStatus processing, QualityStatus quality) noexcept {
    if (!identity.valid()) return SafetyDecision::RejectIdentity;
    if (!quality_pair_allowed(processing, quality)) return SafetyDecision::RejectStatePair;
    return SafetyDecision::Accept;
}
// These DTOs do not implement WAL persistence, replay deduplication, device readback or auth.
} // namespace aoi::domain::c01_review
