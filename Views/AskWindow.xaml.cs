using System.Windows;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

/// <summary>The app's own question dialog, in place of a message box - see <see cref="AskRequest"/>.</summary>
public partial class AskWindow : Window
{
    private bool _confirmFirst;

    public AskWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_confirmFirst && ConfirmButton.IsVisible && ConfirmButton.IsEnabled) ConfirmButton.Focus();
            else CancelButton.Focus();
        };
    }

    /// <summary>True when the primary button was chosen; always false for a notice.</summary>
    public static bool Ask(Window? owner, AskRequest request)
    {
        var window = new AskWindow { Title = request.Title ?? request.Heading };
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.Heading.Text = request.Heading;
        window.Body.Text = request.Body;
        window.Body.Visibility = string.IsNullOrWhiteSpace(request.Body) ? Visibility.Collapsed : Visibility.Visible;
        window.CancelButton.Content = request.Cancel;
        window._confirmFirst = request.ConfirmIsDefault;

        if (request.Environment is { } environment)
        {
            window.Environment.DataContext = environment;
            window.Environment.Visibility = Visibility.Visible;
        }

        if (request.Points is { Count: > 0 } points)
        {
            window.Points.ItemsSource = points;
            window.Points.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            window.Note.Text = request.Note;
            window.Note.Visibility = Visibility.Visible;
        }

        if (request.Confirm is { } confirm)
        {
            window.ConfirmButton.Content = confirm;
        }
        else
        {
            // A notice: one button, which Enter and Escape both choose.
            window.ConfirmButton.Visibility = Visibility.Collapsed;
            window.CancelButton.IsDefault = true;
            window.CancelButton.Style = (Style)window.FindResource("PrimaryButton");
        }

        if (!string.IsNullOrWhiteSpace(request.Acknowledgement))
        {
            window.AcknowledgeText.Text = request.Acknowledgement;
            window.Acknowledgement.Visibility = Visibility.Visible;
            window.ConfirmButton.IsEnabled = false;
            window.ConfirmButton.ToolTip = "Tick the acknowledgement first";
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(window.ConfirmButton, true);
        }

        return window.ShowDialog() == true;
    }

    private void Acknowledge_Changed(object sender, RoutedEventArgs e)
    {
        var ticked = AcknowledgeBox.IsChecked == true;
        ConfirmButton.IsEnabled = ticked;
        ConfirmButton.ToolTip = ticked ? null : "Tick the acknowledgement first";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
