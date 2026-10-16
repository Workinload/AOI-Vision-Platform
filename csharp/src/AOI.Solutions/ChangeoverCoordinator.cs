using AOI.Contracts;
using AOI.Domain;

namespace AOI.Solutions;

/// <summary>Crash-durable two-node coordinator; never auto-unholds production after recovery.</summary>
public sealed class ChangeoverCoordinator
{
    private readonly SqliteSupport _db;
    public ChangeoverCoordinator(string path)
    {
        _db = new(path);
        _db.Initialize("""
            CREATE TABLE IF NOT EXISTS transactions(id TEXT PRIMARY KEY, phase INTEGER NOT NULL,
              release_id TEXT NOT NULL, next_epoch INTEGER NOT NULL, certificate TEXT, detail TEXT);
            """);
    }

    public ChangeoverStatus? Status(Guid id)
    {
        using var db = _db.Open();
        using var cmd = SqliteSupport.Command(db, null,
            "SELECT phase,release_id,next_epoch,certificate,detail FROM transactions WHERE id=$id",
            ("$id", id.ToString("D")));
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        return new(id, (ChangeoverPhase)row.GetInt32(0), row.GetString(1), row.GetInt64(2),
            row.IsDBNull(3) ? null : WireFormat.Parse<DualPreparedCertificate>(row.GetString(3)),
            row.IsDBNull(4) ? null : row.GetString(4));
    }

    private void Set(Guid id, ChangeoverPhase phase, DualPreparedCertificate? certificate = null,
        string? detail = null)
    {
        using var db = _db.Open();
        SqliteSupport.Run(db, null, """
            UPDATE transactions SET phase=$p,certificate=COALESCE($c,certificate),detail=$d WHERE id=$id
            """, ("$p", (int)phase), ("$c", certificate is null ? null : WireFormat.Serialize(certificate)),
            ("$d", detail), ("$id", id.ToString("D")));
    }

    public ChangeoverStatus Execute(ChangeoverRequest request, ISimChangeoverNode master,
        ISimChangeoverNode slave, SimInterlocks evidence)
    {
        request.Validate(); evidence.RequireSafe();
        if (master.NodeId != "master" || master.Role != MachineRole.Master ||
            slave.NodeId != "slave" || slave.Role != MachineRole.Slave)
            throw new InvalidOperationException("Explicit Master and Slave required.");
        if (Status(request.TransactionId) is not null)
            throw new InvalidOperationException("Transaction exists; recover, never replay blindly.");
        using (var db = _db.Open())
            SqliteSupport.Run(db, null, """
                INSERT INTO transactions(id,phase,release_id,next_epoch)
                VALUES($id,0,$release,$epoch)
                """, ("$id", request.TransactionId.ToString("D")),
                ("$release", request.Target.ReleaseId), ("$epoch", request.NextEpoch));
        var activated = false;
        try
        {
            var m = master.Prepare(request, evidence);
            var s = slave.Prepare(request, evidence);
            var cert = new DualPreparedCertificate(request.TransactionId, request.Target.ReleaseId,
                request.Target.ContentSha256, request.NextEpoch, m, s);
            cert.Validate();
            Set(request.TransactionId, ChangeoverPhase.PreparedBoth, cert);
            // Durable coordinator COMMIT_INTENT before any node activates.
            Set(request.TransactionId, ChangeoverPhase.CommitIntent, cert);
            master.Activate(cert, evidence); activated = true;
            slave.Activate(cert, evidence);
            master.FinalizePair(cert, evidence);
            slave.FinalizePair(cert, evidence);
            Set(request.TransactionId, ChangeoverPhase.Committed, cert);
        }
        catch (Exception ex)
        {
            if (!activated && master.Snapshot().Phase == NodeChangeoverPhase.Prepared &&
                slave.Snapshot().Phase is NodeChangeoverPhase.Idle or NodeChangeoverPhase.Prepared)
            {
                // After COMMIT_INTENT this is still a recovery case, NOT unilateral abort.
                if (Status(request.TransactionId)?.Phase == ChangeoverPhase.Preparing)
                {
                    master.Abort(request.TransactionId, "PREPARE_FAILED");
                    if (slave.Snapshot().Phase == NodeChangeoverPhase.Prepared)
                        slave.Abort(request.TransactionId, "PREPARE_FAILED");
                    Set(request.TransactionId, ChangeoverPhase.Aborted, detail: ex.Message);
                }
                else Set(request.TransactionId, ChangeoverPhase.RecoveryRequired, detail: ex.Message);
            }
            else Set(request.TransactionId, ChangeoverPhase.RecoveryRequired, detail: ex.Message);
        }
        return Status(request.TransactionId)!;
    }

    /// <summary>Power-loss recovery is fail-closed: expose evidence, do not infer peer readiness.</summary>
    public ChangeoverStatus Recover(Guid id, ISimChangeoverNode master, ISimChangeoverNode slave)
    {
        var current = Status(id) ?? throw new KeyNotFoundException("Unknown transaction.");
        if (current.Phase is ChangeoverPhase.Committed or ChangeoverPhase.Aborted)
            return current;
        var m = master.Snapshot(); var s = slave.Snapshot();
        if (current.Phase == ChangeoverPhase.Preparing &&
            m.Phase is NodeChangeoverPhase.Idle or NodeChangeoverPhase.Prepared &&
            s.Phase is NodeChangeoverPhase.Idle or NodeChangeoverPhase.Prepared)
        {
            if (m.Phase == NodeChangeoverPhase.Prepared) master.Abort(id, "CRASH_BEFORE_COMMIT_INTENT");
            if (s.Phase == NodeChangeoverPhase.Prepared) slave.Abort(id, "CRASH_BEFORE_COMMIT_INTENT");
            Set(id, ChangeoverPhase.Aborted, detail: "Aborted safely before commit intent.");
        }
        else Set(id, ChangeoverPhase.RecoveryRequired,
            detail: "MANUAL_RECONCILIATION_REQUIRED: both nodes retained in safe hold.");
        return Status(id)!;
    }
}
