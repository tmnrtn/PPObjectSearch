using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class AuditHistoryView : ScreenView
{
    public AuditHistoryView() => InitializeComponent();

    protected override async void OnFirstShown()
    {
        if (DataContext is AuditHistoryViewModel viewModel) await viewModel.LoadAsync();
    }

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
