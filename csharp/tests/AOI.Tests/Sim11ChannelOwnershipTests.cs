using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

/// <summary>Bounded 11-channel SIM_LIGHT pixels, role ownership and line-parent identity.</summary>
[TestFixture]
public sealed class Sim11ChannelOwnershipTests
{
    private string _root = string.Empty;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-11ch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void EachCoreCapturesOnlyItsOwnedChannelsAndParentFrameOnce()
    {
        var item = WorkpieceIdentity.New("SIM-11CH", 1, "SIM_LIGHT_TEST_RELEASE");
        var master = new VisionAssetLedger(Path.Combine(_root, "master"));
        var slave = new VisionAssetLedger(Path.Combine(_root, "slave"));
        var rule = new SimPixelRule(127, 0.1);
        var mine = SimVisionFixture.CaptureOwnedChannels(item, MachineRole.Master, master, rule);
        var theirs = SimVisionFixture.CaptureOwnedChannels(item, MachineRole.Slave, slave, rule);

        Assert.That(mine.Select(r => r.ChannelId),
            Is.EquivalentTo(new[] { "CH01", "CH06", "CH07", "CH10", "CH11" }));
        Assert.That(theirs.Select(r => r.ChannelId),
            Is.EquivalentTo(new[] { "CH02", "CH03", "CH04", "CH05", "CH08", "CH09" }));
        Assert.That(master.Stats().Assets, Is.EqualTo(6)); // 5 logical + one unmodified line parent
        Assert.That(slave.Stats().Assets, Is.EqualTo(7)); // 6 logical + one unmodified line parent
        Assert.That(master.Audit().Problems, Is.Empty);
        Assert.That(slave.Audit().Problems, Is.Empty);

        var all = mine.Concat(theirs).ToArray();
        Assert.That(all, Has.Length.EqualTo(11));
        Assert.That(all.All(x => x.IsSimulated && x.Outcome == ChannelOutcome.Ok), Is.True);
        Assert.That(all.Where(x => x.ParentFrameId is not null).Count(), Is.EqualTo(4));
        var expected = ChannelCatalog.All.Where(x => !x.IsPositioning).Select(x => x.ChannelId);
        var decision = QualityRules.Evaluate(item, expected, all, endBarcodeMatches: true);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Good));
        Assert.That(decision.IsSimulation, Is.True);
        Assert.That(decision.Proof, Does.Contain("SIM_ONLY"));
    }
}
