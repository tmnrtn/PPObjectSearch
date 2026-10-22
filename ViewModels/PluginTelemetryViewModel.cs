using System.Collections.ObjectModel;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// A plug-in's telemetry in the environment's App Insights resource, for the details window's App
/// Insights tab: how often it ran and failed over a period, how long it took, and its latest
/// failures with the exception each threw. Read when the tab is first shown, and on demand.
/// </summary>
public sealed class PluginTelemetryViewModel : ObservableObject, IDisposable
{
    /// <summary>The latest failures listed; the portal has the rest.</summary>
    internal const int MaxFailures = 50;

    private readonly Func<AzureMonitorClient> _newClient;
    private readonly TelemetryBinding _binding;
    private readonly TelemetryScope _scope;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _typeNames;
    private readonly Action<string?> _openUrl;
    private readonly AzureEndpoints _azure;
    private readonly string? _tenantId;
    private readonly Func<DateTimeOffset> _now;
    private IReadOnlyList<string>? _names;
    private CancellationTokenSource? _cts;

    public PluginTelemetryViewModel(
        Func<AzureMonitorClient> newClient,
        TelemetryBinding binding,
        TelemetryScope scope,
        Func<CancellationToken, Task<IReadOnlyList<string>>> typeNames,
        Action<string?> openUrl,
        AzureEndpoints azure,
        string? tenantId,
        Func<DateTimeOffset>? now = null)
    {
        _newClient = newClient;
        _binding = binding;
        _scope = scope with { OnlyThisEnvironment = binding.OnlyThisEnvironment };
        _typeNames = typeNames;
        _openUrl = openUrl;
        _azure = azure;
        _tenantId = tenantId;
        _now = now ?? (() => DateTimeOffset.UtcNow);

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        OpenInPortalCommand = new RelayCommand(_ => OpenInPortal());
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenInPortalCommand { get; }

    /// <summary>"From contoso-dataverse (Contoso Prod)".</summary>
    public string SourceLabel => $"From {_binding.Label}";

    public IReadOnlyList<string> Periods { get; } = ["Last 24 hours", "Last 7 days", "Last 30 days"];

    private string _period = "Last 7 days";
    public string Period
    {
        get => _period;
        set
        {
            if (SetProperty(ref _period, value) && HasLoaded) _ = LoadAsync();
        }
    }

    internal TimeSpan Span => Period switch
    {
        "Last 24 hours" => TimeSpan.FromDays(1),
        "Last 30 days" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(7)
    };

    public bool HasLoaded { get; private set; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = string.Empty;
    /// <summary>What was read, or why nothing could be.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private PluginTelemetrySummary _summary = PluginTelemetrySummary.None;
    public PluginTelemetrySummary Summary
    {
        get => _summary;
        private set
        {
            if (!SetProperty(ref _summary, value)) return;
            OnPropertyChanged(nameof(ExecutionsLabel));
            OnPropertyChanged(nameof(FailuresLabel));
            OnPropertyChanged(nameof(FailureRateLabel));
            OnPropertyChanged(nameof(AverageLabel));
            OnPropertyChanged(nameof(P95Label));
            OnPropertyChanged(nameof(LastLabel));
        }
    }

    public string ExecutionsLabel => Summary.Executions.ToString("N0");
    public string FailuresLabel => Summary.Failures.ToString("N0");
    public string FailureRateLabel => Summary.Executions > 0 ? $"{Math.Round(100.0 * Summary.Failures / Summary.Executions):0}%" : "—";
    public string AverageLabel => Duration(Summary.AverageMs);
    public string P95Label => Duration(Summary.P95Ms);
    public string LastLabel => Summary.Last is { } last ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    internal static string Duration(double? ms) => ms switch
    {
        null => "—",
        < 1000 => $"{ms:0} ms",
        _ => $"{ms / 1000:0.0} s"
    };

    public ObservableCollection<FailureEvent> Failures { get; } = new();

    private FailureEvent? _selectedFailure;
    public FailureEvent? SelectedFailure
    {
        get => _selectedFailure;
        set
        {
            if (!SetProperty(ref _selectedFailure, value)) return;
            OnPropertyChanged(nameof(SelectedDetails));
        }
    }

    /// <summary>The selected failure's exception in full, or its message where there is no exception.</summary>
    public string SelectedDetails => SelectedFailure is { } f ? f.Details ?? f.ErrorMessage ?? f.ErrorLine : string.Empty;

    public bool HasFailures => Failures.Count > 0;

    /// <summary>Reads the period - the newest read wins.</summary>
    public async Task LoadAsync()
    {
        var superseded = _cts;
        var cts = _cts = new CancellationTokenSource();
        if (superseded is not null) await superseded.CancelAsync();

        IsBusy = true;
        Status = "Reading App Insights...";
        try
        {
            _names ??= await _typeNames(cts.Token);
            if (_names.Count == 0)
            {
                Status = "This object runs no plug-in type, so App Insights has nothing for it.";
                return;
            }

            var to = _now();
            var from = to - Span;
            using var client = _newClient();

            var summary = await DataverseExportProfile.ReadPluginSummaryAsync(client, _binding.AppId, _scope, _names, from, to, cts.Token);
            var data = new FailureData();
            var query = new FailureQuery { WorkflowIds = new HashSet<Guid>(), PluginTypeNames = _names.ToHashSet(StringComparer.OrdinalIgnoreCase) };
            await DataverseExportProfile.Instance.ReadFailuresAsync(client, _binding.AppId, _scope, from, to, query, data, cts.Token);
            if (cts.IsCancellationRequested) return;

            Summary = summary;
            Failures.Clear();
            foreach (var failure in data.Events.OrderByDescending(e => e.When).Take(MaxFailures)) Failures.Add(failure);
            SelectedFailure = Failures.FirstOrDefault();
            OnPropertyChanged(nameof(HasFailures));

            Status = (summary.Executions, Failures.Count) switch
            {
                (0, 0) => $"No executions of this plug-in in App Insights in the {Period.ToLowerInvariant()}. " +
                          "Is the Dataverse export pointed at this resource?",
                (_, 0) => $"No failures in the {Period.ToLowerInvariant()}.",
                (_, var n) when n >= MaxFailures => $"The latest {n} failures; the portal has the rest.",
                (_, var n) => $"{n:N0} failure{(n == 1 ? string.Empty : "s")} in the {Period.ToLowerInvariant()}."
            };
            if (data.Notes.Count > 0) Status += " " + string.Join(" ", data.Notes);
            HasLoaded = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer read took over, or the window closed.
        }
        catch (Exception ex)
        {
            Log.Warn("A plug-in's App Insights telemetry could not be read", ex);
            Status = $"App Insights ({_binding.Name}) could not be read - {ex.Message}{(ex as AzureMonitorException)?.Advice}";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsBusy = false;
        }
    }

    /// <summary>The selected failure's operation in the portal's Logs - or, with none selected, the plug-in's executions.</summary>
    private void OpenInPortal()
    {
        var query = SelectedFailure?.OperationId is { Length: > 0 } operation
            ? AppInsightsLinks.OperationQuery(operation)
            : "dependencies\n| where type =~ 'Plugin'\n" +
              (_names is { Count: > 0 } names
                  ? "| where " + string.Join(" or ", names.Select(n => $"name startswith {Kql.Literal(n)}")) + "\n"
                  : string.Empty) +
              "| order by timestamp desc";

        _openUrl(AppInsightsLinks.Logs(_azure, _tenantId, _binding.ResourceId, query, Span));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
