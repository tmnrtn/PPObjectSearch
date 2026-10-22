using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class AppInsightsPickerWindow : Window
{
    public AppInsightsPickerWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            if (DataContext is AppInsightsPickerViewModel viewModel) await viewModel.LoadAsync();
        };
        Closed += (_, _) => (DataContext as AppInsightsPickerViewModel)?.Dispose();
    }

    /// <summary>True when the window closed on Remove rather than on a choice.</summary>
    public bool Removed { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        Removed = true;
        DialogResult = true;
    }
}
