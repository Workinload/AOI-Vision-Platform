using Avalonia;
using Avalonia.Headless;

namespace AOI.Ui.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var builder = AppBuilder.Configure<App>();
        builder = args.Contains("--headless", StringComparer.Ordinal)
            ? builder.UseHeadless(new AvaloniaHeadlessPlatformOptions())
            : builder.UsePlatformDetect();
        return builder.LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
