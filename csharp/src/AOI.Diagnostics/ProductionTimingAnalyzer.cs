namespace AOI.Diagnostics;

/// <summary>
/// A5-facing observed-vs-expected replay: matches ONLY direct, persisted milestones.
/// Ordering/latency use Stopwatch ticks only inside one process run; cross-Core UTC
/// timestamps are display evidence, not a shared monotonic stopwatch.
/// </summary>
public static class ProductionTimingAnalyzer
{
    public static TimingComparison Compare(Guid workpieceId, IReadOnlyList<TimingExpectation> expected,
        IReadOnlyList<TimingEvent> actual)
    {
        if (workpieceId == Guid.Empty) throw new ArgumentException("A WorkpieceId is required.");
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var events = actual.Where(e => e.Origin == "DIRECT_OBSERVATION" &&
            e.WorkpieceId == workpieceId).OrderBy(e => e.Utc)
            .ThenBy(e => e.NodeId, StringComparer.Ordinal).ThenBy(e => e.Sequence).ToArray();
        var used = new HashSet<Guid>();
        var differences = new List<TimingDifference>();
        int matches = 0;
        TimingEvent? previous = null;
        for (int i = 0; i < expected.Count; i++)
        {
            var target = expected[i];
            if (target.MaxSincePrevious is { } max && max <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(expected), "Deadlines must be positive.");
            bool MainFilter(TimingEvent e) => !used.Contains(e.EventId) &&
                e.Stage == target.Stage &&
                (target.NodeId is null || e.NodeId == target.NodeId) &&
                (target.CaptureId is null || e.CaptureId == target.CaptureId) &&
                (target.PhysicalDeviceId is null || e.PhysicalDeviceId == target.PhysicalDeviceId) &&
                (target.ChannelId is null || e.ChannelId == target.ChannelId) &&
                (target.AttemptNo is null || e.AttemptNo == target.AttemptNo) &&
                (target.PlcDmAddress is null || e.PlcDmAddress == target.PlcDmAddress);

            var candidates = events.Where(MainFilter).ToArray();
            var hit = candidates.FirstOrDefault(e =>
                target.PlcValue is null || e.PlcValue == target.PlcValue);
            if (hit is null)
            {
                var mismatch = candidates.FirstOrDefault();
                differences.Add(new TimingDifference(
                    mismatch is null ? "EXPECTED_EVENT_NOT_OBSERVED" : "PLC_VALUE_MISMATCH",
                    "ERROR", i, target.Stage, mismatch?.EventId,
                    mismatch is null
                        ? $"No direct persisted {target.Stage}; legacy Core events do not prove this milestone."
                        : $"PLC value expected {target.PlcValue}, observed {mismatch.PlcValue} at {mismatch.PlcDmAddress}."));
                continue;
            }
            used.Add(hit.EventId);
            matches++;
            if (previous is not null)
            {
                if (Elapsed(previous, hit) is { } duration)
                {
                    if (duration < TimeSpan.Zero)
                        differences.Add(new("OUT_OF_ORDER", "ERROR", i, target.Stage, hit.EventId,
                            $"{target.Stage} was observed before preceding expected milestone in the same process."));
                    else if (target.MaxSincePrevious is { } limit && duration > limit)
                        differences.Add(new("DEADLINE_EXCEEDED", "ERROR", i, target.Stage, hit.EventId,
                            $"{duration.TotalMilliseconds:F1}ms elapsed; budget {limit.TotalMilliseconds:F1}ms."));
                }
                else
                {
                    differences.Add(new("CROSS_PROCESS_CLOCK_UNVERIFIED", "WARNING", i, target.Stage, hit.EventId,
                        "Cannot prove relative ordering/deadline across independent process runs without clock-sync evidence."));
                }
            }
            previous = hit;
        }
        return new TimingComparison(workpieceId, expected.Count, matches, differences.ToArray());
    }

    /// <summary>Null means incomparable clocks; NEVER compare Stopwatch timestamps across processes.</summary>
    public static TimeSpan? Elapsed(TimingEvent first, TimingEvent last)
    {
        if (first.Origin != "DIRECT_OBSERVATION" || last.Origin != "DIRECT_OBSERVATION" ||
            first.ProcessRunId is not Guid run || run == Guid.Empty ||
            first.ProcessRunId != last.ProcessRunId ||
            first.TickFrequency is not long hertz || hertz <= 0 ||
            first.TickFrequency != last.TickFrequency ||
            first.MonotonicTick is not long start || last.MonotonicTick is not long end)
            return null;
        return TimeSpan.FromSeconds((end - (double)start) / hertz);
    }

    /// <summary>
    /// Infer ONLY defects proven by recorded evidence. Do NOT infer that an unseen
    /// PLC request/physical trigger never physically happened: it may be uninstrumented.
    /// Missing milestones are reported by Compare() against an explicit A5 expectation.
    /// </summary>
    public static TimingDifference[] Diagnose(
        IReadOnlyList<TimingEvent> events, bool truncated = false,
        DateTimeOffset? evaluatedUtc = null,
        TimeSpan? frameDeadline = null, TimeSpan? camDoneDeadline = null,
        TimeSpan? inspectionDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var now = evaluatedUtc ?? DateTimeOffset.UtcNow;
        var frameBudget = frameDeadline ?? TimeSpan.FromSeconds(10);
        var doneBudget = camDoneDeadline ?? TimeSpan.FromSeconds(2);
        var inspectionBudget = inspectionDeadline ?? TimeSpan.FromSeconds(20);
        if (frameBudget <= TimeSpan.Zero || doneBudget <= TimeSpan.Zero ||
            inspectionBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(frameDeadline), "Timing budgets must be positive.");
        var found = new List<TimingDifference>();
        void Error(string code, TimingEvent e, string detail) =>
            found.Add(new TimingDifference(code, "ERROR", -1, e.Stage, e.EventId, detail));

        var direct = events.Where(e => e.Origin == "DIRECT_OBSERVATION").ToArray();
        foreach (var capture in direct.Where(e => e.CaptureId is not null)
            .GroupBy(e => (e.NodeId, e.WorkpieceId, e.CaptureId,
                e.PhysicalDeviceId, e.AttemptNo)))
        {
            var list = capture.ToArray();
            var ready = list.FirstOrDefault(e => e.Stage == TimingStage.FrameReady);
            var done = list.Where(e => e.Stage == TimingStage.CamDoneAcknowledged).ToArray();
            var inspection = list.Where(e => e.Stage == TimingStage.InspectionCompleted).ToArray();
            var request = list.FirstOrDefault(e => e.Stage == TimingStage.PlcCaptureRequestObserved);
            var arrived = list.FirstOrDefault(e => e.Stage == TimingStage.FrameArrived);
            var sentDone = list.FirstOrDefault(e => e.Stage == TimingStage.CamDoneSent);
            bool hasFailure = list.Any(e => e.Stage is
                TimingStage.CaptureFailed or TimingStage.CaptureTimedOut);
            if (request is not null && arrived is null && !hasFailure &&
                now - request.Utc > frameBudget)
                Error("FRAME_NOT_ARRIVED_BEFORE_DEADLINE", request,
                    $"Request aged beyond {frameBudget.TotalMilliseconds:F0}ms without an observed frame; UTC-age estimate.");
            if (ready is not null && done.Length == 0 && !hasFailure &&
                now - (sentDone?.Utc ?? ready.Utc) > doneBudget)
                Error(sentDone is null ? "CAMDONE_NOT_SENT_BEFORE_DEADLINE" : "CAMDONE_ACK_TIMEOUT",
                    sentDone ?? ready,
                    $"No confirmed CamDone by {doneBudget.TotalMilliseconds:F0}ms; UTC-age estimate.");
            if (done.Any(e => e.PlcValue == 2) && inspection.Length == 0 && !hasFailure &&
                now - done.Max(e => e.Utc) > inspectionBudget)
                Error("INSPECTION_RESULT_TIMEOUT", done[^1],
                    $"CamDone confirmed but inspection is not completed within {inspectionBudget.TotalMilliseconds:F0}ms.");
            foreach (var acknowledgement in done)
            {
                var sent = list.FirstOrDefault(e =>
                    e.Stage == TimingStage.CamDoneSent && e.OperationId == acknowledgement.OperationId);
                if (sent is null)
                    Error("UNPAIRED_CAMDONE_ACK", acknowledgement,
                        "CamDone acknowledgement has no matching recorded FINS write operation.");
                if (ready is null && acknowledgement.PlcValue == 2)
                    Error("CAMDONE_OK_WITHOUT_FRAME_READY", acknowledgement,
                        "CamDone=2 observed without a directly recorded FrameReady for this capture.");
                if (ready is not null && Elapsed(ready, acknowledgement) is { } delta &&
                    delta < TimeSpan.Zero)
                    Error("CAMDONE_BEFORE_FRAME_READY", acknowledgement,
                        "CamDone reply precedes FrameReady within the same process run.");
            }
            foreach (var result in inspection)
            {
                if (ready is null && result.Status is "OK" or "NG")
                    Error("INSPECTION_WITHOUT_FRAME", result,
                        "An OK/NG algorithm result was observed without a corresponding FrameReady.");
                if (ready is not null && Elapsed(ready, result) is { } delta &&
                    delta < TimeSpan.Zero)
                    Error("INSPECTION_BEFORE_FRAME_READY", result,
                        "Inspection completed before the frame became ready.");
            }
        }

        // One FrameId must not be attributed to different captures/workpieces.
        foreach (var group in direct.Where(e => e.FrameId is not null &&
                e.Stage is TimingStage.FrameArrived or TimingStage.FrameReady)
            .GroupBy(e => e.FrameId))
        {
            if (group.Select(e => (e.WorkpieceId, e.CaptureId, e.PhysicalDeviceId))
                .Distinct().Count() > 1)
                Error("FRAME_REUSED_ACROSS_CAPTURES", group.First(),
                    "One immutable FrameId was bound to multiple capture/physical item identities.");
        }
        foreach (var response in direct.Where(e => e.Stage is
            TimingStage.FinsWriteAcknowledged or TimingStage.FinalPlcWriteAcknowledged))
        {
            var matchingSendStage = response.Stage == TimingStage.FinalPlcWriteAcknowledged
                ? TimingStage.FinalPlcWriteSent : TimingStage.FinsWriteSent;
            if (!direct.Any(e => e.Stage == matchingSendStage &&
                e.NodeId == response.NodeId && e.ProcessRunId == response.ProcessRunId &&
                e.OperationId == response.OperationId &&
                e.PlcDmAddress == response.PlcDmAddress && e.PlcValue == response.PlcValue))
                Error("UNPAIRED_FINS_WRITE_ACK", response, "No matching transmitted FINS write evidence.");
        }

        // Specific old-machine PLC dual write order is checked on ACKNOWLEDGED writes,
        // not merely virtual PLC memory snapshots.
        var finals = direct.Where(e => e.Stage == TimingStage.FinalPlcWriteAcknowledged &&
            e.PlcDmAddress is "D1016" or "D2099").ToArray();
        foreach (var group in finals.GroupBy(e => (e.NodeId,e.WorkpieceId,e.ProcessRunId)))
        {
            var d1016 = group.FirstOrDefault(e => e.PlcDmAddress == "D1016");
            var d2099 = group.FirstOrDefault(e => e.PlcDmAddress == "D2099");
            if (d1016 is null || d2099 is null) continue;
            var time = Elapsed(d1016, d2099);
            if (time is null) continue;
            bool valid = d2099.PlcValue switch
            {
                2 => d1016.PlcValue is 1 or 2 && time.Value > TimeSpan.Zero,
                3 or 4 => d1016.PlcValue == 2 && time.Value < TimeSpan.Zero,
                _ => false
            };
            if (!valid)
                Error("LEGACY_FINAL_FINS_ORDER_MISMATCH", d2099,
                    $"Actual ACK sequence D1016={d1016.PlcValue}; D2099={d2099.PlcValue}; " +
                    $"D1016→D2099 monotonic delta {time.Value.TotalMilliseconds:F1}ms.");
        }
        if (truncated)
            found.Add(new TimingDifference("TIMELINE_TRUNCATED", "WARNING", -1,
                TimingStage.CoreEventPersisted, null,
                "Read limit reached; missing milestones cannot be concluded from a truncated timeline."));
        return found.OrderBy(x => x.Code, StringComparer.Ordinal)
            .ThenBy(x => x.ActualEventId).ToArray();
    }
}
