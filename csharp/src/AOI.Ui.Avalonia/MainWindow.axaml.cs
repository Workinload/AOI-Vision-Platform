using Avalonia.Controls;

namespace AOI.Ui.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow() : this(new MainWindowViewModel("master", false)) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => viewModel.Start();
        Closed += (_, _) => viewModel.Stop();
    }
}
