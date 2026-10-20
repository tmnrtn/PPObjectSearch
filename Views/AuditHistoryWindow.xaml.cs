using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class AuditHistoryWindow : Window
{
    public AuditHistoryWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is AuditHistoryViewModel viewModel) await viewModel.LoadAsync();
        };
        Closed += (_, _) => (DataContext as AuditHistoryViewModel)?.Detach();
    }
}
