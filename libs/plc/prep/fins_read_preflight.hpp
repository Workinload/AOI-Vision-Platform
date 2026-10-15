#pragma once
// Issue #16 / W023+W024 BRANCH_DEV ONLY: pure memory FINS/UDP read-frame checks.
// No sockets, no registers, no PLC write API, no hardware connection.
// Not a public C01/C03 contract and NOT a production protocol implementation.
#if !defined(AOI_SIM_ONLY) || AOI_SIM_ONLY != 1
#error "FINS preflight fixture must be compiled only with AOI_SIM_ONLY=1"
#endif
#include <cstddef>
#include <cstdint>
#include <span>
#include <string_view>

namespace aoi::plc::preflight {

enum class Check {
    OkDraftRead, UnsafeEndpoint, ShortHeader, WriteDisabled,
    BadRequestLength, BadHeader, SidMismatch, BadCommand,
    BadAreaOrAddress, BadWordCount, BadResponseLength,
    BadResponseHeader, RouteMismatch, EndCodeNotOk
};

struct DraftRead {
    std::uint8_t sid{};
    std::uint8_t destination_node{};
    std::uint8_t source_node{};
    std::uint16_t dm_start{};
    std::uint16_t words{};
};

// Literal-only, deliberately narrower than the OS's loopback list. No DNS and
// no IPv4 aliases. Never use this as proof of OS-level egress isolation.
[[nodiscard]] constexpr bool numeric_loopback_only(std::string_view host) noexcept {
    return host == "127.0.0.1" || host == "::1" || host == "[::1]";
}

[[nodiscard]] constexpr std::uint16_t be16(std::uint8_t hi, std::uint8_t lo) noexcept {
    return static_cast<std::uint16_t>((static_cast<unsigned>(hi) << 8U) | lo);
}

// Does not authorize reads from any disputed real address. This only checks
// a synthetic 18-byte FINS memory-area-read request fixture.
[[nodiscard]] inline Check parse_draft_read(std::span<const std::uint8_t> bytes,
                                           std::uint8_t expected_sid,
                                           DraftRead& out) noexcept {
    if (bytes.size() < 12U) return Check::ShortHeader;
    // Deny writes before inspecting payload length, including malformed writes.
    if (bytes[10] == 0x01U && bytes[11] == 0x02U) return Check::WriteDisabled;
    if (bytes.size() != 18U) return Check::BadRequestLength;
    if (bytes[0] != 0x80U || bytes[1] != 0x00U || bytes[2] != 0x02U ||
        bytes[3] != 0x00U || bytes[5] != 0x00U || bytes[6] != 0x00U ||
        bytes[8] != 0x00U || bytes[4] == 0U || bytes[7] == 0U)
        return Check::BadHeader;
    if (expected_sid == 0U || bytes[9] != expected_sid) return Check::SidMismatch;
    if (bytes[10] != 0x01U || bytes[11] != 0x01U) return Check::BadCommand;
    if (bytes[12] != 0x82U || bytes[15] != 0x00U) return Check::BadAreaOrAddress;
    const auto start = be16(bytes[13], bytes[14]);
    const auto count = be16(bytes[16], bytes[17]);
    if (count == 0U || count > 20U || static_cast<std::uint32_t>(start) + count > 65536U)
        return Check::BadWordCount;
    out = DraftRead{bytes[9], bytes[4], bytes[7], start, count};
    return Check::OkDraftRead;
}

// A synthetic FINS response must match route, SID, command, endcode AND exact
// byte count. Partial or ambiguous data is never accepted as successful read.
[[nodiscard]] inline Check check_draft_read_reply(std::span<const std::uint8_t> request,
                                                   std::span<const std::uint8_t> response) noexcept {
    DraftRead parsed{};
    const auto req_check = parse_draft_read(request,
                                           request.size() >= 10U ? request[9] : 0U,
                                           parsed);
    if (req_check != Check::OkDraftRead) return req_check;
    if (response.size() < 14U) return Check::BadResponseLength;
    if (response[0] != 0xC0U || response[1] != 0x00U || response[2] != 0x02U ||
        response[5] != 0x00U || response[8] != 0x00U)
        return Check::BadResponseHeader;
    if (response[3] != request[6] || response[4] != request[7] ||
        response[6] != request[3] || response[7] != request[4] ||
        response[9] != request[9])
        return Check::RouteMismatch;
    if (response[10] != 0x01U || response[11] != 0x01U)
        return Check::BadResponseHeader;
    if (response[12] != 0x00U || response[13] != 0x00U)
        return Check::EndCodeNotOk;
    if (response.size() != 14U + 2U * parsed.words)
        return Check::BadResponseLength;
    return Check::OkDraftRead;
}

} // namespace aoi::plc::preflight
