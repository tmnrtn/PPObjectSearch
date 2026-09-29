using System.Windows;

namespace PPObjectSearch.Views;

/// <summary>Asks for one name - saving a new configuration, or renaming one.</summary>
public partial class NamePromptWindow : Window
{
    public NamePromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The trimmed name, or null if the dialog was cancelled or left blank.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial)
    {
        var window = new NamePromptWindow { Title = title, Owner = owner };
        window.Prompt.Text = prompt;
        window.NameBox.Text = initial;

        if (window.ShowDialog() != true) return null;

        var name = window.NameBox.Text.Trim();
        return name.Length == 0 ? null : name;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
