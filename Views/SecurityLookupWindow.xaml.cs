using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class SecurityLookupWindow : Window
{
    public SecurityLookupWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is SecurityLookupViewModel viewModel) await viewModel.EnsureListsAsync();
        };
    }
}
