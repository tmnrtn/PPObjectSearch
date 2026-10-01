using System.Windows;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>Where each new object details window opens, so several are visible at once.</summary>
public class DetailsWindowsCascadeTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private static readonly Size WindowSize = new(1180, 760);

    [Fact]
    public void The_next_window_steps_down_and_right_from_the_last()
    {
        var (left, top) = DetailsWindows.Cascade(new Rect(300, 100, 1180, 760), WindowSize, WorkArea);

        Assert.Equal(300 + DetailsWindows.CascadeStep, left);
        Assert.Equal(100 + DetailsWindows.CascadeStep, top);
    }

    [Fact]
    public void A_run_of_windows_keeps_stepping()
    {
        var position = new Rect(100, 50, 1180, 760);

        for (var i = 0; i < 5; i++)
        {
            var (left, top) = DetailsWindows.Cascade(position, WindowSize, WorkArea);
            position = new Rect(left, top, WindowSize.Width, WindowSize.Height);
        }

        Assert.Equal(100 + 5 * DetailsWindows.CascadeStep, position.Left);
        Assert.Equal(50 + 5 * DetailsWindows.CascadeStep, position.Top);
    }

    [Fact]
    public void A_window_that_would_run_off_the_right_edge_starts_again_at_the_top_left()
    {
        var (left, top) = DetailsWindows.Cascade(new Rect(720, 100, 1180, 760), WindowSize, WorkArea);

        Assert.Equal(DetailsWindows.CascadeStep, left);
        Assert.Equal(DetailsWindows.CascadeStep, top);
    }

    [Fact]
    public void A_window_that_would_run_off_the_bottom_starts_again_at_the_top_left()
    {
        var (left, top) = DetailsWindows.Cascade(new Rect(100, 270, 1180, 760), WindowSize, WorkArea);

        Assert.Equal(DetailsWindows.CascadeStep, left);
        Assert.Equal(DetailsWindows.CascadeStep, top);
    }

    [Fact]
    public void The_work_area_offset_is_respected_for_a_second_monitor_or_a_top_taskbar()
    {
        var offsetArea = new Rect(1920, 40, 1920, 1000);

        var (left, top) = DetailsWindows.Cascade(new Rect(3000, 300, 1180, 760), WindowSize, offsetArea);

        Assert.Equal(1920 + DetailsWindows.CascadeStep, left);
        Assert.Equal(40 + DetailsWindows.CascadeStep, top);
    }

    [Fact]
    public void An_unsized_window_is_measured_by_the_previous_one()
    {
        // Width and Height are NaN until a window sizes itself; the last window stands in.
        var (left, _) = DetailsWindows.Cascade(new Rect(100, 50, 1800, 760), new Size(double.NaN, double.NaN), WorkArea);

        Assert.Equal(DetailsWindows.CascadeStep, left);
    }

    [Fact]
    public void A_window_never_opens_above_or_left_of_the_work_area()
    {
        var (left, top) = DetailsWindows.Cascade(new Rect(-500, -300, 1180, 760), WindowSize, WorkArea);

        Assert.True(left >= WorkArea.Left);
        Assert.True(top >= WorkArea.Top);
    }

    [Fact]
    public void With_nothing_open_the_count_is_zero() =>
        Assert.Equal(0, DetailsWindows.Count);
}
