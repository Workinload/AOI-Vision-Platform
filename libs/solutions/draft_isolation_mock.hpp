#pragma once
// A7 isolated, in-memory research mock. NOT C01, NOT W083/W084 implementation.
// This interface is deliberately local and MUST NOT be integrated into Core.
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <tuple>
#include <utility>

namespace aoi::a7::research {
struct StationDraft {
    std::string product_id;
    std::string station_id;
    std::string machine_profile_sha256;
    std::string model_sha256;
    std::string detection_roi;
    double threshold = 0;
    std::uint64_t revision = 0;
};

enum class PatchResult { Applied, Conflict, Invalid, ForbiddenActive };

class DraftStore final {
public:
    // Active is immutable from this API. No device/PLC operations exist.
    bool seed_active(StationDraft value) {
        if (!valid(value)) return false;
        return active_.emplace(key(value), std::move(value)).second;
    }
    PatchResult patch(StationDraft value, std::uint64_t expected_revision) {
        if (!valid(value)) return PatchResult::Invalid;
        const auto k = key(value);
        const auto it = drafts_.find(k);
        const auto existing = it == drafts_.end() ? active_.find(k) : active_.end();
        const std::uint64_t current = it != drafts_.end() ? it->second.revision :
            (existing != active_.end() ? existing->second.revision : 0);
        if (current != expected_revision || current == UINT64_MAX) return PatchResult::Conflict;
        value.revision = current + 1;
        drafts_.insert_or_assign(k, std::move(value));
        return PatchResult::Applied;
    }
    std::optional<StationDraft> draft(const std::string& product, const std::string& station) const {
        const auto it = drafts_.find({product, station});
        if (it == drafts_.end()) return std::nullopt;
        return it->second;
    }
    std::optional<StationDraft> active(const std::string& product, const std::string& station) const {
        const auto it = active_.find({product, station});
        if (it == active_.end()) return std::nullopt;
        return it->second;
    }
    bool can_publish() const noexcept { return false; } // C01 and dependency gates pending.
    bool can_deploy() const noexcept { return false; } // SIM_ONLY and no vPLC readback.
private:
    using Key = std::pair<std::string, std::string>;
    static Key key(const StationDraft& v) { return {v.product_id, v.station_id}; }
    static bool valid(const StationDraft& v) {
        return !v.product_id.empty() && !v.station_id.empty() &&
               v.machine_profile_sha256.size() == 64 && v.model_sha256.size() == 64 &&
               !v.detection_roi.empty() && v.threshold >= 0 && v.threshold <= 1;
    }
    std::map<Key, StationDraft> active_;
    std::map<Key, StationDraft> drafts_;
};
} // namespace aoi::a7::research
