using AOI.Contracts;
using AOI.Domain;

namespace AOI.Ui.Client;

/// <summary>
/// A fact-based projection of the existing read-only Core snapshot.
/// v0.1 has channel results, not PLC requests, FrameReady or FINS CamDone receipts.
/// A missing event is shown as unavailable, never inferred from an OK result.
/// </summary>
public sealed record CaptureDebugRow(
    string ChannelId, string StationId, string PhysicalDeviceId, string PlcAddress,
    string OwnerNode, string RequestEvidence, string SensorEvidence, string FrameReadyEvidence,
    string PngEvidence, string CamDoneEvidence, string InspectionResult,
    string ResultUtc, string Error);

/// <summary>Image evidence belongs to an immutable WorkpieceId + ChannelId pair.</summary>
public sealed record CapturePngEvidence(
    Guid WorkpieceId, string ChannelId, bool Verified, int ByteCount,
    string Message, DateTimeOffset CheckedUtc);

public sealed record CaptureDebugEvent(
    Guid WorkpieceId, string ChannelId, string PlcAddress,
    string Kind, string Detail, string Source, DateTimeOffset? SourceUtc,
    DateTimeOffset ObservedUtc)
{
    public string ShortWorkpiece => WorkpieceId.ToString("N")[..8];
    public string SourceTime => SourceUtc?.ToUniversalTime().ToString("HH:mm:ss.fff 'UTC'")
        ?? "源时间未提供";
    public string ObservedTime => ObservedUtc.ToUniversalTime().ToString("HH:mm:ss.fff 'UTC'");
}

public static class CaptureDebugProjection
{
    public const string NotReported = "IPC未提供";
    public static IReadOnlyList<CaptureDebugRow> ForWorkpiece(
        MonitorProjection snapshot, Guid? workpieceId,
        IReadOnlyDictionary<string, CapturePngEvidence>? checkedPng = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool isLatest = workpieceId is not null &&
            snapshot.Workpiece?.WorkpieceId == workpieceId.Value;
        var conveyorItem = workpieceId is null ? null :
            snapshot.Conveyor?.Active.FirstOrDefault(x => x.WorkpieceId == workpieceId.Value);
        return ChannelCatalog.All.Select(channel =>
        {
            var state = snapshot.Channels.FirstOrDefault(x =>
                x.Definition.ChannelId == channel.ChannelId);
            bool confirmedForThisWorkpiece = isLatest && state?.IsCurrentWorkpiece == true &&
                state.IsSimulated && state.UpdatedUtc is not null;
            string result = confirmedForThisWorkpiece ? state!.Status
                : isLatest ? "本件结果未入库" : "历史逐通道结果未开放IPC";
            string at = confirmedForThisWorkpiece
                ? state!.UpdatedUtc!.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'")
                : "—";
            var sensor = conveyorItem?.StationId == channel.StationId
                ? conveyorItem.SensorTriggered ? "SIM传感器状态=触发" : "SIM传感器状态=未触发"
                : "该工件未在此站 / 无在途IPC";

            // A verified PNG is NOT proof of camera trigger or CamDone order.
            string png = "尚未请求PNG核验";
            string error = confirmedForThisWorkpiece &&
                (state!.Status.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                 state.Status.Contains("MISSING", StringComparison.OrdinalIgnoreCase) ||
                 state.Status.Contains("UNCERTAIN", StringComparison.OrdinalIgnoreCase))
                ? "Core通道异常：" + state.Status : "";
            if (workpieceId is not null && checkedPng is not null &&
                checkedPng.TryGetValue(channel.ChannelId, out var proof) &&
                proof.WorkpieceId == workpieceId.Value)
            {
                png = proof.Verified
                    ? "Core SHA256已校验 · " + proof.ByteCount + " bytes"
                    : "PNG不可用 · " + proof.Message;
                if (!proof.Verified)
                    error = string.IsNullOrEmpty(error) ? proof.Message : error + " / " + proof.Message;
            }

            return new CaptureDebugRow(channel.ChannelId, channel.StationId,
                channel.PhysicalDeviceId, channel.PlcSignalAddress, channel.OwnerRole.ToString(),
                NotReported + " (PLC请求)", sensor, NotReported + " (FrameReady)",
                png, NotReported + " (FINS CamDone)", result, at, error);
        }).ToArray();
    }
}

/// <summary>
/// Bounded local OBSERVATION LOG, not a substitute for A1/A2/A7 persisted event trace.
/// Records exactly what a trusted Core snapshot changed; never asserts unobserved PLC order.
/// </summary>
public sealed class CaptureSnapshotObserver(int capacity = 180)
{
    private readonly int _capacity = capacity > 0 ? capacity :
        throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly Queue<(string Key, CaptureDebugEvent Event)> _recent = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IReadOnlyList<CaptureDebugEvent> Recent(Guid? workpieceId = null) =>
        _recent.Reverse().Select(x => x.Event)
            .Where(x => workpieceId is null || x.WorkpieceId == workpieceId.Value).ToArray();

    public void Observe(MonitorProjection snapshot, DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsOnline) return;

        if (snapshot.Workpiece is { } workpiece)
            Add("workpiece|" + workpiece.WorkpieceId,
                new CaptureDebugEvent(workpiece.WorkpieceId, "—", "D1999",
                    "WORKPIECE_IN_SNAPSHOT",
                    "Core 最新工件 " + workpiece.Barcode + " / Release " + workpiece.ReleaseId,
                    "Core WorkpieceIdentity", workpiece.CreatedAtUtc, observedUtc));

        if (snapshot.Conveyor is { } conveyor)
        {
            foreach (var item in conveyor.Active)
            {
                var signature = item.WorkpieceId + "|" + item.StationId + "|" +
                    item.SensorTriggered + "|" + item.Phase;
                Add("conveyor|" + signature, new CaptureDebugEvent(item.WorkpieceId,
                    "—", "—", "SIM_SENSOR_SNAPSHOT",
                    item.StationId + " / " + item.Phase +
                    (item.SensorTriggered ? " / sensor=true" : " / sensor=false"),
                    "Core SIM_CONVEYOR (not PLC request)", null, observedUtc));
            }
        }

        if (snapshot.Workpiece is { } latest)
        {
            foreach (var channel in snapshot.Channels)
            {
                if (!channel.IsCurrentWorkpiece || !channel.IsSimulated ||
                    channel.UpdatedUtc is null || !channel.Status.StartsWith("SIM_", StringComparison.Ordinal))
                    continue;
                var def = channel.Definition;
                Add("channel|" + latest.WorkpieceId + "|" + def.ChannelId + "|" +
                    channel.Status + "|" + channel.UpdatedUtc.Value.UtcTicks,
                    new CaptureDebugEvent(latest.WorkpieceId, def.ChannelId,
                        def.PlcSignalAddress, "CHANNEL_RESULT_SNAPSHOT", channel.Status,
                        "Core ChannelSnapshot (not FrameReady/CamDone)",
                        channel.UpdatedUtc, observedUtc));
            }
            if (snapshot.Final is { } final)
                Add("final|" + latest.WorkpieceId + "|" + final.Disposition + "|" + final.Proof,
                    new CaptureDebugEvent(latest.WorkpieceId, "—", "D1016 / D2099",
                        "FINAL_SNAPSHOT", final.Disposition + " / " + final.Proof,
                        "Core FinalDecision (not FINS write order)", null, observedUtc));
        }

        if (snapshot.Holds is { } holds)
        {
            foreach (var hold in holds.Recent)
                Add("hold|" + hold.WorkpieceId + "|" + hold.ReasonCode + "|" + hold.RaisedUtc.UtcTicks,
                    new CaptureDebugEvent(hold.WorkpieceId, "—", "—",
                        "CORE_HOLD", hold.ReasonCode + " / " + hold.Detail,
                        "Core WAL hold", hold.RaisedUtc, observedUtc));
        }
    }

    private void Add(string key, CaptureDebugEvent value)
    {
        if (!_seen.Add(key)) return;
        _recent.Enqueue((key, value));
        while (_recent.Count > _capacity)
        {
            var removed = _recent.Dequeue();
            _seen.Remove(removed.Key);
        }
    }

    public void Clear()
    {
        _seen.Clear();
        _recent.Clear();
    }
}
