using AOI.Domain;

namespace AOI.Diagnostics;

/// <summary>Deterministic evidence-only replay. Findings are advisory HOLDs, never PLC commands.</summary>
public static class DiagnosticAnalyzer
{
    public static DiagnosticAssessment Analyze(DiagnosticSnapshot snapshot, TimeSpan staleAfter)
    {
        if (snapshot.SchemaVersion != DiagnosticsFormat.SnapshotVersion ||
            snapshot.Mode != DiagnosticsFormat.SimMode || staleAfter <= TimeSpan.Zero)
            throw new ArgumentException("Unsupported mode or liveness threshold.");
        var findings = new List<DiagnosticFinding>();
        void Add(string severity, string code, string node, Guid? id, string proof) =>
            findings.Add(new(severity, code, node, id, proof));

        var nodes = snapshot.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        foreach (var expected in new[] { "master", "slave" })
            if (!nodes.TryGetValue(expected, out var present) || !present.DatabaseAvailable)
                Add("ERROR", "NODE_DB_MISSING", expected, null, "Independent Core WAL unavailable.");

        var identities = new Dictionary<Guid, WorkpieceIdentity>();
        foreach (var node in snapshot.Nodes)
        {
            foreach (var wp in node.Workpieces)
            {
                var id = wp.Identity;
                if (identities.TryGetValue(id.WorkpieceId, out var prior) && prior != id)
                    Add("CRITICAL", "CROSS_NODE_IDENTITY_CONFLICT", node.NodeId, id.WorkpieceId,
                        "Same WorkpieceId differs in epoch/release/trace/barcode across independent Core WALs.");
                else identities[id.WorkpieceId] = id;
                if (id.CreatedAtUtc > snapshot.CapturedUtc)
                    Add("WARNING", "FUTURE_WORKPIECE_TIME", node.NodeId, id.WorkpieceId, "Workpiece timestamp is ahead of snapshot.");
                if (wp.Final is null && snapshot.CapturedUtc - id.CreatedAtUtc > staleAfter)
                    Add("ERROR", "WORKPIECE_STALLED", node.NodeId, id.WorkpieceId,
                        $"No persisted final by threshold {staleAfter.TotalSeconds:F0}s (not physical conveyor proof).");
                if (wp.Final is { Disposition: FinalDisposition.Good } final &&
                    final.IsSimulation == false && node.Results.Any(r => r.WorkpieceId == id.WorkpieceId && r.IsSimulated))
                    Add("CRITICAL", "SIMULATION_AS_PRODUCTION", node.NodeId, id.WorkpieceId,
                        "Simulated pixel result appears under non-simulation GOOD.");
                if (wp.Final is { Disposition: FinalDisposition.Good } && node.NodeId == "master" &&
                    !node.Results.Any(r => r.WorkpieceId == id.WorkpieceId))
                    Add("CRITICAL", "FINAL_WITHOUT_RESULTS", node.NodeId, id.WorkpieceId,
                        "GOOD persisted without any local or peer results.");
            }
            var localIds = node.Workpieces.ToDictionary(w => w.Identity.WorkpieceId, w => w.Identity);
            foreach (var r in node.Results)
            {
                if (!localIds.TryGetValue(r.WorkpieceId, out var wp))
                {
                    Add("CRITICAL", "ORPHAN_RESULT", node.NodeId, r.WorkpieceId, "Result lacks local registered workpiece.");
                    continue;
                }
                try { r.ValidateFor(wp); }
                catch (Exception)
                {
                    Add("CRITICAL", "RESULT_IDENTITY_OR_CHANNEL_MISMATCH", node.NodeId, r.WorkpieceId,
                        $"Channel={r.ChannelId}; capture/result identity validation failed.");
                }
            }
            foreach (var duplicate in node.Results.GroupBy(r =>
                (r.WorkpieceId, r.ChannelId, r.Epoch, r.ReleaseId, r.AttemptNo)).Where(g => g.Count() > 1))
                Add("CRITICAL", "DUPLICATE_ATTEMPT", node.NodeId, duplicate.Key.WorkpieceId,
                    $"Repeated attempt for {duplicate.Key.ChannelId}.");
            foreach (var reused in node.Results.GroupBy(r => r.FrameId).Where(g =>
                g.Select(x => x.WorkpieceId).Distinct().Count() > 1))
                Add("CRITICAL", "FRAME_REUSED_ACROSS_WORKPIECES", node.NodeId, null,
                    $"FrameId={reused.Key} tied to different physical workpiece identities.");
            foreach (var reused in node.Results.GroupBy(r => r.CaptureId).Where(g =>
                g.Select(x => x.WorkpieceId).Distinct().Count() > 1))
                Add("CRITICAL", "CAPTURE_REUSED_ACROSS_WORKPIECES", node.NodeId, null,
                    $"CaptureId={reused.Key} tied to different physical workpiece identities.");
            foreach (var msg in node.Outbox)
            {
                if (!msg.EnvelopeValid)
                    Add("CRITICAL", "OUTBOX_ENVELOPE_CORRUPT", node.NodeId, msg.WorkpieceId,
                        $"Message={msg.MessageId}; envelope/hash mismatch.");
                if (msg.SenderNodeId != node.NodeId)
                    Add("CRITICAL", "OUTBOX_WRONG_SENDER", node.NodeId, msg.WorkpieceId,
                        $"Message={msg.MessageId}; wrong sender.");
                if (msg.AcknowledgedUtc is null && snapshot.CapturedUtc - msg.CreatedUtc > staleAfter)
                    Add("ERROR", "OUTBOX_ACK_STALLED", node.NodeId, msg.WorkpieceId,
                        $"Message={msg.MessageId}; durable ACK missing beyond threshold.");
                if (nodes.TryGetValue(msg.ReceiverNodeId, out var receiver) && receiver.DatabaseAvailable)
                {
                    var matching = receiver.Inbox.Where(x => x.MessageId == msg.MessageId &&
                        x.SenderNodeId == msg.SenderNodeId).ToArray();
                    if (matching.Length == 0 && msg.AcknowledgedUtc is not null)
                        Add("CRITICAL", "ACK_WITHOUT_RECEIVER_INBOX", node.NodeId, msg.WorkpieceId,
                            $"Message={msg.MessageId}; sender ACK present without receiver durable inbox.");
                    foreach (var inbound in matching)
                        if (inbound.PayloadHash != msg.PayloadHash)
                            Add("CRITICAL", "PEER_PAYLOAD_HASH_CONFLICT", node.NodeId, msg.WorkpieceId,
                                $"Message={msg.MessageId}; sender/receiver digest differs.");
                }
            }
        }
        return new DiagnosticAssessment(snapshot.CapturedUtc, identities.Count,
            snapshot.Nodes.Sum(x => x.Results.Length), findings
                .OrderBy(f => f.Severity, StringComparer.Ordinal)
                .ThenBy(f => f.Code, StringComparer.Ordinal)
                .ThenBy(f => f.NodeId, StringComparer.Ordinal)
                .ThenBy(f => f.WorkpieceId).ToArray());
    }

    /// <summary>Reproduce a sequence offline without opening original databases or any socket.</summary>
    public static DiagnosticAssessment[] Replay(string journalPath, TimeSpan staleAfter) =>
        FlightRecorder.ReadVerified(journalPath)
            .Select(entry => Analyze(entry.Snapshot, staleAfter)).ToArray();
}
