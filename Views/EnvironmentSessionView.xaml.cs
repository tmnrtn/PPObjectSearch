using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class EnvironmentSessionView : UserControl
{
    private Window? _window;

    /// <summary>Set while the view puts focus in the box itself, so that does not open the dropdown.</summary>
    private bool _focusingSearchBox;

    public EnvironmentSessionView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            FocusMostUsefulBox();
            HookWindow();
        };
        Unloaded += (_, _) => UnhookWindow();
        DataContextChanged += (_, e) =>
        {
            // The view is reused across tabs; a dropdown left open belongs to the tab left behind.
            if (e.OldValue is EnvironmentSessionViewModel old) old.SearchDropdownOpen = false;
            FocusMostUsefulBox();
        };
    }

    private EnvironmentSessionViewModel? Session => DataContext as EnvironmentSessionViewModel;

    // ---------------------------------------------------------------- search dropdown

    /// <summary>
    /// The dropdown stays open on its own (StaysOpen), so it would float over other windows and
    /// stay behind when this one moves: both close it.
    /// </summary>
    private void HookWindow()
    {
        UnhookWindow();
        _window = Window.GetWindow(this);
        if (_window is null) return;

        _window.Deactivated += CloseDropdown;
        _window.LocationChanged += CloseDropdown;
    }

    private void UnhookWindow()
    {
        if (_window is null) return;

        _window.Deactivated -= CloseDropdown;
        _window.LocationChanged -= CloseDropdown;
        _window = null;
    }

    private void CloseDropdown(object? sender, EventArgs e)
    {
        if (Session is { } session) session.SearchDropdownOpen = false;
    }

    /// <summary>Nothing to search inside, save or reopen until the environment is loaded.</summary>
    private void OpenDropdown()
    {
        if (Session is { IsConnected: true } session) session.SearchDropdownOpen = true;
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_focusingSearchBox) OpenDropdown();
    }

    /// <summary>A click on the box opens the dropdown again, even when the box already has focus.</summary>
    private void SearchBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenDropdown();

    /// <summary>Focus leaving for anywhere but the dropdown's own "⋯" menu closes it.</summary>
    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject target && IsInDropdown(target)) return;
        CloseDropdown(sender, e);
    }

    private bool IsInDropdown(DependencyObject element)
    {
        for (var current = element; current is not null;)
        {
            if (current is ContextMenu || ReferenceEquals(current, SearchDropdown.Child)) return true;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>↓ and ↑ walk the dropdown, Enter runs its row, Esc closes it and then clears the box.</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Session is not { } session) return;

        switch (e.Key)
        {
            case Key.Down when session.IsConnected:
                session.MoveSuggestion(+1);
                e.Handled = true;
                break;
            case Key.Up when session.SearchDropdownOpen:
                session.MoveSuggestion(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = session.RunSuggestion();
                break;
            case Key.Escape:
                session.Escape();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The DataGrid does not select on right-click, so without this the context menu would act on
    /// whichever row was left-clicked last rather than the one under the cursor.
    /// </summary>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;

        // Null for the header row or the empty space below the rows - leave the selection alone.
        if (ItemsControl.ContainerFromElement(grid, (DependencyObject)e.OriginalSource) is not DataGridRow row) return;

        // A row already in the selection keeps it, so the menu can act on all of them; any other
        // row becomes the only one selected, so the menu acts on the row under the cursor.
        if (!row.IsSelected) grid.SelectedItem = row.Item;
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not EnvironmentSessionViewModel session || sender is not DataGrid grid) return;

        var selected = grid.SelectedItems.OfType<PPObjectSearch.Models.SolutionComponentItem>().ToList();

        // In grid order, so a copied list reads as the grid does. A single row needs no ordering,
        // which spares walking a large solution's rows on every click.
        if (selected.Count <= 1)
        {
            session.SetSelection(selected);
            return;
        }

        var set = selected.ToHashSet();
        session.SetSelection(grid.Items.OfType<PPObjectSearch.Models.SolutionComponentItem>().Where(set.Contains));
    }

    /// <summary>
    /// The solution picker's list closed without a pick: put the loaded solution's name back.
    /// </summary>
    private void SolutionPicker_SearchEnded(object? sender, EventArgs e)
    {
        if (DataContext is EnvironmentSessionViewModel session) session.EndSolutionSearch();
    }

    /// <summary>
    /// Focus moving between the picker's own text box and its list is still the same search; only
    /// leaving the picker altogether ends it.
    /// </summary>
    private void SolutionPicker_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ComboBox { IsKeyboardFocusWithin: false, IsDropDownOpen: false }) SolutionPicker_SearchEnded(sender, e);
    }

    /// <summary>
    /// Double-clicking a row opens its details. Only a row: a double-click on a column header (to
    /// resize it) or on the empty space below the rows is left alone.
    /// </summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement((DataGrid)sender, (DependencyObject)e.OriginalSource) is not DataGridRow row) return;
        if (DataContext is not EnvironmentSessionViewModel session) return;

        if (session.ShowDetailsCommand.CanExecute(row.Item))
        {
            session.ShowDetailsCommand.Execute(row.Item);
            e.Handled = true;
        }
    }

    /// <summary>Profiles may have come and gone since the menu last opened.</summary>
    private void AccountMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (DataContext is EnvironmentSessionViewModel session) session.RaiseBrowserProfile();
    }

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

            _focusingSearchBox = true;
            try
            {
                target.Focus();
                Keyboard.Focus(target);
            }
            finally
            {
                _focusingSearchBox = false;
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
