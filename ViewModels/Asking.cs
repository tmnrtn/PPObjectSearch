namespace PPObjectSearch.ViewModels;

/// <summary>
/// A question the app puts to the user in <c>AskWindow</c>: an optional environment line, a heading,
/// the body, and two buttons named for what they do ("Open 14 windows", "Keep running"). With no
/// <see cref="Confirm"/> it is a notice with one button.
/// </summary>
public sealed record AskRequest(string Heading, string Body)
{
    /// <summary>The window title; the heading when not given.</summary>
    public string? Title { get; init; }

    /// <summary>A tab or an <see cref="EnvironmentLineInfo"/>; no environment line when null.</summary>
    public object? Environment { get; init; }

    /// <summary>A short list under the body, one point a line.</summary>
    public IReadOnlyList<string>? Points { get; init; }

    /// <summary>A reassurance or caveat after the body, in Muted.</summary>
    public string? Note { get; init; }

    /// <summary>A tick, in RedSoft, that must be given before <see cref="Confirm"/> is enabled.</summary>
    public string? Acknowledgement { get; init; }

    /// <summary>The primary button's verb; null for a notice.</summary>
    public string? Confirm { get; init; }

    public string Cancel { get; init; } = "Cancel";

    /// <summary>Focus the primary button rather than Cancel - only where saying yes is harmless.</summary>
    public bool ConfirmIsDefault { get; init; }
}

public static class Asking
{
    /// <summary>Closing a window while its run is still writing.</summary>
    public static AskRequest StopRun { get; } = new("Stop the run?",
        "Changes are still being written. Stopping ends the run after the current one.")
    {
        Note = "Nothing already written is undone. The window stays open until the run has stopped.",
        Confirm = "Stop after this change",
        Cancel = "Keep running"
    };

    /// <summary>Shows the question over the active window; true when the primary button was chosen.</summary>
    public static bool Show(AskRequest request) => Views.AskWindow.Ask(WriteConfirmation.ActiveWindow(), request);
}
