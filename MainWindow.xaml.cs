using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.Ai;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();
    private Point? _dragStart;

    public MainWindow()
    {
        // Screens opened as windows - by the setting, or popped out of a tab - need somewhere to go.
        ScreenHost.Current.Attach(new Views.ScreenWindows(ScreenHost.Current));

        InitializeComponent();
        DataContext = _shell;
        Loaded += (_, _) => WarnAboutSettings();

        // The assistant's tools read screens through their views, as the panel does.
        _shell.AiWorkspace.ReadView = ReadForAi;
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.IsAiPanelOpen)) SizeAiColumn();
        };
        SizeAiColumn();

        // The tab chosen - by click, Ctrl+Tab or the list of every screen - scrolls into view.
        _shell.Screens.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ScreenHost.SelectedTab) || _shell.Screens.SelectedTab is not { } tab) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (ScreenTabs.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container) container.BringIntoView();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    /// <summary>The strip scrolls sideways under the wheel, as tabs past its width are not wrapped.</summary>
    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    /// <summary>The assistant panel's width, kept while it is hidden and shown again.</summary>
    private double _aiWidth = 380;

    private void AskAi_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) =>
        _shell.ToggleAiPanelCommand.Execute(null);

    private void SizeAiColumn() =>
        AiColumn.Width = _shell.IsAiPanelOpen ? new GridLength(_aiWidth) : new GridLength(0);

    private void AiSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (AiColumn.ActualWidth > 0) _aiWidth = Math.Max(300, AiColumn.ActualWidth);
    }

    /// <summary>
    /// A screen as the assistant reads it: an open screen's view, or - for the selected environment -
    /// the main window's view of it. Null for an environment not on screen.
    /// </summary>
    private AiScreen? ReadForAi(object key, string title)
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => ReadForAi(key, title));

        return key switch
        {
            OpenScreen { Content: DependencyObject view } => Views.ScreenText.Read(view, title),
            EnvironmentSessionViewModel session when ReferenceEquals(session, _shell.SelectedSession) => Views.ScreenText.Read(HomeContent, title),
            _ => null
        };
    }

    /// <summary>
    /// Said once, at startup: the app carries on with defaults, and nothing typed into it from
    /// now on will be saved until the file is fixed.
    /// </summary>
    private void WarnAboutSettings()
    {
        if (_shell.SettingsProblem is { } problem)
        {
            MessageBox.Show(this, problem, "Settings not loaded", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _shell.Shutdown();
        base.OnClosing(e);
    }

    // Sidebar drag-to-reorder. The drag only starts once the mouse has moved past the system
    // threshold, so plain clicks (including the close button) behave as before.

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);

        // Clicking the environment already selected brings it back in front of any screen tab.
        _shell.Screens.ShowHome();
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragStart = null;
        if (sender is ListBoxItem { DataContext: EnvironmentSessionViewModel session } item)
            DragDrop.DoDragDrop(item, session, DragDropEffects.Move);
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(EnvironmentSessionViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(EnvironmentSessionViewModel)) is not EnvironmentSessionViewModel dragged) return;
        if (sender is not ListBoxItem { DataContext: EnvironmentSessionViewModel target }) return;

        _shell.MoveTab(dragged, _shell.Sessions.IndexOf(target));
        e.Handled = true;
    }

    /// <summary>Browser profiles can come and go between visits, so the menu reads them as it opens.</summary>
    private void EnvironmentMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EnvironmentSessionViewModel session }) session.RaiseBrowserProfile();
    }

    /// <summary>The context menu's own DataContext is the row's session, so it closes that one.</summary>
    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EnvironmentSessionViewModel session })
            _shell.CloseTabCommand.Execute(session);
    }
}
