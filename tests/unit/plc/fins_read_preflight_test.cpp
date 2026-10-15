// Standalone C++20 memory-only fixture tests; no UDP is opened.
#include "../../../libs/plc/prep/fins_read_preflight.hpp"
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <string_view>
#include <vector>

using aoi::plc::preflight::Check;
using aoi::plc::preflight::DraftRead;
using aoi::plc::preflight::check_draft_read_reply;
using aoi::plc::preflight::numeric_loopback_only;
using aoi::plc::preflight::parse_draft_read;

namespace {
int passed = 0;
int failed = 0;
void check(bool actual, std::string_view label) {
    if (!actual) { std::cerr << "FAIL " << label << '\n'; ++failed; }
    else { std::cout << "PASS " << label << '\n'; ++passed; }
}
std::vector<std::uint8_t> from_hex(std::string_view hex) {
    std::vector<std::uint8_t> result;
    if (hex.size() % 2 != 0) throw std::invalid_argument("odd hex");
    const auto digit = [](char c) -> unsigned {
        if (c >= '0' && c <= '9') return static_cast<unsigned>(c - '0');
        if (c >= 'A' && c <= 'F') return static_cast<unsigned>(c - 'A') + 10U;
        if (c >= 'a' && c <= 'f') return static_cast<unsigned>(c - 'a') + 10U;
        throw std::invalid_argument("non hex");
    };
    for (std::size_t i = 0; i < hex.size(); i += 2)
        result.push_back(static_cast<std::uint8_t>((digit(hex[i]) << 4U) | digit(hex[i+1])));
    return result;
}
}

int main() {
    const auto req = from_hex("8000020032000082005A01018203EE000001");
    const auto ok_reply = from_hex("C000020082000032005A010100000001");
    DraftRead decoded{};
    check(parse_draft_read(req, 0x5aU, decoded) == Check::OkDraftRead,
          "golden DM read request accepted as draft bytes only");
    check(decoded.sid == 0x5aU && decoded.destination_node == 50U &&
          decoded.source_node == 130U && decoded.dm_start == 1006U && decoded.words == 1U,
          "golden DM address/SID/word count decoded without authorizing real route");
    check(check_draft_read_reply(req, ok_reply) == Check::OkDraftRead,
          "exact golden successful read response bytes accepted");
    check(numeric_loopback_only("127.0.0.1") && numeric_loopback_only("::1") &&
          numeric_loopback_only("[::1]"), "numeric loopback literals allowed only for future simulator");
    for (std::string_view unsafe : {"192.168.0.1", "10.1.1.1", "127.0.0.2", "127.1",
                                    "localhost", "0.0.0.0", "::", "[::]", "", "[fe80::1]",
                                    "127.0.0.1:9600", "127.000.000.001"}) {
        check(!numeric_loopback_only(unsafe), "unsafe/ambiguous endpoint rejected");
    }
    auto broken = req;
    broken[9] = 0x5bU;
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::SidMismatch,
          "wrong request SID rejected");
    broken = req; broken[2] = 0x01U;
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::BadHeader,
          "unexpected gateway count rejected");
    broken = req; broken.pop_back();
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::BadRequestLength,
          "truncated read request rejected");
    broken = req; broken.push_back(0U);
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::BadRequestLength,
          "read request trailing byte rejected");
    broken = req; broken[11] = 0x02U; broken.push_back(0U);
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::WriteDisabled,
          "WRITE always denied (including malformed payload length)");
    broken = req; broken[11] = 0x03U;
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::BadCommand,
          "unsupported command denied");
    broken = req; broken[12] = 0x80U;
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::BadAreaOrAddress,
          "unsupported memory area denied");
    broken = req; broken[17] = 0U;
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::BadWordCount,
          "zero-word request denied");
    broken = req; broken[17] = 21U;
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::BadWordCount,
          "oversized-count request denied");
    broken = req; broken[13] = 0xffU; broken[14] = 0xffU; broken[17] = 2U;
    check(parse_draft_read(broken, 0x5aU, decoded) == Check::BadWordCount,
          "address + count overrun denied");
    broken = ok_reply; broken[9] = 0x5bU;
    check(check_draft_read_reply(req, broken) == Check::RouteMismatch,
          "response SID mismatch denied");
    broken = ok_reply; broken[7] = 0x33U;
    check(check_draft_read_reply(req, broken) == Check::RouteMismatch,
          "response source-node route mismatch denied");
    broken = ok_reply; broken[0] = 0x80U;
    check(check_draft_read_reply(req, broken) == Check::BadResponseHeader,
          "command ICF cannot masquerade as response");
    broken = ok_reply; broken[13] = 0x05U;
    check(check_draft_read_reply(req, broken) == Check::EndCodeNotOk,
          "non-zero PLC end-code cannot become successful read");
    broken = ok_reply; broken.pop_back();
    check(check_draft_read_reply(req, broken) == Check::BadResponseLength,
          "truncated word cannot become successful read");
    broken = ok_reply; broken.push_back(0U);
    check(check_draft_read_reply(req, broken) == Check::BadResponseLength,
          "surplus reply byte rejected");
    const auto barcode = from_hex("8000020032000082005A01018207D0000014");
    check(parse_draft_read(barcode, 0x5aU, decoded) == Check::OkDraftRead &&
          decoded.dm_start == 2000U && decoded.words == 20U,
          "legacy 20-word barcode read is only a draft byte shape; actual 10-word source remains UNKNOWN");
    std::cout << "SUMMARY passed=" << passed << " failed=" << failed << " SIM_ONLY=1\n";
    return failed == 0 ? 0 : 1;
}
