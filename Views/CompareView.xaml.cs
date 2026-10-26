using System.Windows;

namespace PPObjectSearch.Views;

public partial class CompareView : ScreenView
{
    public CompareView() => InitializeComponent();

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
