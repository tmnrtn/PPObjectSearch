using System.Windows;

namespace PPObjectSearch.Views;

public partial class ContentSearchWindow : Window
{
    public ContentSearchWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
