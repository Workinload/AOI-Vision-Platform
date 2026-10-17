using AOI.Diagnostics;
using AOI.Domain;
using AOI.Runtime.Plc;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AOI.Diagnostics.Tests;

[TestFixture]
public sealed class FinalOutputAuditTests
{
    private string _root = "";
    private const ushort Token = 3;
    private static readonly LegacyFinsNodes Nodes = LegacyFinsNodes.Demo;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "a7-final-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private FinalOutputAuditJournal Journal() =>
        new(Path.Combine(_root, "master"));

    private static byte[] ReadRequest(ushort address, byte sid) =>
        LegacyFinsWire.MakeRequest(false, address, new ushort[] { 0 }, sid, Nodes);

    private static byte[] WriteRequest(ushort address, ushort value, byte sid) =>
        LegacyFinsWire.MakeRequest(true, address, new ushort[] { value }, sid, Nodes);

    private static byte[] Reply(byte[] request, ushort code = 0, ushort? readback = null) =>
        LegacyFinsWire.MakeResponse(request, code,
            readback is null ? Array.Empty<ushort>() : new ushort[] { readback.Value });

    private static WorkpieceIdentity Workpiece() =>
        WorkpieceIdentity.New("CAN-REPEAT-SCANNER", 7, "SIM_RELEASE_P0");

    private static Guid BeginWithScan(FinalOutputAuditJournal journal, WorkpieceIdentity item,
        FinalOutputAuditScenario scenario, FinalDisposition disposition, Guid? message = null)
    {
        Guid attempt = journal.Begin(item, Token, scenario, disposition, message);
        byte[] request = ReadRequest(2099, 3);
        journal.ScannerRequestVerified(attempt, Guid.NewGuid(), request, Reply(request, readback: 1));
        return attempt;
    }

    private static void WriteConfirmed(FinalOutputAuditJournal journal, Guid attempt,
        int step, ushort address, ushort value, byte sid, ushort? actualReadback = null)
    {
        Guid op = Guid.NewGuid();
        var raw = WriteRequest(address, value, sid);
        journal.PrepareStep(attempt, step, op, raw);
        journal.WireSent(attempt, step, op);
        journal.RecordWriteReply(attempt, step, op, raw, Reply(raw));
        byte[] read = ReadRequest(address, (byte)(sid + 20));
        journal.RecordReadback(attempt, step, Guid.NewGuid(),
            read, Reply(read, readback: actualReadback ?? value));
    }

    [TestCase(FinalOutputAuditScenario.Normal, FinalDisposition.Good, 1016, 1, 2099, 2)]
    [TestCase(FinalOutputAuditScenario.Normal, FinalDisposition.Reject, 1016, 2, 2099, 2)]
    [TestCase(FinalOutputAuditScenario.BarcodeFailure, FinalDisposition.Reject, 2099, 3, 1016, 2)]
    [TestCase(FinalOutputAuditScenario.BarcodeMismatch, FinalDisposition.Reject, 2099, 4, 1016, 2)]
    public void BothActualFinsDatagramsAndReadbacks_PersistInOldMachineOrder(
        FinalOutputAuditScenario scenario, FinalDisposition disposition,
        int firstAddressRaw, int firstValueRaw, int secondAddressRaw, int secondValueRaw)
    {
        ushort firstAddress = checked((ushort)firstAddressRaw);
        ushort firstValue = checked((ushort)firstValueRaw);
        ushort secondAddress = checked((ushort)secondAddressRaw);
        ushort secondValue = checked((ushort)secondValueRaw);
        var item = Workpiece();
        var j = Journal();
        Guid message = Guid.NewGuid();
        Guid id = BeginWithScan(j, item, scenario, disposition, message);
        WriteConfirmed(j, id, 1, firstAddress, firstValue, 17);
        WriteConfirmed(j, id, 2, secondAddress, secondValue, 18);
        j.LinkPersistedPeerMessage(id, message, "PEER_INBOX_COMMITTED");

        var r = new FinalOutputAuditReader(_root);
        var trace = r.Read(item.WorkpieceId)!;
        Assert.That(trace, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(trace.FullyVerified, Is.True, string.Join(",", trace.Findings));
            Assert.That(trace.RequiresManualReconciliation, Is.False);
            Assert.That(trace.ScannerRequestVerified, Is.True);
            Assert.That(trace.Steps.Select(x => x.State),
                Is.EqualTo(new[] {
                    FinalOutputAuditStepState.Verified, FinalOutputAuditStepState.Verified }));
            Assert.That(trace.Steps.Select(x => x.DmAddress),
                Is.EqualTo(new[] { firstAddress, secondAddress }));
            Assert.That(trace.Steps.Select(x => x.ExpectedValue),
                Is.EqualTo(new[] { firstValue, secondValue }));
            Assert.That(trace.Steps.Select(x => x.FinsSid), Is.EqualTo(new byte?[] { 17, 18 }));
            Assert.That(trace.Steps.All(x => x.WriteRequestSha256?.Length == 64 &&
                x.WriteResponseSha256?.Length == 64 &&
                x.ReadRequestSha256?.Length == 64 &&
                x.ReadResponseSha256?.Length == 64), Is.True);
        });
        string realWriteHash = FinalOutputFinsEvidence.Sha256(
            WriteRequest(firstAddress, firstValue, 17));
        Assert.That(trace.Steps[0].WriteRequestSha256, Is.EqualTo(realWriteHash));
        Assert.That(r.SearchByPeerMessageId(message).Select(t => t.WorkpieceId),
            Is.EqualTo(new[] { item.WorkpieceId }));
        Assert.That(trace.Events.Where(e => e.StepNumber > 0)
            .Select(e => e.Sequence), Is.Ordered.Ascending);
        TestContext.Progress.WriteLine(
            $"A7_FINAL_TWO_STEP_PASS scenario={scenario} workpiece={item.WorkpieceId:D} " +
            $"first=D{firstAddress}:{firstValue} second=D{secondAddress}:{secondValue} " +
            $"sid=17,18 packet_sha256={realWriteHash} complete=1");
    }

    [Test]
    public void LostAckAfterActualSend_MustRemainUnknownAcrossProcessRestartWithoutRetry()
    {
        var item = Workpiece();
        var writer = Journal();
        Guid id = BeginWithScan(writer, item,
            FinalOutputAuditScenario.BarcodeMismatch, FinalDisposition.Reject);
        Guid op = Guid.NewGuid();
        byte[] request = WriteRequest(2099, 4, 55);
        writer.PrepareStep(id, 1, op, request);
        writer.WireSent(id, 1, op);
        writer.AckUnknown(id, 1, op, "UDP_RECEIVE_TIMEOUT");
        var trace = new FinalOutputAuditReader(_root).Read(item.WorkpieceId)!;
        Assert.That(trace.Steps[0].State, Is.EqualTo(FinalOutputAuditStepState.SentAckUnknown));
        Assert.That(trace.Steps[1].State, Is.EqualTo(FinalOutputAuditStepState.NotStarted));
        Assert.That(trace.RequiresManualReconciliation, Is.True);
        Assert.Throws<InvalidOperationException>(() =>
            writer.PrepareStep(id, 2, Guid.NewGuid(), WriteRequest(1016, 2, 56)));
        var afterRestart = Journal();
        Assert.Throws<InvalidOperationException>(() =>
            afterRestart.Begin(item, Token, FinalOutputAuditScenario.BarcodeMismatch,
                FinalDisposition.Reject));
        Assert.Throws<InvalidOperationException>(() => afterRestart.WireSent(id, 1, op));
        var stable = new FinalOutputAuditReader(_root).Read(item.WorkpieceId)!;
        Assert.That(stable.Steps[0].State, Is.EqualTo(FinalOutputAuditStepState.SentAckUnknown));
        TestContext.Progress.WriteLine($"A7_FINAL_ACK_UNKNOWN_PASS workpiece={item.WorkpieceId:D} " +
            "step1=D2099:4 sid=55 no_automatic_retry=1");
    }

    [Test]
    public void PreparedBeforeCrash_IsNeverMislabelledAsKnownNotSent()
    {
        var item = Workpiece();
        var j = Journal();
        Guid id = BeginWithScan(j, item, FinalOutputAuditScenario.Normal, FinalDisposition.Good);
        j.PrepareStep(id, 1, Guid.NewGuid(), WriteRequest(1016, 1, 71));
        var r = new FinalOutputAuditReader(_root).Read(item.WorkpieceId)!;
        Assert.That(r.Steps[0].State,
            Is.EqualTo(FinalOutputAuditStepState.PreparedSendUncertain));
        Assert.That(r.Findings, Does.Contain("STEP_1_PREPARED_SEND_OUTCOME_UNKNOWN"));
        Assert.That(r.FullyVerified, Is.False);
        Assert.That(r.RequiresManualReconciliation, Is.True);
    }

    [Test]
    public void ExplicitlyNotSent_RequiresAdapterProofBeforeSocketDispatch()
    {
        var item = Workpiece();
        var j = Journal();
        Guid id = BeginWithScan(j, item,
            FinalOutputAuditScenario.BarcodeFailure, FinalDisposition.Reject);
        Guid op = Guid.NewGuid();
        j.PrepareStep(id, 1, op, WriteRequest(2099, 3, 25));
        j.ExplicitlyNotSent(id, 1, op, "TRANSPORT_PREFLIGHT_REJECTED_BEFORE_SEND");
        Assert.Throws<InvalidOperationException>(() => j.WireSent(id, 1, op));
        var trace = new FinalOutputAuditReader(_root).Read(item.WorkpieceId)!;
        Assert.That(trace.Steps[0].State,
            Is.EqualTo(FinalOutputAuditStepState.ExplicitlyNotSent));
        Assert.That(trace.Findings, Does.Contain("STEP_1_EXPLICITLY_NOT_SENT"));
    }

    [Test]
    public void RefusedFinsResponse_RecordsRawReplyHashAndBlocksSecondStep()
    {
        var item = Workpiece();
        var j = Journal();
        Guid id = BeginWithScan(j, item,
            FinalOutputAuditScenario.BarcodeFailure, FinalDisposition.Reject);
        Guid op = Guid.NewGuid();
        byte[] request = WriteRequest(2099, 3, 40);
        j.PrepareStep(id, 1, op, request);
        j.WireSent(id, 1, op);
        byte[] refused = Reply(request, code: 5);
        j.RecordWriteReply(id, 1, op, request, refused);
        var trace = new FinalOutputAuditReader(_root).Read(item.WorkpieceId)!;
        Assert.That(trace.Steps[0].State, Is.EqualTo(FinalOutputAuditStepState.AckRejected));
        Assert.That(trace.Steps[0].WriteResponseSha256,
            Is.EqualTo(FinalOutputFinsEvidence.Sha256(refused)));
        Assert.Throws<InvalidOperationException>(() =>
            j.PrepareStep(id, 2, Guid.NewGuid(), WriteRequest(1016, 2, 41)));
    }

    [Test]
    public void WrongSidAndWrongReadback_DoNotProduceConfirmedOutput()
    {
        var item = Workpiece();
        var j = Journal();
        Guid id = BeginWithScan(j, item, FinalOutputAuditScenario.Normal, FinalDisposition.Good);
        Guid op = Guid.NewGuid();
        var request = WriteRequest(1016, 1, 29);
        j.PrepareStep(id, 1, op, request);
        j.WireSent(id, 1, op);
        byte[] otherSid = WriteRequest(1016, 1, 30);
        Assert.Throws<InvalidOperationException>(() =>
            j.RecordWriteReply(id, 1, op, otherSid, Reply(otherSid)));
        j.RecordWriteReply(id, 1, op, request, Reply(request));
        var read = ReadRequest(1016, 31);
        var mismatch = j.RecordReadback(id, 1, Guid.NewGuid(), read,
            Reply(read, readback: 2));
        Assert.That(mismatch.Phase, Is.EqualTo(FinalOutputAuditPhase.ReadbackMismatch));
        var trace = new FinalOutputAuditReader(_root).Read(item.WorkpieceId)!;
        Assert.That(trace.Steps[0].State,
            Is.EqualTo(FinalOutputAuditStepState.ReadbackMismatch));
        Assert.That(trace.FullyVerified, Is.False);
        Assert.Throws<InvalidOperationException>(() =>
            j.PrepareStep(id, 2, Guid.NewGuid(), WriteRequest(2099, 2, 32)));
    }

    [Test]
    public void ExistingTimingSqliteWal_AutomaticallyAddsAuditTablesWithoutDestroyingOldEvents()
    {
        var item = Workpiece();
        var old = new ProductionTimingJournal(Path.Combine(_root, "master"), "master");
        var capture = Guid.NewGuid();
        old.Append(new TimingObservation(item, TimingStage.FrameArrived,
            CaptureId: capture, PhysicalDeviceId: "HIK-M-2",
            ChannelId: "CH06", AttemptNo: 1, FrameId: Guid.NewGuid()));
        Assert.That(new FinalOutputAuditReader(_root).Read(item.WorkpieceId), Is.Null);
        var j = Journal();
        Guid id = BeginWithScan(j, item, FinalOutputAuditScenario.Normal, FinalDisposition.Reject);
        Assert.That(new FinalOutputAuditReader(_root).Read(item.WorkpieceId)!.AttemptId,
            Is.EqualTo(id));
        var oldTrace = new ProductionTimelineReader(_root)
            .Query(new TimingFilter(item.WorkpieceId, CaptureId: capture));
        Assert.That(oldTrace.Events.Single().Stage, Is.EqualTo(TimingStage.FrameArrived));
    }

    [Test]
    public void ScannerNotVerifiedCannotStartWriteAndTwoDistinctItemsWithSameBarcodeStaySeparate()
    {
        var j = Journal();
        var first = Workpiece();
        var other = Workpiece();
        var missing = j.Begin(first, 1, FinalOutputAuditScenario.Normal, FinalDisposition.Reject);
        Assert.Throws<InvalidOperationException>(() =>
            j.PrepareStep(missing, 1, Guid.NewGuid(), WriteRequest(1016, 2, 1)));
        var valid = BeginWithScan(j, other, FinalOutputAuditScenario.Normal, FinalDisposition.Reject);
        Assert.That(new FinalOutputAuditReader(_root).Read(first.WorkpieceId)!.WorkpieceId,
            Is.Not.EqualTo(new FinalOutputAuditReader(_root).Read(other.WorkpieceId)!.WorkpieceId));
        Assert.That(valid, Is.Not.EqualTo(missing));
    }
}
