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
    public static bool Ask(Window? owner, EnvironmentSessionViewModel session, string title, string action) =>
        Ask(owner, (object)session, title, action, counts: null, caveat: null, confirmLabel: title);

    /// <summary>
    /// The same confirmation with a tally of what will happen and its caveats, for a change made of
    /// several kinds of write. <paramref name="environment"/> is a tab or an <see cref="EnvironmentLineInfo"/>.
    /// </summary>
    public static bool Ask(Window? owner, object environment, string title, string action,
        IReadOnlyList<WriteCount>? counts, string? caveat, string confirmLabel)
    {
        var window = new WriteConfirmWindow { Title = title, Owner = owner };
        window.Environment.DataContext = environment;
        window.Heading.Text = title;
        window.Action.Text = action;
        window.ConfirmButton.Content = confirmLabel;

        if (counts is { Count: > 0 })
        {
            window.Counts.ItemsSource = counts;
            window.Counts.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(caveat))
        {
            window.Caveat.Text = caveat;
            window.Caveat.Visibility = Visibility.Visible;
        }

        return window.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
