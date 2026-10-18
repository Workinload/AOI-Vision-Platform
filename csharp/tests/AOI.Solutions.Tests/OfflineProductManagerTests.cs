using AOI.Domain;
using AOI.Solutions;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

[TestFixture]
public sealed class OfflineProductManagerTests
{
    private string db = string.Empty;
    private OfflineProductManager manager = null!;

    [SetUp]
    public void SetUp()
    {
        db = Path.Combine(Path.GetTempPath(), "aoi-offline-" + Guid.NewGuid().ToString("N") + ".sqlite");
        manager = OfflineProductManager.Open(db, "SIM_OPERATOR_TEST");
        manager.EnsureSimProfile();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(db)) File.Delete(db);
    }

    [Test]
    public void DraftEditPublishPersistsImmutableReleaseWithRealDigest()
    {
        manager.CreateSimSolution("S1", "Offline Test Product");
        var view = manager.ReadChannel("S1", "CH02");
        Assert.That(view.SolutionId, Is.EqualTo("S1"));
        Assert.That(view.StationRevision, Is.GreaterThanOrEqualTo(0));

        var result = manager.Publish("S1");
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Published), result.Message);
        Assert.That(result.Release, Is.Not.Null);
        var release = result.Release!;
        Assert.That(release.ReleaseId, Is.Not.Empty);
        Assert.That(release.ContentSha256, Has.Length.EqualTo(64));

        var readBack = manager.RequirePublished(release.ReleaseId);
        Assert.That(readBack.ContentSha256, Is.EqualTo(release.ContentSha256));
        readBack.Verify();
    }

    [Test]
    public void StaleExpectedRevisionIsRejectedBeforeWrite()
    {
        manager.CreateSimSolution("S2", "Stale Revision Product");
        var draft = manager.Repository.GetDraft("S2", "LOADING");
        Assert.That(draft, Is.Not.Null);
        var recipe = draft!.Recipe;
        Assert.Throws<InvalidOperationException>(() => manager.SaveStation("S2", recipe, expectedRevision: 99));
    }

    [Test]
    public void NonFiniteTunableIsRejectedNotPublished()
    {
        manager.CreateSimSolution("S3", "Bad Parameter Product");
        var draft = manager.Repository.GetDraft("S3", "LOADING")!;
        var bad = draft.Recipe with
        {
            Tunables = new Dictionary<string, double> { ["brightThreshold"] = double.NaN }
        };
        Assert.Throws<InvalidDataException>(() => manager.SaveStation("S3", bad, draft.Revision));
    }

    [Test]
    public void PublishWithoutAnyDraftIsRejectedWithMessage()
    {
        var result = manager.Publish("DOES_NOT_EXIST");
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Rejected));
        Assert.That(result.Release, Is.Null);
        Assert.That(result.Message, Is.Not.Empty);
    }
}
