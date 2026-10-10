using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class FailuresWindow : Window
{
    public FailuresWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is FailuresViewModel viewModel) await viewModel.LoadAsync();
        };
        Closed += (_, _) => (DataContext as FailuresViewModel)?.Dispose();
    }
}
