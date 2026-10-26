using System.Windows;

namespace PPObjectSearch.Views;

public partial class DependencyExplorerView : ScreenView
{
    public DependencyExplorerView() => InitializeComponent();

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
