using System.Windows;

namespace PPObjectSearch.Views;

public partial class DependencyExplorerWindow : Window
{
    public DependencyExplorerWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
