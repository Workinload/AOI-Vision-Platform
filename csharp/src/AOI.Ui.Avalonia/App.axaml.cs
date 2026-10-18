using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AOI.Ui.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var role = args.Contains("--slave", StringComparer.Ordinal) ? "slave" : "master";
            var once = args.Contains("--once", StringComparer.Ordinal);
            var window = new MainWindow(new MainWindowViewModel(role, once));
            if (role == "slave")
                window.Opened += (_, _) => OffsetSlaveWindow(window);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// SIM_FULL launch starts the Master and Slave UI within seconds of each other. Left alone
    /// both windows open at the identical default position, so the operator can only ever see
    /// whichever started last and cannot compare the two machines side by side. Shift the Slave
    /// window right of the Master one and clamp it inside the current working area so it can
    /// never be positioned off-screen. Presentational only: no Core, IPC, WAL or production
    /// state is read or modified here.
    /// </summary>
    internal static void OffsetSlaveWindow(Window window)
    {
        try
        {
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null) return;
            var area = screen.WorkingArea;
            double scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
            int width = (int)Math.Round(window.Bounds.Width * scaling);
            int height = (int)Math.Round(window.Bounds.Height * scaling);

            int x = area.X + (area.Width / 2);
            int y = area.Y + 40;
            if (width > 0) x = area.X + ((area.Width - width) / 2) + (width / 4);
            if (height > 0) y = area.Y + Math.Max(0, (area.Height - height) / 2);
            if (width > 0) x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
            if (height > 0) y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
            window.Position = new PixelPoint(x, y);
        }
        catch (Exception)
        {
            // Cosmetic only: never take down the operator UI over window placement.
        }
    }
}
