using System.Windows;

namespace PPObjectSearch.Views;

public partial class QueueSyncWindow : Window
{
    public QueueSyncWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
