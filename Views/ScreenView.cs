using System.Windows;
using System.Windows.Controls;
using PPObjectSearch.Services;

namespace PPObjectSearch.Views;

/// <summary>
/// A secondary screen's content - an object's details, Failures, Compare and the like. The same
/// view shows as a tab in the main window or in a <see cref="ScreenWindow"/>, and moves between
/// the two, so what a window used to do as it opened and closed happens here instead: once when
/// the screen is first shown, and once when it closes, from its tab or its window.
/// </summary>
public class ScreenView : UserControl
{
    public static readonly DependencyProperty ScreenTitleProperty = DependencyProperty.Register(
        nameof(ScreenTitle), typeof(string), typeof(ScreenView),
        new PropertyMetadata(string.Empty, (d, e) =>
        {
            if (((ScreenView)d).Screen is { } screen) screen.Title = e.NewValue as string ?? string.Empty;
        }));

    private bool _shown;

    public ScreenView()
    {
        Loaded += (_, _) =>
        {
            // Moving between a tab and a window loads the view again; its work starts only once.
            if (_shown) return;
            _shown = true;
            OnFirstShown();
        };
    }

    /// <summary>The tab's label and the window's title.</summary>
    public string ScreenTitle
    {
        get => (string)GetValue(ScreenTitleProperty);
        set => SetValue(ScreenTitleProperty, value);
    }

    /// <summary>The size a window showing this screen opens at.</summary>
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 720;
    public double WindowMinWidth { get; set; }
    public double WindowMinHeight { get; set; }

    /// <summary>The open screen showing this view, once it is opened.</summary>
    public OpenScreen? Screen { get; private set; }

    /// <summary>The first time the view is on screen, wherever that is.</summary>
    protected virtual void OnFirstShown()
    {
    }

    /// <summary>The screen has closed: stop whatever it still has running.</summary>
    protected virtual void OnScreenClosed()
    {
    }

    /// <summary>Opens this view as a screen: a tab or a window, as the setting says.</summary>
    /// <param name="isDetails">An object's details, which Close all details closes.</param>
    public OpenScreen Open(bool isDetails = false) => Open(ScreenHost.Current, isDetails);

    internal OpenScreen Open(ScreenHost host, bool isDetails)
    {
        Screen = new OpenScreen(this, ScreenTitle, isDetails, OnScreenClosed);
        return host.Show(Screen);
    }
}
