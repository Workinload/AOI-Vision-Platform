using AOI.Contracts;
using AOI.Diagnostics;
using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AOI.Diagnostics.Tests;

[TestFixture]
public sealed class ProductionReportTests
{
    private string _root = "";
    private static readonly DateTimeOffset RegisteredUtc =
        new(2026, 10, 17, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo SgTime =
        TimeZoneInfo.CreateCustomTimeZone("A7_SG_UTC8", TimeSpan.FromHours(8), "Singapore", "SGT");

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-reports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (ProductionStore Master, ProductionStore Slave) Stores() =>
        (new ProductionStore(Path.Combine(_root, "master", "production.db")),
         new ProductionStore(Path.Combine(_root, "slave", "production.db")));

    private static WorkpieceIdentity Item(string barcode) =>
        WorkpieceIdentity.New(barcode, 1, "SIM_RELEASE") with { CreatedAtUtc = RegisteredUtc };

    private static void Register(ProductionStore master, WorkpieceIdentity identity, int seq)
    {
        master.CreateAndQueueRegistration(identity,
            PeerEnvelope.Create("master", "slave", seq, identity, "workpiece_registered", identity));
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+iSuoAAAAASUVORK5CYII=");

    private ChannelResult SaveChannel(ProductionStore store, WorkpieceIdentity identity,
        ChannelOutcome outcome, bool materializeImage = false)
    {
        Guid frame = Guid.NewGuid();
        string sha = Convert.ToHexString(SHA256.HashData(OnePixelPng)).ToLowerInvariant();
        var result = new ChannelResult(Guid.NewGuid(), identity.WorkpieceId, identity.Epoch,
            identity.ReleaseId, identity.TraceId, "CH06", "ST05", "HIK-M-2",
            Guid.NewGuid(), frame, null, 1, sha, outcome, true, RegisteredUtc);
        store.SaveLocalResult(result);
        if (materializeImage)
        {
            string folder = Path.Combine(_root, "master", "assets",
                identity.WorkpieceId.ToString("N"), "CH06");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, frame.ToString("N") + ".png"), OnePixelPng);
        }
        return result;
    }

    private WorkpieceIdentity[] FiveWorkpieces(ProductionStore master, bool imageForFirst = false)
    {
        var items = new[] { Item("REPEAT"), Item("REPEAT"), Item("ITEM-03"),
            Item("ITEM-04"), Item("ITEM-05") };
        for (int i = 0; i < items.Length; i++)
        {
            Register(master, items[i], i + 1);
            if (i == 4)
            {
                master.RecordWorkpieceHold(items[i], "SENSOR_TIMEOUT", "Simulation missing trigger.");
                continue;
            }
            bool good = i is 0 or 2;
            SaveChannel(master, items[i], good ? ChannelOutcome.Ok : ChannelOutcome.Ng,
                materializeImage: imageForFirst && i == 0);
            master.StoreFinal(items[i], new FinalDecision(
                good ? FinalDisposition.Good : FinalDisposition.Reject, true,
                ["CH06"], ["CH06"], good ? "COMPLETE_OK:SIM_ONLY" : "COMPLETE_NG:SIM_ONLY"));
        }
        return items;
    }

    [Test]
    public void FivePhysicalUnits_RepeatBarcodeDoesNotDeduplicate_AndDailyWeeklyDenominatorsAreCorrect()
    {
        var (master, _) = Stores();
        var items = FiveWorkpieces(master);
        var reports = new ProductionReports(_root);
        var daily = reports.Daily(new DateOnly(2026, 10, 17), SgTime);
        Assert.Multiple(() =>
        {
            Assert.That(daily.Total, Is.EqualTo(5));
            Assert.That(daily.Good, Is.EqualTo(2));
            Assert.That(daily.Ng, Is.EqualTo(2));
            Assert.That(daily.Hold, Is.EqualTo(1));
            Assert.That(daily.InProgress, Is.Zero);
            Assert.That(daily.Completed, Is.EqualTo(4));
            Assert.That(daily.GoodRate, Is.EqualTo(0.5));
            Assert.That(daily.GoodShareOfRegistered, Is.EqualTo(0.4));
            Assert.That(daily.FromUtc, Is.EqualTo(new DateTimeOffset(2026, 10, 16, 16, 0, 0, TimeSpan.Zero)));
        });
        var week = reports.Weekly(new DateOnly(2026, 10, 17), SgTime);
        Assert.That(week.Total, Is.EqualTo(5));
        Assert.That(week.FromUtc, Is.EqualTo(new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero)));

        var duplicates = reports.Search(new ProductionSearch(Barcode: "REPEAT"));
        Assert.That(duplicates.TotalMatches, Is.EqualTo(2));
        Assert.That(duplicates.Rows.Select(x => x.WorkpieceId).Distinct(), Has.Count.EqualTo(2));
        Assert.That(duplicates.Rows.All(x => x.WorkpieceId == items[0].WorkpieceId ||
                                             x.WorkpieceId == items[1].WorkpieceId), Is.True);

        var station = reports.Search(new ProductionSearch(StationId: "ST05"));
        Assert.That(station.TotalMatches, Is.EqualTo(4));
        Assert.That(reports.Search(new ProductionSearch(StationId: "ST09")).TotalMatches, Is.Zero);
        var page = reports.Search(new ProductionSearch(Limit: 2, Offset: 0));
        Assert.That(page.Rows, Has.Length.EqualTo(2));
        Assert.That(page.HasMore, Is.True);
        Assert.That(reports.Search(new ProductionSearch(Limit: 2, Offset: 4)).HasMore, Is.False);
        Assert.That(reports.Search(new ProductionSearch(WorkpieceId: items[4].WorkpieceId))
            .Rows.Single().Disposition, Is.EqualTo(ProductionDisposition.Hold));
    }

    [Test]
    public void TraceFollowsRealPngAndEvents_AndCsvJsonAreExported()
    {
        var (master, _) = Stores();
        var items = FiveWorkpieces(master, imageForFirst: true);
        var reports = new ProductionReports(_root);
        var traced = reports.Trace(items[0].WorkpieceId);
        Assert.That(traced.Workpiece.WorkpieceId, Is.EqualTo(items[0].WorkpieceId));
        Assert.That(traced.Channels, Has.Length.EqualTo(1));
        Assert.That(traced.Channels[0].Result.ChannelId, Is.EqualTo("CH06"));
        Assert.That(traced.Channels[0].Image.State, Is.EqualTo("VERIFIED"));
        Assert.That(traced.Channels[0].Image.HashMatches, Is.True);
        Assert.That(traced.Channels[0].Image.RelativePath, Does.StartWith("assets/"));
        Assert.That(traced.Events.Select(e => e.Kind), Does.Contain("CHANNEL_RESULT_LOCAL"));
        Assert.That(reports.SearchEvents(new ProductionEventSearch(
            WorkpieceId: items[0].WorkpieceId, ContainsText: "CH06")), Has.Length.EqualTo(1));

        string csv = Path.Combine(_root, "exports", "history.csv");
        string json = Path.Combine(_root, "exports", "trace.json");
        string stats = Path.Combine(_root, "exports", "daily.csv");
        ProductionReportExports.SaveHistoryCsv(reports.Search(new ProductionSearch()), csv);
        ProductionReportExports.SaveJson(traced, json);
        ProductionReportExports.SavePeriodCsv(reports.Daily(new DateOnly(2026, 10, 17), SgTime), stats);
        Assert.That(File.ReadAllText(csv), Does.Contain(items[0].WorkpieceId.ToString("D")));
        Assert.That(File.ReadAllText(csv), Does.Contain("REPEAT"));
        Assert.That(File.ReadAllText(json), Does.Contain("VERIFIED"));
        Assert.That(File.ReadAllText(json), Does.Contain("assets/"));
        Assert.That(File.ReadAllText(stats), Does.Contain("GoodRateCompletedDenominator"));
    }

    [Test]
    public void TraceDoesNotClaimMissingOrTamperedImageAsVerified()
    {
        var (master, _) = Stores();
        var identity = Item("PIXEL");
        Register(master, identity, 1);
        var result = SaveChannel(master, identity, ChannelOutcome.Ok, materializeImage: true);
        var reports = new ProductionReports(_root);
        Assert.That(reports.Trace(identity.WorkpieceId).Channels.Single().Image.State, Is.EqualTo("VERIFIED"));
        string png = Path.Combine(_root, "master", "assets", identity.WorkpieceId.ToString("N"),
            "CH06", result.FrameId.ToString("N") + ".png");
        File.AppendAllBytes(png, [1, 2, 3]); // intentionally corrupt real bytes while keeping PNG signature
        Assert.That(reports.Trace(identity.WorkpieceId).Channels.Single().Image.State,
            Is.EqualTo("SHA256_MISMATCH"));
        File.Delete(png);
        Assert.That(reports.Trace(identity.WorkpieceId).Channels.Single().Image.State,
            Is.EqualTo("FILE_MISSING"));
    }

    [Test]
    public void UnfinishedIsNotHold_AndBadInputsDoNotQueryOrLeakOtherRows()
    {
        var (master, _) = Stores();
        var identity = Item("A' OR 1=1 --");
        Register(master, identity, 1);
        var reports = new ProductionReports(_root);
        var day = reports.Daily(new DateOnly(2026, 10, 17), SgTime);
        Assert.That(day.Total, Is.EqualTo(1));
        Assert.That(day.Hold, Is.Zero);
        Assert.That(day.InProgress, Is.EqualTo(1));
        Assert.That(day.GoodRate, Is.Null);
        Assert.That(reports.Search(new ProductionSearch(Barcode: "A' OR 1=1 --")).TotalMatches, Is.EqualTo(1));
        Assert.That(reports.Search(new ProductionSearch(Barcode: "' OR 1=1 --")).TotalMatches, Is.Zero);
        Assert.That(reports.Search(new ProductionSearch(
            FromUtc: new DateTimeOffset(2026,10,17,2,0,0,TimeSpan.Zero),
            UntilUtc: new DateTimeOffset(2026,10,17,3,0,0,TimeSpan.Zero))).TotalMatches, Is.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => reports.Search(new ProductionSearch(Limit: 501)));
        Assert.Throws<ArgumentOutOfRangeException>(() => reports.Search(new ProductionSearch(
            FromUtc: RegisteredUtc, UntilUtc: RegisteredUtc)));
        Assert.Throws<KeyNotFoundException>(() => reports.Trace(Guid.NewGuid()));
    }

    [Test]
    public void CsvQuotingDefusesSpreadsheetInjection()
    {
        var page = new ProductionHistoryPage(1,
            [new ProductionHistoryRow(Guid.NewGuid(), "=1+2", Guid.NewGuid(), 1,
                "SIM", RegisteredUtc, ProductionDisposition.InProgress, 0, null)], 0, 100);
        string path = Path.Combine(_root, "inj.csv");
        ProductionReportExports.SaveHistoryCsv(page, path);
        var csv = File.ReadAllText(path);
        Assert.That(csv, Does.Contain("\"'=1+2\""));
    }
}
