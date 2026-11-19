using System.Windows;
using System.Windows.Controls;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

/// <summary>Ask AI settings: on or off, provider, model, key, and the environments it may read.</summary>
public partial class AiSettingsWindow : Window
{
    public AiSettingsWindow()
    {
        InitializeComponent();
    }

    private AiSettingsViewModel? ViewModel => DataContext as AiSettingsViewModel;

    // A PasswordBox's password does not bind, so it is handed over as it is typed.
    private void AnthropicKey_Changed(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) vm.AnthropicKey = ((PasswordBox)sender).Password;
    }

    private void OpenRouterKey_Changed(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) vm.OpenRouterKey = ((PasswordBox)sender).Password;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.Save();
        DialogResult = true;
    }
}
