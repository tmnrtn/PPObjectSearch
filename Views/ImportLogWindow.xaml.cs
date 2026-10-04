using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ImportLogWindow : Window
{
    public ImportLogWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ImportLogViewModel viewModel) await viewModel.LoadAsync();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as ImportLogViewModel)?.Stop();
        base.OnClosed(e);
    }
}
