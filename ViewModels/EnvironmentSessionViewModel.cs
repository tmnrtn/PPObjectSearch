using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PPObjectSearch.Auth;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

public sealed class TypeFilterOption
{
    public required string Name { get; init; }
    public required int Count { get; init; }
    public bool IsAll { get; init; }

    public string Label => IsAll ? $"All types ({Count})" : $"{Name} ({Count})";

    public override string ToString() => Label;
}

/// <summary>
/// One environment tab: its own connection, tenant, signed-in account, solution selection,
/// result set and filters. Tabs are independent, so two tabs can point at environments in
/// different tenants under different identities at the same time.
/// </summary>
public sealed class EnvironmentSessionViewModel : ObservableObject, IDisposable
{
    private const string AllTypesKey = " all";

    private readonly AppSettings _settings;
    private readonly AuthenticationService _auth;
    private readonly DispatcherTimer _searchDebounce;
    private readonly List<SolutionComponentItem> _allItems = new();

    private EnvironmentAuthContext _authContext;
    private DataverseClient? _client;
    private MakerPortalLinkBuilder? _linkBuilder;
    private CancellationTokenSource? _loadCts;
    private bool _suppressSolutionReload;
    private string[] _searchTerms = Array.Empty<string>();
    private string? _preferredSolutionUniqueName;

    public EnvironmentSessionViewModel(AuthenticationService auth, AppSettings settings, TabState? state = null)
    {
        _auth = auth;
        _settings = settings;
        _authContext = new EnvironmentAuthContext(auth, state?.TenantId, state?.AccountId);

        _environmentUrl = state?.EnvironmentUrl ?? string.Empty;
        _title = DeriveTitle(_environmentUrl);
        _preferredSolutionUniqueName = state?.SolutionUniqueName ?? settings.DefaultSolutionUniqueName;

        Items = new ObservableCollection<SolutionComponentItem>();
        ItemsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
        };

        ConnectCommand = new AsyncRelayCommand(_ => ConnectAsync());
        SwitchAccountCommand = new AsyncRelayCommand(_ => ConnectAsync(forceAccountPicker: true));
        RefreshCommand = new AsyncRelayCommand(_ => LoadSolutionComponentsAsync(), _ => IsConnected);
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
        OpenLinkCommand = new RelayCommand(OpenLink, p => (p as SolutionComponentItem)?.MakerUrl is not null);
        CopyLinkCommand = new RelayCommand(p => CopyToClipboard((p as SolutionComponentItem)?.MakerUrl));
        CopyIdCommand = new RelayCommand(p => CopyToClipboard((p as SolutionComponentItem)?.ObjectId.ToString()));
        CopyNameCommand = new RelayCommand(p => CopyToClipboard((p as SolutionComponentItem)?.Name));
    }

    /// <summary>Raised when the tab's persistable state changes, so the shell can save it.</summary>
    public event EventHandler? StateChanged;

    // ---------------------------------------------------------------- commands

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand SwitchAccountCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    public RelayCommand CopyIdCommand { get; }
    public RelayCommand CopyNameCommand { get; }

    // ---------------------------------------------------------------- state

    public ObservableCollection<SolutionComponentItem> Items { get; }
    public ListCollectionView ItemsView { get; }
    public ObservableCollection<SolutionInfo> Solutions { get; } = new();
    public ObservableCollection<TypeFilterOption> TypeFilters { get; } = new();

    private string _title;
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    private string _environmentUrl;
    public string EnvironmentUrl
    {
        get => _environmentUrl;
        set
        {
            if (!SetProperty(ref _environmentUrl, value)) return;

            if (!IsConnected)
            {
                Title = DeriveTitle(value);
                // A different environment may live in a different tenant - re-discover on connect.
                _authContext = new EnvironmentAuthContext(_auth);
                OnPropertyChanged(nameof(AccountName));
            }
        }
    }

    public string? AccountName => _authContext.AccountName;

    public string? TenantId => _authContext.TenantId;

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsSolutionPickerEnabled));
            }
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsSolutionPickerEnabled));
        }
    }

    public bool IsSolutionPickerEnabled => IsConnected && !IsBusy;

    private string _status = "Enter an environment URL and connect.";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private string _resultSummary = string.Empty;
    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
        }
    }

    private TypeFilterOption? _selectedTypeFilter;
    public TypeFilterOption? SelectedTypeFilter
    {
        get => _selectedTypeFilter;
        set
        {
            if (SetProperty(ref _selectedTypeFilter, value)) ApplyFilter();
        }
    }

    private SolutionInfo? _selectedSolution;
    public SolutionInfo? SelectedSolution
    {
        get => _selectedSolution;
        set
        {
            if (!SetProperty(ref _selectedSolution, value) || _suppressSolutionReload || value is null) return;

            _preferredSolutionUniqueName = value.UniqueName;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _ = LoadSolutionComponentsAsync();
        }
    }

    private bool _linksAvailable = true;
    public bool LinksAvailable
    {
        get => _linksAvailable;
        private set => SetProperty(ref _linksAvailable, value);
    }

    public TabState ToState() => new()
    {
        EnvironmentUrl = EnvironmentUrl,
        TenantId = _authContext.TenantId,
        AccountId = _authContext.AccountId,
        SolutionUniqueName = _preferredSolutionUniqueName
    };

    // ---------------------------------------------------------------- connect

    public async Task ConnectAsync(bool forceAccountPicker = false)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        try
        {
            IsBusy = true;

            _client?.Dispose();
            _client = new DataverseClient(_authContext, EnvironmentUrl);
            _environmentUrl = _client.EnvironmentUrl;
            OnPropertyChanged(nameof(EnvironmentUrl));

            Status = "Identifying tenant...";
            await _authContext.EnsureTenantAsync(_client.EnvironmentUrl, cts.Token);
            _authContext.ForceAccountPicker = forceAccountPicker;

            Status = "Signing in...";
            await _client.WhoAmIAsync(cts.Token);

            IsConnected = true;
            OnPropertyChanged(nameof(AccountName));
            OnPropertyChanged(nameof(TenantId));

            Title = await _client.GetOrganizationNameAsync(cts.Token) ?? DeriveTitle(_client.EnvironmentUrl);

            Status = "Resolving environment...";
            var environmentId = _settings.GetEnvironmentId(_client.EnvironmentUrl)
                                ?? await _client.GetEnvironmentIdAsync(cts.Token);

            _linkBuilder = new MakerPortalLinkBuilder(environmentId, _client.EnvironmentUrl, _settings.MakerLinkTemplates);
            LinksAvailable = _linkBuilder.CanBuildLinks;

            Status = "Loading solutions...";
            var solutions = await _client.GetSolutionsAsync(cts.Token);

            _suppressSolutionReload = true;
            Solutions.Clear();
            foreach (var solution in solutions.OrderBy(s => s.IsDefaultSolution ? 0 : 1)
                                              .ThenBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
            {
                Solutions.Add(solution);
            }

            _selectedSolution = Solutions.FirstOrDefault(s =>
                                   !string.IsNullOrWhiteSpace(_preferredSolutionUniqueName) &&
                                   string.Equals(s.UniqueName, _preferredSolutionUniqueName, StringComparison.OrdinalIgnoreCase))
                               ?? Solutions.FirstOrDefault(s => s.IsDefaultSolution)
                               ?? Solutions.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedSolution));
            _suppressSolutionReload = false;

            _preferredSolutionUniqueName = _selectedSolution?.UniqueName;
            StateChanged?.Invoke(this, EventArgs.Empty);

            if (SelectedSolution is null)
            {
                Status = "Connected, but no solutions were returned.";
                return;
            }

            await LoadSolutionComponentsAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = "Connection failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Drops all loaded state and the signed-in account, e.g. after an app-wide sign out.</summary>
    public void Reset(string status)
    {
        _loadCts?.Cancel();
        _client?.Dispose();
        _client = null;
        _linkBuilder = null;
        _authContext = new EnvironmentAuthContext(_auth);

        _allItems.Clear();
        Items.Clear();
        Solutions.Clear();
        TypeFilters.Clear();
        _selectedSolution = null;
        OnPropertyChanged(nameof(SelectedSolution));

        IsConnected = false;
        ResultSummary = string.Empty;
        Title = DeriveTitle(EnvironmentUrl);
        Status = status;
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(TenantId));
    }

    // ---------------------------------------------------------------- load

    private async Task LoadSolutionComponentsAsync()
    {
        if (_client is null || SelectedSolution is null) return;

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var solution = SelectedSolution;

        try
        {
            IsBusy = true;
            Items.Clear();
            _allItems.Clear();
            TypeFilters.Clear();
            ResultSummary = string.Empty;
            Status = $"Loading objects in '{solution.FriendlyName}'...";

            var progress = new Progress<int>(count =>
                Status = $"Loading objects in '{solution.FriendlyName}'... {count:N0}");

            var components = await _client.GetSolutionComponentsAsync(solution.SolutionId, progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            foreach (var item in components)
            {
                item.MakerUrl = _linkBuilder?.Build(item, solution.SolutionId);
                _allItems.Add(item);
            }

            _allItems.Sort((a, b) =>
            {
                var byType = string.Compare(a.ComponentTypeName, b.ComponentTypeName, StringComparison.CurrentCultureIgnoreCase);
                return byType != 0
                    ? byType
                    : string.Compare(a.PrimaryLabel, b.PrimaryLabel, StringComparison.CurrentCultureIgnoreCase);
            });

            RebuildTypeFilters();

            foreach (var item in _allItems) Items.Add(item);
            ApplyFilter();

            Status = LinksAvailable
                ? $"Loaded {_allItems.Count:N0} objects from '{solution.FriendlyName}'."
                : $"Loaded {_allItems.Count:N0} objects from '{solution.FriendlyName}'. Maker portal links are " +
                  "unavailable - the environment id could not be resolved (add it to \"EnvironmentIds\" in settings.json).";
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer load
        }
        catch (Exception ex)
        {
            Status = "Load failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _loadCts)) IsBusy = false;
        }
    }

    private void RebuildTypeFilters()
    {
        var previous = SelectedTypeFilter?.Name;

        TypeFilters.Clear();
        TypeFilters.Add(new TypeFilterOption { Name = AllTypesKey, Count = _allItems.Count, IsAll = true });

        foreach (var group in _allItems.GroupBy(i => i.ComponentTypeName)
                                       .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            TypeFilters.Add(new TypeFilterOption { Name = group.Key, Count = group.Count() });
        }

        _selectedTypeFilter = TypeFilters.FirstOrDefault(t => t.Name == previous) ?? TypeFilters[0];
        OnPropertyChanged(nameof(SelectedTypeFilter));
    }

    // ---------------------------------------------------------------- filter

    private void ApplyFilter()
    {
        _searchTerms = SearchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToArray();

        ItemsView.Refresh();

        var shown = ItemsView.Count;
        ResultSummary = shown == _allItems.Count
            ? $"{shown:N0} objects"
            : $"{shown:N0} of {_allItems.Count:N0} objects";
    }

    private bool FilterItem(object obj)
    {
        if (obj is not SolutionComponentItem item) return false;

        var typeFilter = SelectedTypeFilter;
        if (typeFilter is { IsAll: false } &&
            !string.Equals(item.ComponentTypeName, typeFilter.Name, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var term in _searchTerms)
        {
            if (!item.SearchIndex.Contains(term, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- helpers

    private static string DeriveTitle(string environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl)) return "New environment";

        try
        {
            var value = environmentUrl.Contains("://", StringComparison.Ordinal)
                ? environmentUrl
                : "https://" + environmentUrl;
            var host = new Uri(value).Host;
            var label = host.Split('.').FirstOrDefault();
            return string.IsNullOrWhiteSpace(label) ? host : label;
        }
        catch
        {
            return environmentUrl.Trim();
        }
    }

    private static void OpenLink(object? parameter)
    {
        if (parameter is not SolutionComponentItem { MakerUrl: { } url }) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the link:\n{url}\n\n{ex.Message}",
                "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by another process - not worth interrupting the user.
        }
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _searchDebounce.Stop();
        _client?.Dispose();
    }
}
