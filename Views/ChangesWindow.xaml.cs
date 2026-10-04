using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ChangesWindow : Window
{
    public ChangesWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ChangesViewModel viewModel) await viewModel.LoadAsync();
        };
    }
}
