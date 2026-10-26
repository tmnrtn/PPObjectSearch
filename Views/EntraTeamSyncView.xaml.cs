using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class EntraTeamSyncView : ScreenView
{
    public EntraTeamSyncView() => InitializeComponent();

    protected override void OnScreenClosed()
    {
        (DataContext as EntraTeamSyncViewModel)?.Dispose();
    }
}
