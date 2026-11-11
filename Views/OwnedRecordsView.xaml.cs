using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

/// <summary>The records one user or team owns, table by table - see the XAML.</summary>
public partial class OwnedRecordsView : ScreenView
{
    public OwnedRecordsView() => InitializeComponent();

    /// <summary>A count still running stops with the window.</summary>
    protected override void OnScreenClosed() => (DataContext as OwnedRecordsViewModel)?.Dispose();
}
