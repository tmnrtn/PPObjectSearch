using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

/// <summary>The confirmation in front of a one-click change - see <see cref="WriteConfirmation"/>.</summary>
public partial class WriteConfirmWindow : Window
{
    public WriteConfirmWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>True when the change should go ahead.</summary>
    public static bool Ask(Window? owner, EnvironmentSessionViewModel session, string title, string action)
    {
        var window = new WriteConfirmWindow { Title = title, Owner = owner };
        window.Environment.DataContext = session;
        window.Heading.Text = title;
        window.Action.Text = action;
        window.ConfirmButton.Content = title;

        return window.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
