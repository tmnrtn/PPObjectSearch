using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class SecurityLookupView : ScreenView
{
    public SecurityLookupView() => InitializeComponent();

    protected override async void OnFirstShown()
    {
        if (DataContext is SecurityLookupViewModel viewModel) await viewModel.EnsureListsAsync();
    }

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
