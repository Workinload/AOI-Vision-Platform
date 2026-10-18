using AOI.Solutions;
using AOI.Ui.Avalonia;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

/// <summary>
/// HONEST EVIDENCE BOUNDARY: these are Avalonia Headless software renders of the real
/// MainWindow XAML in the test process. They are NOT native windows, NOT real desktop
/// screenshots, and NOT operator clicks. The PNGs are written to the temp evidence folder.
/// </summary>
[TestFixture]
public sealed class OfflineProductHeadlessRenderTests
{
    [Test]
    public void OfflineProductWorkspaceRendersInAvaloniaHeadless()
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        // Unique evidence directory per run: never overwrite or delete earlier
        // SQLite/-wal/-shm evidence produced by an independent review run.
        string root = Path.Combine(Path.GetTempPath(),
            "aoi146-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string db = Path.Combine(root, "solutions.sqlite");
        try
        {
            var seed = OfflineProductManager.Open(db, "SIM_HEADLESS");
            seed.EnsureSimProfile();
            seed.CreateSimSolution("HEADLESS", "Headless Render Product");

            // 1) Isolated test gate: the full editor is visible and can be exercised.
            var testVm = new MainWindowViewModel("master", true, OfflineWriteGate.IsolatedTestDatabase);
            testVm.ApplyOfflineLaunch(new OfflineRecipeLaunchOptions(true, db, "SIM_HEADLESS", null));
            var testTexts = Render(testVm, Path.Combine(root,
                "offline-product-workspace-HEADLESS_RENDER_TEST_GATE.png"));
            Assert.That(testTexts.Any(t => t.Contains("OFFLINE_RECIPE_MANAGEMENT")), Is.True);
            Assert.That(testTexts.Any(t => t.Contains("Headless Render Product")), Is.True);
            Assert.That(testTexts.Any(t => t.Contains("TEST_ONLY")), Is.True);
            Assert.That(testTexts.Any(t => t.Contains("不可变发布")), Is.True);
            Assert.That(testTexts.Any(t => t.Contains("激活")), Is.True);

            // 2) Production gate: the shipped default MainWindow path is fail-closed.
            var productionVm = new MainWindowViewModel("master", true);
            productionVm.ApplyOfflineLaunch(new OfflineRecipeLaunchOptions(true, db, "SIM_HEADLESS", null));
            var productionTexts = Render(productionVm, Path.Combine(root,
                "offline-product-workspace-HEADLESS_RENDER_PRODUCTION_GATE.png"));
            Assert.That(productionTexts.Any(t => t.Contains("fail closed")), Is.True);
            Assert.That(productionTexts.Any(t => t.Contains("#149")), Is.True);
            TestContext.Out.WriteLine("AOI146_HEADLESS_RENDER_DIR=" + root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string[] Render(MainWindowViewModel vm, string outputPath)
    {
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var frame = window.GetLastRenderedFrame();
        Assert.That(frame, Is.Not.Null, "headless render produced no frame");
        frame!.Save(outputPath, PngBitmapEncoderOptions.Default);
        Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(4096));
        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(x => x.Text ?? "").ToArray();
        window.Close();
        return texts;
    }
}