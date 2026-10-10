using System.Windows;

namespace PPObjectSearch.Views;

public partial class DocumentationWindow : Window
{
    public DocumentationWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
