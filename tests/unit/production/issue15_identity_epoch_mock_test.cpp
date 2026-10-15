// Issue #15 / Agent01 / SIM_ONLY / BRANCH_DEV.
// Stateful, in-memory fault-injection fixture. Not a W035 implementation,
// not a C01 DTO, and not a PLC, camera, SQLite, or PeerLink adapter.
// Only A0's unmodified C00-DRAFT domain types and pure assess() are reused.
#include <aoi/domain/contract.hpp>

#include <algorithm>
#include <cstdint>
#include <functional>
#include <iostream>
#include <map>
#include <optional>
#include <random>
#include <set>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace {
using namespace aoi::domain;

void check(bool good, const std::string& label) {
    if (!good) throw std::runtime_error(label);
}

std::string channel(int ch) {
    check(ch >= 1 && ch <= 11, "fixture channel out of range");
    return std::string(ch < 10 ? "CH0" : "CH") + std::to_string(ch);
}

struct Expected {
    std::string capture;
    std::uint32_t attempt{0};
};
struct SyntheticResultEvent {
    std::string workpiece;
    std::uint64_t epoch{0};
    std::string release;
    std::string channel;
    std::string capture;
    std::uint32_t attempt{0};
    ProcessingStatus processing{ProcessingStatus::Succeeded};
    QualityStatus quality{QualityStatus::Ok};
};
enum class Receive { Accepted, ExactDuplicate, Quarantined };

struct Piece {
    std::string id;
    std::string raw_barcode;
    std::string trusted_sim_slot;
    std::string release;
    std::uint64_t epoch{1};
    BarcodeStatus barcode{BarcodeStatus::Unverified};
    bool position_ok{false};
    bool conflict_hold{false};
    bool closed{false};
    std::map<std::string, Expected> expected;
    std::map<std::string, Result> accepted;
};

// Single-threaded toy ledger for deterministic negative tests. It deliberately
// does not expose a public runtime interface, allocate a PLC address, or persist.
class SimulatedLedger {
public:
    std::optional<std::string> intake(const std::string& trusted_sim_slot,
                                      const std::string& barcode,
                                      const std::string& release) {
        if (trusted_sim_slot.empty() || barcode.empty() || release.empty() ||
            active_slots_.contains(trusted_sim_slot)) {
            quarantine_.push_back("INTAKE_UNTRUSTED_OR_AMBIGUOUS_SLOT");
            return std::nullopt;
        }
        const std::string id = "WP-SIM-" + std::to_string(++next_id_);
        auto [it, inserted] = pieces_.emplace(id, Piece{});
        check(inserted, "fixture generated duplicate id");
        it->second.id = id;
        it->second.raw_barcode = barcode;
        it->second.trusted_sim_slot = trusted_sim_slot;
        it->second.release = release;
        active_slots_[trusted_sim_slot] = id;
        return id;
    }

    const Piece& at(const std::string& id) const { return pieces_.at(id); }
    std::size_t accepted_count() const { return accepted_count_; }
    std::size_t quarantine_count() const { return quarantine_.size(); }
    const std::string& last_quarantine() const { return quarantine_.back(); }

    void end_barcode(const std::string& id, const std::string& observed_raw) {
        auto& p = pieces_.at(id);
        if (p.closed) throw std::runtime_error("fixture barcode on closed piece");
        p.barcode = observed_raw.empty() ? BarcodeStatus::Missing
                  : observed_raw == p.raw_barcode ? BarcodeStatus::Matched
                  : BarcodeStatus::Mismatched;
    }

    // Explicit, trusted SIM-only expectation: no FIFO/barcode/last-seen inference.
    bool expect(const std::string& id, int ch, const std::string& capture,
                std::uint32_t attempt) {
        auto& p = pieces_.at(id);
        const auto key = channel(ch);
        if (p.closed || capture.empty() || attempt == 0) return false;
        auto found = p.expected.find(key);
        if (found != p.expected.end() && attempt <= found->second.attempt) return false;
        p.expected[key] = {capture, attempt};
        p.accepted.erase(key);  // A new authorized attempt invalidates the old one.
        if (ch == 1) p.position_ok = false;
        return true;
    }

    SyntheticResultEvent event(const std::string& id, int ch) const {
        const auto& p = at(id);
        const auto& e = p.expected.at(channel(ch));
        return {id, p.epoch, p.release, channel(ch), e.capture, e.attempt};
    }

    // target_id represents the trusted capture expectation to which a frame was
    // explicitly bound. The event's self-declared ID must never choose a target.
    Receive receive_for(const std::string& target_id, const SyntheticResultEvent& ev) {
        auto found = pieces_.find(target_id);
        if (found == pieces_.end()) return quarantine("NO_TRUSTED_TARGET");
        auto& p = found->second;
        if (p.closed) return quarantine("CLOSED_WORKPIECE");
#ifndef AOI_TEST_ONLY_MUTATE_WORKPIECE_GUARD
        if (ev.workpiece != target_id) return quarantine("WORKPIECE_MISMATCH");
#endif
#ifndef AOI_TEST_ONLY_MUTATE_EPOCH_GUARD
        if (ev.epoch != p.epoch) return quarantine("EPOCH_MISMATCH");
#endif
        if (ev.release != p.release) return quarantine("RELEASE_MISMATCH");
        const auto expected = p.expected.find(ev.channel);
        if (expected == p.expected.end()) return quarantine("NO_EXPLICIT_EXPECTATION");
        if (ev.capture.empty() || ev.capture != expected->second.capture ||
            ev.attempt == 0 || ev.attempt != expected->second.attempt) {
            return quarantine("CAPTURE_OR_ATTEMPT_MISMATCH");
        }
        const auto prior = p.accepted.find(ev.channel);
        if (prior != p.accepted.end()) {
            const auto& previous = prior->second;
            if (previous.processing == ev.processing && previous.quality == ev.quality &&
                previous.key.workpiece.value == ev.workpiece &&
                previous.key.epoch == ev.epoch && previous.key.release.value == ev.release) {
                return Receive::ExactDuplicate;
            }
            p.conflict_hold = true;  // Conflicting replay must never remain OK.
            return quarantine("CONFLICTING_DUPLICATE");
        }
        p.accepted.emplace(ev.channel, Result{
            Correlation{WorkpieceId(ev.workpiece), ev.epoch, ReleaseId(ev.release),
                        ChannelId(ev.channel), CaptureId(ev.capture), ev.attempt},
            ev.processing, ev.quality, false});
        ++accepted_count_;
        if (ev.channel == "CH01" && ev.processing == ProcessingStatus::Succeeded &&
            ev.quality == QualityStatus::Ok) p.position_ok = true;
        return Receive::Accepted;
    }

    bool new_epoch(const std::string& id, std::uint64_t next_epoch) {
        auto& p = pieces_.at(id);
        if (p.closed || next_epoch <= p.epoch) return false;
        p.epoch = next_epoch;
        p.barcode = BarcodeStatus::Unverified;
        p.position_ok = false;
        p.conflict_hold = false;
        p.expected.clear();
        p.accepted.clear();
        return true;
    }

    // Only a synthetic test cleanup: does not assert a real final PLC action.
    void release_slot_for_simulation(const std::string& id) {
        auto& p = pieces_.at(id);
        p.closed = true;
        active_slots_.erase(p.trusted_sim_slot);
    }

    Decision decision(const std::string& id) const {
        const auto& p = pieces_.at(id);
        if (p.closed) return {FinalOutcome::HoldUndecided, "closed_in_simulation", {}};
        if (p.conflict_hold) return {FinalOutcome::HoldUndecided, "conflicting_duplicate", {}};
        if (!p.position_ok) return {FinalOutcome::HoldUndecided, "CH01_position_missing", {}};
        DecisionInput input{WorkpieceId(p.id), p.epoch, ReleaseId(p.release), p.barcode, {}, {}};
        for (int ch = 2; ch <= 11; ++ch) {
            const auto key = channel(ch);
            input.required_channels.emplace_back(key);
            auto found = p.accepted.find(key);
            if (found != p.accepted.end()) input.results.push_back(found->second);
        }
        return assess(input);  // A0's existing illustrative safety predicate.
    }

private:
    Receive quarantine(std::string why) {
        quarantine_.push_back(std::move(why));
        return Receive::Quarantined;
    }
    std::uint64_t next_id_{0};
    std::size_t accepted_count_{0};
    std::unordered_map<std::string, Piece> pieces_;
    std::unordered_map<std::string, std::string> active_slots_;
    std::vector<std::string> quarantine_;
};

std::string start(SimulatedLedger& sim, const std::string& slot,
                  const std::string& raw = "SAME-RAW-BARCODE") {
    const auto id = sim.intake(slot, raw, "REL-SIM-1");
    check(id.has_value(), "fixture trusted intake failed");
    return *id;
}
void plan(SimulatedLedger& sim, const std::string& id) {
    for (int ch = 1; ch <= 11; ++ch) {
        check(sim.expect(id, ch, "SIM-CAP-" + id + "-" + channel(ch), 1),
              "explicit capture expectation failed");
    }
}
void deliver(SimulatedLedger& sim, const std::string& id, int ch) {
    check(sim.receive_for(id, sim.event(id, ch)) == Receive::Accepted,
          "expected matching synthetic event was not accepted");
}
void deliver_all(SimulatedLedger& sim, const std::string& id) {
    for (int ch = 1; ch <= 11; ++ch) deliver(sim, id, ch);
}
void expect_outcome(const SimulatedLedger& sim, const std::string& id,
                    FinalOutcome outcome, const std::string& reason) {
    auto d = sim.decision(id);
    check(d.outcome == outcome && d.reason == reason,
          "wrong outcome/reason: got '" + d.reason + "'");
}

using Case = std::pair<const char*, std::function<void()>>;
std::vector<Case> cases() {
    std::vector<Case> tests;
    const auto add = [&](const char* label, std::function<void()> fn) {
        tests.emplace_back(label, std::move(fn));
    };
    add("same barcode never chooses WorkpieceId", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"), b = start(sim, "slot-B");
        check(a != b, "two intakes collided on barcode");
        plan(sim, a); plan(sim, b);
        sim.end_barcode(a, "SAME-RAW-BARCODE");
        sim.end_barcode(b, "SAME-RAW-BARCODE");
        deliver_all(sim, a);
        expect_outcome(sim, a, FinalOutcome::Ok, "valid_ok");
        expect_outcome(sim, b, FinalOutcome::HoldUndecided, "CH01_position_missing");
        auto stolen = sim.event(a, 1);
        stolen.capture = sim.event(b, 1).capture; // even a matching capture cannot choose the target.
        check(sim.receive_for(b, stolen) == Receive::Quarantined, "cross-piece contamination");
        check(sim.last_quarantine() == "WORKPIECE_MISMATCH", "wrong identity guard");
        deliver_all(sim, b);
        expect_outcome(sim, b, FinalOutcome::Ok, "valid_ok");
    });
    add("untrusted or duplicated SIM slot quarantined", [] {
        SimulatedLedger sim;
        check(!sim.intake("", "ABC", "REL").has_value(), "unknown slot guessed");
        (void)start(sim, "slot-A");
        check(!sim.intake("slot-A", "OTHER", "REL").has_value(), "overlapping slot accepted");
        check(sim.quarantine_count() == 2, "slot ambiguity not auditable");
    });
    add("same slot reused creates a different identity", [] {
        SimulatedLedger sim;
        auto previous = start(sim, "slot-A");
        plan(sim, previous);
        auto late = sim.event(previous, 2);
        sim.release_slot_for_simulation(previous);
        auto next = start(sim, "slot-A");
        check(next != previous, "slot reuse recycled WorkpieceId");
        plan(sim, next);
        check(sim.receive_for(previous, late) == Receive::Quarantined, "closed workpiece accepted late event");
        check(sim.receive_for(next, late) == Receive::Quarantined, "late event entered newly recycled slot");
    });
    add("old Epoch result quarantined after explicit reset", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        auto old = sim.event(a, 3);
        check(sim.new_epoch(a, 2), "reset failed");
        check(!sim.new_epoch(a, 2), "same Epoch reused");
        plan(sim, a);
        auto near_match = sim.event(a, 3);
        near_match.epoch = old.epoch; // all other fields valid; isolates the Epoch guard.
        check(sim.receive_for(a, near_match) == Receive::Quarantined, "stale Epoch accepted");
        check(sim.last_quarantine() == "EPOCH_MISMATCH", "wrong Epoch quarantine reason");
        sim.end_barcode(a, "SAME-RAW-BARCODE");
        for (int ch = 1; ch <= 11; ++ch) if (ch != 3) deliver(sim, a, ch);
        expect_outcome(sim, a, FinalOutcome::HoldUndecided, "required_channel_missing");
        deliver(sim, a, 3);
        expect_outcome(sim, a, FinalOutcome::Ok, "valid_ok");
    });
    add("future Epoch spoof also quarantined", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        auto forged = sim.event(a, 2); forged.epoch += 1;
        check(sim.receive_for(a, forged) == Receive::Quarantined, "future Epoch accepted");
        check(sim.accepted_count() == 0, "forged event changed business state");
    });
    add("wrong Release cannot enter active Workpiece", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        auto forged = sim.event(a, 2); forged.release = "OTHER-RELEASE";
        check(sim.receive_for(a, forged) == Receive::Quarantined, "release spoof accepted");
        check(sim.last_quarantine() == "RELEASE_MISMATCH", "missing release quarantine");
    });
    add("missing and foreign CaptureId cannot fill channel", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        auto bad = sim.event(a, 2); bad.capture.clear();
        check(sim.receive_for(a, bad) == Receive::Quarantined, "empty CaptureId accepted");
        bad.capture = "SOME-OTHER-CAPTURE";
        check(sim.receive_for(a, bad) == Receive::Quarantined, "unbound capture accepted");
        check(sim.accepted_count() == 0, "no-match altered accepted set");
    });
    add("old Attempt cannot backfill authorized re-shoot", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        auto old = sim.event(a, 2); deliver(sim, a, 2);
        check(sim.expect(a, 2, "SIM-RESHOOT-CAP-2", 2), "authorized new attempt failed");
        check(!sim.expect(a, 2, "SIM-REGRESSION", 1), "attempt regressed");
        check(sim.receive_for(a, old) == Receive::Quarantined, "superseded attempt accepted");
        sim.end_barcode(a, "SAME-RAW-BARCODE");
        for (int ch = 1; ch <= 11; ++ch) if (ch != 2) deliver(sim, a, ch);
        expect_outcome(sim, a, FinalOutcome::HoldUndecided, "required_channel_missing");
        deliver(sim, a, 2);
        expect_outcome(sim, a, FinalOutcome::Ok, "valid_ok");
    });
    add("same event delivered twice has one business effect", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        auto e = sim.event(a, 2);
        check(sim.receive_for(a, e) == Receive::Accepted, "initial synthetic delivery failed");
        const auto before = sim.accepted_count();
        for (int i = 0; i < 8; ++i)
            check(sim.receive_for(a, e) == Receive::ExactDuplicate, "duplicate not idempotent");
        check(sim.accepted_count() == before, "duplicate incremented counted effects");
    });
    add("conflicting replay must taint verdict to HOLD", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        sim.end_barcode(a, "SAME-RAW-BARCODE"); deliver_all(sim, a);
        expect_outcome(sim, a, FinalOutcome::Ok, "valid_ok");
        auto conflict = sim.event(a, 2); conflict.quality = QualityStatus::Ng;
        check(sim.receive_for(a, conflict) == Receive::Quarantined, "conflicting replay accepted");
        expect_outcome(sim, a, FinalOutcome::HoldUndecided, "conflicting_duplicate");
    });
    add("CH01 positioning cannot substitute for missing CH11", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        sim.end_barcode(a, "SAME-RAW-BARCODE");
        for (int ch = 1; ch <= 10; ++ch) deliver(sim, a, ch);
        expect_outcome(sim, a, FinalOutcome::HoldUndecided, "required_channel_missing");
        auto out_of_range = sim.event(a, 1); out_of_range.channel = "CH99";
        check(sim.receive_for(a, out_of_range) == Receive::Quarantined, "CH99 filled CH11");
        deliver(sim, a, 11);
        expect_outcome(sim, a, FinalOutcome::Ok, "valid_ok");
    });
    add("CH01 missing blocks ten otherwise successful defects", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        sim.end_barcode(a, "SAME-RAW-BARCODE");
        for (int ch = 2; ch <= 11; ++ch) deliver(sim, a, ch);
        expect_outcome(sim, a, FinalOutcome::HoldUndecided, "CH01_position_missing");
    });
    add("timeout and missing quality cannot become OK", [] {
        for (int variant = 0; variant < 2; ++variant) {
            SimulatedLedger sim;
            auto a = start(sim, "slot-A"); plan(sim, a);
            sim.end_barcode(a, "SAME-RAW-BARCODE");
            for (int ch = 1; ch <= 11; ++ch) {
                auto e = sim.event(a, ch);
                if (ch == 6) {
                    if (variant == 0) e.processing = ProcessingStatus::Timeout;
                    else e.quality = QualityStatus::Missing;
                }
                check(sim.receive_for(a, e) == Receive::Accepted, "fault event rejected before verdict check");
            }
            expect_outcome(sim, a, FinalOutcome::HoldUndecided, "missing_or_failed_processing");
        }
    });
    add("one valid NG is distinct from HOLD and OK", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a);
        sim.end_barcode(a, "SAME-RAW-BARCODE");
        for (int ch = 1; ch <= 11; ++ch) {
            auto e = sim.event(a, ch);
            if (ch == 7) e.quality = QualityStatus::Ng;
            check(sim.receive_for(a, e) == Receive::Accepted, "NG event rejected");
        }
        expect_outcome(sim, a, FinalOutcome::Ng, "valid_ng");
    });
    add("wrong or missing end barcode denies OK", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"); plan(sim, a); deliver_all(sim, a);
        expect_outcome(sim, a, FinalOutcome::HoldUndecided, "barcode_not_verified");
        sim.end_barcode(a, "NOT-THE-SAME-CODE");
        expect_outcome(sim, a, FinalOutcome::BarcodeMismatch, "barcode_mismatch");
        sim.end_barcode(a, "");
        expect_outcome(sim, a, FinalOutcome::HoldUndecided, "barcode_not_verified");
    });
    add("deterministic shuffled two-workpiece frames stay isolated", [] {
        SimulatedLedger sim;
        auto a = start(sim, "slot-A"), b = start(sim, "slot-B");
        plan(sim, a); plan(sim, b);
        sim.end_barcode(a, "SAME-RAW-BARCODE"); sim.end_barcode(b, "SAME-RAW-BARCODE");
        std::vector<std::pair<std::string, SyntheticResultEvent>> queue;
        for (int ch = 1; ch <= 11; ++ch) {
            queue.emplace_back(a, sim.event(a, ch));
            queue.emplace_back(b, sim.event(b, ch));
        }
        std::mt19937 random(0xA01135u);
        std::shuffle(queue.begin(), queue.end(), random);
        for (auto& [target, e] : queue) {
            check(sim.receive_for(target, e) == Receive::Accepted, "shuffled exact event denied");
            check(sim.receive_for(target, e) == Receive::ExactDuplicate, "shuffled duplicate double-effect");
        }
        check(sim.accepted_count() == 22, "shuffled deliveries did not count each channel once");
        expect_outcome(sim, a, FinalOutcome::Ok, "valid_ok");
        expect_outcome(sim, b, FinalOutcome::Ok, "valid_ok");
    });
    return tests;
}
} // namespace

int main() {
    std::cout << "AOI Issue #15 SIMULATED PREP; C00-DRAFT; NOT_W035_VERIFIED; "
                 "NO_PLC_NETWORK_OR_DB\n";
    const auto all = cases();
    int passed = 0;
    for (const auto& [name, fn] : all) {
        try {
            fn();
            ++passed;
            std::cout << "PASS " << name << '\n';
        } catch (const std::exception& ex) {
            std::cerr << "FAIL " << name << " : " << ex.what() << '\n';
        }
    }
    std::cout << "SUMMARY " << passed << '/' << all.size() << " cases; "
              << (passed == static_cast<int>(all.size()) ? "PREP_PASS" : "PREP_FAIL") << '\n';
    return passed == static_cast<int>(all.size()) ? 0 : 1;
}
