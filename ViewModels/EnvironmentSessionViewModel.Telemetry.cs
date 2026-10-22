using System.Windows;
using PPObjectSearch.Auth;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>The environment's Application Insights telemetry: whether Azure can be reached, and where.</summary>
public sealed partial class EnvironmentSessionViewModel
{
    // ---------------------------------------------------------------- the chosen resource

    /// <summary>The Dataverse organization id, read when the tab connects - one of what telemetry rows are matched on.</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>The App Insights resource this environment's Dataverse export goes to, if one has been chosen.</summary>
    public TelemetryBinding? AppInsightsBinding => _settings.GetTelemetryBinding(EnvironmentUrl, TelemetryKind.DataverseExport);

    public bool HasAppInsights => AppInsightsBinding is not null;

    /// <summary>For the sidebar tooltip; empty when no resource is chosen.</summary>
    public string AppInsightsDescription => AppInsightsBinding is { } binding ? $"App Insights: {binding.Label}" : string.Empty;

    /// <summary>Which rows of the resource are this environment's.</summary>
    public TelemetryScope TelemetryScope => new(
        OrganizationId,
        EnvironmentId,
        EnvironmentHost == "Not connected" ? null : EnvironmentHost,
        AppInsightsBinding?.OnlyThisEnvironment ?? true);

    /// <summary>Raised when the resource is chosen or removed, so open windows can offer it or stop.</summary>
    public event EventHandler? AppInsightsChanged;

    public void RaiseAppInsights()
    {
        OnPropertyChanged(nameof(AppInsightsBinding));
        OnPropertyChanged(nameof(HasAppInsights));
        OnPropertyChanged(nameof(AppInsightsDescription));
        OnPropertyChanged(nameof(DetailShortcuts));
        AppInsightsChanged?.Invoke(this, EventArgs.Empty);
    }

    private RelayCommand? _chooseAppInsightsCommand;
    public RelayCommand ChooseAppInsightsCommand => _chooseAppInsightsCommand ??= new RelayCommand(_ => ChooseAppInsights());

    private void ChooseAppInsights()
    {
        if (_client is not { } client || !IsConnected)
        {
            Status = "Connect first - the resources are listed as this tab's account.";
            return;
        }

        using var azure = client.CreateAzureMonitorClient();
        var viewModel = new AppInsightsPickerViewModel(azure, TelemetryScope, AppInsightsBinding);
        var window = new Views.AppInsightsPickerWindow { DataContext = viewModel, Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        SetAppInsights(window.Removed ? null : viewModel.Result());
    }

    /// <summary>Saves - or, with null, removes - the environment's resource.</summary>
    internal void SetAppInsights(TelemetryBinding? binding)
    {
        _settings.SetTelemetryBinding(EnvironmentUrl, TelemetryKind.DataverseExport, binding);
        _settings.Save();
        RaiseAppInsights();

        Status = binding is null
            ? $"App Insights is no longer read for {EnvironmentHost}."
            : $"{EnvironmentHost} reads App Insights from {binding.Label}.";
    }

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

            var results = await AzureAccessProbe.RunAsync(azure, AppInsightsBinding?.AppId, progress);
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
