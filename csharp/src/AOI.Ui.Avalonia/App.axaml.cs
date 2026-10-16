using Avalonia;
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
            desktop.MainWindow = new MainWindow(new MainWindowViewModel(role, once));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
