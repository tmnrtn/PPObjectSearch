using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Services;

/// <summary>
/// Secondary screens as tabs in the main window or windows of their own: where each opens, moving
/// between the two, and closing - which stops the screen's work however it is closed.
/// </summary>
public sealed class ScreenHostTests
{
    private readonly FakeScreenWindows _windows = new();
    private readonly ScreenHost _host;
    private readonly List<string> _stopped = new();

    public ScreenHostTests() => _host = new ScreenHost(_windows);

    private OpenScreen Screen(string title, bool isDetails = false) =>
        new(new object(), title, isDetails, () => _stopped.Add(title));

    // ---------------------------------------------------------------- opening

    [Fact]
    public void Screens_open_as_tabs_by_default_and_the_new_tab_is_shown()
    {
        var failures = _host.Show(Screen("Failures"));
        var details = _host.Show(Screen("Details"));

        Assert.Equal(ScreenPlacement.Tab, _host.Placement);
        Assert.Equal([failures, details], _host.Tabs);
        Assert.Same(details, _host.SelectedTab);
        Assert.True(details.IsSelected);
        Assert.False(failures.IsSelected);
        Assert.False(_host.IsHomeSelected);
        Assert.True(_host.HasTabs);
        Assert.Empty(_windows.Windows);
        Assert.Equal(2, _windows.MainActivations);
    }

    [Fact]
    public void With_the_window_setting_a_screen_opens_in_a_window_and_no_tab()
    {
        _host.Placement = ScreenPlacement.Window;

        var failures = _host.Show(Screen("Failures"));

        Assert.Empty(_host.Tabs);
        Assert.False(_host.HasTabs);
        Assert.True(_host.IsHomeSelected);
        Assert.True(failures.IsInWindow);
        Assert.Equal([failures], _windows.Windows);
        Assert.Equal([failures], _host.Open);
    }

    [Fact]
    public void Changing_the_setting_leaves_open_screens_where_they_are()
    {
        var tab = _host.Show(Screen("Failures"));

        _host.Placement = ScreenPlacement.Window;
        var window = _host.Show(Screen("Compare"));

        Assert.Equal([tab], _host.Tabs);
        Assert.Equal([window], _windows.Windows);
    }

    // ---------------------------------------------------------------- moving

    [Fact]
    public void Popping_out_moves_a_tab_into_a_window()
    {
        var failures = _host.Show(Screen("Failures"));

        _host.PopOutCommand.Execute(failures);

        Assert.Empty(_host.Tabs);
        Assert.True(_host.IsHomeSelected);
        Assert.True(failures.IsInWindow);
        Assert.False(failures.IsSelected);
        Assert.Equal([failures], _windows.Windows);
        Assert.Empty(_stopped);
    }

    [Fact]
    public void Popping_back_in_releases_the_window_and_shows_the_tab()
    {
        _host.Placement = ScreenPlacement.Window;
        var failures = _host.Show(Screen("Failures"));

        _host.PopInCommand.Execute(failures);

        Assert.Equal(["Open Failures", "Release Failures"], _windows.Calls);
        Assert.Equal([failures], _host.Tabs);
        Assert.Same(failures, _host.SelectedTab);
        Assert.False(failures.IsInWindow);
        Assert.Empty(_stopped);
        Assert.Equal([failures], _host.Open);
    }

    [Fact]
    public void Popping_out_a_window_or_in_a_tab_does_nothing()
    {
        var tab = _host.Show(Screen("Tab"));
        _host.Placement = ScreenPlacement.Window;
        var window = _host.Show(Screen("Window"));

        _host.PopIn(tab);
        _host.PopOut(window);

        Assert.Equal([tab], _host.Tabs);
        Assert.Equal(["Open Window"], _windows.Calls);
    }

    [Fact]
    public void Activating_a_tab_selects_it_and_brings_the_main_window_forward()
    {
        var failures = _host.Show(Screen("Failures"));
        _host.Show(Screen("Details"));
        var before = _windows.MainActivations;

        _host.Activate(failures);

        Assert.Same(failures, _host.SelectedTab);
        Assert.Equal(before + 1, _windows.MainActivations);
    }

    [Fact]
    public void Activating_a_window_brings_that_window_forward()
    {
        _host.Placement = ScreenPlacement.Window;
        var failures = _host.Show(Screen("Failures"));

        _host.Activate(failures);

        Assert.Equal(["Open Failures", "Activate Failures"], _windows.Calls);
    }

    [Fact]
    public void Showing_home_leaves_the_tabs_open()
    {
        var failures = _host.Show(Screen("Failures"));

        _host.ShowHomeCommand.Execute(null);

        Assert.True(_host.IsHomeSelected);
        Assert.False(failures.IsSelected);
        Assert.Equal([failures], _host.Tabs);
    }

    [Fact]
    public void A_screen_that_is_not_a_tab_cannot_be_selected()
    {
        _host.Placement = ScreenPlacement.Window;
        var window = _host.Show(Screen("Window"));

        _host.SelectedTab = window;

        Assert.Null(_host.SelectedTab);
    }

    // ---------------------------------------------------------------- closing

    [Fact]
    public void Closing_a_tab_stops_its_work_and_shows_its_neighbour()
    {
        var first = _host.Show(Screen("First"));
        var second = _host.Show(Screen("Second"));
        var third = _host.Show(Screen("Third"));
        _host.SelectedTab = second;
        var closed = 0;
        second.Closed += (_, _) => closed++;

        _host.CloseCommand.Execute(second);

        Assert.Equal(["Second"], _stopped);
        Assert.Equal(1, closed);
        Assert.True(second.IsClosed);
        Assert.Equal([first, third], _host.Tabs);
        Assert.Same(third, _host.SelectedTab);
        Assert.Equal([first, third], _host.Open);
    }

    [Fact]
    public void Closing_the_last_tab_shows_the_environment_again()
    {
        var only = _host.Show(Screen("Only"));

        _host.Close(only);

        Assert.True(_host.IsHomeSelected);
        Assert.False(_host.HasTabs);
    }

    [Fact]
    public void Closing_a_tab_that_is_not_showing_keeps_the_one_that_is()
    {
        var first = _host.Show(Screen("First"));
        var second = _host.Show(Screen("Second"));

        _host.Close(first);

        Assert.Same(second, _host.SelectedTab);
    }

    [Fact]
    public void Closing_a_screen_in_a_window_closes_the_window()
    {
        _host.Placement = ScreenPlacement.Window;
        var failures = _host.Show(Screen("Failures"));

        _host.Close(failures);

        Assert.Equal(["Open Failures", "Close Failures"], _windows.Calls);
        Assert.Equal(["Failures"], _stopped);
        Assert.Empty(_host.Open);
    }

    [Fact]
    public void Closing_its_window_closes_the_screen_once()
    {
        _host.Placement = ScreenPlacement.Window;
        var failures = _host.Show(Screen("Failures"));

        _host.WindowClosed(failures);
        _host.WindowClosed(failures);
        _host.Close(failures);

        Assert.Equal(["Failures"], _stopped);
        Assert.Empty(_host.Open);
        Assert.Equal(["Open Failures"], _windows.Calls);
    }

    [Fact]
    public void Closing_everything_closes_tabs_and_windows()
    {
        _host.Show(Screen("Tab"));
        _host.Placement = ScreenPlacement.Window;
        _host.Show(Screen("Window"));

        _host.CloseAll();

        Assert.Equal(["Tab", "Window"], _stopped);
        Assert.Empty(_host.Open);
        Assert.Empty(_host.Tabs);
        Assert.Empty(_windows.Windows);
    }

    [Fact]
    public void Details_are_counted_and_closed_apart_from_other_screens()
    {
        _host.Show(Screen("Failures"));
        _host.Show(Screen("Flow", isDetails: true));
        _host.Placement = ScreenPlacement.Window;
        _host.Show(Screen("Table", isDetails: true));
        var changes = 0;
        _host.Changed += (_, _) => changes++;

        Assert.Equal(2, _host.DetailsCount);

        _host.CloseAll(s => s.IsDetails);

        Assert.Equal(["Flow", "Table"], _stopped);
        Assert.Equal(0, _host.DetailsCount);
        Assert.Equal(["Failures"], _host.Tabs.Select(t => t.Title));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_screen_title_can_change_while_it_is_open()
    {
        var details = _host.Show(Screen("Loading…"));
        var changed = new List<string?>();
        details.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        details.Title = "Order approval";

        Assert.Equal("Order approval", details.Title);
        Assert.Equal([nameof(OpenScreen.Title)], changed);
    }
}
