#pragma once
// M00 Contract C00-DRAFT. A0 must freeze after W003/W004 and A10 review.
// No physical hardware writer in this library.
#include <cstdint>
#include <string>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include <utility>

namespace aoi::domain {
template<class Tag> struct StrongId {
    std::string value;
    explicit StrongId(std::string v = {}) : value(std::move(v)) {}
    [[nodiscard]] bool valid() const noexcept { return !value.empty(); }
    friend bool operator==(const StrongId&, const StrongId&) = default;
};
struct WorkpieceTag{}; struct ReleaseTag{}; struct CaptureTag{}; struct ChannelTag{};
using WorkpieceId = StrongId<WorkpieceTag>;
using ReleaseId = StrongId<ReleaseTag>;
using CaptureId = StrongId<CaptureTag>;
using ChannelId = StrongId<ChannelTag>;

enum class ProcessingStatus { Pending, Running, Succeeded, Error, Cancelled, Timeout };
enum class QualityStatus { Undecided, Ok, Ng, Missing, SkippedByVerifiedCondition };
enum class BarcodeStatus { Matched, Mismatched, Missing, Unverified };
enum class FinalOutcome { Ok, Ng, HoldUndecided, BarcodeMismatch };

struct Correlation {
    WorkpieceId workpiece;
    std::uint64_t epoch{0};
    ReleaseId release;
    ChannelId channel;
    CaptureId capture;
    std::uint32_t attempt_no{0};
};
struct Result {
    Correlation key;
    ProcessingStatus processing{ProcessingStatus::Pending};
    QualityStatus quality{QualityStatus::Undecided};
    bool superseded{false};
};
struct DecisionInput {
    WorkpieceId workpiece;
    std::uint64_t epoch{0};
    ReleaseId release;
    BarcodeStatus barcode{BarcodeStatus::Unverified};
    std::vector<ChannelId> required_channels;
    std::vector<Result> results;
};
struct Decision {
    FinalOutcome outcome{FinalOutcome::HoldUndecided};
    std::string reason{"incomplete_evidence"};
    std::vector<std::string> missing_channels;
};

// Very small, pure safety invariant for early tests. NOT the full W040 final quality engine.
[[nodiscard]] inline Decision assess(const DecisionInput& input) {
    Decision d;
    if (!input.workpiece.valid() || input.epoch == 0 || !input.release.valid()) {
        d.reason = "invalid_identity"; return d;
    }
    if (input.barcode == BarcodeStatus::Mismatched) {
        d.outcome = FinalOutcome::BarcodeMismatch; d.reason = "barcode_mismatch"; return d;
    }
    if (input.barcode != BarcodeStatus::Matched) {
        d.reason = "barcode_not_verified"; return d;
    }
    std::unordered_set<std::string> required;
    for (const auto& ch : input.required_channels) {
        if (!ch.valid() || !required.insert(ch.value).second) {
            d.reason = "invalid_required_channels"; return d;
        }
    }
    if (required.empty()) { d.reason = "invalid_required_channels"; return d; }
    std::unordered_set<std::string> seen;
    bool ng = false;
    for (const auto& r : input.results) {
        if (r.superseded || !required.contains(r.key.channel.value)) continue;
        if (r.key.workpiece != input.workpiece || r.key.epoch != input.epoch ||
            r.key.release != input.release || r.key.attempt_no == 0 || !r.key.capture.valid()) {
            d.reason = "identity_release_or_capture_mismatch"; return d;
        }
        if (!seen.insert(r.key.channel.value).second) {
            d.reason = "duplicate_active_channel"; return d;
        }
        if (r.processing != ProcessingStatus::Succeeded ||
            (r.quality != QualityStatus::Ok && r.quality != QualityStatus::Ng)) {
            d.reason = "missing_or_failed_processing"; return d;
        }
        ng |= r.quality == QualityStatus::Ng;
    }
    for (const auto& ch : input.required_channels) {
        if (!seen.contains(ch.value)) d.missing_channels.push_back(ch.value);
    }
    if (!d.missing_channels.empty()) { d.reason = "required_channel_missing"; return d; }
    d.outcome = ng ? FinalOutcome::Ng : FinalOutcome::Ok;
    d.reason = ng ? "valid_ng" : "valid_ok";
    return d;
}
} // namespace aoi::domain
