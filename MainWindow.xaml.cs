using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();
    private Point? _dragStart;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
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
