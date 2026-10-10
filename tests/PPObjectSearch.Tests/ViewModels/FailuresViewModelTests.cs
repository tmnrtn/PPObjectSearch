using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The failures window's period: a fixed span back from now, or whole days chosen.</summary>
public class FailuresViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 14, 30, 0, TimeSpan.FromHours(2));

    /// <summary>A date as the date picker gives it: no kind.</summary>
    private static DateTime Picked(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);

    [Theory]
    [InlineData("Last hour", 1.0 / 24)]
    [InlineData("Last 24 hours", 1)]
    [InlineData("Last 7 days", 7)]
    [InlineData("Last 30 days", 30)]
    [InlineData("Something else", 7)]
    public void A_fixed_period_runs_back_from_now(string period, double days)
    {
        var (from, to) = FailuresViewModel.Range(period, null, null, Now);

        Assert.Equal(Now, to);
        Assert.Equal(TimeSpan.FromDays(days).TotalMinutes, (to - from).TotalMinutes, 3);
    }

    [Fact]
    public void A_custom_period_runs_from_the_start_of_its_first_day_to_the_end_of_its_last()
    {
        var (from, to) = FailuresViewModel.Range("Custom", Picked(2026, 9, 1, 15), Picked(2026, 9, 3), Now);

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, Now.Offset), from);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, Now.Offset), to);
    }

    [Fact]
    public void A_custom_period_ending_today_stops_at_now()
    {
        var (_, to) = FailuresViewModel.Range("Custom", Picked(2026, 10, 1), Picked(2026, 10, 4), Now);

        Assert.Equal(Now, to);
    }

    [Fact]
    public void A_custom_period_without_dates_falls_back_to_the_last_seven_days()
    {
        var (from, to) = FailuresViewModel.Range("Custom", null, null, Now);

        Assert.Equal(Now.AddDays(-7), from);
        Assert.Equal(Now, to);
    }

    [Fact]
    public void Dates_chosen_the_wrong_way_round_are_swapped()
    {
        var (from, to) = FailuresViewModel.Range("Custom", Picked(2026, 9, 10), Picked(2026, 9, 5), Now);

        Assert.Equal(new DateTimeOffset(2026, 9, 5, 0, 0, 0, Now.Offset), from);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 0, 0, 0, Now.Offset), to);
    }
}
