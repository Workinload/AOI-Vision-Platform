using AOI.Domain;

namespace AOI.Diagnostics;

/// <summary>One row per physical WorkpieceId; Barcode is never a primary key.</summary>
public enum ProductionDisposition { Good, Ng, Hold, InProgress }

public sealed record ProductionSearch(
    Guid? WorkpieceId = null, string? Barcode = null, string? StationId = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? UntilUtc = null,
    int Limit = 100, int Offset = 0)
{
    public void Validate()
    {
        if (WorkpieceId == Guid.Empty || Limit is < 1 or > 500 || Offset is < 0 or > 100000 ||
            (Barcode is { Length: > 256 }) || (StationId is { Length: > 64 }) ||
            (FromUtc is not null && UntilUtc is not null && FromUtc >= UntilUtc))
            throw new ArgumentOutOfRangeException(nameof(ProductionSearch), "Invalid production search range or page.");
    }
}

public sealed record ProductionHistoryRow(
    Guid WorkpieceId, string Barcode, Guid TraceId, long Epoch, string ReleaseId,
    DateTimeOffset RegisteredUtc, ProductionDisposition Disposition,
    int CapturedChannels, string? HoldReason);

public sealed record ProductionHistoryPage(
    long TotalMatches, ProductionHistoryRow[] Rows, int Offset, int Limit)
{
    public bool HasMore => (long)Offset + Rows.Length < TotalMatches;
}

/// <summary>
/// Counts use Master workpiece registrations in [FromUtc,UntilUtc), NOT camera/defect events.
/// GoodRate = Good/(Good+Ng) and is null when no completed GOOD/NG exist.
/// Holds/InProgress remain included in the registration denominator.
/// </summary>
public sealed record ProductionPeriodSummary(
    string Label, DateTimeOffset FromUtc, DateTimeOffset UntilUtc,
    long Total, long Good, long Ng, long Hold, long InProgress,
    double? GoodRate, double? GoodShareOfRegistered)
{
    public long Completed => Good + Ng;
}

public sealed record ProductionEventSearch(
    Guid? WorkpieceId = null, string? NodeId = null, string? Kind = null,
    string? StationId = null, string? ContainsText = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? UntilUtc = null,
    int Limit = 100)
{
    public void Validate()
    {
        if (WorkpieceId == Guid.Empty || Limit is < 1 or > 500 ||
            (NodeId is not null && NodeId is not ("master" or "slave")) ||
            Kind is { Length: > 128 } || StationId is { Length: > 64 } ||
            ContainsText is { Length: > 256 } ||
            (FromUtc is not null && UntilUtc is not null && FromUtc >= UntilUtc))
            throw new ArgumentOutOfRangeException(nameof(ProductionEventSearch), "Invalid event filter.");
    }
}

public sealed record ProductionEventRow(
    string NodeId, Guid EventId, DateTimeOffset Utc, string Kind,
    Guid? WorkpieceId, Guid? MessageId, string Details);

/// <summary>Manifest points at existing Core-owned files; only Core/A0 may serve previews over IPC.</summary>
public sealed record TraceImage(
    string NodeId, string ChannelId, Guid FrameId, Guid? ParentFrameId,
    string? RelativePath, string? ParentRelativePath, string? ContentSha256,
    string SourceFrameSha256, bool Present, bool? HashMatches,
    string State);

public sealed record ProductionChannelTrace(
    string NodeId, ChannelResult Result, TraceImage Image);

public sealed record ProductionWorkpieceTrace(
    ProductionHistoryRow Workpiece, ProductionChannelTrace[] Channels,
    ProductionEventRow[] Events);
