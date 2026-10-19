using AOI.Solutions;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

[TestFixture]
public sealed class OfflineProductEntryTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-offline-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void RunRootResolvesToItsSolutionsDatabase()
    {
        string db = Path.Combine(_root, "solutions.sqlite");
        File.WriteAllBytes(db, []);
        Assert.That(OfflineProductEntry.ResolveSolutionsDatabase(_root), Is.EqualTo(db));
    }

    [Test]
    public void ExplicitSolutionsDatabasePathIsAccepted()
    {
        string db = Path.Combine(_root, "solutions.sqlite");
        File.WriteAllBytes(db, []);
        Assert.That(OfflineProductEntry.ResolveSolutionsDatabase(db), Is.EqualTo(db));
    }

    [Test]
    public void OtherDatabaseNamesAreRejectedInsteadOfGuessed()
    {
        string other = Path.Combine(_root, "production.db");
        File.WriteAllBytes(other, []);
        Assert.Throws<InvalidDataException>(() =>
            OfflineProductEntry.ResolveSolutionsDatabase(other));
    }

    [Test]
    public void MissingEntryFailsClosedAndCreatesNothing()
    {
        string missing = Path.Combine(_root, "absent");
        Assert.Throws<FileNotFoundException>(() =>
            OfflineProductEntry.ResolveSolutionsDatabase(missing));
        Assert.That(Directory.Exists(missing), Is.False);
        Assert.Throws<ArgumentException>(() =>
            OfflineProductEntry.ResolveSolutionsDatabase(" "));
    }
}
