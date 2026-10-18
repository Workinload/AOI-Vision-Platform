using AOI.Contracts;
using AOI.Domain;

namespace AOI.Solutions;

/// <summary>Durable revisioned drafts, content-addressed immutable releases and operator audit.</summary>
public sealed class SolutionRepository
{
    private readonly SqliteSupport _db;
    public SolutionRepository(string path, bool readOnly = false)
    {
        _db = new(path, readOnly);
        if (!readOnly) _db.Initialize("""
            CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS solutions(id TEXT PRIMARY KEY, profile_id TEXT NOT NULL, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS drafts(solution_id TEXT NOT NULL, station_id TEXT NOT NULL,
              revision INTEGER NOT NULL, body TEXT NOT NULL, actor TEXT NOT NULL,
              PRIMARY KEY(solution_id, station_id));
            CREATE TABLE IF NOT EXISTS releases(id TEXT PRIMARY KEY, digest TEXT NOT NULL,
              body TEXT NOT NULL, published_utc TEXT NOT NULL, actor TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(seq INTEGER PRIMARY KEY AUTOINCREMENT,
              utc TEXT NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, detail TEXT NOT NULL);
            """);
    }

    private static void Audit(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, string actor, string action, string detail) =>
        SqliteSupport.Run(db, tx, "INSERT INTO audit(utc,actor,action,detail) VALUES($t,$a,$k,$d)",
            ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$a", actor), ("$k", action), ("$d", detail));

    /// <summary>
    /// Minimal read-only query added for the offline operator workspace: the UI must show the
    /// products that genuinely exist in this A6 database, never an invented second list.
    /// </summary>
    public IReadOnlyList<ProductSolution> ListSolutions()
    {
        using var db = _db.Open();
        using var cmd = SqliteSupport.Command(db, null, "SELECT id,profile_id,name FROM solutions ORDER BY id");
        using var reader = cmd.ExecuteReader();
        var list = new List<ProductSolution>();
        while (reader.Read())
            list.Add(new ProductSolution(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    /// <summary>
    /// Additive read-only query for the offline operator workspace: releases must be read
    /// back from the actual database, never from an in-memory UI claim.
    /// </summary>
    public IReadOnlyList<PublishedRelease> ListReleases()
    {
        using var db = _db.Open();
        using var cmd = SqliteSupport.Command(db, null,
            "SELECT body FROM releases ORDER BY published_utc,id");
        using var reader = cmd.ExecuteReader();
        var list = new List<PublishedRelease>();
        while (reader.Read())
        {
            var release = WireFormat.Parse<PublishedRelease>(reader.GetString(0));
            release.Verify();
            list.Add(release);
        }
        return list;
    }

    /// <summary>
    /// Additive read-only query: the offline operator confirms the whole product's draft
    /// revisions before publication. The publish transaction re-checks these exact values.
    /// </summary>
    public IReadOnlyDictionary<string, long> ListDraftRevisions(string solutionId)
    {
        using var db = _db.Open();
        using var cmd = SqliteSupport.Command(db, null,
            "SELECT station_id,revision FROM drafts WHERE solution_id=$id ORDER BY station_id",
            ("$id", solutionId));
        using var reader = cmd.ExecuteReader();
        var revisions = new SortedDictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            revisions[reader.GetString(0)] = reader.GetInt64(1);
        return revisions;
    }

    public void RegisterProfile(MachineProfile profile, string actor)
    {
        profile.Validate();
        SolutionSafety.RequireActor(actor);
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        var json = WireFormat.Serialize(profile);
        var old = SqliteSupport.Scalar(db, tx, "SELECT data FROM profiles WHERE id=$id", ("$id", profile.ProfileId)) as string;
        if (old is not null && old != json) throw new InvalidOperationException("MachineProfile is immutable after registration.");
        if (old is null)
            SqliteSupport.Run(db, tx, "INSERT INTO profiles(id,data) VALUES($id,$json)", ("$id", profile.ProfileId), ("$json", json));
        Audit(db, tx, actor, "PROFILE_REGISTERED", profile.ProfileId);
        tx.Commit();
    }

    public void CreateSolution(ProductSolution solution, string actor)
    {
        SolutionSafety.RequireId(solution.SolutionId, nameof(solution.SolutionId));
        SolutionSafety.RequireActor(actor);
        if (string.IsNullOrWhiteSpace(solution.DisplayName) || solution.DisplayName.Length > 128)
            throw new ArgumentException("Invalid product name.");
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        if (SqliteSupport.Scalar(db, tx, "SELECT id FROM profiles WHERE id=$id", ("$id", solution.ProfileId)) is null)
            throw new InvalidOperationException("MachineProfile not found.");
        SqliteSupport.Run(db, tx, "INSERT INTO solutions(id,profile_id,name) VALUES($id,$p,$n)",
            ("$id", solution.SolutionId), ("$p", solution.ProfileId), ("$n", solution.DisplayName));
        Audit(db, tx, actor, "SOLUTION_CREATED", solution.SolutionId);
        tx.Commit();
    }

    /// <summary>
    /// Additive operator creation: the product row and ALL mapped station drafts are
    /// inserted in ONE immediate A6 transaction, so a failure cannot leave an empty or
    /// partially configured product behind. Inputs and every recipe are validated first.
    /// </summary>
    public void CreateSolutionWithDrafts(ProductSolution solution,
        IReadOnlyDictionary<string, StationRecipe> stationDrafts, string actor)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(stationDrafts);
        SolutionSafety.RequireId(solution.SolutionId, nameof(solution.SolutionId));
        SolutionSafety.RequireActor(actor);
        if (string.IsNullOrWhiteSpace(solution.DisplayName) || solution.DisplayName.Length > 128)
            throw new ArgumentException("Invalid product name.");
        var expectedStations = ChannelCatalog.All.Select(c => c.StationId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var actualStations = stationDrafts.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!actualStations.SequenceEqual(expectedStations, StringComparer.Ordinal))
            throw new InvalidDataException(
                "A product requires exactly one draft for every mapped station.");
        foreach (var (stationId, recipe) in stationDrafts)
        {
            if (!string.Equals(recipe.StationId, stationId, StringComparison.Ordinal))
                throw new InvalidDataException("Station draft id does not match its key.");
            recipe.Validate();
        }
        using var db = _db.Open();
        using var tx = db.BeginTransaction(deferred: false);
        if (SqliteSupport.Scalar(db, tx, "SELECT id FROM profiles WHERE id=$id",
                ("$id", solution.ProfileId)) is null)
            throw new InvalidOperationException("MachineProfile not found.");
        SqliteSupport.Run(db, tx, "INSERT INTO solutions(id,profile_id,name) VALUES($id,$p,$n)",
            ("$id", solution.SolutionId), ("$p", solution.ProfileId), ("$n", solution.DisplayName));
        Audit(db, tx, actor, "SOLUTION_CREATED", solution.SolutionId);
        foreach (var (stationId, recipe) in stationDrafts)
        {
            var canonical = recipe.Canonical();
            SqliteSupport.Run(db, tx, """
                INSERT INTO drafts(solution_id,station_id,revision,body,actor)
                VALUES($s,$st,1,$b,$a)
                """, ("$s", solution.SolutionId), ("$st", stationId),
                ("$b", WireFormat.Serialize(canonical)), ("$a", actor));
            Audit(db, tx, actor, "DRAFT_SAVED", solution.SolutionId + "/" + stationId + "/1");
        }
        tx.Commit();
    }

    public StationDraft? GetDraft(string solutionId, string stationId)
    {
        using var db = _db.Open();
        using var cmd = SqliteSupport.Command(db, null,
            "SELECT revision,body,actor FROM drafts WHERE solution_id=$id AND station_id=$station",
            ("$id", solutionId), ("$station", stationId));
        using var row = cmd.ExecuteReader();
        return row.Read()
            ? new(solutionId, stationId, row.GetInt64(0), WireFormat.Parse<StationRecipe>(row.GetString(1)), row.GetString(2))
            : null;
    }

    public StationDraft SaveDraft(string solutionId, StationRecipe recipe, long expectedRevision, string actor)
    {
        SolutionSafety.RequireId(solutionId, nameof(solutionId));
        SolutionSafety.RequireActor(actor);
        recipe.Validate();
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        if (SqliteSupport.Scalar(db, tx, "SELECT id FROM solutions WHERE id=$id", ("$id", solutionId)) is null)
            throw new InvalidOperationException("Unknown solution.");
        var old = SqliteSupport.Scalar(db, tx,
            "SELECT revision FROM drafts WHERE solution_id=$s AND station_id=$st",
            ("$s", solutionId), ("$st", recipe.StationId));
        var revision = old is null ? 0L : Convert.ToInt64(old);
        if (revision != expectedRevision) throw new InvalidOperationException("DRAFT_REVISION_CONFLICT");
        var next = checked(revision + 1);
        var canonical = recipe.Canonical();
        SqliteSupport.Run(db, tx, """
            INSERT INTO drafts(solution_id,station_id,revision,body,actor)
            VALUES($s,$st,$r,$b,$a)
            ON CONFLICT(solution_id,station_id) DO UPDATE SET
              revision=excluded.revision, body=excluded.body, actor=excluded.actor
            """, ("$s", solutionId), ("$st", recipe.StationId),
            ("$r", next), ("$b", WireFormat.Serialize(canonical)), ("$a", actor));
        Audit(db, tx, actor, "DRAFT_SAVED", solutionId + "/" + recipe.StationId + "/" + next);
        tx.Commit();
        return new(solutionId, recipe.StationId, next, canonical, actor);
    }

    public PublishedRelease Publish(string solutionId, string actor)
    {
        SolutionSafety.RequireActor(actor);
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        using var sol = SqliteSupport.Command(db, tx,
            "SELECT profile_id FROM solutions WHERE id=$id", ("$id", solutionId));
        var profileId = sol.ExecuteScalar() as string ?? throw new InvalidOperationException("Unknown solution.");
        var profileJson = SqliteSupport.Scalar(db, tx, "SELECT data FROM profiles WHERE id=$id", ("$id", profileId)) as string
            ?? throw new InvalidOperationException("Missing profile.");
        var profile = WireFormat.Parse<MachineProfile>(profileJson);
        profile.Validate();
        var stations = new List<StationRecipe>();
        using (var cmd = SqliteSupport.Command(db, tx,
            "SELECT body FROM drafts WHERE solution_id=$id ORDER BY station_id", ("$id", solutionId)))
        using (var rows = cmd.ExecuteReader())
            while (rows.Read()) stations.Add(WireFormat.Parse<StationRecipe>(rows.GetString(0)));
        var manifest = new ReleaseManifest(solutionId, profile.ProfileId, profile.Version,
            profile.CatalogSha256, stations).Canonical();
        manifest.Validate();
        foreach (var station in manifest.Stations) RoiReleaseWorkflow.ValidateStationRois(station);
        var digest = SolutionSafety.Sha256(WireFormat.Serialize(manifest));
        var release = new PublishedRelease("SIMREL-" + digest[..32], digest,
            manifest, DateTimeOffset.UtcNow, actor);
        release.Verify();
        var old = SqliteSupport.Scalar(db, tx, "SELECT body FROM releases WHERE id=$id",
            ("$id", release.ReleaseId)) as string;
        if (old is null)
            SqliteSupport.Run(db, tx, """
                INSERT INTO releases(id,digest,body,published_utc,actor)
                VALUES($id,$d,$b,$utc,$a)
                """, ("$id", release.ReleaseId), ("$d", digest),
                ("$b", WireFormat.Serialize(release)),
                ("$utc", release.PublishedAtUtc.ToString("O")), ("$a", actor));
        else
        {
            var original = WireFormat.Parse<PublishedRelease>(old);
            original.Verify();
            if (original.ContentSha256 != digest) throw new InvalidDataException("Release digest collision.");
            release = original;
        }
        Audit(db, tx, actor, "RELEASE_PUBLISHED", release.ReleaseId);
        tx.Commit();
        return release;
    }

    /// <summary>
    /// Safe operator publication. Inside ONE immediate A6 transaction (write lock held before
    /// any read) it verifies the operator's whole-product draft revision snapshot (CAS), builds
    /// and validates the manifest, runs the caller's fail-closed plugin/ROI checks, and only
    /// then inserts the immutable release. A concurrent draft change or any validation failure
    /// rolls the transaction back, so no partial release can exist. The legacy two-argument
    /// Publish above is unchanged for the existing CLI.
    /// </summary>
    public PublishedRelease Publish(string solutionId, string actor,
        IReadOnlyDictionary<string, long> expectedStationRevisions,
        Action<ReleaseManifest> validateManifest)
    {
        ArgumentNullException.ThrowIfNull(expectedStationRevisions);
        ArgumentNullException.ThrowIfNull(validateManifest);
        SolutionSafety.RequireActor(actor);
        using var db = _db.Open();
        // IMMEDIATE takes the write lock up front: no other writer can change drafts between
        // the CAS read and the immutable insert below. (Deferred would upgrade late in WAL.)
        using var tx = db.BeginTransaction(deferred: false);
        using var sol = SqliteSupport.Command(db, tx,
            "SELECT profile_id FROM solutions WHERE id=$id", ("$id", solutionId));
        var profileId = sol.ExecuteScalar() as string
            ?? throw new InvalidOperationException("Unknown solution.");
        var profileJson = SqliteSupport.Scalar(db, tx, "SELECT data FROM profiles WHERE id=$id",
            ("$id", profileId)) as string ?? throw new InvalidOperationException("Missing profile.");
        var profile = WireFormat.Parse<MachineProfile>(profileJson);
        profile.Validate();
        var stations = new List<(string StationId, long Revision, StationRecipe Recipe)>();
        using (var cmd = SqliteSupport.Command(db, tx,
            "SELECT station_id,revision,body FROM drafts WHERE solution_id=$id ORDER BY station_id",
            ("$id", solutionId)))
        using (var rows = cmd.ExecuteReader())
            while (rows.Read())
                stations.Add((rows.GetString(0), rows.GetInt64(1),
                    WireFormat.Parse<StationRecipe>(rows.GetString(2))));
        var actual = stations.ToDictionary(x => x.StationId, x => x.Revision, StringComparer.Ordinal);
        foreach (var (stationId, revision) in actual)
        {
            if (!expectedStationRevisions.TryGetValue(stationId, out long expected))
                throw new InvalidOperationException("DRAFT_REVISION_CONFLICT: " + stationId +
                    " has no operator-confirmed expected revision.");
            if (expected != revision)
                throw new InvalidOperationException("DRAFT_REVISION_CONFLICT: " + stationId +
                    " expected=" + expected.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " actual=" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (string expectedStation in expectedStationRevisions.Keys)
            if (!actual.ContainsKey(expectedStation))
                throw new InvalidOperationException("DRAFT_REVISION_CONFLICT: missing draft for " +
                    expectedStation);
        var manifest = new ReleaseManifest(solutionId, profile.ProfileId, profile.Version,
            profile.CatalogSha256, stations.Select(x => x.Recipe).ToArray()).Canonical();
        manifest.Validate();
        foreach (var station in manifest.Stations) RoiReleaseWorkflow.ValidateStationRois(station);
        // Caller (OfflineProductManager) validates installed plugins/ROI BEFORE the insert.
        validateManifest(manifest);
        var digest = SolutionSafety.Sha256(WireFormat.Serialize(manifest));
        var release = new PublishedRelease("SIMREL-" + digest[..32], digest,
            manifest, DateTimeOffset.UtcNow, actor);
        release.Verify();
        var old = SqliteSupport.Scalar(db, tx, "SELECT body FROM releases WHERE id=$id",
            ("$id", release.ReleaseId)) as string;
        if (old is null)
            SqliteSupport.Run(db, tx, """
                INSERT INTO releases(id,digest,body,published_utc,actor)
                VALUES($id,$d,$b,$utc,$a)
                """, ("$id", release.ReleaseId), ("$d", digest),
                ("$b", WireFormat.Serialize(release)),
                ("$utc", release.PublishedAtUtc.ToString("O")), ("$a", actor));
        else
        {
            var original = WireFormat.Parse<PublishedRelease>(old);
            original.Verify();
            if (original.ContentSha256 != digest)
                throw new InvalidDataException("Release digest collision.");
            release = original;
        }
        Audit(db, tx, actor, "RELEASE_PUBLISHED", release.ReleaseId);
        tx.Commit();
        return release;
    }

    public PublishedRelease RequireRelease(string releaseId)
    {
        using var db = _db.Open();
        var json = SqliteSupport.Scalar(db, null, "SELECT body FROM releases WHERE id=$id",
            ("$id", releaseId)) as string ?? throw new KeyNotFoundException(releaseId);
        var release = WireFormat.Parse<PublishedRelease>(json);
        release.Verify();
        foreach (var station in release.Manifest.Stations) RoiReleaseWorkflow.ValidateStationRois(station);
        return release;
    }
}
