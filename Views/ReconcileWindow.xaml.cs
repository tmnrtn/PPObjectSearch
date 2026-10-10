using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ReconcileWindow : Window
{
    public ReconcileWindow() => InitializeComponent();

    /// <summary>
    /// True means something was written, which the comparison behind this window is now out of
    /// date about. Closing without applying reports false so nothing is re-read for no reason.
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e) => CloseReporting();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        CloseReporting();
    }

    /// <summary>What the caller reads back is the view model's own record of writes, so however
    /// the window is closed the comparison is refreshed if anything changed.</summary>
    private void CloseReporting() => Close();

    /// <summary>
    /// The title bar's close, Alt+F4 and the buttons all come through here. A run in progress keeps
    /// the window: closing it would not stop the writes, only hide what became of them. The user is
    /// offered to stop the run instead, which ends after the row being written.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is ReconcileViewModel { IsRunning: true } running)
        {
            e.Cancel = true;

            var stop = MessageBox.Show(this,
                "Changes are still being written. Stop after the current one?\n\n" +
                "Nothing already written is undone. The window stays open until the run has stopped.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (stop == MessageBoxResult.Yes) running.CancelRunCommand.Execute(null);
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
