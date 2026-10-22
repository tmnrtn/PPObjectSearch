using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One Application Insights resource in the picker.</summary>
public sealed class AppInsightsChoice
{
    public required AppInsightsComponent Component { get; init; }

    /// <summary>The one already chosen for this environment.</summary>
    public bool IsCurrent { get; init; }

    public string Name => Component.Name;
    public string Where => string.Join(" · ", new[] { Component.SubscriptionName, Component.ResourceGroup, Component.Location }
        .Where(p => !string.IsNullOrWhiteSpace(p)));
    public bool HasAppId => !string.IsNullOrWhiteSpace(Component.AppId);
}

/// <summary>
/// Chooses the Application Insights resource an environment's Dataverse export goes to, from every
/// resource the tab's account can see in Azure. Picking one runs a quick check that the resource is
/// receiving this environment's telemetry, so a wrong pick shows before anything relies on it.
/// </summary>
public sealed class AppInsightsPickerViewModel : ObservableObject, IDisposable
{
    private readonly AzureMonitorClient _client;
    private readonly TelemetryScope _scope;
    private readonly TelemetryBinding? _current;
    private readonly Func<DateTimeOffset> _now;
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _loadCts;

    public AppInsightsPickerViewModel(AzureMonitorClient client, TelemetryScope scope, TelemetryBinding? current,
        Func<DateTimeOffset>? now = null)
    {
        _client = client;
        _scope = scope;
        _current = current;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _onlyThisEnvironment = current?.OnlyThisEnvironment ?? true;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = Filter;
    }

    public string Title => "App Insights resource";

    public ObservableCollection<AppInsightsChoice> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    public bool HasCurrent => _current is not null;
    public string CurrentLabel => _current is null ? "None chosen" : $"Now: {_current.Label}";

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ItemsView.Refresh();
        }
    }

    private AppInsightsChoice? _selected;
    public AppInsightsChoice? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(CanSave));
            _ = CheckAsync();
        }
    }

    private bool _onlyThisEnvironment;
    /// <summary>Keep only rows naming this environment - off for a resource that receives this one alone.</summary>
    public bool OnlyThisEnvironment
    {
        get => _onlyThisEnvironment;
        set
        {
            if (SetProperty(ref _onlyThisEnvironment, value)) _ = CheckAsync();
        }
    }

    public bool CanSave => Selected is { HasAppId: true } && !IsLoading;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(CanSave));
        }
    }

    private string _status = string.Empty;
    /// <summary>Progress while listing, then how many were found.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private string _notes = string.Empty;
    /// <summary>Subscriptions that could not be read - the warning under the list.</summary>
    public string Notes
    {
        get => _notes;
        private set => SetProperty(ref _notes, value);
    }

    private string _checkText = string.Empty;
    /// <summary>What the check of the selected resource found.</summary>
    public string CheckText
    {
        get => _checkText;
        private set => SetProperty(ref _checkText, value);
    }

    private bool _checkFoundTelemetry;
    public bool CheckFoundTelemetry
    {
        get => _checkFoundTelemetry;
        private set => SetProperty(ref _checkFoundTelemetry, value);
    }

    /// <summary>Lists every resource the account can see, and selects the current one.</summary>
    public async Task LoadAsync()
    {
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        Status = "Listing Application Insights resources...";
        try
        {
            var (components, notes) = await _client.ListAllComponentsAsync(new Progress<string>(m => Status = m), cts.Token);

            Items.Clear();
            foreach (var component in components)
            {
                Items.Add(new AppInsightsChoice
                {
                    Component = component,
                    IsCurrent = _current is not null && string.Equals(component.ResourceId, _current.ResourceId, StringComparison.OrdinalIgnoreCase)
                });
            }

            Notes = string.Join(Environment.NewLine, notes);
            Status = components.Count == 1 ? "1 resource found." : $"{components.Count:N0} resources found.";
            Selected = Items.FirstOrDefault(i => i.IsCurrent);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Log.Warn("Application Insights resources could not be listed", ex);
            Status = "Could not list resources - " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Whether the selected resource is receiving this environment's telemetry - the newest pick wins.</summary>
    internal async Task CheckAsync()
    {
        var superseded = _checkCts;
        var cts = _checkCts = new CancellationTokenSource();
        if (superseded is not null) await superseded.CancelAsync();

        CheckFoundTelemetry = false;
        if (Selected is not { } choice)
        {
            CheckText = string.Empty;
            return;
        }

        if (!choice.HasAppId)
        {
            CheckText = "This resource has no app id, so it cannot be queried.";
            return;
        }

        CheckText = $"Checking {choice.Name}...";
        try
        {
            var check = await TelemetryProfiles.CheckAsync(_client, DataverseExportProfile.Instance, choice.Component.AppId!,
                _scope with { OnlyThisEnvironment = OnlyThisEnvironment }, _now(), cts.Token);
            if (cts.IsCancellationRequested) return;

            CheckText = check.Describe(OnlyThisEnvironment);
            CheckFoundTelemetry = OnlyThisEnvironment ? check.Matching > 0 : check.Total > 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer pick took over.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) CheckText = "Could not check this resource - " + ex.Message;
        }
    }

    /// <summary>The binding to save for the selected resource, or null when none is selected.</summary>
    public TelemetryBinding? Result() => Selected is { HasAppId: true } choice
        ? new TelemetryBinding
        {
            Kind = nameof(TelemetryKind.DataverseExport),
            ResourceId = choice.Component.ResourceId,
            AppId = choice.Component.AppId!,
            Name = choice.Component.Name,
            SubscriptionName = choice.Component.SubscriptionName,
            OnlyThisEnvironment = OnlyThisEnvironment
        }
        : null;

    private bool Filter(object obj)
    {
        if (obj is not AppInsightsChoice choice) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        return choice.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               choice.Where.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _checkCts?.Cancel();
        _loadCts?.Dispose();
        _checkCts?.Dispose();
    }
}
