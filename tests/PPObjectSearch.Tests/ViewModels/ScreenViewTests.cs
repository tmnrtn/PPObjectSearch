using System.Windows;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.Views;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// A screen's view moves between a tab and a window, so its work starts once however often it is
/// loaded, and stops once when it closes - from its tab or its window.
/// </summary>
public sealed class ScreenViewTests
{
    private sealed class RecordingView : ScreenView
    {
        public int Shown { get; private set; }
        public int Stopped { get; private set; }

        protected override void OnFirstShown() => Shown++;
        protected override void OnScreenClosed() => Stopped++;

        public void Load() => RaiseEvent(new RoutedEventArgs(LoadedEvent));
    }

    [Fact]
    public Task Its_work_starts_on_the_first_load_only() => EnvironmentSessionThread.Run(() =>
    {
        var view = new RecordingView();

        view.Load();
        view.Load();

        Assert.Equal(1, view.Shown);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(ScreenPlacement.Tab)]
    [InlineData(ScreenPlacement.Window)]
    public Task Closing_stops_its_work_once_wherever_it_is(ScreenPlacement placement) => EnvironmentSessionThread.Run(() =>
    {
        var host = new ScreenHost(new FakeScreenWindows()) { Placement = placement };
        var view = new RecordingView();
        var screen = view.Open(host, isDetails: false);

        host.Close(screen);
        host.Close(screen);

        Assert.Equal(1, view.Stopped);
        Assert.Same(view, screen.Content);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Popping_out_and_back_in_does_not_stop_its_work() => EnvironmentSessionThread.Run(() =>
    {
        var host = new ScreenHost(new FakeScreenWindows());
        var view = new RecordingView();
        var screen = view.Open(host, isDetails: false);

        host.PopOut(screen);
        host.PopIn(screen);

        Assert.Equal(0, view.Stopped);
        Assert.Same(screen, host.SelectedTab);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_title_follows_the_view() => EnvironmentSessionThread.Run(() =>
    {
        var host = new ScreenHost(new FakeScreenWindows());
        var view = new RecordingView { ScreenTitle = "Loading…" };
        var screen = view.Open(host, isDetails: true);

        view.ScreenTitle = "Failures — Contoso";

        Assert.Equal("Failures — Contoso", screen.Title);
        Assert.True(screen.IsDetails);
        Assert.Same(screen, view.Screen);
        return Task.CompletedTask;
    });
}
