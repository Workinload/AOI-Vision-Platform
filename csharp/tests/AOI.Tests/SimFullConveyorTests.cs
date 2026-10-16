using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

/// <summary>SIM_FULL physical queue mechanics; never requires industrial equipment.</summary>
[TestFixture]
public sealed class SimFullConveyorTests
{
    private string _folder = string.Empty;
    [SetUp]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "aoi-simfull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }
    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static ushort[] Read(SimFullVirtualPlc plc, ushort address, ushort count)
    {
        byte[] req = FinsDm.Request(false, address, new ushort[count], 42);
        return FinsDm.DecodeResponse(req, plc.Handle(req));
    }
    private static void Write(SimFullVirtualPlc plc, ushort address, params ushort[] words)
    {
        byte[] req = FinsDm.Request(true, address, words, 43);
        FinsDm.DecodeResponse(req, plc.Handle(req));
    }

    [Test]
    public void FiveItemsMoveInFIFOOrderWithThreeOverlappingInFlightAndImmutableTokens()
    {
        var plc = new SimFullVirtualPlc(5, 3);
        var initialBoot = Read(plc, 4007, 2);
        Assert.That(initialBoot, Has.Length.EqualTo(2));
        Assert.That(initialBoot.Any(x => x != 0), Is.True);
        for (ushort token = 1; token <= 3; token++)
        {
            Assert.That(Read(plc, SimFullVirtualPlc.LoadTokenAddress, 1)[0],
                Is.EqualTo(token));
            Assert.That(FinsDm.SimBarcode(Read(plc, 2000, 10)),
                Is.EqualTo(SimFullVirtualPlc.Barcode(token)));
            Write(plc, SimFullVirtualPlc.LoadAckAddress, token);
        }
        Assert.That(plc.MaxObservedInFlight, Is.EqualTo(3));
        Assert.That(Read(plc, SimFullVirtualPlc.ActiveCountAddress, 1)[0], Is.EqualTo(3));
        Assert.That(Read(plc, SimFullVirtualPlc.LoadTokenAddress, 1)[0], Is.Zero);
        Assert.That(Read(plc, SimFullVirtualPlc.ExitTokenAddress, 1)[0], Is.EqualTo(1));
        Assert.That(FinsDm.SimBarcode(Read(plc, SimFullVirtualPlc.EndBarcodeAddress, 10)),
            Is.EqualTo("SIM-0001"));

        // Reordered exit, double loading and direct physical output spoofing are denied.
        byte[] early = FinsDm.Request(true, SimFullVirtualPlc.ExitDecisionAddress,
            new ushort[] { 2, 1 }, 8);
        Assert.That(plc.Handle(early), Is.Empty);
        byte[] unsafeOutput = FinsDm.Request(true, 1016, new ushort[] { 1 }, 8);
        Assert.That(plc.Handle(unsafeOutput), Is.Empty);
        Assert.That(plc.ReadForTest(1016), Is.Zero);
        for (ushort token = 1; token <= 5; token++)
        {
            Assert.That(Read(plc, SimFullVirtualPlc.ExitTokenAddress, 1)[0], Is.EqualTo(token));
            Write(plc, SimFullVirtualPlc.ExitDecisionAddress, token, 1);
            if (token <= 2)
                Write(plc, SimFullVirtualPlc.LoadAckAddress, (ushort)(token + 3));
        }
        Assert.That(plc.ExitedCount, Is.EqualTo(5));
        Assert.That(plc.Completed.Select(x => x.Token), Is.EqualTo(new ushort[] { 1, 2, 3, 4, 5 }));
        Assert.That(plc.ReadForTest(1016), Is.EqualTo(1));
        Assert.That(plc.ReadForTest(2099), Is.EqualTo(2));
        Assert.That(Read(plc, SimFullVirtualPlc.ExitTokenAddress, 1)[0], Is.Zero);
        Assert.That(Read(plc, SimFullVirtualPlc.LoadTokenAddress, 1)[0], Is.Zero);
        Assert.That(Read(plc, SimFullVirtualPlc.ActiveCountAddress, 1)[0], Is.Zero);
    }

    [Test]
    public void EndBarcodeFaultAndRepeatedBarcodeDoNotAllowConveyorTokenConfusion()
    {
        var plc = new SimFullVirtualPlc(3, 3, flipEndBarcodeToken: 2, repeatBarcodes: true);
        var ids = new List<Guid>();
        for (ushort token = 1; token <= 3; token++)
        {
            Assert.That(FinsDm.SimBarcode(Read(plc, 2000, 10)), Is.EqualTo("SIM-DUPLICATE"));
            ids.Add(WorkpieceIdentity.New("SIM-DUPLICATE", 1, "SIM_LIGHT_TEST_RELEASE").WorkpieceId);
            Write(plc, SimFullVirtualPlc.LoadAckAddress, token);
        }
        Assert.That(ids.Distinct().Count(), Is.EqualTo(3), "Barcode must not be primary key.");
        Write(plc, SimFullVirtualPlc.ExitDecisionAddress, 1, 1);
        Assert.That(Read(plc, SimFullVirtualPlc.ExitTokenAddress, 1)[0], Is.EqualTo(2));
        Assert.That(FinsDm.SimBarcode(Read(plc, SimFullVirtualPlc.EndBarcodeAddress, 10)),
            Is.EqualTo("SIM-BAD-END"));
        Assert.That(plc.ReadForTest(1016), Is.EqualTo(1),
            "Incorrect end barcode must never silently change a previous output.");
        byte[] wrongToken = FinsDm.Request(true, SimFullVirtualPlc.ExitDecisionAddress,
            new ushort[] { 3, 1 }, 50);
        Assert.That(plc.Handle(wrongToken), Is.Empty);
        Assert.That(plc.ExitedCount, Is.EqualTo(1));
    }

    [Test]
    public void DurableTokenLedgerRejectsReuseAndSilentRestart()
    {
        var path = Path.Combine(_folder, "cycles.db");
        var ledger = new SimFullCycleLedger(path, 0x12345678);
        var a = WorkpieceIdentity.New("SAME", 1, "SIM_LIGHT_TEST_RELEASE");
        var b = WorkpieceIdentity.New("SAME", 1, "SIM_LIGHT_TEST_RELEASE");
        ledger.Register(1, a);
        ledger.Register(2, b);
        Assert.That(a.WorkpieceId, Is.Not.EqualTo(b.WorkpieceId));
        Assert.That(ledger.Find(1), Is.EqualTo(a));
        Assert.That(ledger.Find(2), Is.EqualTo(b));
        Assert.That(ledger.RegisteredCount, Is.EqualTo(2));
        Assert.Throws<InvalidOperationException>(() => ledger.Register(1, b));

        var final = new FinalDecision(FinalDisposition.Good, true,
            new[] { "CH02" }, new[] { "CH02" }, "COMPLETE_OK:SIM_ONLY");
        ledger.MarkExited(1, final);
        Assert.Throws<InvalidOperationException>(() => ledger.MarkExited(1, final));
        Assert.Throws<InvalidOperationException>(() => ledger.MarkExited(3, final));
        Assert.Throws<InvalidOperationException>(() =>
            ledger.MarkExited(2, final with { IsSimulation = false }));
        Assert.Throws<InvalidOperationException>(() =>
            new SimFullCycleLedger(path, 0x12345678));
        Assert.Throws<InvalidOperationException>(() =>
            new SimFullCycleLedger(path, 0x87654321));
    }
}
