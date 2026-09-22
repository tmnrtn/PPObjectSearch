using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ReferenceEntitySettingsWindow : Window
{
    public ReferenceEntitySettingsWindow() => InitializeComponent();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReferenceEntitySettingsViewModel viewModel) return;

        // Closing on an unusable key would save a configuration that cannot run, so the dialog
        // stays open and says what is missing.
        if (!viewModel.Apply(out var error))
        {
            MessageBox.Show(error, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
