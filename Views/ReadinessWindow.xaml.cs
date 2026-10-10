using System.Windows;

namespace PPObjectSearch.Views;

public partial class ReadinessWindow : Window
{
    public ReadinessWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
