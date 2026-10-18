using AOI.Contracts;
using AOI.Diagnostics;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using AOI.Runtime.Plc;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AOI.Diagnostics.Tests;

[TestFixture]
public sealed class CrossLedgerReconciliationTests
{
    private string _root = "";
    private string Master => Path.Combine(_root, "master");
    private string A1Path => Path.Combine(Master, "final-plc-output.db");
    private string A7Path => Path.Combine(Master, ProductionTimingJournal.FileName);

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "a7-crossledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Master);
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static WorkpieceIdentity Item() =>
        WorkpieceIdentity.New("REPEATED-BARCODE", 8, "RELEASE_CROSSLEDGER");

    private static byte[] Read(ushort addr, byte sid) =>
        LegacyFinsWire.MakeRequest(false, addr, new ushort[] { 0 },
            sid, LegacyFinsNodes.Demo);

    private static byte[] Write(ushort addr, ushort value, byte sid) =>
        LegacyFinsWire.MakeRequest(true, addr, new[] { value },
            sid, LegacyFinsNodes.Demo);

    private static byte[] Reply(byte[] req, ushort code = 0, ushort? word = null) =>
        LegacyFinsWire.MakeResponse(req, code,
            word is ushort n ? new[] { n } : Array.Empty<ushort>());

    private (ProductionStore Store, FinalPlcOutputJournal A1,
        FinalOutputAuditJournal A7, Guid Intent, Guid Attempt) Started(
        WorkpieceIdentity item, LegacyExitScenario scenario,
        FinalDisposition disposition, ushort token = 3)
    {
        var store = new ProductionStore(Path.Combine(Master, "production.db"));
        store.CreateAndQueueRegistration(item,
            PeerEnvelope.Create("master", "slave", token, item,
                "workpiece_registered", item));
        if (scenario == LegacyExitScenario.Normal)
        {
            store.StoreFinal(item, new FinalDecision(disposition, true,
                ["CH02"], ["CH02"], "COMPLETE_SIM_ONLY"));
        }
        else
        {
            store.RecordWorkpieceHold(item, "END_BARCODE_MISMATCH",
                "Simulated end-scanner failure has already been persisted.");
        }
        var a1 = new FinalPlcOutputJournal(A1Path);
        var progress = a1.Prepare(store, item, token, scenario, disposition);
        var a7 = new FinalOutputAuditJournal(Master);
        var a7Scenario = scenario switch
        {
            LegacyExitScenario.Normal => FinalOutputAuditScenario.Normal,
            LegacyExitScenario.BarcodeFailure => FinalOutputAuditScenario.BarcodeFailure,
            _ => FinalOutputAuditScenario.BarcodeMismatch
        };
        Guid attempt = a7.Begin(item, token, a7Scenario, disposition);
        var scanner = Read(2099, 4);
        a7.ScannerRequestVerified(attempt, Guid.NewGuid(),
            scanner, Reply(scanner, word: 1));
        return (store, a1, a7, progress.Intent.IntentId, attempt);
    }

    private static FinalOutputWriteClaim Step(FinalPlcOutputJournal a1,
        FinalOutputAuditJournal a7, Guid intent, Guid attempt, byte sid,
        bool acknowledge = true, bool readback = true)
    {
        var claim = a1.ClaimNextWrite(intent)!;
        Assert.That(claim, Is.Not.Null);
        var request = Write(claim.DmAddress, claim.Value, sid);
        var sent = new LegacyFinsWriteProof(claim.ClaimId, sid,
            claim.DmAddress, claim.Value, DateTimeOffset.UtcNow,
            AcknowledgedUtc: null, ReplyVerified: false);
        Assert.That(a1.ObserveWireSend(claim.ClaimId, sent), Is.True);
        a7.PrepareStep(attempt, claim.StepIndex + 1, claim.ClaimId, request);
        a7.WireSent(attempt, claim.StepIndex + 1, claim.ClaimId);
        if (acknowledge)
        {
            a7.RecordWriteReply(attempt, claim.StepIndex + 1, claim.ClaimId,
                request, Reply(request));
            var ack = sent with { AcknowledgedUtc = DateTimeOffset.UtcNow, ReplyVerified = true };
            Assert.That(a1.ConfirmFinsAck(claim.ClaimId, ack), Is.True);
            if (readback)
            {
                byte[] read = Read(claim.DmAddress, (byte)(sid + 20));
                a7.RecordReadback(attempt, claim.StepIndex + 1,
                    Guid.NewGuid(), read, Reply(read, word: claim.Value));
                Assert.That(a1.ConfirmReadback(claim.ClaimId, claim.Value), Is.True);
            }
        }
        return claim;
    }

    [TestCase(LegacyExitScenario.Normal, FinalDisposition.Good)]
    [TestCase(LegacyExitScenario.Normal, FinalDisposition.Reject)]
    [TestCase(LegacyExitScenario.BarcodeFailure, FinalDisposition.Reject)]
    [TestCase(LegacyExitScenario.BarcodeMismatch, FinalDisposition.Reject)]
    public void ActualA1OneTimeClaimsAndA7RawPacketWalsAgreeOnBothLegacySteps(
        LegacyExitScenario scenario, FinalDisposition disposition)
    {
        var item = Item();
        var context = Started(item, scenario, disposition);
        using (context.A1)
        {
            Step(context.A1, context.A7, context.Intent, context.Attempt, 41);
            Step(context.A1, context.A7, context.Intent, context.Attempt, 42);
        }

        var result = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CrossLedgerStatus.CONFIRMED),
                string.Join(", ", result.Findings.Select(f => f.Code)));
            Assert.That(result.Findings, Is.Empty);
            Assert.That(result.Steps.Length, Is.EqualTo(2));
            Assert.That(result.Steps.All(s => s.BothLedgersConfirmed), Is.True);
            Assert.That(result.Steps.Select(s => s.ClaimId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(result.Steps.Select(s => s.AuthoritySid),
                Is.EqualTo(new byte?[] { 41, 42 }));
            Assert.That(result.Steps.Select(s => s.AuditSid),
                Is.EqualTo(new byte?[] { 41, 42 }));
            Assert.That(result.Steps.All(s => s.RawRequestSha256?.Length == 64 &&
                s.RawResponseSha256?.Length == 64 &&
                s.ReadRequestSha256?.Length == 64 &&
                s.ReadResponseSha256?.Length == 64), Is.True);
            Assert.That(result.RealPlcWriteAuthorized, Is.False);
            Assert.That(result.PhysicalEjectionVerified, Is.False,
                "Two verified FINS replies never prove physical ejection.");
        });
        TestContext.Progress.WriteLine($"A7_CROSS_LEDGER_CONFIRMED scenario={scenario} " +
            $"workpiece={item.WorkpieceId:D} token=3 claims=2 sid=41,42 " +
            $"steps={string.Join(",", result.Steps.Select(s => "D" + s.DmAddress + "=" + s.Value))} " +
            "raw_sha256=2 ack=2 readback=2 no_physical_ejection_claim=1");
    }

    [Test]
    public void FirstWriteAckLostAndNoSecondRequest_RemainsAckUnknown()
    {
        var item = Item();
        var s = Started(item, LegacyExitScenario.BarcodeMismatch, FinalDisposition.Reject);
        using (s.A1)
        {
            var claim = Step(s.A1, s.A7, s.Intent, s.Attempt, 51, acknowledge: false);
            s.A7.AckUnknown(s.Attempt, 1, claim.ClaimId, "UDP_RECEIVE_TIMEOUT");
            s.A1.MarkUnknown(claim.ClaimId, "FINS_ACK_OR_READBACK_UNKNOWN");
        }
        var report = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(report.Status, Is.EqualTo(CrossLedgerStatus.ACK_UNKNOWN),
            string.Join(",", report.Findings.Select(f => f.Code)));
        Assert.That(report.Steps[1].ClaimId, Is.Null);
        Assert.That(report.RequiresManualReconciliation, Is.True);
        TestContext.Progress.WriteLine($"A7_CROSS_LEDGER_ACK_UNKNOWN workpiece={item.WorkpieceId:D} " +
            "first=D2099:4 second=ABSENT no_replay=1");
    }

    [Test]
    public void AckedButIndependentReadbackAbsent_IsReadbackUnknownNotConfirmed()
    {
        var item = Item();
        var s = Started(item, LegacyExitScenario.Normal, FinalDisposition.Good);
        using (s.A1)
        {
            var claim = Step(s.A1, s.A7, s.Intent, s.Attempt, 50, readback: false);
            s.A7.ReadbackUnknown(s.Attempt, 1, Guid.NewGuid(), "FINS_READBACK_NOT_PROVEN");
            s.A1.MarkUnknown(claim.ClaimId, "FINS_ACK_OR_READBACK_UNKNOWN");
        }
        var report = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(report.Status, Is.EqualTo(CrossLedgerStatus.READBACK_UNKNOWN),
            string.Join(",", report.Findings.Select(f => f.Code)));
        Assert.That(report.RequiresManualReconciliation, Is.True);
    }

    [Test]
    public void OnlyOneVerifiedWord_NeverImpliesSecondWordWasWritten()
    {
        var item = Item();
        var s = Started(item, LegacyExitScenario.Normal, FinalDisposition.Good);
        using (s.A1)
            Step(s.A1, s.A7, s.Intent, s.Attempt, 63);
        var report = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(report.Status, Is.EqualTo(CrossLedgerStatus.INCOMPLETE),
            string.Join(",", report.Findings.Select(f => f.Code)));
        Assert.That(report.Findings.Select(f => f.Code), Does.Contain("STEP_2_NOT_STARTED"));
    }

    [Test]
    public void MissingAuditDbIsFailClosedAndNeverInterpretsA1GoodAsPlcConfirmed()
    {
        var item = Item();
        var store = new ProductionStore(Path.Combine(Master, "production.db"));
        store.CreateAndQueueRegistration(item, PeerEnvelope.Create("master",
            "slave", 1, item, "workpiece_registered", item));
        store.StoreFinal(item, new FinalDecision(FinalDisposition.Good, true,
            ["CH02"], ["CH02"], "COMPLETE_SIM_ONLY"));
        using (var a1 = new FinalPlcOutputJournal(A1Path))
            _ = a1.Prepare(store, item, 1, LegacyExitScenario.Normal, FinalDisposition.Good);
        var report = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(report.Status, Is.EqualTo(CrossLedgerStatus.AUDIT_MISSING));
        Assert.That(report.PhysicalEjectionVerified, Is.False);
    }

    [Test]
    public void LiveCoreMustNotBeReadOrCheckpointed_ExclusiveLeaseRequired()
    {
        var item = Item();
        var s = Started(item, LegacyExitScenario.Normal, FinalDisposition.Good);
        using (s.A1)
        {
            var report = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
            Assert.That(report.Status, Is.EqualTo(CrossLedgerStatus.CONFLICT));
            Assert.That(report.Findings.Select(f => f.Code),
                Does.Contain("SOURCE_NOT_QUIESCENT"));
        }
    }

    [Test]
    public void ForgedSidAndSwappedIdentityCauseConflict()
    {
        var item = Item();
        var s = Started(item, LegacyExitScenario.Normal, FinalDisposition.Good);
        using (s.A1)
        {
            Step(s.A1, s.A7, s.Intent, s.Attempt, 27);
            Step(s.A1, s.A7, s.Intent, s.Attempt, 28);
        }
        Update(A7Path, """
            UPDATE final_output_audit_events SET fins_sid=99
             WHERE phase='StepPrepared' AND step_number=1
            """);
        var wrongSid = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(wrongSid.Status, Is.EqualTo(CrossLedgerStatus.CONFLICT));
        Assert.That(wrongSid.Findings.Select(f => f.Code),
            Does.Contain("STEP_1_SID_OR_SEND_CONFLICT"));

        Update(A7Path, """
            UPDATE final_output_audit_events SET fins_sid=27
             WHERE phase='StepPrepared' AND step_number=1
            """);
        var alternate = Guid.NewGuid();
        Update(A1Path, """
            UPDATE a1_final_output_events
               SET event_json = json_set(event_json,
                   '$.intent.workpiece.workpieceId', $other)
             WHERE sequence = (
                 SELECT min(sequence) FROM a1_final_output_events)
            """, ("$other", alternate.ToString("D")));
        var swapped = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(swapped.Status, Is.EqualTo(CrossLedgerStatus.CONFLICT));
        Assert.That(swapped.Findings.Select(f => f.Code),
            Does.Contain("INVALID_LEDGER_EVIDENCE"));
    }

    [Test]
    public void RepeatedAckAndContradictoryReadbackFailClosed()
    {
        var item = Item();
        var s = Started(item, LegacyExitScenario.BarcodeFailure, FinalDisposition.Reject);
        using (s.A1)
        {
            Step(s.A1, s.A7, s.Intent, s.Attempt, 33);
            Step(s.A1, s.A7, s.Intent, s.Attempt, 34);
        }
        Update(A7Path, """
            INSERT INTO final_output_audit_events(
                event_id,attempt_id,phase,step_number,operation_id,fins_sid,
                dm_address,value,request_sha256,response_sha256,readback_value,
                peer_message_id,utc,process_run_id,monotonic_tick,tick_frequency,detail)
            SELECT lower(hex(randomblob(16))),attempt_id,phase,step_number,
                   operation_id,fins_sid,dm_address,value,request_sha256,
                   response_sha256,readback_value,peer_message_id,utc,
                   process_run_id,monotonic_tick,tick_frequency,detail
              FROM final_output_audit_events
             WHERE phase='AckAccepted' AND step_number=1 LIMIT 1
            """);
        var duplicate = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(duplicate.Status, Is.EqualTo(CrossLedgerStatus.CONFLICT));
        Assert.That(duplicate.Findings.Select(f => f.Code),
            Does.Contain("STEP_1_DUPLICATE_A7_AckAccepted"));

        Update(A7Path, """
            DELETE FROM final_output_audit_events
             WHERE sequence = (
               SELECT max(sequence) FROM final_output_audit_events
               WHERE phase = 'AckAccepted' AND step_number = 1)
            """);
        Update(A7Path, """
            UPDATE final_output_audit_events SET readback_value=777
             WHERE phase='ReadbackVerified' AND step_number=1
            """);
        var mismatched = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(mismatched.Status, Is.EqualTo(CrossLedgerStatus.CONFLICT));
        Assert.That(mismatched.Findings.Select(f => f.Code),
            Does.Contain("STEP_1_RAW_READBACK_CONFLICT"));
    }

    [Test]
    public void SourceWalAndShmByteHashesRemainUnchangedAfterSnapshotInspection()
    {
        var item = Item();
        var s = Started(item, LegacyExitScenario.Normal, FinalDisposition.Reject);
        using (s.A1)
        {
            Step(s.A1, s.A7, s.Intent, s.Attempt, 67);
            Step(s.A1, s.A7, s.Intent, s.Attempt, 68);
        }
        var evidence = new[] { A1Path, A1Path + "-wal", A1Path + "-shm",
                               A7Path, A7Path + "-wal", A7Path + "-shm" }
            .Where(File.Exists).ToArray();
        var before = evidence.ToDictionary(p => p,
            p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var report = CrossLedgerReadOnlyReconciliation.Capture(_root, item.WorkpieceId);
        Assert.That(report.Status, Is.EqualTo(CrossLedgerStatus.CONFIRMED));
        foreach (var p in evidence)
            Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),
                Is.EqualTo(before[p]), "Read-only check modified ORIGINAL evidence " + p);
        TestContext.Progress.WriteLine("A7_CROSS_LEDGER_SOURCE_IMMUTABLE_PASS " +
            $"files={evidence.Length} original_sha256_unchanged=1");
    }

    [Test]
    public void BatchQueryUsesOneSnapshotAndDoesNotDeduplicateIdenticalBarcodes()
    {
        var first = Item();
        var second = Item(); // Same barcode, different immutable WorkpieceId.
        var a = Started(first, LegacyExitScenario.Normal, FinalDisposition.Good, token: 1);
        using (a.A1)
        {
            Step(a.A1, a.A7, a.Intent, a.Attempt, 80);
            Step(a.A1, a.A7, a.Intent, a.Attempt, 81);
        }
        var b = Started(second, LegacyExitScenario.BarcodeMismatch,
            FinalDisposition.Reject, token: 2);
        using (b.A1)
        {
            var claim = Step(b.A1, b.A7, b.Intent, b.Attempt, 82, acknowledge: false);
            b.A7.AckUnknown(b.Attempt, 1, claim.ClaimId, "UDP_TIMEOUT");
            b.A1.MarkUnknown(claim.ClaimId, "FINS_ACK_OR_READBACK_UNKNOWN");
        }
        var batch = CrossLedgerReadOnlyReconciliation.CaptureAll(_root, 20);
        Assert.Multiple(() =>
        {
            Assert.That(batch.Truncated, Is.False);
            Assert.That(batch.Workpieces.Length, Is.EqualTo(2));
            Assert.That(batch.Workpieces.Select(x => x.WorkpieceId).Distinct().Count(),
                Is.EqualTo(2));
            Assert.That(batch.Workpieces.Select(x => x.PhysicalToken),
                Is.EquivalentTo(new ushort?[] { 1, 2 }));
            Assert.That(batch.Workpieces.Single(x => x.WorkpieceId == first.WorkpieceId).Status,
                Is.EqualTo(CrossLedgerStatus.CONFIRMED));
            Assert.That(batch.Workpieces.Single(x => x.WorkpieceId == second.WorkpieceId).Status,
                Is.EqualTo(CrossLedgerStatus.ACK_UNKNOWN));
            Assert.That(batch.OverallStatus, Is.EqualTo(CrossLedgerStatus.ACK_UNKNOWN));
        });
        var bounded = CrossLedgerReadOnlyReconciliation.CaptureAll(_root, 1);
        Assert.That(bounded.Truncated, Is.True);
        Assert.That(bounded.Workpieces.Length, Is.EqualTo(1));
        Assert.That(bounded.OverallStatus, Is.Not.EqualTo(CrossLedgerStatus.CONFIRMED));
        TestContext.Progress.WriteLine("A7_CROSS_LEDGER_BATCH_PASS items=2 " +
            "same_barcode=1 confirmed=1 ack_unknown=1 snapshot_count=1 truncation_safe=1");
    }

    private static void Update(string dbPath, string sql,
        params (string Key, string Value)[] args)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        Assert.That(cmd.ExecuteNonQuery(), Is.GreaterThan(0));
    }
}
