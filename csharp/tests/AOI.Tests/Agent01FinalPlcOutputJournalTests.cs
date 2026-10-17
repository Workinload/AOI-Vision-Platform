using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using AOI.Runtime.Plc;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class Agent01FinalPlcOutputJournalTests
{
    private string _folder = string.Empty;
    private string JournalPath => Path.Combine(_folder, "master", "final-output.db");

    [SetUp]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "aoi-a1-final-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private ProductionStore Store() =>
        new(Path.Combine(_folder, "master", "production.db"));

    private static WorkpieceIdentity Item(string barcode = "SAME-OUTPUT-BARCODE") =>
        WorkpieceIdentity.New(barcode, 4, "SIM_LIGHT_TEST_RELEASE");

    private static void Register(ProductionStore store, WorkpieceIdentity item, ushort token)
    {
        store.CreateAndQueueRegistration(item, PeerEnvelope.Create("master", "slave",
            token, item, "workpiece_registered", item));
    }

    private static FinalDecision Good() => new(FinalDisposition.Good, true,
        ["CH02"], ["CH02"], "COMPLETE_OK:SIM_ONLY");

    private static FinalDecision Ng() => new(FinalDisposition.Reject, true,
        ["CH02"], ["CH02"], "COMPLETE_NG:SIM_ONLY");

    private static FinalOutputProgress Normal(ProductionStore store,
        FinalPlcOutputJournal journal, WorkpieceIdentity item, ushort token,
        FinalDisposition disposition = FinalDisposition.Good)
    {
        Register(store, item, token);
        store.StoreFinal(item, disposition == FinalDisposition.Good ? Good() : Ng());
        return journal.Prepare(store, item, token, LegacyExitScenario.Normal, disposition);
    }

    private static FinalOutputProgress Abnormal(ProductionStore store,
        FinalPlcOutputJournal journal, WorkpieceIdentity item, ushort token,
        LegacyExitScenario scenario)
    {
        Register(store, item, token);
        store.RecordWorkpieceHold(item, "END_BARCODE_MISMATCH",
            "Master persisted an abnormal end scanner identity mismatch.");
        return journal.Prepare(store, item, token, scenario, FinalDisposition.Reject);
    }

    // The transport exposes the exact OperationId and SID before a physical SendAsync.
    // Test receipts are structurally identical to A2's WriteObservedAsync callback.
    private static LegacyFinsWriteProof Send(FinalPlcOutputJournal journal,
        FinalOutputWriteClaim claim, byte sid = 31)
    {
        var first = new LegacyFinsWriteProof(Guid.NewGuid(), sid,
            claim.DmAddress, claim.Value, DateTimeOffset.UtcNow,
            AcknowledgedUtc: null, ReplyVerified: false);
        Assert.That(journal.ObserveWireSend(claim.ClaimId, first), Is.True);
        return first;
    }

    private static LegacyFinsWriteProof Acknowledge(FinalPlcOutputJournal journal,
        FinalOutputWriteClaim claim, LegacyFinsWriteProof first)
    {
        var ack = first with { AcknowledgedUtc = DateTimeOffset.UtcNow, ReplyVerified = true };
        Assert.That(journal.ConfirmFinsAck(claim.ClaimId, ack), Is.True);
        return ack;
    }

    [Test]
    public async Task TwoConfirmedStepsAreActualLoopbackFinsDatagramsAndNeverSendAgainAfterReopen()
    {
        var store = Store();
        var item = Item("SIM-0001");
        var shadow = new LegacyPlcShadow(new SimFullVirtualPlc(2));
        shadow.IssueEndScan(1, LegacyExitScenario.Normal, item.Barcode);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var udp = new LegacyPlcShadowUdp(shadow, port: 0);
        var serving = udp.RunAsync(cancellation.Token);
        var port = await udp.Listening;
        try
        {
            var client = new LegacyFinsLoopbackClient(port,
                LegacyFinsNodes.Demo, TimeSpan.FromSeconds(2));
            Guid intentId;
            using (var journal = new FinalPlcOutputJournal(JournalPath))
            {
                var ready = Normal(store, journal, item, 1);
                intentId = ready.Intent.IntentId;
                Assert.That(ready.Steps.Select(x => x.DmAddress),
                    Is.EqualTo(new ushort[] { 1016, 2099 }));
                Assert.That(ready.Steps.Select(x => x.Value),
                    Is.EqualTo(new ushort[] { 1, 2 }));
                for (int i = 0; i < 2; i++)
                {
                    var claim = journal.ClaimNextWrite(intentId)!;
                    Assert.That(claim.StepIndex, Is.EqualTo(i));
                    // The durable onBeforeSend callback is invoked by A2 before UDP SendAsync.
                    var ack = await client.WriteObservedAsync(
                        claim.DmAddress, claim.Value,
                        proof => journal.ObserveWireSend(claim.ClaimId, proof),
                        cancellation.Token);
                    Assert.That(ack.ReplyVerified, Is.True);
                    journal.ConfirmFinsAck(claim.ClaimId, ack);
                    ushort observed = (await client.ReadAsync(
                        claim.DmAddress, 1, cancellation.Token)).Single();
                    Assert.That(journal.ConfirmReadback(claim.ClaimId, observed), Is.True);
                    Assert.That(journal.Get(intentId).ConfirmedSteps, Is.EqualTo(i + 1));
                }
                Assert.That(journal.Get(intentId).Complete, Is.True);
                Assert.That(journal.ClaimNextWrite(intentId), Is.Null);
            }

            var outputWrites = shadow.WireHistory
                .Where(x => x.IsWrite && x.Address is 1016 or 2099).ToArray();
            Assert.That(outputWrites.Select(x => x.Address),
                Is.EqualTo(new ushort[] { 1016, 2099 }));
            Assert.That(outputWrites.Select(x => x.Values.Single()),
                Is.EqualTo(new ushort[] { 1, 2 }));
            Assert.That(outputWrites.All(x => !string.IsNullOrWhiteSpace(x.RequestHex) &&
                                              !string.IsNullOrWhiteSpace(x.ResponseHex)),
                Is.True, "Output steps must correspond to actual FINS UDP packets.");

            using var reopened = new FinalPlcOutputJournal(JournalPath);
            Assert.That(reopened.Get(intentId).Complete, Is.True);
            Assert.That(reopened.Get(intentId).RequiresManualReconciliation, Is.False);
            Assert.That(reopened.ClaimNextWrite(intentId), Is.Null);
            Assert.That(reopened.EventsFor(intentId).Count(e =>
                e.Kind == FinalOutputEventKind.ReadbackConfirmed), Is.EqualTo(2));
            Console.WriteLine("A1_FINAL_REAL_UDP_PASS token=1 steps=D1016_1,D2099_2 " +
                "sid_proofs=2 readbacks=2 replay_completed=1 repeat_writes=0");
        }
        finally
        {
            cancellation.Cancel();
            await serving;
        }
    }

    [TestCase(LegacyExitScenario.BarcodeFailure, (ushort)3)]
    [TestCase(LegacyExitScenario.BarcodeMismatch, (ushort)4)]
    public void AbnormalPersistedHoldEnforcesReverseSequenceAndNeverCreatesGood(
        LegacyExitScenario scenario, ushort firstValue)
    {
        var store = Store();
        var item = Item();
        using var journal = new FinalPlcOutputJournal(JournalPath);
        var intent = Abnormal(store, journal, item, 3, scenario);
        Assert.That(intent.Steps.Select(s => s.DmAddress),
            Is.EqualTo(new ushort[] { 2099, 1016 }));
        Assert.That(intent.Steps.Select(s => s.Value),
            Is.EqualTo(new ushort[] { firstValue, 2 }));
        Assert.Throws<InvalidOperationException>(() =>
            journal.Prepare(store, item, 3, LegacyExitScenario.Normal, FinalDisposition.Good));

        var first = journal.ClaimNextWrite(intent.Intent.IntentId)!;
        var wire1 = Send(journal, first);
        Acknowledge(journal, first, wire1);
        Assert.That(journal.ConfirmReadback(first.ClaimId, firstValue), Is.True);
        Assert.That(journal.Get(intent.Intent.IntentId).ConfirmedSteps, Is.EqualTo(1));

        var second = journal.ClaimNextWrite(intent.Intent.IntentId)!;
        Assert.That(second.DmAddress, Is.EqualTo((ushort)1016));
        var wire2 = Send(journal, second, sid: 32);
        Acknowledge(journal, second, wire2);
        Assert.That(journal.ConfirmReadback(second.ClaimId, 2), Is.True);
        Assert.That(journal.Get(intent.Intent.IntentId).Complete, Is.True);
        Assert.That(store.HasWorkpieceHold(item.WorkpieceId), Is.True);
        Assert.That(store.LatestFinal(item.WorkpieceId), Is.Null);
        Console.WriteLine("A1_FINAL_ABNORMAL_ORDER_PASS scenario=" + scenario +
            " D2099=" + firstValue + " then D1016=2 durable_hold=1 good=0");
    }

    [Test]
    public void AClaimedPossiblySentWriteIsUnknownAfterRestartAndNeverAutomaticallyRepeated()
    {
        var store = Store();
        var item = Item();
        Guid intentId, claimId;
        using (var journal = new FinalPlcOutputJournal(JournalPath))
        {
            intentId = Normal(store, journal, item, 1).Intent.IntentId;
            var claim = journal.ClaimNextWrite(intentId)!;
            claimId = claim.ClaimId;
            Assert.That(journal.Get(intentId).Phase,
                Is.EqualTo("CLAIMED_POSSIBLY_SENT"));
            Assert.Throws<InvalidOperationException>(() => journal.ClaimNextWrite(intentId));
        }
        using (var restarted = new FinalPlcOutputJournal(JournalPath))
        {
            var recovered = restarted.Get(intentId);
            Assert.That(recovered.Phase, Is.EqualTo("UNKNOWN_ACK_MANUAL_RECONCILIATION"));
            Assert.That(recovered.UnknownReason, Is.EqualTo("PROCESS_RESTART_WITH_UNRESOLVED_WRITE"));
            Assert.That(recovered.ConfirmedSteps, Is.Zero);
            Assert.That(recovered.MayClaimNextWrite, Is.False);
            Assert.Throws<InvalidOperationException>(() => restarted.ClaimNextWrite(intentId));
            Assert.That(restarted.MarkUnknown(claimId, "SECOND_RETRY_ATTEMPT"), Is.False);
            Assert.That(restarted.Prepare(store, item, 1, LegacyExitScenario.Normal,
                FinalDisposition.Good).Intent.IntentId, Is.EqualTo(intentId));
        }
        using var again = new FinalPlcOutputJournal(JournalPath);
        Assert.That(again.Get(intentId).UnknownReason,
            Is.EqualTo("PROCESS_RESTART_WITH_UNRESOLVED_WRITE"));
        Assert.That(again.EventsFor(intentId).Count(x =>
            x.Kind == FinalOutputEventKind.OutcomeUnknown), Is.EqualTo(1));
        Console.WriteLine("A1_FINAL_RESTART_UNKNOWN_PASS claimed=1 replay_writes=0");
    }

    [Test]
    public void LostAckAndWrongReadbackKeepOnlyThatWorkpieceInManualReconciliation()
    {
        var store = Store();
        var a = Item();
        var b = Item(); // identical barcode but distinct WorkpieceId
        using var journal = new FinalPlcOutputJournal(JournalPath);
        var first = Normal(store, journal, a, 1);
        var second = Normal(store, journal, b, 2);

        var claim = journal.ClaimNextWrite(first.Intent.IntentId)!;
        Send(journal, claim);
        Assert.That(journal.MarkDisconnected("UDP_ACK_LOST"), Is.EqualTo(1));
        Assert.That(journal.Get(first.Intent.IntentId).UnknownReason,
            Is.EqualTo("UDP_ACK_LOST"));
        Assert.Throws<InvalidOperationException>(() =>
            journal.ClaimNextWrite(first.Intent.IntentId));

        var healthyClaim = journal.ClaimNextWrite(second.Intent.IntentId)!;
        var sent = Send(journal, healthyClaim, 39);
        Acknowledge(journal, healthyClaim, sent);
        Assert.That(journal.ConfirmReadback(healthyClaim.ClaimId, 9), Is.False);
        Assert.That(journal.Get(second.Intent.IntentId).UnknownReason,
            Is.EqualTo("READBACK_VALUE_MISMATCH"));
        Assert.That(journal.GetAll(), Has.Count.EqualTo(2));
        Assert.That(journal.GetAll().All(x => x.RequiresManualReconciliation &&
                                          !x.MayClaimNextWrite), Is.True);
        Console.WriteLine("A1_FINAL_ACK_LOSS_PASS same_barcode=2 distinct_workpieces=2 no_replay=1");
    }

    [Test]
    public void SlaveCaptureHoldCannotBeConvertedIntoNormalGoodEvenAfterRestart()
    {
        var store = Store();
        var item = Item();
        Register(store, item, 1);
        Assert.That(store.RecordWorkpieceHold(item, "REMOTE_CAPTURE_MISSING",
            "Slave CH02 frame missing; exact WorkpieceId must remain held."), Is.True);
        using (var journal = new FinalPlcOutputJournal(JournalPath))
        {
            Assert.Throws<InvalidOperationException>(() =>
                journal.Prepare(store, item, 1, LegacyExitScenario.Normal,
                    FinalDisposition.Good));
            Assert.That(journal.GetAll(), Is.Empty);
        }
        using var restarted = new FinalPlcOutputJournal(JournalPath);
        Assert.That(store.HasWorkpieceHold(item.WorkpieceId), Is.True);
        Assert.That(store.LatestFinal(item.WorkpieceId), Is.Null);
        Assert.That(restarted.GetAll(), Is.Empty);
        Console.WriteLine("A1_FINAL_SLAVE_HOLD_PASS no_good=1 no_output_intent=1");
    }

    [Test]
    public void ExclusiveWriterLeasePreventsSecondMasterFromIssuingTheSameOutput()
    {
        using var first = new FinalPlcOutputJournal(JournalPath);
        Assert.Throws<IOException>(() => new FinalPlcOutputJournal(JournalPath));
        Assert.That(first.GetAll(), Is.Empty);
    }
}
