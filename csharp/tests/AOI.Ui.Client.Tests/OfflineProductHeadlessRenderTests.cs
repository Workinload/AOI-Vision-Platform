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
        IReadOnlyList<Rect> RoiEditorRects, Rect? SaveButtonRect, Size ClientSize,
        bool OfflineEntryBarVisible, bool OfflineEditorVisible);

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
            Assert.That(normal.RoiEditorRects, Has.Count.GreaterThanOrEqualTo(4),
                "four real Core ROI editors must remain usable on the default page");
            Assert.That(normal.OfflineEntryBarVisible, Is.False,
                "the large offline DB entry bar is hidden on the Core ROI sub-page");
            Assert.That(normal.OfflineEditorVisible, Is.False);
            Assert.That(normal.Texts.Any(t => t.Contains("校验并持久保存 SIM_ONLY ROI 草稿")),
                Is.True);
            AssertBoundsInsideWindow(normal, "1920x1080");

            // 1b) Real minimum window size: the same controls must lie inside the actual
            // client bounds, not merely report IsEffectivelyVisible (the native failure was
            // clipped by the tab viewport). Layout bounds are checked on a 1250x760 window.
            var shortVm = new MainWindowViewModel("master", true);
            shortVm.SelectedWorkspaceIndex = 5;
            var shortWindow = Render(shortVm, Path.Combine(root,
                "product-roi-CORE_ROI_SHORT_1250x760.png"), 1250, 760);
            Assert.That(shortVm.OfflineProduct.ProductRoiTabIndex, Is.EqualTo(0));
            Assert.That(shortWindow.CoreRoiSaveButtonVisible, Is.True);
            Assert.That(shortWindow.RoiEditorRects, Has.Count.GreaterThanOrEqualTo(4));
            Assert.That(shortWindow.ClientSize.Width, Is.EqualTo(1250));
            Assert.That(shortWindow.ClientSize.Height, Is.EqualTo(760));
            AssertBoundsInsideWindow(shortWindow, "1250x760");

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
            Assert.That(production.Texts.Any(t => t.Contains("双 Core 独占锁")), Is.True);
            TestContext.Out.WriteLine("AOI146_HEADLESS_RENDER_DIR=" + root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void AssertBoundsInsideWindow(RenderResult result, string label)
    {
        Assert.That(result.SaveButtonRect, Is.Not.Null, label + " save button missing");
        var save = result.SaveButtonRect!.Value;
        Assert.That(save.Left, Is.GreaterThanOrEqualTo(0), label + " save button left clipped");
        Assert.That(save.Top, Is.GreaterThanOrEqualTo(0), label + " save button top clipped");
        Assert.That(save.Right, Is.LessThanOrEqualTo(result.ClientSize.Width + 0.5),
            label + " save button right clipped");
        Assert.That(save.Bottom, Is.LessThanOrEqualTo(result.ClientSize.Height + 0.5),
            label + " save button bottom clipped");
        Assert.That(result.RoiEditorRects, Has.Count.GreaterThanOrEqualTo(4),
            label + " ROI editors missing");
        foreach (var edit in result.RoiEditorRects)
        {
            Assert.That(edit.Left, Is.GreaterThanOrEqualTo(0), label + " ROI editor left clipped");
            Assert.That(edit.Top, Is.GreaterThanOrEqualTo(0), label + " ROI editor top clipped");
            Assert.That(edit.Right, Is.LessThanOrEqualTo(result.ClientSize.Width + 0.5),
                label + " ROI editor right clipped");
            Assert.That(edit.Bottom, Is.LessThanOrEqualTo(result.ClientSize.Height + 0.5),
                label + " ROI editor bottom clipped");
        }
    }

    private static RenderResult Render(MainWindowViewModel vm, string outputPath,
        int width = 1920, int height = 1080)
    {
        var window = new MainWindow(vm) { Width = width, Height = height };
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
        var editorRects = controls.OfType<TextBox>()
            .Where(x => x.IsEffectivelyVisible && x.Bounds.Width > 26)
            .Select(x => ControlRect(x, window))
            .Where(x => x is not null).Select(x => x!.Value).ToArray();
        Rect? saveRect = saveButton is null ? null : ControlRect(saveButton, window);
        var entryHeader = controls.OfType<TextBlock>().FirstOrDefault(x =>
            (x.Text ?? "").StartsWith("OFFLINE_RECIPE_MANAGEMENT", StringComparison.Ordinal));
        var offlineEditor = controls.OfType<TextBlock>().FirstOrDefault(x =>
            (x.Text ?? "").Contains("A6 产品方案（真实列表）", StringComparison.Ordinal));
        var texts = controls.OfType<TextBlock>().Select(x => x.Text ?? "").ToArray();
        var result = new RenderResult(texts,
            saveButton?.IsEffectivelyVisible == true,
            saveButton?.IsEffectivelyVisible == true && saveButton.IsEnabled,
            editorRects, saveRect, window.Bounds.Size,
            entryHeader?.IsEffectivelyVisible == true,
            offlineEditor?.IsEffectivelyVisible == true);
        window.Close();
        return result;
    }

    private static Rect? ControlRect(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window);
        return origin is null ? null : new Rect(origin.Value, control.Bounds.Size);
    }
}