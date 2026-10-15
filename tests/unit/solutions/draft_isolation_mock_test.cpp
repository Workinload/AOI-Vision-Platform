// Standalone isolated C++20 negative tests; no Qt / PLC / cross-agent contract.
#include "../../../libs/solutions/draft_isolation_mock.hpp"
#include <cassert>
#include <iostream>
#include <string>

using namespace aoi::a7::research;
static StationDraft mk(std::string product, std::string roi, double threshold) {
    return {std::move(product), "CH02", std::string(64, 'a'), std::string(64, 'b'),
            std::move(roi), threshold, 1};
}
int main() {
    DraftStore store;
    assert(store.seed_active(mk("A", "ROI-A", 0.1)));
    assert(store.seed_active(mk("B", "ROI-B", 0.9)));
    assert(store.patch(mk("A", "ROI-A2", 0.3), 1) == PatchResult::Applied);
    assert(store.draft("A", "CH02")->revision == 2);
    assert(store.draft("A", "CH02")->detection_roi == "ROI-A2");
    assert(store.active("A", "CH02")->detection_roi == "ROI-A");
    assert(store.active("B", "CH02")->threshold == 0.9);
    assert(!store.draft("B", "CH02").has_value());
    assert(store.patch(mk("A", "stale", 0.5), 1) == PatchResult::Conflict);
    assert(store.draft("A", "CH02")->detection_roi == "ROI-A2");
    auto missing_model = mk("C", "ROI-C", 0.4); missing_model.model_sha256.clear();
    assert(store.patch(missing_model, 0) == PatchResult::Invalid);
    auto bad_roi = mk("C", "", 0.4);
    assert(store.patch(bad_roi, 0) == PatchResult::Invalid);
    auto invalid_threshold = mk("C", "ROI-C", 1.1);
    assert(store.patch(invalid_threshold, 0) == PatchResult::Invalid);
    assert(!store.can_publish());
    assert(!store.can_deploy());
    std::cout << "A7_RESEARCH_MOCK_PASS 11 assertions groups; SIMULATED; no production integration\n";
}
