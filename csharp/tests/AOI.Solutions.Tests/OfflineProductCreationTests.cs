using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

/// <summary>
/// Atomic operator product creation: a product row and all 7 station drafts commit together
/// or not at all. No empty/partial product may remain after an input or write failure.
/// </summary>
[TestFixture]
public sealed class OfflineProductCreationTests
{
    private string _root = "";
    private OfflineProductManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-offline-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _manager = OfflineProductManager.Open(Path.Combine(_root, "solutions.sqlite"),
            "SIM_OPERATOR_TEST");
        _manager.EnsureSimProfile();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static Dictionary<string, StationRecipe> AllStations(
        Func<IGrouping<string, ChannelDefinition>, StationRecipe>? mutate = null) =>
        ChannelCatalog.All.GroupBy(c => c.StationId)
            .ToDictionary(g => g.Key, g => mutate?.Invoke(g) ?? new StationRecipe(g.Key,
                g.Select(c => c.ChannelId).ToArray(),
                new Dictionary<string, double>
                {
                    ["brightThreshold"] = 127, ["minimumBrightFraction"] = 0.1
                }), StringComparer.Ordinal);

    [Test]
    public void IncompleteStationSetIsRejectedWithNoProductRowOrDrafts()
    {
        var repo = _manager.Repository;
        var drafts = AllStations();
        drafts.Remove("ST05");
        Assert.Throws<InvalidDataException>(() =>
            repo.CreateSolutionWithDrafts(new ProductSolution("PARTIAL", "SIM_MACHINE_01", "Partial"),
                drafts, "SIM_OPERATOR_TEST"));
        Assert.That(repo.ListSolutions(), Is.Empty);
        Assert.That(repo.ListDraftRevisions("PARTIAL"), Is.Empty);
    }

    [Test]
    public void InvalidStationRecipeIsRejectedBeforeAnyProductRow()
    {
        var repo = _manager.Repository;
        var drafts = AllStations(g => new StationRecipe(g.Key,
            g.Select(c => c.ChannelId).ToArray(),
            new Dictionary<string, double> { ["brightThreshold"] = double.NaN }));
        Assert.Throws<InvalidDataException>(() =>
            repo.CreateSolutionWithDrafts(new ProductSolution("BAD", "SIM_MACHINE_01", "Bad"),
                drafts, "SIM_OPERATOR_TEST"));
        Assert.That(repo.ListSolutions(), Is.Empty);
        Assert.That(repo.ListDraftRevisions("BAD"), Is.Empty);
    }

    [Test]
    public void InvalidDefaultStationRuleIsRejectedBeforeAnyProductRow()
    {
        var invalid = new (string Id, double Threshold, double Fraction)[]
        {
            ("T500", 500, 0.1),
            ("TZERO", 0, 0.1),
            ("THALF", 0.5, 0.1),
            ("TNAN", double.NaN, 0.1),
            ("TINF", double.PositiveInfinity, 0.1),
            ("FOVER", 127, 1.5),
            ("FNEG", 127, -0.1),
            ("FNAN", 127, double.NaN)
        };
        foreach (var (id, threshold, fraction) in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _manager.CreateSimSolution(id, "Invalid Rule", threshold, fraction), id);
            Assert.That(_manager.Repository.ListSolutions(), Is.Empty, id);
            Assert.That(_manager.Repository.ListDraftRevisions(id), Is.Empty, id);
        }
    }

    [Test]
    public void CreateSimSolutionWritesSevenStationDraftsInOneTransaction()
    {
        _manager.CreateSimSolution("OK", "Atomic Product");
        var revisions = _manager.Repository.ListDraftRevisions("OK");
        Assert.That(revisions, Has.Count.EqualTo(7));
        Assert.That(revisions.Values, Is.All.EqualTo(1));
        Assert.That(revisions.Keys, Is.EquivalentTo(ChannelCatalog.All
            .Select(c => c.StationId).Distinct(StringComparer.Ordinal)));
        Assert.That(_manager.Repository.GetDraft("OK", "ST05")!.Recipe.Tunables["brightThreshold"],
            Is.EqualTo(127));
    }

    [Test]
    public void DuplicateProductIdFailsWithoutPartialDraftsOrChangesToExisting()
    {
        _manager.CreateSimSolution("OK", "Atomic Product");
        var before = _manager.Repository.ListDraftRevisions("OK");
        Assert.Throws<SqliteException>(() => _manager.CreateSimSolution("OK", "Duplicate Product"));
        var after = _manager.Repository.ListDraftRevisions("OK");
        Assert.That(after, Is.EqualTo(before));
        Assert.That(_manager.Repository.ListSolutions(), Has.Count.EqualTo(1));
    }
}