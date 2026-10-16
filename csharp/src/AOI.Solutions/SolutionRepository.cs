using AOI.Contracts;
using AOI.Domain;

namespace AOI.Solutions;

/// <summary>Durable revisioned drafts, content-addressed immutable releases and operator audit.</summary>
public sealed class SolutionRepository
{
    private readonly SqliteSupport _db;
    public SolutionRepository(string path)
    {
        _db = new(path);
        _db.Initialize("""
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

    public PublishedRelease RequireRelease(string releaseId)
    {
        using var db = _db.Open();
        var json = SqliteSupport.Scalar(db, null, "SELECT body FROM releases WHERE id=$id",
            ("$id", releaseId)) as string ?? throw new KeyNotFoundException(releaseId);
        var release = WireFormat.Parse<PublishedRelease>(json);
        release.Verify();
        return release;
    }
}
