using System.ComponentModel;
using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _shell.Shutdown();
        base.OnClosing(e);
    }
}
