using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AOI.Solutions;

/// <summary>
/// ROI is versioned in existing StationRecipe.Tunables. No change to A0 wire
/// contracts or the JSON/hash shape of previously published releases.
/// Existing brightThreshold and minimumBrightFraction are preserved.
/// </summary>
public sealed record NormalizedInspectionRoi(double X, double Y, double Width, double Height)
{
    public static NormalizedInspectionRoi FullFrame { get; } = new(0, 0, 1, 1);

    public void Validate(string channelId)
    {
        new RoiDraftEdit(channelId, X, Y, Width, Height, 0, "release").Validate();
    }

    public PixelInspectionRoi ToPixels(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "A real image must have positive dimensions.");
        // Normalize to source-frame coordinates; do not resize or silently
        // shift the operator's selection. End coordinates may reach image edge.
        int left = checked((int)Math.Floor(X * imageWidth));
        int top = checked((int)Math.Floor(Y * imageHeight));
        int right = checked((int)Math.Ceiling((X + Width) * imageWidth));
        int bottom = checked((int)Math.Ceiling((Y + Height) * imageHeight));
        var pixel = new PixelInspectionRoi(left, top, right - left, bottom - top);
        pixel.Validate(imageWidth, imageHeight);
        return pixel;
    }
}

public sealed record PixelInspectionRoi(int X, int Y, int Width, int Height)
{
    public void Validate(int imageWidth, int imageHeight)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            (long)X + Width > imageWidth || (long)Y + Height > imageHeight)
            throw new InvalidDataException("Published ROI lies outside the source frame.");
    }
}

public sealed record PublishedChannelRoi(
    string ChannelId, NormalizedInspectionRoi Bounds, long SourceRevision);

public sealed record RoiImportResult(
    string SolutionId, string ChannelId, string StationId,
    long SourceRoiRevision, long StationDraftRevision, NormalizedInspectionRoi Bounds);

public static class RoiReleaseWorkflow
{
    // All five fields are legal A6 StationRecipe.Tunables identifiers.
    // Keep old StationRecipe schema intact: otherwise existing release digests change.
    private static string Prefix(string channelId) => "roi." + channelId + ".";
    private static readonly string[] Fields = ["x", "y", "width", "height", "revision"];
    private const long MaxExactDoubleInteger = 9007199254740992L;

    /// <summary>
    /// Read one A0 GUI ROI draft from its actual owning Core's WAL.
    /// ReadOnly mode never creates an empty database or alters the GUI draft.
    /// </summary>
    public static RoiDraftState ReadCoreDraft(string dataRoot, string channelId)
    {
        var channel = ChannelCatalog.Require(channelId);
        string node = channel.OwnerRole == MachineRole.Master ? "master" : "slave";
        string path = Path.Combine(Path.GetFullPath(dataRoot), node, "sim-config.sqlite");
        if (!File.Exists(path))
            throw new FileNotFoundException("No saved Core ROI draft. Save from the GUI first.", path);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString();
        using var db = new SqliteConnection(connection);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT channel_id,x,y,width,height,revision,actor,edited_utc
            FROM sim_roi_drafts WHERE channel_id=$channel
            """;
        cmd.Parameters.AddWithValue("$channel", channelId);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new KeyNotFoundException("No saved Core ROI for " + channelId);
        var draft = new RoiDraftState(
            row.GetString(0), row.GetDouble(1), row.GetDouble(2),
            row.GetDouble(3), row.GetDouble(4), row.GetInt64(5),
            row.GetString(6),
            DateTimeOffset.Parse(row.GetString(7), CultureInfo.InvariantCulture));
        draft.Validate();
        if (draft.ChannelId != channel.ChannelId || draft.Revision > MaxExactDoubleInteger)
            throw new InvalidDataException("Invalid GUI ROI revision or channel ownership.");
        return draft;
    }

    /// <summary>
    /// Explicit operator action: GUI/Core ROI draft -> product station DRAFT.
    /// It never edits an immutable release, PLC, running item, or active epoch.
    /// Two independent revisions are required to prevent stale UI writes.
    /// </summary>
    public static RoiImportResult ImportCoreDraft(
        SolutionRepository repository, string dataRoot, string solutionId,
        string channelId, long expectedSourceRevision, long expectedStationRevision,
        string actor)
    {
        ArgumentNullException.ThrowIfNull(repository);
        SolutionSafety.RequireActor(actor);
        var channel = ChannelCatalog.Require(channelId);
        var source = ReadCoreDraft(dataRoot, channelId);
        if (source.Revision != expectedSourceRevision)
            throw new InvalidOperationException("ROI_SOURCE_REVISION_CONFLICT");
        var draft = repository.GetDraft(solutionId, channel.StationId)
            ?? throw new InvalidOperationException("Station draft missing; bootstrap the solution first.");
        if (draft.Revision != expectedStationRevision)
            throw new InvalidOperationException("DRAFT_REVISION_CONFLICT");
        var values = new Dictionary<string, double>(draft.Recipe.Tunables, StringComparer.Ordinal);
        string prefix = Prefix(channelId);
        foreach (string key in values.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            values.Remove(key);
        values.Add(prefix + "x", source.X);
        values.Add(prefix + "y", source.Y);
        values.Add(prefix + "width", source.Width);
        values.Add(prefix + "height", source.Height);
        values.Add(prefix + "revision", source.Revision);
        var recipe = draft.Recipe with { Tunables = values };
        ValidateStationRois(recipe);
        var saved = repository.SaveDraft(solutionId, recipe, expectedStationRevision, actor);
        return new(solutionId, channelId, channel.StationId, source.Revision,
            saved.Revision, new(source.X, source.Y, source.Width, source.Height));
    }

    /// <summary>
    /// Used by both draft preview and published release runtime. No ROI keys
    /// means legacy full-frame inspection; partial/misowned keys are rejected.
    /// </summary>
    public static PublishedChannelRoi? ReadStationRoi(StationRecipe station, string channelId)
    {
        station.Validate();
        var channel = ChannelCatalog.Require(channelId);
        if (channel.StationId != station.StationId || !station.ChannelIds.Contains(channelId, StringComparer.Ordinal))
            throw new InvalidDataException("ROI channel/station misbinding.");
        string prefix = Prefix(channelId);
        var keys = station.Tunables.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (keys.Length == 0) return null;
        if (keys.Length != Fields.Length || Fields.Any(field => !station.Tunables.ContainsKey(prefix + field)))
            throw new InvalidDataException("Incomplete published ROI fields for " + channelId);
        double revision = station.Tunables[prefix + "revision"];
        if (!double.IsFinite(revision) || revision < 1 || revision > MaxExactDoubleInteger ||
            revision != Math.Truncate(revision))
            throw new InvalidDataException("Unrepresentable published ROI source revision.");
        var roi = new NormalizedInspectionRoi(
            station.Tunables[prefix + "x"], station.Tunables[prefix + "y"],
            station.Tunables[prefix + "width"], station.Tunables[prefix + "height"]);
        roi.Validate(channelId);
        return new(channelId, roi, checked((long)revision));
    }

    public static void ValidateStationRois(StationRecipe station)
    {
        station.Validate();
        foreach (var key in station.Tunables.Keys.Where(k => k.StartsWith("roi.", StringComparison.Ordinal)))
        {
            var supported = station.ChannelIds.Any(channel =>
                Fields.Any(field => string.Equals(key, Prefix(channel) + field, StringComparison.Ordinal)));
            if (!supported) throw new InvalidDataException("Unknown/misowned ROI parameter: " + key);
        }
        foreach (string channel in station.ChannelIds) _ = ReadStationRoi(station, channel);
    }

    /// <summary>Exact integration API for A0 Host's per-channel pixel rule.</summary>
    public static PublishedChannelRoi? ReadPublishedRoi(PublishedRelease release, string channelId)
    {
        ArgumentNullException.ThrowIfNull(release);
        release.Verify();
        var channel = ChannelCatalog.Require(channelId);
        var station = release.Manifest.Stations.Single(s => s.StationId == channel.StationId);
        return ReadStationRoi(station, channelId);
    }

    public static StationRecipe RequireStation(PublishedRelease release, string channelId)
    {
        release.Verify();
        var channel = ChannelCatalog.Require(channelId);
        return release.Manifest.Stations.Single(s => s.StationId == channel.StationId);
    }
}
