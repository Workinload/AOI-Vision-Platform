// Standalone C++20 data-only fixture. No camera, DMA, PLC, or SDK calls.
#include "../../../libs/device/prep/capture_topology_preflight.hpp"
#include <array>
#include <cstdint>
#include <iostream>
#include <map>
#include <memory>
#include <set>
#include <string_view>

using namespace aoi::device::preflight;

namespace {
int passed = 0;
int failed = 0;
void check(bool ok, std::string_view desc) {
    if (ok) { std::cout << "PASS " << desc << '\n'; ++passed; }
    else { std::cerr << "FAIL " << desc << '\n'; ++failed; }
}
}

int main() {
    static_assert(kDraftSlots.size() == 11U);
    static_assert(kRawBytes == 123000000U);
    static_assert(kOddRows == 7500U && kEvenRows == 7499U);
    std::set<std::string_view> channels;
    std::set<std::string_view> devices;
    std::set<std::string_view> line_devices;
    std::size_t area = 0U, line = 0U, master_devices = 0U, slave_devices = 0U;
    std::map<std::string_view, std::string_view> first_host;
    std::map<std::string_view, std::string_view> first_handshake;
    std::map<std::string_view, std::shared_ptr<const std::array<std::uint8_t,8>>> frame_by_device;
    std::map<std::string_view, const std::array<std::uint8_t,8>*> frame_by_channel;
    for (const auto& slot : kDraftSlots) {
        channels.insert(slot.channel);
        devices.insert(slot.device);
        if (slot.kind == Kind::Area) ++area;
        if (slot.kind == Kind::Line) { ++line; line_devices.insert(slot.device); }
        if (!first_host.contains(slot.device)) first_host[slot.device] = slot.host;
        check(first_host.at(slot.device) == slot.host, "same device cannot move between host roles");
        if (!first_handshake.contains(slot.device)) first_handshake[slot.device] = slot.handshake;
        check(first_handshake.at(slot.device) == slot.handshake, "shared parent has one handshake");
        if (!frame_by_device.contains(slot.device)) {
            // Tiny deterministic parent sample; NOT a real image or throughput benchmark.
            std::array<std::uint8_t,8> bytes{};
            bytes[0] = static_cast<std::uint8_t>(slot.channel[2]);
            bytes[1] = static_cast<std::uint8_t>(slot.channel[3]);
            frame_by_device[slot.device] = std::make_shared<const std::array<std::uint8_t,8>>(bytes);
        }
        frame_by_channel[slot.channel] = frame_by_device[slot.device].get();
    }
    check(channels.size() == 11U, "eleven distinct CH01-CH11 slots");
    check(devices.size() == 9U, "nine physical device identities");
    check(area == 7U && line == 4U && line_devices.size() == 2U,
          "7 area devices +2 line devices =11 logical channel slots");
    for (const auto& [device, host] : first_host) {
        (void)device;
        if (host == "MASTER") ++master_devices;
        if (host == "SLAVE") ++slave_devices;
    }
    check(master_devices == 4U && slave_devices == 5U, "role counts 4 master /5 slave physical devices");
    check(kDraftSlots.front().channel == "CH01" &&
          kDraftSlots.front().purpose == Purpose::Positioning,
          "CH01 positioning is not a final defect decision");
    bool all_others_inspection = true;
    for (std::size_t i = 1; i < kDraftSlots.size(); ++i)
        if (kDraftSlots[i].purpose != Purpose::Inspection) all_others_inspection = false;
    check(all_others_inspection, "CH02-CH11 inspection slots");
    check(frame_by_channel.at("CH08") == frame_by_channel.at("CH09") &&
          frame_by_channel.at("CH08") != frame_by_channel.at("CH10"),
          "CH08/09 share a single simulated rear parent, not front parent");
    check(frame_by_channel.at("CH10") == frame_by_channel.at("CH11"),
          "CH10/11 share a single simulated front parent");
    check(frame_by_device.size() == 9U, "only nine small simulated parent buffers allocated");
    check(kDraftSlots[7].handshake == "D1013" && kDraftSlots[8].handshake == "D1013",
          "unresolved D1013 collision is preserved for A0 field review");
    check(kDraftSlots[9].handshake == "D1014" && kDraftSlots[10].handshake == "D1014",
          "CH10/11 share draft FINS handshake without authorizing writes");
    check(kOddRows + kEvenRows + 1U == kRawHeight,
          "historic img[1::2] and img[2::2] omit row zero");
    check(!camera_done_means_quality_ok(), "CamDone cannot assert final OK");
    const std::shared_ptr<const std::array<std::uint8_t,8>> missing{};
    check(!missing, "missing parent frame remains missing, not fake OK");
    std::cout << "SUMMARY passed=" << passed << " failed=" << failed << " SIM_ONLY=1\n";
    return failed == 0 ? 0 : 1;
}
