using System.Windows;

namespace PPObjectSearch.Views;

/// <summary>Deployment settings: choose the values, save, see what is still blank - see the view model.</summary>
public partial class DeploymentSettingsWindow : Window
{
    public DeploymentSettingsWindow() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
