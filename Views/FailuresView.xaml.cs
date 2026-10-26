using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class FailuresView : ScreenView
{
    public FailuresView() => InitializeComponent();

    protected override async void OnFirstShown()
    {
        if (DataContext is FailuresViewModel viewModel) await viewModel.LoadAsync();
    }

    protected override void OnScreenClosed() => (DataContext as FailuresViewModel)?.Dispose();
}
