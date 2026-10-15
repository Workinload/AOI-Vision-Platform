#pragma once
// Issue #16 / W028+W029 BRANCH_DEV ONLY. Deterministic static test fixture.
// No camera SDK, capture callback, production FrameRef or shared C01 DTO.
#if !defined(AOI_SIM_ONLY) || AOI_SIM_ONLY != 1
#error "Capture preflight fixture must be compiled only with AOI_SIM_ONLY=1"
#endif
#include <array>
#include <cstddef>
#include <string_view>

namespace aoi::device::preflight {

enum class Kind { Area, Line };
enum class Purpose { Positioning, Inspection };
struct Slot {
    std::string_view channel;
    std::string_view host;
    std::string_view device;
    std::string_view handshake; // static reference; not an approved PLC address
    std::string_view parent;    // simulated shared capture-group identity
    Kind kind;
    Purpose purpose;
};

// Mirrors immutable station_channel_map.csv, including unresolved D1013.
inline constexpr std::array<Slot, 11> kDraftSlots{{
    {"CH01", "MASTER", "HIK-M-1", "D1006", "area-M1", Kind::Area, Purpose::Positioning},
    {"CH02", "SLAVE", "HIK-S-2", "D1008", "area-S2", Kind::Area, Purpose::Inspection},
    {"CH03", "SLAVE", "HIK-S-1", "D1007", "area-S1", Kind::Area, Purpose::Inspection},
    {"CH04", "SLAVE", "HIK-S-4", "D1010", "area-S4", Kind::Area, Purpose::Inspection},
    {"CH05", "SLAVE", "HIK-S-3", "D1009", "area-S3", Kind::Area, Purpose::Inspection},
    {"CH06", "MASTER", "HIK-M-2", "D1011", "area-M2", Kind::Area, Purpose::Inspection},
    {"CH07", "MASTER", "HIK-M-3", "D1012", "area-M3", Kind::Area, Purpose::Inspection},
    {"CH08", "SLAVE", "LINE-S-8", "D1013", "rear-line", Kind::Line, Purpose::Inspection},
    {"CH09", "SLAVE", "LINE-S-8", "D1013", "rear-line", Kind::Line, Purpose::Inspection},
    {"CH10", "MASTER", "LINE-M-2", "D1014", "front-line", Kind::Line, Purpose::Inspection},
    {"CH11", "MASTER", "LINE-M-2", "D1014", "front-line", Kind::Line, Purpose::Inspection},
}};

// Historical 8-bit frame size is only a mathematical fixture; NOT a 123MB
// allocation, DMA, or physical throughput test.
inline constexpr std::size_t kRawWidth = 8200U;
inline constexpr std::size_t kRawHeight = 15000U;
inline constexpr std::size_t kRawBytes = kRawWidth * kRawHeight;
inline constexpr std::size_t kOddRows = kRawHeight / 2U; // img[1::2,:]
inline constexpr std::size_t kEvenRows = (kRawHeight - 1U) / 2U; // img[2::2,:]

// CamDone means acquisition handshake completion, never quality success.
[[nodiscard]] constexpr bool camera_done_means_quality_ok() noexcept { return false; }
} // namespace aoi::device::preflight
