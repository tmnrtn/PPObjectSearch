using System.Windows;

namespace PPObjectSearch.Views;

public partial class ReadinessView : ScreenView
{
    public ReadinessView() => InitializeComponent();

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
