using PPObjectSearch.Auth;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>The environment's Application Insights telemetry: whether Azure can be reached, and where.</summary>
public sealed partial class EnvironmentSessionViewModel
{
    // ---------------------------------------------------------------- Azure access check

    private AsyncRelayCommand? _checkAzureAccessCommand;
    public AsyncRelayCommand CheckAzureAccessCommand => _checkAzureAccessCommand ??=
        new AsyncRelayCommand(_ => CheckAzureAccessAsync());

    /// <summary>The sign-in client in use, as the check reports it.</summary>
    internal string SignInClientDescription => string.IsNullOrWhiteSpace(_settings.ClientId)
        ? $"built-in ({AuthenticationService.DefaultClientId})"
        : $"ClientId setting ({_settings.ClientId.Trim()})";

    /// <summary>
    /// Tries each Azure API the App Insights features use, as this tab's account, and says what
    /// worked - and, for what did not, whether it is the sign-in client or the account's Azure role.
    /// </summary>
    internal async Task<IReadOnlyList<AzureProbeResult>?> CheckAzureAccessAsync()
    {
        if (_client is not { } client || !IsConnected) return null;

        try
        {
            using var azure = client.CreateAzureMonitorClient();
            var progress = new Progress<string>(message => Status = message);

            var results = await AzureAccessProbe.RunAsync(azure, appId: null, progress);
            var text = AzureAccessProbe.ToText(EnvironmentHost, SignInClientDescription, results);
            Log.Info(text);

            var failed = results.Count(r => !r.Ok);
            Status = failed == 0 ? "Azure access check: everything worked." : $"Azure access check: {failed} of {results.Count} checks failed.";

            if (Ask(new AskRequest(failed == 0 ? "Azure can be reached" : "Some Azure checks failed",
                    $"Signed in as {AccountName ?? "this tab's account"}, with the {SignInClientDescription} sign-in client.")
                {
                    Title = "Azure access",
                    Environment = this,
                    Points = results.Select(r => r.Line).ToList(),
                    Note = "Nothing was changed. The results are in today's log too.",
                    Confirm = "Copy results",
                    Cancel = "Close"
                }))
            {
                Status = ClipboardText.TryCopy(text, out var problem)
                    ? "Copied the Azure access check."
                    : $"Could not copy the results - the clipboard is held by another application ({problem}).";
            }

            return results;
        }
        catch (Exception ex)
        {
            Log.Warn("The Azure access check failed", ex);
            Status = "The Azure access check could not run - " + ex.Message;
            return null;
        }
    }
}
