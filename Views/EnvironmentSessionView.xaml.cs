using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class EnvironmentSessionView : UserControl
{
    public EnvironmentSessionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += (_, _) => FocusMostUsefulBox();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => FocusMostUsefulBox();

    /// <summary>
    /// A connected tab is there to be searched; a new tab needs its URL first.
    /// </summary>
    private void FocusMostUsefulBox()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var target = DataContext is EnvironmentSessionViewModel { IsConnected: true }
                ? (Control)SearchBox
                : EnvironmentBox;

            target.Focus();
            Keyboard.Focus(target);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
