using System.Windows;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The gate in front of every one-click change: the write guard first, then a confirmation that
/// names the change, the environment and its type. Nothing is written unless both say yes.
/// </summary>
public static class WriteConfirmation
{
    /// <summary>Asks; replaced in tests. Returns true for yes.</summary>
    internal static Func<string, string, bool> Prompt { get; set; } = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>Shown when the guard refuses; replaced in tests.</summary>
    internal static Action<string, string> Refuse { get; set; } = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>The text of the confirmation, so it can be checked.</summary>
    public static string Message(string action, string environmentName, string environmentHost, WritePermission permission) =>
        $"{action}\n\n" +
        $"Environment:  {environmentName}  ({permission.Type.SkuLabel})\n" +
        $"{environmentHost}" +
        (permission.IsAllowlisted ? "\nWrites allowed - this environment is allowlisted in settings.json." : string.Empty) +
        "\n\nThe change is recorded in the run log.";

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

        return Prompt(Message(action, session.Title, session.EnvironmentHost, permission), title);
    }
}
