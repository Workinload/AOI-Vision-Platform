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
/// screenshots, and NOT operator clicks. The PNGs are written to a per-run temp folder.
/// </summary>
[TestFixture]
public sealed class OfflineProductHeadlessRenderTests
{
    private sealed record RenderResult(
        string[] Texts, bool CoreRoiSaveButtonVisible, bool CoreRoiSaveButtonEnabled,
        int VisibleRoiEditors, bool OfflineEntryBarVisible, bool OfflineEditorVisible);

    [Test]
    public void ProductRoiNavigationRendersInAvaloniaHeadless()
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

            // 1) Ordinary --master/--slave startup: product/ROI page must keep the existing
            //    Core ROI editor and its visible save button (native operator-smoke path).
            var normalVm = new MainWindowViewModel("master", true);
            normalVm.SelectedWorkspaceIndex = 5; // the operator navigation the smoke script uses
            var normal = Render(normalVm, Path.Combine(root,
                "product-roi-CORE_ROI_DEFAULT.png"));
            Assert.That(normalVm.OfflineProduct.ProductRoiTabIndex, Is.EqualTo(0));
            Assert.That(normalVm.OfflineProduct.IsLegacyCoreRoiTab, Is.True);
            Assert.That(normal.CoreRoiSaveButtonVisible, Is.True,
                "the existing Core ROI save button must stay visible by default");
            Assert.That(normal.CoreRoiSaveButtonEnabled, Is.True);
            Assert.That(normal.VisibleRoiEditors, Is.GreaterThanOrEqualTo(4),
                "four real Core ROI editors must remain usable on the default page");
            Assert.That(normal.OfflineEntryBarVisible, Is.False,
                "the large offline DB entry bar is hidden on the Core ROI sub-page");
            Assert.That(normal.OfflineEditorVisible, Is.False);
            Assert.That(normal.Texts.Any(t => t.Contains("校验并持久保存 SIM_ONLY ROI 草稿")),
                Is.True);

            // 2) Explicit --offline-recipe-management (isolated test gate): new editor selected.
            var testVm = new MainWindowViewModel("master", true, OfflineWriteGate.IsolatedTestDatabase);
            testVm.ApplyOfflineLaunch(new OfflineRecipeLaunchOptions(true, db, "SIM_HEADLESS", null));
            var test = Render(testVm, Path.Combine(root,
                "offline-product-workspace-HEADLESS_RENDER_TEST_GATE.png"));
            Assert.That(testVm.OfflineProduct.ProductRoiTabIndex, Is.EqualTo(1));
            Assert.That(testVm.OfflineProduct.IsOfflineEditorTab, Is.True);
            Assert.That(test.OfflineEntryBarVisible, Is.True);
            Assert.That(test.OfflineEditorVisible, Is.True);
            Assert.That(test.CoreRoiSaveButtonVisible, Is.False);
            Assert.That(test.Texts.Any(t => t.Contains("OFFLINE_RECIPE_MANAGEMENT")), Is.True);
            Assert.That(test.Texts.Any(t => t.Contains("Headless Render Product")), Is.True);
            Assert.That(test.Texts.Any(t => t.Contains("TEST_ONLY")), Is.True);
            Assert.That(test.Texts.Any(t => t.Contains("不可变发布")), Is.True);
            Assert.That(test.Texts.Any(t => t.Contains("激活")), Is.True);

            // 3) Production gate: explicit offline entry is visible but writes fail closed.
            var productionVm = new MainWindowViewModel("master", true);
            productionVm.ApplyOfflineLaunch(new OfflineRecipeLaunchOptions(true, db, "SIM_HEADLESS", null));
            var production = Render(productionVm, Path.Combine(root,
                "offline-product-workspace-HEADLESS_RENDER_PRODUCTION_GATE.png"));
            Assert.That(productionVm.OfflineProduct.ProductRoiTabIndex, Is.EqualTo(1));
            Assert.That(production.OfflineEntryBarVisible, Is.True);
            Assert.That(production.Texts.Any(t => t.Contains("fail closed")), Is.True);
            Assert.That(production.Texts.Any(t => t.Contains("#149")), Is.True);
            TestContext.Out.WriteLine("AOI146_HEADLESS_RENDER_DIR=" + root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static RenderResult Render(MainWindowViewModel vm, string outputPath)
    {
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var frame = window.GetLastRenderedFrame();
        Assert.That(frame, Is.Not.Null, "headless render produced no frame");
        frame!.Save(outputPath, PngBitmapEncoderOptions.Default);
        Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(4096));

        var controls = window.GetVisualDescendants().ToArray();
        var saveButton = controls.OfType<Button>().FirstOrDefault(x =>
            string.Equals(x.Content as string, "校验并持久保存 SIM_ONLY ROI 草稿",
                StringComparison.Ordinal));
        int visibleEditors = controls.OfType<TextBox>()
            .Count(x => x.IsEffectivelyVisible && x.Bounds.Width > 26);
        var entryHeader = controls.OfType<TextBlock>().FirstOrDefault(x =>
            (x.Text ?? "").StartsWith("OFFLINE_RECIPE_MANAGEMENT", StringComparison.Ordinal));
        var offlineEditor = controls.OfType<TextBlock>().FirstOrDefault(x =>
            (x.Text ?? "").Contains("A6 产品方案（真实列表）", StringComparison.Ordinal));
        var texts = controls.OfType<TextBlock>().Select(x => x.Text ?? "").ToArray();
        var result = new RenderResult(texts,
            saveButton?.IsEffectivelyVisible == true,
            saveButton?.IsEffectivelyVisible == true && saveButton.IsEnabled,
            visibleEditors,
            entryHeader?.IsEffectivelyVisible == true,
            offlineEditor?.IsEffectivelyVisible == true);
        window.Close();
        return result;
    }
}
