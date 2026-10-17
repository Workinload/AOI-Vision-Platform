using AOI.Contracts;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class SimProductionReportContractTests
{
    private static SimProductionReports Example()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var end = DateTimeOffset.UtcNow.AddHours(1);
        var day = new SimReportPeriod("day", start, end, 5, 2, 1, 1, 1, 2.0 / 3.0);
        var week = day with { Label = "week" };
        return new SimProductionReports(true, DateTimeOffset.UtcNow, day, week, 5, [
            new SimReportWorkpiece(Guid.NewGuid(), "DUP", "SIMREL-1", start, "Good", 11, null),
            new SimReportWorkpiece(Guid.NewGuid(), "DUP", "SIMREL-1", start, "Ng", 11, null),
            new SimReportWorkpiece(Guid.NewGuid(), "DUP", "SIMREL-1", start, "Hold", 11, "BARCODE_MISMATCH")
        ]);
    }

    [Test]
    public void DuplicateBarcodeRemainsIndependentAndDailyWeeklyCountsRoundTrip()
    {
        var original = Example();
        original.Validate();
        var decoded = WireFormat.Parse<SimProductionReports>(WireFormat.Serialize(original));
        decoded.Validate();
        Assert.That(decoded.Recent.Select(x => x.WorkpieceId).Distinct().Count(), Is.EqualTo(3));
        Assert.That(decoded.Recent.Select(x => x.Barcode).Distinct().Count(), Is.EqualTo(1));
        Assert.That(decoded.Today.Ng, Is.EqualTo(1));
        Assert.That(decoded.ThisWeek.GoodRate, Is.EqualTo(2.0 / 3.0));
    }

    [Test]
    public void InconsistentTotalsRatesDuplicateIdsAndProductionClaimAreRejected()
    {
        var model = Example();
        Assert.Throws<InvalidDataException>(() =>
            (model with { IsSimulation = false }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (model with { Today = model.Today with { Good = 4 } }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (model with { Today = model.Today with { GoodRate = 1 } }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (model with { Recent = [model.Recent[0], model.Recent[0]] }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (model with { Recent = [model.Recent[0] with { WorkpieceId = Guid.Empty }] }).Validate());
    }
}
