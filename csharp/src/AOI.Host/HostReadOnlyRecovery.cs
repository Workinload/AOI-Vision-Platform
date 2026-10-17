using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime.Agent01;
using Microsoft.Data.Sqlite;

namespace AOI.Host;

/// <summary>
/// Read-only operator evidence from an already-stopped SIM_FULL run.
/// Does NOT start a PLC client, take an A1 writer lease, alter a WAL, open a
/// camera or grant any permission to restart, repeat, or release hardware.
/// A stored inspection Good/Reject is NOT a confirmed physical PLC output.
/// </summary>
internal static class HostReadOnlyRecovery
{
    internal sealed record WorkpieceEvidence(
        Guid WorkpieceId, ushort? PhysicalToken, string PhysicalPhase,
        bool InspectionDecisionPersisted, string? WorkpieceHoldCode,
        string PlcOutputEvidence, int PlcAckEvents, int PlcReadbackEvents,
        bool RequiresManualReconciliation);

    internal sealed record Snapshot(
        bool SimOnly, bool RealPlcWritesEnabled, bool AutomaticProductionResumeAllowed,
        DateTimeOffset ObservedUtc, string? MasterPeerOrPlcInterlock,
        string? SlavePeerInterlock, int RegisteredWorkpieces,
        int PhysicallyExitedWorkpieces, int UnverifiedPlcOutputs,
        WorkpieceEvidence[] Workpieces);

    private sealed record OutputEvents(Guid IntentId, FinalOutputEvent[] Events);

    private static SqliteConnection OpenExisting(string file)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("Required recovery evidence is absent.", file);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? PeerInterlock(string folder)
    {
        string file = Path.Combine(folder, "host-peer-interlock.sqlite");
        if (!File.Exists(file)) return null;
        using var db = OpenExisting(file);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT reason_code FROM host_peer_interlock WHERE singleton=1";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// First copy the stopped Core's committed SQLite/WAL evidence, then open
    /// ONLY the temporary copies with SQLite. Windows SQLite may checkpoint
    /// journal sidecars even for a ReadOnly connection: opening the source
    /// would change forensic evidence. The source is never opened by SQLite.
    /// </summary>
    public static Snapshot Capture(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        sourceRoot = Path.GetFullPath(sourceRoot);
        string masterFolder = Path.Combine(sourceRoot, "master");
        if (!File.Exists(Path.Combine(masterFolder, "production.db")))
            throw new FileNotFoundException(
                "No previous Master production WAL to inspect.");

        string snapshotRoot = Path.Combine(Path.GetTempPath(),
            "aoi-crash-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotRoot);
        try
        {
            // A1's writer lease is exclusively held by a live Host. Never
            // take an unsynchronized forensic snapshot while that Core runs.
            string writerLock = Path.Combine(masterFolder,
                "final-plc-output.db.writer-lock");
            using var stoppedCoreLease = File.Exists(writerLock)
                ? new FileStream(writerLock, FileMode.Open, FileAccess.Read, FileShare.None)
                : null;
            foreach (string role in new[] { "master", "slave" })
            {
                string sourceFolder = Path.Combine(sourceRoot, role);
                string targetFolder = Path.Combine(snapshotRoot, role);
                Directory.CreateDirectory(targetFolder);
                foreach (string fileName in new[] {
                    "production.db", "sim-full-cycle.db",
                    "final-plc-output.db", "host-peer-interlock.sqlite" })
                {
                    foreach (string extension in new[] { "", "-wal", "-shm" })
                    {
                        string source = Path.Combine(sourceFolder, fileName + extension);
                        if (File.Exists(source))
                            File.Copy(source, Path.Combine(targetFolder,
                                fileName + extension), overwrite: false);
                    }
                }
            }
            // All SQLite checkpointing/SHM housekeeping is confined to these
            // throwaway copies. The original WAL is byte-for-byte untouched.
            return CaptureCopiedEvidence(snapshotRoot);
        }
        finally
        {
            Directory.Delete(snapshotRoot, recursive: true);
        }
    }

    private static Snapshot CaptureCopiedEvidence(string root)
    {
        string master = Path.Combine(root, "master");
        string slave = Path.Combine(root, "slave");
        var identities = new Dictionary<Guid, bool>();
        var holds = new Dictionary<Guid, string>( );
        using (var db = OpenExisting(Path.Combine(master, "production.db")))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT workpiece_id,final_json FROM workpieces";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                Guid workpiece = Guid.Parse(rows.GetString(0));
                if (workpiece == Guid.Empty ||
                    !identities.TryAdd(workpiece, !rows.IsDBNull(1)))
                    throw new InvalidDataException("Duplicated or invalid Master workpiece identity.");
            }
        }
        using (var db = OpenExisting(Path.Combine(master, "production.db")))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT workpiece_id,reason_code FROM workpiece_holds";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                Guid workpiece = Guid.Parse(rows.GetString(0));
                if (!identities.ContainsKey(workpiece) ||
                    !holds.TryAdd(workpiece, rows.GetString(1)))
                    throw new InvalidDataException("Invalid durable HOLD identity.");
            }
        }

        var conveyor = new Dictionary<Guid, (ushort Token, string Phase)>();
        using (var db = OpenExisting(Path.Combine(master, "sim-full-cycle.db")))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT token,identity_json,phase FROM sim_full_tokens ORDER BY token";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ushort token = checked((ushort)rows.GetInt32(0));
                var item = WireFormat.Parse<WorkpieceIdentity>(rows.GetString(1));
                item.Validate();
                string phase = rows.GetString(2);
                if (token == 0 || phase is not ("registered" or "exited") ||
                    !identities.ContainsKey(item.WorkpieceId) ||
                    !conveyor.TryAdd(item.WorkpieceId, (token, phase)))
                    throw new InvalidDataException("Conveyor and Master identity cannot be reconciled.");
            }
        }

        var outputs = new Dictionary<Guid, OutputEvents>();
        string journalPath = Path.Combine(master, "final-plc-output.db");
        if (File.Exists(journalPath))
        {
            var journalRows = new Dictionary<Guid, List<FinalOutputEvent>>();
            using var db = OpenExisting(journalPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT sequence,intent_id,workpiece_id,event_json
                  FROM a1_final_output_events ORDER BY sequence
                """;
            using var rows = cmd.ExecuteReader();
            long lastSequence = 0;
            while (rows.Read())
            {
                var evt = WireFormat.Parse<FinalOutputEvent>(rows.GetString(3));
                long sequence = rows.GetInt64(0);
                if (sequence <= lastSequence || sequence != evt.Sequence ||
                    evt.WorkpieceId.ToString("D") != rows.GetString(2) ||
                    evt.IntentId.ToString("D") != rows.GetString(1) ||
                    !identities.ContainsKey(evt.WorkpieceId))
                    throw new InvalidDataException("Final PLC WAL has an invalid event identity or sequence.");
                lastSequence = sequence;
                if (!journalRows.TryGetValue(evt.WorkpieceId, out var items))
                    journalRows.Add(evt.WorkpieceId, items = new());
                if (items.Count > 0 && items[0].IntentId != evt.IntentId)
                    throw new InvalidDataException("Duplicate final output intent for one workpiece.");
                if (items.Count == 0 &&
                    (evt.Kind != FinalOutputEventKind.IntentPrepared ||
                     evt.Intent is not { } prepared ||
                     prepared.Workpiece.WorkpieceId != evt.WorkpieceId ||
                     prepared.IntentId != evt.IntentId))
                    throw new InvalidDataException("Final PLC effect has no matching persisted intent.");
                items.Add(evt);
            }
            foreach (var (itemId, events) in journalRows)
                outputs.Add(itemId, new(events[0].IntentId, events.ToArray()));
        }

        var list = new List<WorkpieceEvidence>(identities.Count);
        foreach (var (itemId, inspected) in identities)
        {
            if (!conveyor.TryGetValue(itemId, out var position))
                throw new InvalidDataException("A Master workpiece is missing its original physical token.");
            var audit = outputs.GetValueOrDefault(itemId);
            var events = audit?.Events ?? [];
            int acknowledgements = events.Count(e => e.Kind == FinalOutputEventKind.FinsAckObserved);
            int readbacks = events.Count(e => e.Kind == FinalOutputEventKind.ReadbackConfirmed);
            int uniqueReadbacks = events.Where(e => e.Kind == FinalOutputEventKind.ReadbackConfirmed)
                .Select(e => e.StepIndex).Distinct().Count();
            bool unknown = events.Any(e => e.Kind == FinalOutputEventKind.OutcomeUnknown);
            bool claimed = events.Any(e => e.Kind == FinalOutputEventKind.WriteClaimed);
            string state = unknown ? "A1_OUTPUT_ACK_OR_READBACK_UNKNOWN" :
                acknowledgements == 2 && readbacks == 2 && uniqueReadbacks == 2
                    ? "A1_TWO_FINS_STEPS_ACK_AND_READBACK_RECORDED" :
                claimed ? "A1_OUTPUT_INCOMPLETE_POSSIBLY_SENT" :
                events.Length > 0 ? "A1_INTENT_PREPARED_NO_OUTPUT_PROOF" :
                "NO_A1_FINAL_PLC_OUTPUT_EVIDENCE";
            // NO external/ejection permissions can be inferred from detection
            // final, register end values, or an A1 audit-only output projection.
            list.Add(new(itemId, position.Token, position.Phase, inspected,
                holds.GetValueOrDefault(itemId), state, acknowledgements, readbacks,
                position.Phase != "exited" || !inspected ||
                holds.ContainsKey(itemId) || unknown ||
                state != "A1_TWO_FINS_STEPS_ACK_AND_READBACK_RECORDED"));
        }

        var sorted = list.OrderBy(w => w.PhysicalToken).ToArray();
        return new(true, false, false, DateTimeOffset.UtcNow,
            PeerInterlock(master), PeerInterlock(slave),
            sorted.Length, sorted.Count(i => i.PhysicalPhase == "exited"),
            sorted.Count(i => i.PlcOutputEvidence !=
                "A1_TWO_FINS_STEPS_ACK_AND_READBACK_RECORDED"),
            sorted);
    }
}
