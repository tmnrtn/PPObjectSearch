using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ImportLogView : ScreenView
{
    public ImportLogView() => InitializeComponent();

    protected override async void OnFirstShown()
    {
        if (DataContext is ImportLogViewModel viewModel) await viewModel.LoadAsync();
    }

    protected override void OnScreenClosed()
    {
        (DataContext as ImportLogViewModel)?.Stop();
    }
}
