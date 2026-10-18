using AOI.Contracts;
using AOI.Domain;
using System.Globalization;

namespace AOI.Ui.Client;

public sealed record OperatorTimingTile(
    string EventUtc, string NodeId, string Stage, string Provenance,
    string? ChannelId, string ChannelDevice, string PlcEvidence, string CaptureIdentity,
    string Status)
{
    public bool IsDirect => Provenance == "DIRECT_OBSERVATION";
}

public sealed record OperatorTimingFindingTile(string Severity, string Code, string Detail);

public sealed record OperatorTimingDisplay(
    string Summary, IReadOnlyList<OperatorTimingTile> Events,
    IReadOnlyList<OperatorTimingFindingTile> Findings);

/// <summary>
/// Turns immutable A7 timing evidence into operator-readable rows.
/// Sent != ACK; FrameArrived != FrameReady; cross-node UTC is not a common clock.
/// Only direct observations may populate handshake evidence.
/// </summary>
public static class OperatorTimingProjection
{
    public static OperatorTimingDisplay Display(SimTimingReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        reply.Validate();
        var direct = reply.Events.Count(e => e.Origin == "DIRECT_OBSERVATION");
        var legacy = reply.Events.Count - direct;
        string summary = reply.Events.Count == 0
            ? "Core 查询成功，但本件尚无持久化采集时序事件；不能认定 PLC 已确认"
            : $"Core 时间线 {reply.Events.Count} 条（直接观测 {direct} / 旧汇总 {legacy}）" +
              $" · 故障发现 {reply.Findings.Count}" +
              (reply.Truncated ? " · 超过120条，结果已截断" : "") +
              " · 跨节点 UTC 不保证严格时序";
        var events = reply.Events.Select(e =>
        {
            string plc = e.PlcDmAddress is null ? "—" :
                e.PlcDmAddress +
                (e.PlcValue is null ? "" : "=" + e.PlcValue.Value.ToString(CultureInfo.InvariantCulture));
            if (e.FinsSid is not null) plc += " / SID " + e.FinsSid.Value;
            if (e.OperationId is not null) plc += " / Op " + e.OperationId.Value.ToString("N")[..8];
            string capture = e.CaptureId is null ? "—" :
                "Capture " + e.CaptureId.Value.ToString("N")[..8] +
                (e.AttemptNo is null ? "" : " / Try " + e.AttemptNo);
            if (e.FrameId is not null)
                capture += " / Frame " + e.FrameId.Value.ToString("N")[..8];
            string device = (e.ChannelId ?? "物理事件") + " · " +
                (e.PhysicalDeviceId ?? "整机");
            return new OperatorTimingTile(
                e.OccurredUtc.ToUniversalTime().ToString("HH:mm:ss.fff 'UTC'"),
                e.NodeId, e.Stage, e.Origin, e.ChannelId, device, plc, capture,
                e.Status ?? "未报告状态");
        }).ToArray();
        return new OperatorTimingDisplay(summary, events,
            reply.Findings.Select(f =>
                new OperatorTimingFindingTile(f.Severity, f.Code, f.Detail)).ToArray());
    }

    /// <summary>
    /// Annotate the eleven existing camera debug rows, matching the chosen physical
    /// CaptureId/AttemptNo. A prior attempt's ACK never completes a later attempt.
    /// A physical line-scan frame is shared by its two logical channels.
    /// </summary>
    public static IReadOnlyList<CaptureDebugRow> Apply(
        IReadOnlyList<CaptureDebugRow> snapshotRows, SimTimingReply reply)
    {
        ArgumentNullException.ThrowIfNull(snapshotRows);
        ArgumentNullException.ThrowIfNull(reply);
        reply.Validate();
        return snapshotRows.Select(row =>
        {
            var physical = reply.Events.Where(e => e.Origin == "DIRECT_OBSERVATION" &&
                e.PhysicalDeviceId == row.PhysicalDeviceId &&
                (e.ChannelId is null || e.ChannelId == row.ChannelId) &&
                e.CaptureId is not null && e.AttemptNo is not null).ToArray();
            var latest = physical.OrderByDescending(e => e.AttemptNo)
                .ThenByDescending(e => e.OccurredUtc).FirstOrDefault();
            var sameCapture = latest is null
                ? Array.Empty<SimTimingMilestone>()
                : physical.Where(e => e.CaptureId == latest.CaptureId &&
                    e.AttemptNo == latest.AttemptNo).ToArray();
            var request = sameCapture.LastOrDefault(e =>
                e.Stage == "PlcCaptureRequestObserved" &&
                e.PlcDmAddress == row.PlcAddress && e.PlcValue == 1);
            var frameReady = sameCapture.LastOrDefault(e =>
                e.Stage == "FrameReady" && e.FrameId is not null);
            var arrived = sameCapture.LastOrDefault(e =>
                e.Stage == "FrameArrived" && e.FrameId is not null);
            // For a "CamDone acknowledged" assertion we require the actual
            // direct FINS operation identity, SID, DM and acknowledged value.
            var ack = sameCapture.LastOrDefault(e =>
                e.Stage == "CamDoneAcknowledged" &&
                e.PlcDmAddress == row.PlcAddress &&
                e.PlcValue is 2 or 3 && e.OperationId is not null && e.FinsSid is not null);
            var sent = sameCapture.LastOrDefault(e =>
                e.Stage == "CamDoneSent" &&
                e.PlcDmAddress == row.PlcAddress &&
                e.OperationId is not null && e.FinsSid is not null);
            var inspection = sameCapture.LastOrDefault(e =>
                e.Stage == "InspectionCompleted" &&
                e.ChannelId == row.ChannelId && e.FrameId is not null);
            var failed = sameCapture.LastOrDefault(e =>
                e.Stage is "CaptureFailed" or "CaptureTimedOut");

            string requestLabel = request is not null
                ? "PLC采图请求 D=1 已观测 · " + Utc(request)
                : latest is null ? "Core无此相机采集事件" : "请求未见持久化事件（非已确认）";
            string frameLabel = frameReady is not null
                ? "FrameReady · FrameId=" + Short(frameReady.FrameId) + " · " + Utc(frameReady)
                : arrived is not null ? "帧已到达但 FrameReady 尚无证据 · " + Utc(arrived)
                : latest is null ? "Core无帧事件" : "FrameReady 无持久化证据";
            string ackLabel = ack is not null
                ? "FINS CamDone ACK已观测 D=" + ack.PlcValue +
                  " · SID=" + ack.FinsSid + " · Op=" + Short(ack.OperationId) +
                  " · " + Utc(ack)
                : sent is not null
                    ? "CamDone 已发送但未确认 ACK · SID=" + sent.FinsSid +
                      " · Op=" + Short(sent.OperationId)
                    : latest is null ? "Core无CamDone事件" : "CamDone ACK未被证实";
            string result = inspection is null ? row.InspectionResult :
                "InspectionCompleted " + (inspection.Status ?? "状态未报告") +
                " · Attempt " + inspection.AttemptNo;
            string resultUtc = inspection is null ? row.ResultUtc : Utc(inspection);
            string error = row.Error;
            if (failed is not null)
                error += (error.Length == 0 ? "" : " / ") +
                    failed.Stage + " · " + (failed.Status ?? "无错误详情");

            return row with
            {
                RequestEvidence = requestLabel,
                FrameReadyEvidence = frameLabel,
                CamDoneEvidence = ackLabel,
                InspectionResult = result,
                ResultUtc = resultUtc,
                Error = error
            };
        }).ToArray();
    }

    private static string Short(Guid? id) => id is { } value ? value.ToString("N")[..8] : "—";
    private static string Utc(SimTimingMilestone e) =>
        e.OccurredUtc.ToUniversalTime().ToString("HH:mm:ss.fff 'UTC'");
}
