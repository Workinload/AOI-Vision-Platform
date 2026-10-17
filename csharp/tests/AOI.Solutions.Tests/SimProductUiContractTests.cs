using AOI.Contracts;
using AOI.Domain;
using AOI.Solutions;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

[TestFixture]
public sealed class SimProductUiContractTests
{
    private static SimActiveProductSnapshot Sample()
    {
        var hash = new string('a', 64);
        var stations = ChannelCatalog.All.GroupBy(c => c.StationId)
            .Select(g => new SimActiveStation(g.Key, g.Select(c => c.ChannelId).ToArray(),
                (byte)110, 0.1)).ToArray();
        return new SimActiveProductSnapshot("SIM_PRODUCT_A", "SIMREL-" + hash[..32],
            hash, 1, true, stations);
    }

    [Test]
    public void AllElevenProductChannelsAndStationTunablesRoundTrip()
    {
        var data = Sample();
        data.Validate();
        Assert.That(data.Stations, Has.Count.EqualTo(7));
        Assert.That(data.Stations.Sum(s => s.ChannelIds.Count), Is.EqualTo(11));
        var decoded = WireFormat.Parse<SimActiveProductSnapshot>(WireFormat.Serialize(data));
        decoded.Validate();
        Assert.That(decoded.Stations.Single(s => s.StationId == "ST05").BrightThreshold, Is.EqualTo(110));
        Assert.That(decoded.Epoch, Is.EqualTo(1));
    }

    [Test]
    public void RejectsInventedReleaseOrWrongChannelOwnershipOrRealMode()
    {
        var correct = Sample();
        Assert.Throws<InvalidDataException>(() => (correct with { IsSimulation = false }).Validate());
        Assert.Throws<InvalidDataException>(() => (correct with { Epoch = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (correct with { ReleaseId = "SIMREL-false" }).Validate());
        Assert.Throws<InvalidDataException>(() => (correct with {
            Stations = correct.Stations.Select((s, i) => i == 0
                ? s with { ChannelIds = new[] { "CH11" } } : s).ToArray()
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (correct with {
            Stations = correct.Stations.Select((s, i) => i == 0
                ? s with { MinimumBrightFraction = 2.0 } : s).ToArray()
        }).Validate());
    }

    [Test]
    public void PublishedReleaseStationThresholdsAreHashAddressedAndRevisioned()
    {
        string root = Path.Combine(Path.GetTempPath(), "aoi-a6-product-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SolutionRepository(Path.Combine(root, "solutions.db"));
            repository.RegisterProfile(MachineProfile.SimBaseline(), "engineer");
            repository.CreateSolution(new ProductSolution("SIM_PRODUCT_A", "SIM_MACHINE_01", "A"), "engineer");
            foreach (var group in ChannelCatalog.All.GroupBy(c => c.StationId))
                repository.SaveDraft("SIM_PRODUCT_A", new StationRecipe(group.Key,
                    group.Select(c => c.ChannelId).ToArray(),
                    new Dictionary<string, double> {
                        ["brightThreshold"] = group.Key == "ST05" ? 110 : 127,
                        ["minimumBrightFraction"] = 0.1
                    }), 0, "engineer");
            var release = repository.Publish("SIM_PRODUCT_A", "engineer");
            release.Verify();
            Assert.That(release.Manifest.Stations.Single(s => s.StationId == "ST05")
                .Tunables["brightThreshold"], Is.EqualTo(110));
            var draft = repository.GetDraft("SIM_PRODUCT_A", "ST05")!;
            Assert.That(draft.Revision, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() =>
                repository.SaveDraft("SIM_PRODUCT_A", draft.Recipe, 0, "stale_engineer"));
            var update = new StationRecipe(draft.StationId, draft.Recipe.ChannelIds,
                new Dictionary<string, double> {
                    ["brightThreshold"] = 130,
                    ["minimumBrightFraction"] = 0.1
                });
            repository.SaveDraft("SIM_PRODUCT_A", update, 1, "engineer");
            var next = repository.Publish("SIM_PRODUCT_A", "engineer");
            Assert.That(next.ReleaseId, Is.Not.EqualTo(release.ReleaseId));
            Assert.That(repository.RequireRelease(release.ReleaseId).ContentSha256, Is.EqualTo(release.ContentSha256));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
