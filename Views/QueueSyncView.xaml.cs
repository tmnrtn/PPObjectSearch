using System.Windows;

namespace PPObjectSearch.Views;

public partial class QueueSyncView : ScreenView
{
    public QueueSyncView() => InitializeComponent();

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
