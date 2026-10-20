using System.Windows;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One line of a confirmation's tally: "3 created rows are deleted".</summary>
public sealed record WriteCount(int Count, string Label);

/// <summary>
/// What a run's undo is about to do, for the confirmation: what it reverses, a tally by kind of
/// write, the caveats, and the confirm button's words.
/// </summary>
public sealed record UndoConfirmation(string Action, IReadOnlyList<WriteCount> Counts, string Caveat, string ConfirmLabel);

/// <summary>
/// What the environment line at the top of a window shows, for a window that has no tab behind it -
/// the same names as <see cref="EnvironmentSessionViewModel"/>, so <c>EnvironmentLine</c> binds to either.
/// </summary>
public sealed record EnvironmentLineInfo(
    string Title,
    Dataverse.EnvironmentSku EnvironmentSku,
    string EnvironmentHost,
    bool IsWriteAllowlisted,
    string EnvironmentTypeDescription);

/// <summary>
/// The gate in front of every one-click change: the write guard first, then a confirmation that
/// names the change, the environment and its type. Nothing is written unless both say yes.
/// </summary>
public static class WriteConfirmation
{
    /// <summary>
    /// Asks: the environment line (its colour, name, type and Writes allowed chip), the title as
    /// the confirm button, and the change. Replaced in tests. Returns true for yes.
    /// </summary>
    internal static Func<EnvironmentSessionViewModel, string, string, bool> Prompt { get; set; } = (session, title, action) =>
        Views.WriteConfirmWindow.Ask(ActiveWindow(), session, title, action);

    /// <summary>Shown when the guard refuses; replaced in tests.</summary>
    internal static Action<string, string> Refuse { get; set; } = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>The tooltip of a button that writes: what it does, where, and that it asks first.</summary>
    public static string ToolTip(string what, string environmentName) =>
        $"{what} in {environmentName}. Asks first; recorded in the run log.";

    public static async Task<bool> AskAsync(EnvironmentSessionViewModel session, string title, string action)
    {
        WritePermission permission;
        try
        {
            permission = await session.EvaluateWritePermissionAsync();
        }
        catch (Exception ex)
        {
            Refuse($"Could not check whether {session.Title} may be written to - {ex.Message}", title);
            return false;
        }

        if (!permission.Allowed)
        {
            Refuse(permission.Reason, title);
            return false;
        }

        return Prompt(session, title, action);
    }

    /// <summary>The details window when the change was asked for there, else the main window.</summary>
    internal static Window? ActiveWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
}
