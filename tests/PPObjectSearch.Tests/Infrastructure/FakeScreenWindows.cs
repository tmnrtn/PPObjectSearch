using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>Screen windows that are only recorded: which screens are in a window, and what was asked of them.</summary>
public sealed class FakeScreenWindows : IScreenWindows
{
    /// <summary>The screens with a window open now.</summary>
    public List<OpenScreen> Windows { get; } = new();

    /// <summary>Every call, in order, as "Open title", "Close title" and so on.</summary>
    public List<string> Calls { get; } = new();

    public int MainActivations { get; private set; }

    public void Open(OpenScreen screen)
    {
        Windows.Add(screen);
        Calls.Add("Open " + screen.Title);
    }

    public void Close(OpenScreen screen)
    {
        Windows.Remove(screen);
        Calls.Add("Close " + screen.Title);
    }

    public void Release(OpenScreen screen)
    {
        Windows.Remove(screen);
        Calls.Add("Release " + screen.Title);
    }

    public void Activate(OpenScreen screen) => Calls.Add("Activate " + screen.Title);

    public void ActivateMain() => MainActivations++;
}
