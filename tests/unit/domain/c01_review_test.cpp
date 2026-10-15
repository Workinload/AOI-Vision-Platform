#include "aoi/domain/c01_review.hpp"
#include <cassert>
#include <type_traits>
using namespace aoi::domain::c01_review;
static_assert(!std::is_same_v<WorkpieceId, ReleaseId>);
static_assert(!std::is_convertible_v<WorkpieceId, ReleaseId>);
static_assert(!std::is_same_v<CaptureId, ParentCaptureId>);
int main() {
    WorkpieceResultIdentity ok{
        WorkpieceId{"WP-1"}, Epoch{2}, ReleaseId{"REL-1"}, ChannelId{"CH08"},
        CaptureId{"CAP-1"}, AttemptNo{1}, PhysicalDeviceId{"L1"}, CaptureGroupId{"GROUP-1"}};
    assert(ok.valid());
    assert(validate_result(ok, ProcessingStatus::Succeeded, QualityStatus::Ng)==SafetyDecision::Accept);
    assert(validate_result(ok, ProcessingStatus::Error, QualityStatus::Ok)==SafetyDecision::RejectStatePair);
    assert(validate_result(ok, ProcessingStatus::Timeout, QualityStatus::Ng)==SafetyDecision::RejectStatePair);
    assert(validate_result(ok, ProcessingStatus::Pending, QualityStatus::Undecided)==SafetyDecision::Accept);
    ok.attempt.value=0;
    assert(validate_result(ok, ProcessingStatus::Succeeded, QualityStatus::Ok)==SafetyDecision::RejectIdentity);
    ok.attempt.value=1;
    ok.channel=ChannelId{"CH12"};
    assert(!ok.valid());
    ok.channel=ChannelId{"CH11"};
    assert(ok.valid());
    assert(!ChannelId{" CH01"}.valid());
    assert((!ParentFrameIdentity{HostId{"FOREIGN"}, CaptureGroupId{"CG"}, PhysicalDeviceId{"CAM"}, AttemptNo{1}}.valid()));
    assert((ParentFrameIdentity{HostId{"MASTER"}, CaptureGroupId{"CG"}, PhysicalDeviceId{"CAM"}, AttemptNo{1}}.valid()));
    MessageIdentity envelope{MessageId{"M1"},EventId{"E1"},TraceId{"T1"},HostId{"MASTER"},Epoch{1},SequenceNo{1},"C01-REVIEW-0.2",std::string(64,'a')};
    assert(envelope.shape_valid());
    envelope.payload_sha256[0]='Z';
    assert(!envelope.shape_valid());
    envelope.payload_sha256[0]='a';
    envelope.schema_version="C01-FROZEN";
    assert(!envelope.shape_valid());
    return 0;
}
