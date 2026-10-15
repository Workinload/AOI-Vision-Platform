#include <aoi/domain/contract.hpp>
#include <cassert>
#include <iostream>
#include <type_traits>
using namespace aoi::domain;
static_assert(!std::is_convertible_v<ReleaseId, WorkpieceId>);
static_assert(!std::is_convertible_v<ChannelId, CaptureId>);
static Result result(const DecisionInput& in, std::string ch, QualityStatus q) {
    return Result{Correlation{in.workpiece, in.epoch, in.release, ChannelId(std::move(ch)), CaptureId("capture-1"),1},ProcessingStatus::Succeeded,q,false};
}
int main() {
    DecisionInput in{WorkpieceId("wp1"),1,ReleaseId("rel1"),BarcodeStatus::Matched,{ChannelId("CH02"),ChannelId("CH03")},{}};
    assert(assess(in).outcome == FinalOutcome::HoldUndecided); // empty does not mean OK
    in.results.push_back(result(in,"CH02",QualityStatus::Ok));
    assert(assess(in).outcome == FinalOutcome::HoldUndecided); // missing channel
    in.results.push_back(result(in,"CH03",QualityStatus::Ng));
    assert(assess(in).outcome == FinalOutcome::Ng);
    in.results[1].quality = QualityStatus::Ok;
    assert(assess(in).outcome == FinalOutcome::Ok);
    in.results[1].processing = ProcessingStatus::Timeout;
    assert(assess(in).outcome == FinalOutcome::HoldUndecided);
    in.results[1].processing = ProcessingStatus::Succeeded;
    in.results[1].key.epoch = 99;
    assert(assess(in).outcome == FinalOutcome::HoldUndecided);
    in.results[1].key.epoch = 1;
    in.results.push_back(in.results[1]);
    assert(assess(in).outcome == FinalOutcome::HoldUndecided);
    in.results.pop_back();
    in.barcode = BarcodeStatus::Unverified;
    assert(assess(in).outcome == FinalOutcome::HoldUndecided);
    in.barcode = BarcodeStatus::Mismatched;
    assert(assess(in).outcome == FinalOutcome::BarcodeMismatch);
    std::cout << "PASS: 9 M00 contract/safety assertions (simulation draft)\n";
}
