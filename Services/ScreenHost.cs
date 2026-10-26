using System.Collections.ObjectModel;
using PPObjectSearch.Core;

namespace PPObjectSearch.Services;

/// <summary>Where a newly opened screen goes: a tab in the main window, or a window of its own.</summary>
public enum ScreenPlacement
{
    Tab,
    Window
}

/// <summary>
/// One open secondary screen - an object's details, Failures, Compare and the like. Its content is
/// the same view wherever it is shown; the host moves it between a tab and a window.
/// </summary>
public sealed class OpenScreen : ObservableObject
{
    private readonly Action? _onClosed;

    /// <param name="content">The view shown, in a tab or in a window.</param>
    /// <param name="onClosed">Stops the screen's work - what closing its window used to do.</param>
    public OpenScreen(object content, string title, bool isDetails = false, Action? onClosed = null)
    {
        Content = content;
        _title = title;
        IsDetails = isDetails;
        _onClosed = onClosed;
    }

    public object Content { get; }

    /// <summary>An object's details: placed in a cascade, and closed together by Close all details.</summary>
    public bool IsDetails { get; }

    private string _title;
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? string.Empty);
    }

    private bool _isInWindow;
    /// <summary>In a window of its own rather than a tab of the main window.</summary>
    public bool IsInWindow
    {
        get => _isInWindow;
        internal set => SetProperty(ref _isInWindow, value);
    }

    private bool _isSelected;
    /// <summary>The main window's tab showing now.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public bool IsClosed { get; private set; }

    /// <summary>Raised once, after the screen has closed and stopped its work.</summary>
    public event EventHandler? Closed;

    internal void MarkClosed()
    {
        IsClosed = true;
        IsSelected = false;
        _onClosed?.Invoke();
        Closed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The windows side of screen hosting - real windows in the app, a fake in tests.</summary>
public interface IScreenWindows
{
    /// <summary>Shows the screen in a new window of its own.</summary>
    void Open(OpenScreen screen);

    /// <summary>Closes the screen's window.</summary>
    void Close(OpenScreen screen);

    /// <summary>Closes the screen's window but keeps its content, which is moving to a tab.</summary>
    void Release(OpenScreen screen);

    /// <summary>Brings the screen's window to the front.</summary>
    void Activate(OpenScreen screen);

    /// <summary>Brings the main window to the front, where a tab has just been shown.</summary>
    void ActivateMain();
}

/// <summary>
/// The secondary screens open across the app: tabs in the main window, which can be popped out
/// into windows of their own and moved back, and windows opened as windows. Closing either way
/// stops the screen's work. The main window shows its environment when no tab is selected.
/// </summary>
public sealed class ScreenHost : ObservableObject
{
    private readonly List<OpenScreen> _open = new();
    private IScreenWindows _windows;

    public ScreenHost(IScreenWindows windows)
    {
        _windows = windows;
        CloseCommand = new RelayCommand(p => { if (p is OpenScreen s) Close(s); }, p => p is OpenScreen);
        PopOutCommand = new RelayCommand(p => { if (p is OpenScreen s) PopOut(s); }, p => p is OpenScreen);
        PopInCommand = new RelayCommand(p => { if (p is OpenScreen s) PopIn(s); }, p => p is OpenScreen);
        ShowHomeCommand = new RelayCommand(_ => ShowHome());
    }

    /// <summary>The app's host. Windows are attached once the application is running.</summary>
    public static ScreenHost Current { get; } = new(new NoWindows());

    /// <summary>Gives the host its windows - done at startup, as tests have none to give.</summary>
    public void Attach(IScreenWindows windows) => _windows = windows;

    /// <summary>Where screens open; the setting in settings.json.</summary>
    public ScreenPlacement Placement { get; set; } = ScreenPlacement.Tab;

    /// <summary>The screens shown as tabs in the main window, in the order opened.</summary>
    public ObservableCollection<OpenScreen> Tabs { get; } = new();

    /// <summary>Every open screen, in tabs and in windows.</summary>
    public IReadOnlyList<OpenScreen> Open => _open;

    public bool HasTabs => Tabs.Count > 0;

    public int DetailsCount => _open.Count(s => s.IsDetails);

    /// <summary>Raised whenever a screen opens or closes.</summary>
    public event EventHandler? Changed;

    public RelayCommand CloseCommand { get; }
    public RelayCommand PopOutCommand { get; }
    public RelayCommand PopInCommand { get; }
    public RelayCommand ShowHomeCommand { get; }

    private OpenScreen? _selectedTab;
    /// <summary>The tab showing in the main window; null while the environment itself shows.</summary>
    public OpenScreen? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is not null && !Tabs.Contains(value)) return;
            if (ReferenceEquals(_selectedTab, value)) return;

            if (_selectedTab is not null) _selectedTab.IsSelected = false;
            _selectedTab = value;
            if (value is not null) value.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHomeSelected));
        }
    }

    public bool IsHomeSelected => SelectedTab is null;

    /// <summary>Shows the selected environment rather than a screen.</summary>
    public void ShowHome() => SelectedTab = null;

    /// <summary>Opens a screen where the setting says, and brings it into view.</summary>
    public OpenScreen Show(OpenScreen screen) => Show(screen, Placement);

    public OpenScreen Show(OpenScreen screen, ScreenPlacement placement)
    {
        _open.Add(screen);

        if (placement == ScreenPlacement.Window)
        {
            screen.IsInWindow = true;
            _windows.Open(screen);
        }
        else
        {
            AddTab(screen);
        }

        RaiseChanged();
        return screen;
    }

    /// <summary>Brings an open screen into view: its tab, or its window.</summary>
    public void Activate(OpenScreen screen)
    {
        if (screen.IsClosed) return;

        if (screen.IsInWindow)
        {
            _windows.Activate(screen);
        }
        else
        {
            SelectedTab = screen;
            _windows.ActivateMain();
        }
    }

    /// <summary>Closes a screen, from its tab or its window, and stops its work.</summary>
    public void Close(OpenScreen screen)
    {
        if (screen.IsClosed || !_open.Remove(screen)) return;

        if (screen.IsInWindow) _windows.Close(screen);
        else RemoveTab(screen);

        screen.MarkClosed();
        RaiseChanged();
    }

    /// <summary>The user closed the screen's window: the screen goes with it.</summary>
    public void WindowClosed(OpenScreen screen)
    {
        if (screen.IsClosed || !_open.Remove(screen)) return;

        screen.MarkClosed();
        RaiseChanged();
    }

    /// <summary>Moves a tab into a window of its own.</summary>
    public void PopOut(OpenScreen screen)
    {
        if (screen.IsClosed || screen.IsInWindow) return;

        RemoveTab(screen);
        screen.IsInWindow = true;
        _windows.Open(screen);
        RaiseChanged();
    }

    /// <summary>Moves a screen's window back into the main window as a tab.</summary>
    public void PopIn(OpenScreen screen)
    {
        if (screen.IsClosed || !screen.IsInWindow) return;

        _windows.Release(screen);
        screen.IsInWindow = false;
        AddTab(screen);
        RaiseChanged();
    }

    /// <summary>Closes every screen matching <paramref name="which"/>, or every one.</summary>
    public void CloseAll(Func<OpenScreen, bool>? which = null)
    {
        foreach (var screen in _open.Where(which ?? (_ => true)).ToList()) Close(screen);
    }

    private void AddTab(OpenScreen screen)
    {
        Tabs.Add(screen);
        OnPropertyChanged(nameof(HasTabs));
        SelectedTab = screen;
        _windows.ActivateMain();
    }

    /// <summary>A closed or popped-out tab hands the selection to its neighbour, as a browser does.</summary>
    private void RemoveTab(OpenScreen screen)
    {
        var index = Tabs.IndexOf(screen);
        if (index < 0) return;

        var wasSelected = ReferenceEquals(SelectedTab, screen);
        Tabs.RemoveAt(index);
        screen.IsSelected = false;

        if (wasSelected) SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];

        OnPropertyChanged(nameof(HasTabs));
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Until the app attaches its windows, a screen asked for a window has nowhere else to go.</summary>
    private sealed class NoWindows : IScreenWindows
    {
        public void Open(OpenScreen screen) { }
        public void Close(OpenScreen screen) { }
        public void Release(OpenScreen screen) { }
        public void Activate(OpenScreen screen) { }
        public void ActivateMain() { }
    }
}
