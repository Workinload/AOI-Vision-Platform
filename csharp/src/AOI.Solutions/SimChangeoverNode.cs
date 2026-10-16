using AOI.Contracts;
using AOI.Domain;

namespace AOI.Solutions;

/// <summary>Persisted local node emulator. Does not touch FINS, cameras or production outputs.</summary>
public sealed class SimChangeoverNode : ISimChangeoverNode
{
    private readonly SqliteSupport _db;
    public string NodeId { get; }
    public MachineRole Role { get; }

    public SimChangeoverNode(string path, string nodeId, MachineRole role)
    {
        if (nodeId != (role == MachineRole.Master ? "master" : "slave"))
            throw new ArgumentException("Node ID and role mismatch.");
        NodeId = nodeId; Role = role;
        _db = new SqliteSupport(path);
        _db.Initialize("""
            CREATE TABLE IF NOT EXISTS active(
              slot INTEGER PRIMARY KEY CHECK(slot=1), epoch INTEGER NOT NULL,
              release_id TEXT, pending_id TEXT, pending_release TEXT,
              pending_digest TEXT, next_epoch INTEGER, phase INTEGER NOT NULL,
              safe_hold INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS staging(transaction_id TEXT PRIMARY KEY,
              release_id TEXT NOT NULL, digest TEXT NOT NULL, content TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT,
              utc TEXT NOT NULL, action TEXT NOT NULL, details TEXT NOT NULL);
            INSERT OR IGNORE INTO active(slot,epoch,release_id,phase,safe_hold)
              VALUES(1,0,NULL,0,1);
            """);
    }

    private void Event(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, string action, string detail) =>
        SqliteSupport.Run(db, tx, "INSERT INTO events(utc,action,details) VALUES($u,$a,$d)",
            ("$u", DateTimeOffset.UtcNow.ToString("O")), ("$a", action), ("$d", detail));

    private NodeSnapshot Read(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction? tx)
    {
        using var cmd = SqliteSupport.Command(db, tx,
            "SELECT epoch,release_id,pending_id,phase,safe_hold FROM active WHERE slot=1");
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("Missing changeover state.");
        return new(NodeId, Role, row.GetInt64(0), row.IsDBNull(1) ? null : row.GetString(1),
            row.IsDBNull(2) ? null : Guid.Parse(row.GetString(2)),
            (NodeChangeoverPhase)row.GetInt32(3), row.GetInt32(4) != 0, false);
    }

    public NodeSnapshot Snapshot()
    {
        using var db = _db.Open();
        return Read(db, null);
    }

    public PreparedReceipt Prepare(ChangeoverRequest request, SimInterlocks evidence)
    {
        evidence.RequireSafe(); request.Validate();
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        var now = Read(db, tx);
        if (!now.SafeHold || now.Phase != NodeChangeoverPhase.Idle ||
            now.ActiveEpoch != request.ExpectedEpoch || now.ActiveReleaseId != request.ExpectedReleaseId)
            throw new InvalidOperationException("NODE_BUSY_OR_EPOCH_MISMATCH");
        var content = WireFormat.Serialize(request.Target.Manifest.Canonical());
        var readBack = SolutionSafety.Sha256(content);
        if (readBack != request.Target.ContentSha256)
            throw new InvalidDataException("Staging read-back failed.");
        SqliteSupport.Run(db, tx,
            "INSERT INTO staging(transaction_id,release_id,digest,content) VALUES($t,$r,$d,$c)",
            ("$t", request.TransactionId.ToString("D")), ("$r", request.Target.ReleaseId),
            ("$d", readBack), ("$c", content));
        SqliteSupport.Run(db, tx, """
            UPDATE active SET pending_id=$t,pending_release=$r,pending_digest=$d,
              next_epoch=$e,phase=1,safe_hold=1 WHERE slot=1
            """, ("$t", request.TransactionId.ToString("D")), ("$r", request.Target.ReleaseId),
            ("$d", readBack), ("$e", request.NextEpoch));
        Event(db, tx, "PREPARED", request.TransactionId.ToString("D"));
        tx.Commit();
        return new(request.TransactionId, NodeId, Role, request.Target.ReleaseId,
            request.Target.ContentSha256, request.ExpectedEpoch, request.NextEpoch, readBack);
    }

    private void ValidateCertificate(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, DualPreparedCertificate certificate,
        bool allowActivated)
    {
        certificate.Validate();
        var now = Read(db, tx);
        if (now.PendingTransactionId != certificate.TransactionId ||
            now.Phase != (allowActivated ? NodeChangeoverPhase.ActivatedHold : NodeChangeoverPhase.Prepared))
            throw new InvalidOperationException("No matching prepared/activated transaction.");
        using var cmd = SqliteSupport.Command(db, tx, """
            SELECT digest,content FROM staging WHERE transaction_id=$t AND release_id=$r
            """, ("$t", certificate.TransactionId.ToString("D")), ("$r", certificate.ReleaseId));
        using var rows = cmd.ExecuteReader();
        if (!rows.Read() || rows.GetString(0) != certificate.ContentSha256 ||
            SolutionSafety.Sha256(rows.GetString(1)) != certificate.ContentSha256)
            throw new InvalidDataException("Staged content read-back changed.");
    }

    public void Activate(DualPreparedCertificate certificate, SimInterlocks evidence)
    {
        evidence.RequireSafe();
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        ValidateCertificate(db, tx, certificate, false);
        var now = Read(db, tx);
        if (now.ActiveEpoch != certificate.NextEpoch - 1 || !now.SafeHold)
            throw new InvalidOperationException("Old epoch not drained or safe.");
        SqliteSupport.Run(db, tx, """
            UPDATE active SET epoch=$e,release_id=$r,phase=2,safe_hold=1 WHERE slot=1
            """, ("$e", certificate.NextEpoch), ("$r", certificate.ReleaseId));
        Event(db, tx, "ACTIVATED_HOLD", certificate.TransactionId.ToString("D"));
        tx.Commit();
    }

    public void FinalizePair(DualPreparedCertificate certificate, SimInterlocks evidence)
    {
        evidence.RequireSafe();
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        ValidateCertificate(db, tx, certificate, true);
        SqliteSupport.Run(db, tx, "UPDATE active SET phase=3,safe_hold=1 WHERE slot=1");
        Event(db, tx, "COMMITTED_HOLD", certificate.TransactionId.ToString("D"));
        tx.Commit();
    }

    /// <summary>Explicit recovery acknowledgement, both nodes must remain held until A0 integrates release-of-hold.</summary>
    public void Abort(Guid transactionId, string reason)
    {
        if (transactionId == Guid.Empty || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Aborting needs transaction and reason.");
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        var now = Read(db, tx);
        if (now.PendingTransactionId != transactionId ||
            now.Phase != NodeChangeoverPhase.Prepared)
            throw new InvalidOperationException("Activated epoch cannot be blindly rolled back.");
        SqliteSupport.Run(db, tx, """
            UPDATE active SET pending_id=NULL,pending_release=NULL,pending_digest=NULL,
              next_epoch=NULL,phase=0,safe_hold=1 WHERE slot=1
            """);
        SqliteSupport.Run(db, tx, "DELETE FROM staging WHERE transaction_id=$t",
            ("$t", transactionId.ToString("D")));
        Event(db, tx, "ABORTED_BEFORE_ACTIVATION", reason);
        tx.Commit();
    }
}
