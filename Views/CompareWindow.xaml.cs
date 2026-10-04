using System.Windows;

namespace PPObjectSearch.Views;

public partial class CompareWindow : Window
{
    public CompareWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
