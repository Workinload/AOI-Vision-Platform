using AOI.Domain;
using AOI.Solutions;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

[TestFixture]
public sealed class ChangeoverTests
{
    private string _folder = "";
    [SetUp]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "aoi-a6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private string Db(string filename) => Path.Combine(_folder, filename);

    private PublishedRelease PublishValid()
    {
        var repo = new SolutionRepository(Db("solutions.sqlite"));
        repo.RegisterProfile(MachineProfile.SimBaseline(), "operator");
        repo.CreateSolution(new ProductSolution("S1", "SIM_MACHINE_01", "Offline test"), "operator");
        foreach (var group in ChannelCatalog.All.GroupBy(x => x.StationId))
            repo.SaveDraft("S1", new StationRecipe(group.Key,
                group.Select(x => x.ChannelId).ToArray(), new Dictionary<string, double> { ["threshold"] = 0.5 }), 0, "operator");
        return repo.Publish("S1", "operator");
    }

    [Test]
    public void ReleaseMustCoverAllElevenChannelsAndIsImmutable()
    {
        var repo = new SolutionRepository(Db("solutions.sqlite"));
        repo.RegisterProfile(MachineProfile.SimBaseline(), "operator");
        repo.CreateSolution(new ProductSolution("S1", "SIM_MACHINE_01", "Test"), "operator");
        var group = ChannelCatalog.All.GroupBy(x => x.StationId).First();
        repo.SaveDraft("S1", new StationRecipe(group.Key, group.Select(x => x.ChannelId).ToArray(),
            new Dictionary<string, double>()), 0, "operator");
        Assert.Throws<InvalidDataException>(() => repo.Publish("S1", "operator"));
        foreach (var other in ChannelCatalog.All.GroupBy(x => x.StationId).Skip(1))
            repo.SaveDraft("S1", new StationRecipe(other.Key, other.Select(x => x.ChannelId).ToArray(),
                new Dictionary<string, double>()), 0, "operator");
        var release = repo.Publish("S1", "operator");
        Assert.That(repo.Publish("S1", "operator").ReleaseId, Is.EqualTo(release.ReleaseId));
        Assert.That(repo.RequireRelease(release.ReleaseId).ContentSha256, Is.EqualTo(release.ContentSha256));
        Assert.Throws<InvalidOperationException>(() => repo.SaveDraft("S1",
            new StationRecipe(group.Key, group.Select(x => x.ChannelId).ToArray(),
                new Dictionary<string, double>()), 0, "stale-client"));
        var newer = repo.SaveDraft("S1", new StationRecipe(group.Key,
            group.Select(x => x.ChannelId).ToArray(),
            new Dictionary<string, double> { ["threshold"] = 0.7 }), 1, "operator");
        Assert.That(newer.Revision, Is.EqualTo(2));
        Assert.That(repo.RequireRelease(release.ReleaseId).ContentSha256, Is.EqualTo(release.ContentSha256));
    }

    [Test]
    public void BothNodesRequireMatchingReadbackAndEpoch()
    {
        var release = PublishValid();
        var master = new SimChangeoverNode(Db("master.sqlite"), "master", MachineRole.Master);
        var slave = new SimChangeoverNode(Db("slave.sqlite"), "slave", MachineRole.Slave);
        var coordinator = new ChangeoverCoordinator(Db("coordinator.sqlite"));
        var request = new ChangeoverRequest(Guid.NewGuid(), release, 0, null, 1, "operator");
        var result = coordinator.Execute(request, master, slave, SimInterlocks.AllSafeForOfflineTest);
        Assert.That(result.Phase, Is.EqualTo(ChangeoverPhase.Committed), result.Detail);
        Assert.That(master.Snapshot().ActiveEpoch, Is.EqualTo(1));
        Assert.That(slave.Snapshot().ActiveEpoch, Is.EqualTo(1));
        Assert.That(master.Snapshot().SafeHold, Is.True);
        Assert.That(slave.Snapshot().SafeHold, Is.True);
        Assert.That(master.Snapshot().RealHardwareWritesEnabled, Is.False);
        Assert.That(coordinator.Recover(request.TransactionId, master, slave).Phase, Is.EqualTo(ChangeoverPhase.Committed));
    }

    [Test]
    public void FailedInterlockCannotStageOrAdvanceEpoch()
    {
        var release = PublishValid();
        var master = new SimChangeoverNode(Db("master.sqlite"), "master", MachineRole.Master);
        var slave = new SimChangeoverNode(Db("slave.sqlite"), "slave", MachineRole.Slave);
        var coordinator = new ChangeoverCoordinator(Db("coordinator.sqlite"));
        var bad = SimInterlocks.AllSafeForOfflineTest with { NoInFlightWorkpieces = false };
        Assert.Throws<InvalidOperationException>(() => coordinator.Execute(
            new ChangeoverRequest(Guid.NewGuid(), release, 0, null, 1, "operator"), master, slave, bad));
        Assert.That(master.Snapshot().ActiveEpoch, Is.Zero);
        Assert.That(slave.Snapshot().ActiveEpoch, Is.Zero);
    }

    [Test]
    public void LonePreparedNodeCannotActivateWithoutDualProof()
    {
        var release = PublishValid();
        var master = new SimChangeoverNode(Db("master.sqlite"), "master", MachineRole.Master);
        var request = new ChangeoverRequest(Guid.NewGuid(), release, 0, null, 1, "operator");
        var prepared = master.Prepare(request, SimInterlocks.AllSafeForOfflineTest);
        Assert.That(prepared.ReadBackSha256, Is.EqualTo(release.ContentSha256));
        var impostor = prepared with { NodeId = "slave", Role = MachineRole.Slave };
        var forged = new DualPreparedCertificate(request.TransactionId, release.ReleaseId,
            release.ContentSha256, 1, prepared, impostor with { ReadBackSha256 = "bad" });
        Assert.Throws<InvalidDataException>(() => master.Activate(forged, SimInterlocks.AllSafeForOfflineTest));
        Assert.That(master.Snapshot().ActiveEpoch, Is.Zero);
        master.Abort(request.TransactionId, "peer unavailable");
        Assert.That(master.Snapshot().Phase, Is.EqualTo(NodeChangeoverPhase.Idle));
    }

    [Test]
    public void PreparedStateSurvivesRestartAndRejectsBlindRollbackAfterActivation()
    {
        var release = PublishValid();
        var request = new ChangeoverRequest(Guid.NewGuid(), release, 0, null, 1, "operator");
        var master = new SimChangeoverNode(Db("master.sqlite"), "master", MachineRole.Master);
        var slave = new SimChangeoverNode(Db("slave.sqlite"), "slave", MachineRole.Slave);
        var m = master.Prepare(request, SimInterlocks.AllSafeForOfflineTest);
        var s = slave.Prepare(request, SimInterlocks.AllSafeForOfflineTest);
        master = new SimChangeoverNode(Db("master.sqlite"), "master", MachineRole.Master);
        var cert = new DualPreparedCertificate(request.TransactionId, release.ReleaseId,
            release.ContentSha256, 1, m, s);
        master.Activate(cert, SimInterlocks.AllSafeForOfflineTest);
        Assert.Throws<InvalidOperationException>(() => master.Abort(request.TransactionId, "unsafe"));
        Assert.That(master.Snapshot().SafeHold, Is.True);
        Assert.That(master.Snapshot().ActiveEpoch, Is.EqualTo(1));
        Assert.That(slave.Snapshot().ActiveEpoch, Is.Zero);
    }
}
