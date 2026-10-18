using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

[TestFixture]
public sealed class PublishPreCommitValidationTests
{
    private string db = string.Empty;
    private SolutionRepository repo = null!;

    [SetUp]
    public void SetUp()
    {
        db = Path.Combine(Path.GetTempPath(), "aoi-pub-" + Guid.NewGuid().ToString("N") + ".sqlite");
        repo = new SolutionRepository(db);
        repo.RegisterProfile(MachineProfile.SimBaseline(), "SIM_TEST");
        repo.CreateSolution(new ProductSolution("S1", "SIM_MACHINE_01", "PreCommit"), "SIM_TEST");
        foreach (var station in ChannelCatalog.All.GroupBy(x => x.StationId))
            repo.SaveDraft("S1", new StationRecipe(station.Key,
                station.Select(c => c.ChannelId).ToArray(),
                new Dictionary<string, double> { ["brightThreshold"] = 127, ["minimumBrightFraction"] = 0.1 }),
                0, "SIM_TEST");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(db)) File.Delete(db);
    }

    private long ReleaseRows()
    {
        using var con = new SqliteConnection("Data Source=" + db + ";Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM releases";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    [Test]
    public void UnsupportedAlgorithmValidatorLeavesNoReleaseRow()
    {
        long before = ReleaseRows();
        Assert.Throws<InvalidOperationException>(() => repo.Publish("S1", "SIM_TEST",
            _ => throw new InvalidOperationException("PUBLISHED_INSPECTION_PLUGIN_NOT_INSTALLED")));
        Assert.That(ReleaseRows(), Is.EqualTo(before),
            "a rejected capability check must not commit any Release row");
    }

    [Test]
    public void AcceptedValidatorStillPublishesAndPersists()
    {
        var release = repo.Publish("S1", "SIM_TEST", _ => { });
        Assert.That(release.ContentSha256, Has.Length.EqualTo(64));
        Assert.That(ReleaseRows(), Is.EqualTo(1));
        Assert.That(repo.RequireRelease(release.ReleaseId).ContentSha256,
            Is.EqualTo(release.ContentSha256));
    }
}
