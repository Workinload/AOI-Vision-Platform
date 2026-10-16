using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;

namespace AOI.Diagnostics;

/// <summary>SQLite READ ONLY connection to existing A0 per-Core production.db. Never performs migrations or writes.</summary>
public static class ReadOnlyCapture
{
    public static DiagnosticSnapshot Capture(string root, DateTimeOffset? clock = null)
    {
        var nodes = new[] { CaptureNode(root, "master"), CaptureNode(root, "slave") };
        return new DiagnosticSnapshot(DiagnosticsFormat.SnapshotVersion, DiagnosticsFormat.SimMode,
            clock ?? DateTimeOffset.UtcNow, nodes);
    }

    private static NodeEvidence CaptureNode(string root, string node)
    {
        string path = Path.Combine(Path.GetFullPath(root), node, "production.db");
        if (!File.Exists(path))
            return new NodeEvidence(node, false, [], [], [], [], []);
        var builder = new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        };
        using var db = new SqliteConnection(builder.ToString());
        db.Open();
        var workpieces = new List<WorkpieceEvidence>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT identity_json, final_json FROM workpieces ORDER BY workpiece_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                workpieces.Add(new WorkpieceEvidence(WireFormat.Parse<WorkpieceIdentity>(r.GetString(0)) with { Barcode = "[REDACTED]" },
                    r.IsDBNull(1) ? null : WireFormat.Parse<FinalDecision>(r.GetString(1))));
        }
        var results = new List<ChannelResult>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT result_json FROM channel_results ORDER BY workpiece_id,channel_id,attempt_no";
            using var r = cmd.ExecuteReader();
            while (r.Read()) results.Add(WireFormat.Parse<ChannelResult>(r.GetString(0)));
        }
        var outbox = new List<OutboxEvidence>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT message_id,payload_hash,envelope_json,created_utc,acknowledged_utc FROM outbox ORDER BY message_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                Guid id = Guid.Parse(r.GetString(0));
                string hash = r.GetString(1);
                string envelopeJson = r.GetString(2);
                var envelope = WireFormat.Parse<PeerEnvelope>(envelopeJson);
                bool valid = true;
                try { envelope.Validate(); }
                catch (Exception) { valid = false; }
                outbox.Add(new OutboxEvidence(id, envelope.SenderNodeId, envelope.ReceiverNodeId,
                    envelope.WorkpieceId, envelope.TraceId, envelope.Epoch, envelope.SolutionReleaseId,
                    envelope.PayloadType, envelope.ChannelId, hash,
                    DateTimeOffset.Parse(r.GetString(3)),
                    r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)),
                    valid && envelope.MessageId == id && envelope.PayloadHash == hash));
            }
        }
        var inbox = new List<InboxEvidence>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT sender_node_id,message_id,payload_hash,received_utc FROM inbox ORDER BY sender_node_id,message_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                inbox.Add(new InboxEvidence(r.GetString(0), Guid.Parse(r.GetString(1)),
                    r.GetString(2), DateTimeOffset.Parse(r.GetString(3))));
        }
        var events = new List<FlightEventEvidence>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT event_id,utc,kind,workpiece_id,message_id,details FROM flight_events ORDER BY utc,event_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // Existing free-text details may contain production identifiers: deliberately never export it.
                events.Add(new FlightEventEvidence(Guid.Parse(r.GetString(0)),
                    DateTimeOffset.Parse(r.GetString(1)), r.GetString(2),
                    r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)),
                    r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4)), "[REDACTED]"));
            }
        }
        return new NodeEvidence(node, true, workpieces.ToArray(), results.ToArray(),
            outbox.ToArray(), inbox.ToArray(), events.ToArray());
    }
}
