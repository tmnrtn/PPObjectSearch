using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class EntraTeamSyncWindow : Window
{
    public EntraTeamSyncWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as EntraTeamSyncViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
