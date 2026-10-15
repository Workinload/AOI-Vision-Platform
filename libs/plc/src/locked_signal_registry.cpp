#include "aoi/plc/locked_signal_registry.hpp"

#include <algorithm>
#include <array>
#include <cstdint>
#include <fstream>
#include <iterator>
#include <limits>
#include <set>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace aoi::plc::w006 {
namespace {
constexpr std::array<std::string_view, 21> kSignalHead{{
    "SignalId", "PLCAddress", "StationId", "HostRole", "Direction", "DataType",
    "EffectiveWords", "PurposeCN", "ValueLegend", "ValidationState", "RiskNote",
    "EvidenceSource", "EditClass", "PhysicalDeviceId", "EvidenceLevel",
    "LastFieldVerifiedUtc", "ActivationAllowed", "ValueOrigin", "MonitorReadOnlyDefault",
    "ProductionWriteEnabled", "ConflictGroup"
}};
constexpr std::array<std::string_view, 8> kGateHead{{
    "GateId", "Topic", "Status", "SourceConflict", "RequiredEvidence",
    "ProposedOwner", "ProductionWriteAllowed", "FieldSignoff"
}};

// Small dependency-free SHA-256 implementation to protect the original locked
// CSV bytes; this is NOT a network security primitive or hardware credential.
constexpr std::array<std::uint32_t, 64> kShaRound{{
    0x428a2f98U,0x71374491U,0xb5c0fbcfU,0xe9b5dba5U,0x3956c25bU,0x59f111f1U,0x923f82a4U,0xab1c5ed5U,
    0xd807aa98U,0x12835b01U,0x243185beU,0x550c7dc3U,0x72be5d74U,0x80deb1feU,0x9bdc06a7U,0xc19bf174U,
    0xe49b69c1U,0xefbe4786U,0x0fc19dc6U,0x240ca1ccU,0x2de92c6fU,0x4a7484aaU,0x5cb0a9dcU,0x76f988daU,
    0x983e5152U,0xa831c66dU,0xb00327c8U,0xbf597fc7U,0xc6e00bf3U,0xd5a79147U,0x06ca6351U,0x14292967U,
    0x27b70a85U,0x2e1b2138U,0x4d2c6dfcU,0x53380d13U,0x650a7354U,0x766a0abbU,0x81c2c92eU,0x92722c85U,
    0xa2bfe8a1U,0xa81a664bU,0xc24b8b70U,0xc76c51a3U,0xd192e819U,0xd6990624U,0xf40e3585U,0x106aa070U,
    0x19a4c116U,0x1e376c08U,0x2748774cU,0x34b0bcb5U,0x391c0cb3U,0x4ed8aa4aU,0x5b9cca4fU,0x682e6ff3U,
    0x748f82eeU,0x78a5636fU,0x84c87814U,0x8cc70208U,0x90befffaU,0xa4506cebU,0xbef9a3f7U,0xc67178f2U
}};
constexpr std::uint32_t rr(std::uint32_t x, unsigned n) noexcept { return (x >> n) | (x << (32U-n)); }
std::string digest_sha256(std::string_view input) {
    std::vector<std::uint8_t> bytes;
    bytes.reserve(((input.size()+9U+63U)/64U)*64U);
    for (const unsigned char c : input) bytes.push_back(c);
    const std::uint64_t bit_length = static_cast<std::uint64_t>(input.size()) * 8U;
    bytes.push_back(0x80U);
    while ((bytes.size() % 64U) != 56U) bytes.push_back(0U);
    for (int k=7;k>=0;--k) bytes.push_back(static_cast<std::uint8_t>(bit_length >> (8*k)));
    std::array<std::uint32_t,8> h{{0x6a09e667U,0xbb67ae85U,0x3c6ef372U,0xa54ff53aU,
                                  0x510e527fU,0x9b05688cU,0x1f83d9abU,0x5be0cd19U}};
    for (std::size_t pos=0;pos<bytes.size();pos+=64U) {
        std::array<std::uint32_t,64> w{};
        for (std::size_t i=0;i<16U;++i) {
            const auto at=pos+4U*i;
            w[i]=(static_cast<std::uint32_t>(bytes[at])<<24U) |
                 (static_cast<std::uint32_t>(bytes[at+1U])<<16U) |
                 (static_cast<std::uint32_t>(bytes[at+2U])<<8U) |
                  static_cast<std::uint32_t>(bytes[at+3U]);
        }
        for (std::size_t i=16;i<64U;++i) {
            const auto s0=rr(w[i-15U],7U)^rr(w[i-15U],18U)^(w[i-15U]>>3U);
            const auto s1=rr(w[i-2U],17U)^rr(w[i-2U],19U)^(w[i-2U]>>10U);
            w[i]=w[i-16U]+s0+w[i-7U]+s1;
        }
        auto a=h[0],b=h[1],c=h[2],d=h[3],e=h[4],f=h[5],g=h[6],k=h[7];
        for (std::size_t i=0;i<64U;++i) {
            const auto s1=rr(e,6U)^rr(e,11U)^rr(e,25U);
            const auto ch=(e&f)^((~e)&g);
            const auto t1=k+s1+ch+kShaRound[i]+w[i];
            const auto s0=rr(a,2U)^rr(a,13U)^rr(a,22U);
            const auto maj=(a&b)^(a&c)^(b&c);
            const auto t2=s0+maj;
            k=g;g=f;f=e;e=d+t1;d=c;c=b;b=a;a=t1+t2;
        }
        h[0]+=a;h[1]+=b;h[2]+=c;h[3]+=d;h[4]+=e;h[5]+=f;h[6]+=g;h[7]+=k;
    }
    constexpr char chars[]="0123456789abcdef";
    std::string out;
    out.reserve(64U);
    for (const auto word : h) for (int n=7;n>=0;--n) {
        out.push_back(chars[(word>>(n*4))&15U]);
    }
    return out;
}

using Table = std::vector<std::vector<std::string>>;
struct CsvDecoded { bool ok{false}; Table rows{}; };
CsvDecoded strict_csv(std::string_view raw) {
    CsvDecoded result;
    if (raw.size()>=3U && static_cast<unsigned char>(raw[0])==0xefU &&
        static_cast<unsigned char>(raw[1])==0xbbU && static_cast<unsigned char>(raw[2])==0xbfU)
        raw.remove_prefix(3U);
    if (raw.empty() || raw.find('\0')!=std::string_view::npos) return result;
    enum class State { Start, Bare, Quoted, Closed };
    State state=State::Start;
    std::vector<std::string> cells;
    std::string cell;
    auto finish_cell=[&]() {cells.push_back(std::move(cell));cell.clear();state=State::Start;};
    auto finish_row=[&]() {
        finish_cell();
        result.rows.push_back(std::move(cells));
        cells.clear();
    };
    for (std::size_t i=0;i<raw.size();++i) {
        const char c=raw[i];
        if (state==State::Quoted) {
            if (c=='"') {
                if (i+1U<raw.size() && raw[i+1U]=='"') {cell.push_back('"');++i;}
                else state=State::Closed;
            } else cell.push_back(c);
            continue;
        }
        if (c==',' || c=='\r' || c=='\n') {
            if (c==',') finish_cell();
            else {
                finish_row();
                if (c=='\r' && i+1U<raw.size() && raw[i+1U]=='\n') ++i;
            }
            continue;
        }
        if (c=='"') {
            if (state!=State::Start) return CsvDecoded{};
            state=State::Quoted;
            continue;
        }
        if (state==State::Closed) return CsvDecoded{};
        cell.push_back(c);
        state=State::Bare;
    }
    if (state==State::Quoted) return CsvDecoded{};
    if (state!=State::Start || !cells.empty()) finish_row();
    if (result.rows.empty()) return CsvDecoded{};
    result.ok=true;
    return result;
}

template <std::size_t N>
bool valid_header(const Table& rows, const std::array<std::string_view,N>& header) {
    if (rows.empty() || rows[0].size()!=N) return false;
    for (std::size_t i=0;i<N;++i) if (rows[0][i]!=header[i]) return false;
    return true;
}

bool valid_signal(const SignalFact& fact) {
    const auto id=fact.id();
    if (id.empty() || id.size()>128U || fact.at(SignalField::PLCAddress).empty() ||
        fact.at(SignalField::EvidenceSource).empty() || fact.at(SignalField::PurposeCN).empty()) return false;
    if (fact.at(SignalField::ProductionWriteEnabled)!="FALSE" ||
        fact.at(SignalField::MonitorReadOnlyDefault)!="TRUE" ||
        !fact.at(SignalField::LastFieldVerifiedUtc).empty()) return false;
    if (const auto a=fact.at(SignalField::ActivationAllowed);
        a!="FALSE" && a!="PENDING_FIELD_VERIFY") return false;
    const auto host=fact.at(SignalField::HostRole);
    if (host!="MASTER" && host!="SLAVE" && host!="PLC") return false;
    const auto dir=fact.at(SignalField::Direction);
    if (dir!="PC_TO_PLC" && dir!="PLC_REQUEST/PC_REPLY" && dir!="PLC_TO_PC" &&
        dir!="UNVERIFIED" && dir!="PLC_READS" && dir!="BIDIR" &&
        dir!="DECLARATION_ONLY" && dir!="PLC_ONLY") return false;
    const auto dt=fact.at(SignalField::DataType);
    const auto words=fact.at(SignalField::EffectiveWords);
    if (!((dt=="WORD" && words=="1") ||
          ((dt=="FLOAT32" || dt=="FLOAT32_BADC") && words=="2") ||
          ((dt=="WORD_ARRAY" || dt=="BA_STRING") && words=="20") ||
          (dt=="FLOAT32_ARRAY" && words=="6"))) return false;
    const auto vs=fact.at(SignalField::ValidationState);
    if (vs!="BLOCKED_CONFLICT" && vs!="LEGACY_STATIC_NEEDS_FIELD" &&
        vs!="DECLARATION_ONLY" && vs!="PLC_SOURCE_ONLY") return false;
    // Preserve all legacy ambiguity; no input may turn a conflicted row active.
    if ((vs=="BLOCKED_CONFLICT") != !fact.at(SignalField::ConflictGroup).empty()) return false;
    return true;
}

bool valid_gate(const FieldGate& gate) {
    const auto stat=gate.at(GateField::Status);
    return !gate.id().empty() && !gate.at(GateField::Topic).empty() &&
        (stat=="OPEN" || stat=="BLOCKED") &&
        gate.at(GateField::ProductionWriteAllowed)=="FALSE" &&
        gate.at(GateField::FieldSignoff).empty();
}

ImportResult reject(ImportStatus s, std::string_view diagnostic) {
    ImportResult out;
    out.status=s;
    out.diagnostic=std::string(diagnostic);
    return out;
}

bool read_file(const std::string& path, std::string& bytes) {
    std::ifstream in(path,std::ios::binary);
    if (!in.good()) return false;
    in.seekg(0,std::ios::end);
    const auto size=in.tellg();
    if (size<=0 || size>1000000) return false;
    in.seekg(0,std::ios::beg);
    bytes.resize(static_cast<std::size_t>(size));
    in.read(bytes.data(),static_cast<std::streamsize>(bytes.size()));
    return in.gcount()==static_cast<std::streamsize>(bytes.size()) && !in.bad();
}
} // namespace

ImportResult LockedSignalRegistry::from_locked_bytes(std::string_view signals,
                                                       std::string_view gates) {
    if (signals.empty() || gates.empty() || signals.size()>1000000U || gates.size()>1000000U)
        return reject(ImportStatus::MissingInput,"missing or oversized immutable v2 CSV");
    // Always check full original source bytes before trusting even a single column.
    if (digest_sha256(signals)!=signal_source_sha256() ||
        digest_sha256(gates)!=gate_source_sha256())
        return reject(ImportStatus::ChecksumMismatch,"immutable v2 original SHA256 mismatch");
    auto s=strict_csv(signals),g=strict_csv(gates);
    if (!s.ok || !g.ok) return reject(ImportStatus::CsvFormatError,"malformed locked CSV");
    if (!valid_header(s.rows,kSignalHead) || !valid_header(g.rows,kGateHead))
        return reject(ImportStatus::SchemaMismatch,"changed field order or CSV header");
    if (s.rows.size()!=46U || g.rows.size()!=15U)
        return reject(ImportStatus::InvalidSourceFacts,"must contain exactly 45 signals and 14 FG");
    std::vector<SignalFact> signal_rows;
    std::vector<FieldGate> gate_rows;
    signal_rows.reserve(45U);gate_rows.reserve(14U);
    std::set<std::string> ids,fg_ids;
    for (std::size_t i=1;i<s.rows.size();++i) {
        if (s.rows[i].size()!=kSignalHead.size())
            return reject(ImportStatus::CsvFormatError,"invalid signal CSV column count");
        SignalFact fact;
        for (std::size_t j=0;j<kSignalHead.size();++j) fact.original[j]=std::move(s.rows[i][j]);
        if (!valid_signal(fact) || !ids.insert(std::string(fact.id())).second)
            return reject(ImportStatus::InvalidSourceFacts,"invalid/duplicated signal or prohibited write");
        signal_rows.push_back(std::move(fact));
    }
    for (std::size_t i=1;i<g.rows.size();++i) {
        if (g.rows[i].size()!=kGateHead.size())
            return reject(ImportStatus::CsvFormatError,"invalid field gate CSV column count");
        FieldGate gate;
        for (std::size_t j=0;j<kGateHead.size();++j) gate.original[j]=std::move(g.rows[i][j]);
        if (!valid_gate(gate) || !fg_ids.insert(std::string(gate.id())).second)
            return reject(ImportStatus::InvalidSourceFacts,"invalid/duplicated field gate or write grant");
        gate_rows.push_back(std::move(gate));
    }
    const auto blocked=std::count_if(gate_rows.begin(),gate_rows.end(),[](const auto& e){return e.at(GateField::Status)=="BLOCKED";});
    if (blocked!=7 || ids.size()!=45U || fg_ids.size()!=14U)
        return reject(ImportStatus::InvalidSourceFacts,"field gates must be 7 BLOCKED + 7 OPEN");
    ImportResult accepted;
    accepted.status=ImportStatus::Ok;
    accepted.registry.emplace(LockedSignalRegistry(std::move(signal_rows),std::move(gate_rows)));
    accepted.diagnostic="STATIC_ONLY; ALL_FIELD_MEANINGS_UNKNOWN; NO_PRODUCTION_WRITE";
    return accepted;
}

ImportResult LockedSignalRegistry::from_locked_files(const std::string& signals,
                                                       const std::string& gates) {
    std::string raw_signals,raw_gates;
    if (!read_file(signals,raw_signals) || !read_file(gates,raw_gates))
        return reject(ImportStatus::MissingInput,"cannot read bounded immutable CSV files");
    return from_locked_bytes(raw_signals,raw_gates);
}
const SignalFact* LockedSignalRegistry::find_signal(std::string_view id) const noexcept {
    for (const auto& item:signals_) if (item.id()==id) return &item;
    return nullptr;
}
const FieldGate* LockedSignalRegistry::find_gate(std::string_view id) const noexcept {
    for (const auto& item:gates_) if (item.id()==id) return &item;
    return nullptr;
}
std::vector<const SignalFact*> LockedSignalRegistry::find_by_address(std::string_view address) const {
    std::vector<const SignalFact*> result;
    for (const auto& item:signals_) if (item.at(SignalField::PLCAddress)==address) result.push_back(&item);
    return result;
}
std::size_t LockedSignalRegistry::blocked_gates() const noexcept {
    return static_cast<std::size_t>(std::count_if(gates_.begin(),gates_.end(),[](const FieldGate& g){return g.at(GateField::Status)=="BLOCKED";}));
}
std::size_t LockedSignalRegistry::open_gates() const noexcept {
    return static_cast<std::size_t>(std::count_if(gates_.begin(),gates_.end(),[](const FieldGate& g){return g.at(GateField::Status)=="OPEN";}));
}
WriteDecision WritePolicy::assess(const LockedSignalRegistry* registry,
                                  const WriteIntent& intent) noexcept {
    if (!registry) return {false,DenialReason::MissingRegistry};
    const auto* signal=registry->find_signal(intent.signal_id);
    if (!signal) return {false,DenialReason::UnknownSignal};
    // Historic aliases and outgoing quality/scan handshakes must remain UNKNOWN.
    if (signal->has_conflict() || intent.signal_id=="SIG_FINAL_QUALITY" ||
        intent.signal_id=="SIG_DOWNSTREAM_REQUEST")
        return {false,DenialReason::UnknownOrConflictedMapping};
    if (intent.endpoint!="127.0.0.1" && intent.endpoint!="::1" &&
        intent.endpoint!="[::1]") return {false,DenialReason::NonLoopbackTarget};
    if (!intent.claims_approval || intent.approval_age_ms>300000U)
        return {false,DenialReason::MissingOrExpiredApproval};
    // No grant path exists, even for ADMIN and an apparently current approval.
    return {false,DenialReason::ProductionWriteDisabled};
}
} // namespace aoi::plc::w006
