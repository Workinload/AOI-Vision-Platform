#pragma once
// W006 / A2: immutable static facts, not a live PLC / FINS gateway or C01 contract.
// This compilation unit deliberately has no socket, PLC or write-device interface.
#if !defined(AOI_SIM_ONLY) || AOI_SIM_ONLY != 1
#error "W006 SignalRegistry may only be compiled with AOI_SIM_ONLY=1"
#endif
#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace aoi::plc::w006 {

// Exactly the 21 columns of immutable v2 plc_signal_registry.csv, in order.
enum class SignalField : std::size_t {
    SignalId, PLCAddress, StationId, HostRole, Direction, DataType,
    EffectiveWords, PurposeCN, ValueLegend, ValidationState, RiskNote,
    EvidenceSource, EditClass, PhysicalDeviceId, EvidenceLevel,
    LastFieldVerifiedUtc, ActivationAllowed, ValueOrigin,
    MonitorReadOnlyDefault, ProductionWriteEnabled, ConflictGroup, Count
};
// Exactly the 8 columns of immutable v2 field_verification_gates.csv.
enum class GateField : std::size_t {
    GateId, Topic, Status, SourceConflict, RequiredEvidence, ProposedOwner,
    ProductionWriteAllowed, FieldSignoff, Count
};

using SignalColumns = std::array<std::string, static_cast<std::size_t>(SignalField::Count)>;
using GateColumns = std::array<std::string, static_cast<std::size_t>(GateField::Count)>;

struct SignalFact final {
    SignalColumns original;
    [[nodiscard]] std::string_view at(SignalField field) const noexcept {
        return original[static_cast<std::size_t>(field)];
    }
    [[nodiscard]] std::string_view id() const noexcept { return at(SignalField::SignalId); }
    [[nodiscard]] bool has_conflict() const noexcept {
        return !at(SignalField::ConflictGroup).empty() ||
               at(SignalField::ValidationState) == "BLOCKED_CONFLICT";
    }
    // W002 certifies a historical mapping, NEVER actual PLC address/semantic correctness.
    [[nodiscard]] constexpr bool field_meaning_verified() const noexcept { return false; }
    [[nodiscard]] constexpr bool production_write_allowed() const noexcept { return false; }
};
struct FieldGate final {
    GateColumns original;
    [[nodiscard]] std::string_view at(GateField field) const noexcept {
        return original[static_cast<std::size_t>(field)];
    }
    [[nodiscard]] std::string_view id() const noexcept { return at(GateField::GateId); }
    [[nodiscard]] constexpr bool production_write_allowed() const noexcept { return false; }
    [[nodiscard]] constexpr bool signed_by_field_engineer() const noexcept { return false; }
};

enum class ImportStatus {
    Ok, ChecksumMismatch, CsvFormatError, SchemaMismatch,
    InvalidSourceFacts, MissingInput
};
struct ImportResult; // complete after registry definition

// The only public constructor path is pinned to the immutable v2 SHA256 bytes.
// The factory fails closed on changed CSV content, even if rows look plausible.
class LockedSignalRegistry final {
public:
    [[nodiscard]] static ImportResult from_locked_bytes(
        std::string_view signal_csv, std::string_view gate_csv);
    [[nodiscard]] static ImportResult from_locked_files(
        const std::string& signal_path, const std::string& gate_path);
    [[nodiscard]] const std::vector<SignalFact>& signals() const noexcept { return signals_; }
    [[nodiscard]] const std::vector<FieldGate>& gates() const noexcept { return gates_; }
    [[nodiscard]] const SignalFact* find_signal(std::string_view id) const noexcept;
    [[nodiscard]] const FieldGate* find_gate(std::string_view id) const noexcept;
    [[nodiscard]] std::vector<const SignalFact*> find_by_address(std::string_view address) const;
    [[nodiscard]] std::size_t blocked_gates() const noexcept;
    [[nodiscard]] std::size_t open_gates() const noexcept;
    [[nodiscard]] constexpr bool is_live_plc_readback() const noexcept { return false; }
    [[nodiscard]] constexpr bool has_field_production_permission() const noexcept { return false; }
    [[nodiscard]] static constexpr std::string_view signal_source_sha256() noexcept {
        return "97ff0003f2416a1dacf86fecea18ef80ec45fd5f016526333725e0c977e3fee8";
    }
    [[nodiscard]] static constexpr std::string_view gate_source_sha256() noexcept {
        return "c56d84b46176ca86afcb11088398005f6af86b7fc85d5ce0f9bf7a295871b6de";
    }
private:
    std::vector<SignalFact> signals_;
    std::vector<FieldGate> gates_;
    LockedSignalRegistry(std::vector<SignalFact> s, std::vector<FieldGate> g)
      : signals_(std::move(s)), gates_(std::move(g)) {}
};

struct ImportResult final {
    ImportStatus status{ImportStatus::MissingInput};
    std::optional<LockedSignalRegistry> registry{};
    std::string diagnostic{};
    [[nodiscard]] bool accepted() const noexcept {
        return status == ImportStatus::Ok && registry.has_value();
    }
};

// W006 is a policy/read-model only. W026 must introduce a *separate* approved
// transaction gateway; none of these calls can transmit a byte to any target.
enum class DenialReason {
    MissingRegistry, UnknownSignal, UnknownOrConflictedMapping,
    NonLoopbackTarget, MissingOrExpiredApproval, ProductionWriteDisabled
};
struct WriteIntent final {
    std::string_view signal_id;
    std::string_view endpoint;
    std::string_view claimed_role;  // even ADMIN has no physical write privilege
    bool claimed_admin{false};
    bool claims_approval{false};
    std::uint64_t approval_age_ms{0};
};
struct WriteDecision final {
    bool allowed{false};
    DenialReason reason{DenialReason::ProductionWriteDisabled};
    std::string_view source_mode{"SIM_ONLY_POLICY_NO_IO"};
};
class WritePolicy final {
public:
    [[nodiscard]] static WriteDecision assess(
        const LockedSignalRegistry* registry, const WriteIntent& intent) noexcept;
    [[nodiscard]] static constexpr bool production_writes_possible() noexcept { return false; }
};
}  // namespace aoi::plc::w006
