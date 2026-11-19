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
    public RelayCommand ChooseAppInsightsCommand => _chooseAppInsightsCommand ??= new RelayCommand(_ => ChooseAppInsights(checkAccess: false));

    /// <param name="checkAccess">Runs the Azure access check as the picker opens, its results listed there.</param>
    private void ChooseAppInsights(bool checkAccess)
    {
        if (_client is not { } client || !IsConnected)
        {
            Status = "Connect first - the resources are listed as this tab's account.";
            return;
        }

        using var azure = client.CreateAzureMonitorClient();
        var viewModel = new AppInsightsPickerViewModel(azure, TelemetryScope, AppInsightsBinding,
            describeAccess: results => AzureAccessProbe.ToText(EnvironmentHost, SignInClientDescription, results),
            checkAccess: () => ProbeAzureAsync(azure));
        var window = new Views.AppInsightsPickerWindow { DataContext = viewModel, Owner = Application.Current?.MainWindow };
        if (checkAccess) window.Loaded += (_, _) => _ = viewModel.CheckAccessAsync();
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
    /// <summary>"Check Azure access…": the App Insights picker, with the check run and its results listed there.</summary>
    public AsyncRelayCommand CheckAzureAccessCommand => _checkAzureAccessCommand ??=
        new AsyncRelayCommand(_ =>
        {
            ChooseAppInsights(checkAccess: true);
            return Task.CompletedTask;
        });

    /// <summary>The check itself, logged and summed up in the status bar; null if it could not run.</summary>
    internal async Task<IReadOnlyList<AzureProbeResult>?> ProbeAzureAsync(AzureMonitorClient azure)
    {
        try
        {
            var results = await AzureAccessProbe.RunAsync(azure, AppInsightsBinding?.AppId, new Progress<string>(message => Status = message));
            Log.Info(AzureAccessProbe.ToText(EnvironmentHost, SignInClientDescription, results));

            var failed = results.Count(r => !r.Ok);
            Status = failed == 0 ? "Azure access check: everything worked." : $"Azure access check: {failed} of {results.Count} checks failed.";
            return results;
        }
        catch (Exception ex)
        {
            Log.Warn("The Azure access check failed", ex);
            Status = "The Azure access check could not run - " + ex.Message;
            return null;
        }
    }

    /// <summary>The sign-in client in use, as the check reports it.</summary>
    internal string SignInClientDescription => string.IsNullOrWhiteSpace(_settings.ClientId)
        ? $"built-in ({AuthenticationService.DefaultClientId})"
        : $"ClientId setting ({_settings.ClientId.Trim()})";

}
