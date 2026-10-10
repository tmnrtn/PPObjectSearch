using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using System.ComponentModel;
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

    /// <summary>Wording for the catch-all entry, e.g. "All types" / "All sub types".</summary>
    public string AllLabel { get; init; } = "All";

    public string Label => IsAll ? $"{AllLabel} ({Count})" : $"{Name} ({Count})";

    /// <summary>What the closed filter dropdown shows: "All" rather than the list's full wording.</summary>
    public string ShortLabel => IsAll ? "All" : Label;

    public override string ToString() => Label;
}

/// <summary>
/// One environment tab: its own connection, tenant, signed-in account, solution selection,
/// result set and filters. Tabs are independent, so two tabs can point at environments in
/// different tenants under different identities at the same time.
/// </summary>
public sealed partial class EnvironmentSessionViewModel : ObservableObject, IDisposable
{
    private const string AllKey = "\0all";

    /// <summary>The title of the message boxes this tab shows.</summary>
    private const string MessageCaption = "PPObjectSearch";

    private readonly AppSettings _settings;
    private readonly AuthenticationService _auth;
    private readonly DispatcherTimer _searchDebounce;
    private readonly List<SolutionComponentItem> _allItems = new();

    private EnvironmentAuthContext _authContext;
    private DataverseClient? _client;
    private MakerPortalLinkBuilder? _linkBuilder;
    private CancellationTokenSource? _loadCts;
    private bool _suppressSolutionReload;
    private bool _suppressFilterRefresh;
    private string[] _searchTerms = Array.Empty<string>();
    private string? _preferredSolutionUniqueName;

    public EnvironmentSessionViewModel(AuthenticationService auth, AppSettings settings, TabState? state = null)
    {
        _auth = auth;
        _settings = settings;
        // Do not restore TenantId from state - a previous bug may have saved the wrong tenant ID for this URL.
        // Forcing a fresh discovery on startup ensures we always get the correct tenant.
        _authContext = NewAuthContext(state?.AccountId);

        _environmentUrl = state?.EnvironmentUrl ?? string.Empty;
        _title = DeriveTitle(_environmentUrl);
        _preferredSolutionUniqueName = state?.SolutionUniqueName ?? settings.DefaultSolutionUniqueName;
        _rememberedSku = state?.EnvironmentType;

        Items = new BulkObservableCollection<SolutionComponentItem>();
        ItemsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;

        TypeFiltersView = CollectionViewSource.GetDefaultView(TypeFilters);
        TypeFiltersView.Filter = FilterTypeOption;

        SolutionsView = CollectionViewSource.GetDefaultView(Solutions);
        SolutionsView.Filter = FilterSolution;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
        };

        // Arrowing down the list should not read every row it passes.
        _insightDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _insightDebounce.Tick += (_, _) =>
        {
            _insightDebounce.Stop();
            _ = LoadSelectionInsightAsync();
        };

        ConnectCommand = new AsyncRelayCommand(_ => ConnectAsync());
        SwitchAccountCommand = new AsyncRelayCommand(_ => ConnectAsync(forceAccountPicker: true));
        RefreshCommand = new AsyncRelayCommand(_ => LoadSolutionComponentsAsync(useCache: false), _ => IsConnected);
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
        ClearFiltersCommand = new RelayCommand(_ => ClearFilters());
        OpenLinkCommand = new RelayCommand(p => OpenUrl(Target(p)?.MakerUrl), p => Target(p)?.MakerUrl is not null);
        SetBrowserProfileCommand = new RelayCommand(p => SetBrowserProfile(p as BrowserProfileOption));
        CopyLinkCommand = new RelayCommand(p => CopyEach(p, i => i.MakerUrl, "maker portal link"));
        CopyIdCommand = new RelayCommand(p => CopyEach(p, i => i.ObjectId == Guid.Empty ? null : i.ObjectId.ToString(), "object id"));
        CopyNameCommand = new RelayCommand(p => CopyEach(p, i => i.Name, "name"));
        CopyAsTableCommand = new RelayCommand(p => CopyAsTable(Targets(p)), p => Targets(p).Count > 0);
        OpenSelectionDetailsCommand = new RelayCommand(_ => OpenSelectionDetails(), _ => IsConnected && Selection.Count > 1);
        CheckSelectionLayersCommand = new AsyncRelayCommand(
            _ => CheckUnmanagedLayersAsync(Selection), _ => IsConnected && !IsBusy && !IsCheckingLayers && Selection.Count > 0);
        ExportSelectionCommand = new RelayCommand(_ => ExportCsv(Selection), _ => Selection.Count > 0);
        ToggleFavouriteCommand = new RelayCommand(p => ToggleFavourite(Targets(p)), p => IsConnected && Targets(p).Count > 0);
        SaveSearchCommand = new RelayCommand(_ => SaveSearch(), _ => IsConnected);
        ApplySavedSearchCommand = new RelayCommand(p => ApplySavedSearch(p as SavedSearch), p => p is SavedSearch);
        DeleteSavedSearchCommand = new RelayCommand(p => DeleteSavedSearch(p as SavedSearch), p => p is SavedSearch);
        OpenRecentCommand = new RelayCommand(p => OpenRecent(p as RecentObject), p => p is RecentObject && IsConnected);
        CopyTenantIdCommand = new RelayCommand(_ => CopyToClipboard(TenantId, "tenant id"), _ => TenantId is not null);
        DisconnectCommand = new RelayCommand(_ => Reset("Disconnected."), _ => IsConnected);
        ToggleDetailPaneCommand = new RelayCommand(_ => IsDetailPaneOpen = !IsDetailPaneOpen);
        SolutionHistoryCommand = new RelayCommand(_ => ShowSolutionHistory(), _ => IsConnected);
        EntraTeamSyncCommand = new RelayCommand(_ => ShowEntraTeamSync(), _ => IsConnected);
        QueueSyncCommand = new RelayCommand(_ => ShowQueueSync(), _ => IsConnected);
        EnvironmentAdminCommand = new RelayCommand(
            p => ShowEnvironmentAdmin(AdminTabFrom(p)),
            _ => IsConnected);
        AllowWritesCommand = new RelayCommand(_ => AllowWrites(), _ => CanAllowWrites);
        RevokeWritesCommand = new RelayCommand(_ => RevokeWrites(), _ => IsWriteAllowlisted);
        ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => ItemsView.Count > 0);
        // A detail-pane shortcut passes the tab to open on; a row, or nothing, opens the type's own tab.
        ShowDetailsCommand = new RelayCommand(
            p => ShowDetails(Target(p), p as DetailsShortcut),
            p => Target(p) is not null && IsConnected);
        CheckUnmanagedLayersCommand = new AsyncRelayCommand(
            _ => CheckUnmanagedLayersAsync(), _ => IsConnected && !IsBusy && !IsCheckingLayers);
        CancelLayerCheckCommand = new RelayCommand(_ => _layerCts?.Cancel(), _ => IsCheckingLayers);
    }

    /// <summary>Raised when the tab's persistable state changes, so the shell can save it.</summary>
    public event EventHandler? StateChanged;

    // ---------------------------------------------------------------- commands

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand SwitchAccountCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    public RelayCommand CopyAsTableCommand { get; }
    public RelayCommand OpenSelectionDetailsCommand { get; }
    public AsyncRelayCommand CheckSelectionLayersCommand { get; }
    public RelayCommand ExportSelectionCommand { get; }

    // ---------------------------------------------------------------- favourites, saved searches, recent

    public RelayCommand ToggleFavouriteCommand { get; }
    public RelayCommand SaveSearchCommand { get; }
    public RelayCommand ApplySavedSearchCommand { get; }
    public RelayCommand DeleteSavedSearchCommand { get; }
    public RelayCommand OpenRecentCommand { get; }

    private static UserLibrary Library => UserLibrary.Shared;

    public ObservableCollection<SavedSearch> SavedSearches { get; } = new();
    public ObservableCollection<RecentObject> RecentObjects { get; } = new();
    public bool HasSavedSearches => SavedSearches.Count > 0;
    public bool HasRecentObjects => RecentObjects.Count > 0;

    /// <summary>"Add to favourites", or "Remove from favourites" when everything it acts on is one.</summary>
    public string FavouriteHeader
    {
        get
        {
            var rows = Targets(null);
            var all = rows.Count > 0 && rows.All(r => r.IsFavourite);
            var what = rows.Count > 1 ? $" ({rows.Count:N0})" : string.Empty;
            return (all ? "Remove from favourites" : "Add to favourites") + what;
        }
    }

    private bool _favouritesOnly;
    /// <summary>Only the starred objects.</summary>
    public bool FavouritesOnly
    {
        get => _favouritesOnly;
        set
        {
            if (SetProperty(ref _favouritesOnly, value) && !_suppressFilterRefresh) ApplyFilter();
        }
    }

    private void RefreshLibrary()
    {
        SavedSearches.Clear();
        RecentObjects.Clear();

        if (!IsConnected || string.IsNullOrWhiteSpace(EnvironmentUrl)) return;

        var library = Library.For(EnvironmentUrl);
        foreach (var search in library.Searches) SavedSearches.Add(search);
        foreach (var recent in library.Recent.Take(15)) RecentObjects.Add(recent);

        OnPropertyChanged(nameof(HasSavedSearches));
        OnPropertyChanged(nameof(HasRecentObjects));
        RebuildSuggestions();
    }

    private void ToggleFavourite(IReadOnlyList<SolutionComponentItem> rows)
    {
        var favourite = !rows.All(r => r.IsFavourite);
        foreach (var row in rows) row.IsFavourite = favourite;

        Library.SetFavourite(EnvironmentUrl, rows.Where(r => r.ObjectId != Guid.Empty).Select(r => r.ObjectId), favourite);
        OnPropertyChanged(nameof(FavouriteHeader));
        if (FavouritesOnly) ApplyFilter();

        var what = rows.Count == 1 ? rows[0].PrimaryLabel : $"{rows.Count:N0} objects";
        Status = favourite ? $"Added {what} to favourites." : $"Removed {what} from favourites.";
    }

    private void SaveSearch()
    {
        var name = Views.NamePromptWindow.Ask(Application.Current?.MainWindow, "Save search",
            "Name this search - its words and filters - so it can be picked again.", SearchText.Trim());
        if (string.IsNullOrWhiteSpace(name)) return;

        Library.SaveSearch(EnvironmentUrl, new SavedSearch
        {
            Name = name.Trim(),
            SearchText = SearchText,
            Type = SelectedTypeFilter is { IsAll: false } type ? type.Name : null,
            SubType = SelectedSubTypeFilter is { IsAll: false } sub ? sub.Name : null,
            State = SelectedStateFilter is { IsAll: false } state ? state.Name : null,
            Layer = SelectedLayerFilter is { IsAll: false } layer ? layer.Name : null,
            FavouritesOnly = FavouritesOnly
        });

        RefreshLibrary();
        Status = $"Saved the search '{name.Trim()}'.";
    }

    private void ApplySavedSearch(SavedSearch? search)
    {
        if (search is null) return;

        _suppressFilterRefresh = true;

        _selectedTypeFilter = TypeFilters.FirstOrDefault(o => o.Name == search.Type) ?? TypeFilters.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedTypeFilter));
        RebuildSubTypeFilters();

        _selectedSubTypeFilter = SubTypeFilters.FirstOrDefault(o => o.Name == search.SubType) ?? SubTypeFilters.FirstOrDefault();
        _selectedStateFilter = StateFilters.FirstOrDefault(o => o.Name == search.State) ?? StateFilters.FirstOrDefault();
        _selectedLayerFilter = LayerFilters.FirstOrDefault(o => o.Name == search.Layer) ?? LayerFilters.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedSubTypeFilter));
        OnPropertyChanged(nameof(SelectedStateFilter));
        OnPropertyChanged(nameof(SelectedLayerFilter));

        _favouritesOnly = search.FavouritesOnly;
        OnPropertyChanged(nameof(FavouritesOnly));

        _searchText = search.SearchText;
        OnPropertyChanged(nameof(SearchText));

        _suppressFilterRefresh = false;
        ApplyFilter();

        Status = $"Showing the saved search '{search.Name}'.";
    }

    private void DeleteSavedSearch(SavedSearch? search)
    {
        if (search is null) return;

        Library.DeleteSearch(EnvironmentUrl, search.Name);
        RefreshLibrary();
        Status = $"Deleted the saved search '{search.Name}'.";
    }

    private void OpenRecent(RecentObject? recent)
    {
        if (recent is null) return;

        var item = _allItems.FirstOrDefault(i => i.ObjectId == recent.ObjectId);
        if (item is null)
        {
            Status = $"{recent.Label} is not in the solution on show - pick a solution that holds it (the default solution holds everything).";
            return;
        }

        ShowDetails(item);
    }

    private IReadOnlyList<SolutionComponentItem> _selection = Array.Empty<SolutionComponentItem>();

    /// <summary>Every row selected in the grid (Shift/Ctrl+click), in grid order.</summary>
    public IReadOnlyList<SolutionComponentItem> Selection => _selection;

    /// <summary>Called by the view as the grid's selection changes - its SelectedItems cannot be bound.</summary>
    public void SetSelection(IEnumerable<SolutionComponentItem> items)
    {
        _selection = items.ToList();

        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(HasMultipleSelected));
        OnPropertyChanged(nameof(CopyNameHeader));
        OnPropertyChanged(nameof(CopyLinkHeader));
        OnPropertyChanged(nameof(CopyIdHeader));
        OnPropertyChanged(nameof(SelectionHeader));
        OnPropertyChanged(nameof(FavouriteHeader));
        CopyAsTableCommand.RaiseCanExecuteChanged();
        ToggleFavouriteCommand.RaiseCanExecuteChanged();
        OpenSelectionDetailsCommand.RaiseCanExecuteChanged();
        CheckSelectionLayersCommand.RaiseCanExecuteChanged();
        ExportSelectionCommand.RaiseCanExecuteChanged();
        RaiseSwitchCommands();
    }

    public bool HasMultipleSelected => _selection.Count > 1;

    public string CopyNameHeader => HasMultipleSelected ? $"Copy {_selection.Count:N0} names" : "Copy name";
    public string CopyLinkHeader => HasMultipleSelected ? $"Copy {_selection.Count:N0} maker portal links" : "Copy maker portal link";
    public string CopyIdHeader => HasMultipleSelected ? $"Copy {_selection.Count:N0} object ids" : "Copy object id";
    public string SelectionHeader => $"{_selection.Count:N0} selected";

    /// <summary>The rows a command acts on: the one it names, or the whole selection, or the current row.</summary>
    private IReadOnlyList<SolutionComponentItem> Targets(object? parameter)
    {
        if (parameter is SolutionComponentItem one) return new[] { one };
        if (_selection.Count > 0) return _selection;
        return SelectedItem is { } current ? new[] { current } : Array.Empty<SolutionComponentItem>();
    }

    /// <summary>One value per selected row, a line each - ready to paste into a ticket or a query.</summary>
    private void CopyEach(object? parameter, Func<SolutionComponentItem, string?> value, string what)
    {
        var rows = Targets(parameter);
        if (rows.Count <= 1)
        {
            CopyToClipboard(rows.Count == 1 ? value(rows[0]) : null, what);
            return;
        }

        var values = rows.Select(value).Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (values.Count == 0)
        {
            Status = $"Nothing to copy - none of the selected objects has a {what}.";
            return;
        }

        var none = values.Count < rows.Count ? $" ({rows.Count - values.Count:N0} had none)." : ".";
        Status = ClipboardText.TryCopy(string.Join(Environment.NewLine, values), out var failure)
            ? $"Copied {values.Count:N0} {what}s{none}"
            : $"Could not copy - the clipboard is held by another application ({failure}).";
    }

    /// <summary>Tab-separated with a header row, so it pastes into Excel as a table.</summary>
    private void CopyAsTable(IReadOnlyList<SolutionComponentItem> rows)
    {
        static string Cell(string? value) => (value ?? string.Empty).Replace('\t', ' ').Replace("\r", " ").Replace('\n', ' ');

        var lines = new List<string> { "Name\tDisplay name\tObject type\tSub type\tState\tObject id\tMaker portal link" };
        lines.AddRange(rows.Select(i => string.Join('\t',
            Cell(i.Name), Cell(i.DisplayName), Cell(i.ComponentTypeName), Cell(i.SubType), Cell(i.ManagedLabel),
            i.ObjectId == Guid.Empty ? string.Empty : i.ObjectId.ToString(), Cell(i.MakerUrl))));

        Status = ClipboardText.TryCopy(string.Join(Environment.NewLine, lines), out var failure)
            ? $"Copied {rows.Count:N0} row(s) as a table."
            : $"Could not copy - the clipboard is held by another application ({failure}).";
    }

    /// <summary>A details window for each selected row, after asking when that is a lot of windows.</summary>
    private void OpenSelectionDetails()
    {
        var rows = _selection.ToList();
        if (rows.Count > 10 &&
            MessageBox.Show($"Open {rows.Count:N0} details windows?", MessageCaption,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var row in rows) ShowDetails(row);
    }
    public RelayCommand CopyIdCommand { get; }
    public RelayCommand CopyNameCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ShowDetailsCommand { get; }
    public AsyncRelayCommand CheckUnmanagedLayersCommand { get; }
    public RelayCommand CancelLayerCheckCommand { get; }

    /// <summary>
    /// A layer check runs on its own token: sharing the load's would cancel a background refresh
    /// that happened to be running, leaving the stale cached list on screen for good.
    /// </summary>
    private CancellationTokenSource? _layerCts;

    /// <summary>
    /// What each object's layer check found, kept for as long as the connection lasts. A refresh or
    /// a switch of solution replaces the items, and with them the answers to hundreds of requests.
    /// </summary>
    private readonly Dictionary<Guid, bool> _layerChecks = new();

    private bool _isCheckingLayers;
    public bool IsCheckingLayers
    {
        get => _isCheckingLayers;
        private set
        {
            if (!SetProperty(ref _isCheckingLayers, value)) return;

            CheckUnmanagedLayersCommand.RaiseCanExecuteChanged();
            CancelLayerCheckCommand.RaiseCanExecuteChanged();
        }
    }
    public RelayCommand CopyTenantIdCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand ToggleDetailPaneCommand { get; }
    public RelayCommand SolutionHistoryCommand { get; }
    public RelayCommand EntraTeamSyncCommand { get; }
    public RelayCommand QueueSyncCommand { get; }

    /// <summary>Users, Roles, Mailboxes or Queues - the parameter names the tab to open on.</summary>
    public RelayCommand EnvironmentAdminCommand { get; }
    public RelayCommand AllowWritesCommand { get; }
    public RelayCommand RevokeWritesCommand { get; }

    /// <summary>Raised when this tab adds or removes its environment from the write allowlist,
    /// so other tabs on the same environment can show it too.</summary>
    public event EventHandler? WriteAllowlistChanged;

    /// <summary>The environment's imports, upgrades, uninstalls and exports, in their own window.</summary>
    private void ShowSolutionHistory()
    {
        if (_client is null) return;

        var viewModel = new SolutionHistoryViewModel(this, _client);
        Track(new Views.SolutionHistoryWindow { DataContext = viewModel, Owner = Application.Current.MainWindow }).Show();
        _ = viewModel.LoadAsync();
    }

    /// <summary>Compare an Entra group team with its group, sync it, and remove what the sync leaves behind.</summary>
    private void ShowEntraTeamSync()
    {
        if (_client is null) return;

        var viewModel = new EntraTeamSyncViewModel(this, _client);
        Track(new Views.EntraTeamSyncWindow { DataContext = viewModel, Owner = Application.Current.MainWindow }).Show();
        _ = viewModel.LoadTeamsAsync();
    }

    private Views.EnvironmentAdminWindow? _adminWindow;

    /// <summary>
    /// Windows opened from this tab that hold its <see cref="DataverseClient"/>. That client is
    /// disposed when the tab reconnects, switches account, signs out or closes, after which every
    /// action in those windows would fail - and a reconnect to another URL would leave them showing
    /// the old environment under this tab's name. So they close with the connection they belong to.
    /// </summary>
    private readonly List<Window> _toolWindows = new();

    private T Track<T>(T window) where T : Window
    {
        _toolWindows.Add(window);
        window.Closed += (_, _) => _toolWindows.Remove(window);
        return window;
    }

    private void CloseToolWindows()
    {
        foreach (var window in _toolWindows.ToList()) window.Close();
        _toolWindows.Clear();
        _adminWindow = null;
    }

    /// <summary>The tab a shortcut asks for, by value or by name; the Users tab otherwise.</summary>
    private static AdminTab AdminTabFrom(object? parameter) => parameter switch
    {
        AdminTab tab => tab,
        string name when Enum.TryParse<AdminTab>(name, out var named) => named,
        _ => AdminTab.Users
    };

    /// <summary>
    /// Users, roles, mailboxes and queues - read-only. One window per tab: asking again brings it
    /// forward on the tab asked for.
    /// </summary>
    private void ShowEnvironmentAdmin(AdminTab tab)
    {
        if (_client is null) return;

        if (_adminWindow is { DataContext: EnvironmentAdminViewModel open })
        {
            open.SelectedTab = tab;
            if (_adminWindow.WindowState == WindowState.Minimized) _adminWindow.WindowState = WindowState.Normal;
            _adminWindow.Activate();
            return;
        }

        var viewModel = new EnvironmentAdminViewModel(this, _client, tab);
        var window = Track(new Views.EnvironmentAdminWindow { DataContext = viewModel, Owner = Application.Current.MainWindow });
        window.Closed += (_, _) => { if (ReferenceEquals(_adminWindow, window)) _adminWindow = null; };
        _adminWindow = window;
        window.Show();
        _ = viewModel.LoadAsync();
    }

    /// <summary>Make a queue's members match a team's, adding and removing.</summary>
    private void ShowQueueSync()
    {
        if (_client is null) return;

        var viewModel = new QueueSyncViewModel(this, _client);
        Track(new Views.QueueSyncWindow { DataContext = viewModel, Owner = Application.Current.MainWindow }).Show();
        _ = viewModel.LoadAsync();
    }

    // ---------------------------------------------------------------- write allowlist

    /// <summary>Named in AllowProductionWrites, so the write guard lets writes through here.</summary>
    public bool IsWriteAllowlisted => HasHost && WriteGuard.IsAllowlisted(_settings, EnvironmentUrl);

    /// <summary>
    /// Offered only where the guard would otherwise refuse: production, the default environment,
    /// and anything whose type is not known. A sandbox needs no entry.
    /// </summary>
    public bool CanAllowWrites =>
        HasHost && !IsWriteAllowlisted &&
        EnvironmentSku is EnvironmentSku.Production or EnvironmentSku.Default or EnvironmentSku.Unknown;

    /// <summary>For the sidebar tooltip; empty unless writes have been allowed here.</summary>
    public string WriteAccessDescription => IsWriteAllowlisted
        ? "Writes allowed - this environment is in AllowProductionWrites."
        : string.Empty;

    private bool HasHost => EnvironmentHost != "Not connected" && !string.IsNullOrWhiteSpace(EnvironmentUrl);

    /// <summary>
    /// Adds this one environment to the write allowlist, after a confirmation that names it and
    /// says what type it is. Defaults to No: lifting the production guard is never the easy path.
    /// </summary>
    private void AllowWrites()
    {
        if (!CanAllowWrites) return;

        var type = EnvironmentSku switch
        {
            EnvironmentSku.Unknown => "an environment whose type could not be read",
            var sku => "a " + new EnvironmentTypeInfo(sku, null, null).SkuLabel.ToLowerInvariant() + " environment"
        };

        var answer = MessageBox.Show(
            $"Allow PPObjectSearch to write to {EnvironmentHost}?\n\n" +
            $"This is {type}. Reconciling reference data and the admin tools will be able to change " +
            "data and membership here. Every change is still previewed and confirmed first.\n\n" +
            "Only this environment is affected. You can stop allowing writes from the same menu.",
            "Allow writes to this environment",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        if (WriteGuard.Allow(_settings, EnvironmentUrl)) _settings.Save();
        RaiseWriteAllowlist();
        WriteAllowlistChanged?.Invoke(this, EventArgs.Empty);
        Status = $"Writes to {EnvironmentHost} are now allowed.";
    }

    private void RevokeWrites()
    {
        if (WriteGuard.Revoke(_settings, EnvironmentUrl)) _settings.Save();
        RaiseWriteAllowlist();
        WriteAllowlistChanged?.Invoke(this, EventArgs.Empty);
        Status = $"Writes to {EnvironmentHost} are blocked again.";
    }

    /// <summary>Re-reads the allowlist state, e.g. after another tab changed it.</summary>
    public void RaiseWriteAllowlist()
    {
        OnPropertyChanged(nameof(IsWriteAllowlisted));
        OnPropertyChanged(nameof(CanAllowWrites));
        OnPropertyChanged(nameof(WriteAccessDescription));
        AllowWritesCommand.RaiseCanExecuteChanged();
        RevokeWritesCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The Power Platform environment id, where it could be resolved.</summary>
    public string? EnvironmentId => _linkBuilder?.EnvironmentId;

    /// <summary>A Graph client signed in as this tab's account, in this environment's tenant.</summary>
    public Graph.GraphClient CreateGraphClient() => new(_authContext);

    /// <summary>
    /// Whether this environment may be written to. Asks the Power Platform API afresh every time
    /// rather than trusting the type shown in the sidebar, which may be remembered from before.
    /// </summary>
    public async Task<WritePermission> EvaluateWritePermissionAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("The environment is not connected.");

        var environmentId = _linkBuilder?.EnvironmentId ?? _settings.GetEnvironmentId(_client.EnvironmentUrl);
        var type = await _client.GetEnvironmentTypeAsync(environmentId, ct);

        return WriteGuard.Evaluate(_settings, _client.EnvironmentUrl, type);
    }

    // ---------------------------------------------------------------- state

    public BulkObservableCollection<SolutionComponentItem> Items { get; }
    public ListCollectionView ItemsView { get; }

    /// <summary>Every loaded object, unfiltered - used by the cross-environment features.</summary>
    public IReadOnlyList<SolutionComponentItem> AllItems => _allItems;

    /// <summary>The live connection, for per-object lookups. Null until connected.</summary>
    public DataverseClient? Client => _client;
    public ObservableCollection<SolutionInfo> Solutions { get; } = new();

    /// <summary>The solution picker's list, narrowed by what has been typed into it.</summary>
    public ICollectionView SolutionsView { get; }
    public ObservableCollection<TypeFilterOption> TypeFilters { get; } = new();
    public ObservableCollection<TypeFilterOption> SubTypeFilters { get; } = new();
    public ObservableCollection<TypeFilterOption> StateFilters { get; } = new();
    public ObservableCollection<TypeFilterOption> LayerFilters { get; } = new();

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
            OnPropertyChanged(nameof(EnvironmentHost));
            RaiseWriteAllowlist();

            if (!IsConnected)
            {
                // What was remembered belonged to the old URL.
                RememberedSku = null;
                Title = DeriveTitle(value);
                // A different environment may live in a different tenant - re-discover on connect.
                _authContext = NewAuthContext();
                OnPropertyChanged(nameof(AccountName));
                OnPropertyChanged(nameof(AccountInitials));
            }
        }
    }

    /// <summary>The environment's host alone, for the sidebar - "contoso.crm11.dynamics.com".</summary>
    public string EnvironmentHost
    {
        get
        {
            var value = EnvironmentUrl.Trim();
            if (value.Length == 0) return "Not connected";
            if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : EnvironmentUrl;
        }
    }

    private EnvironmentTypeInfo? _environmentType;
    /// <summary>The Power Platform environment type, read in the background after connecting.
    /// Null until then.</summary>
    public EnvironmentTypeInfo? EnvironmentType
    {
        get => _environmentType;
        private set
        {
            if (!SetProperty(ref _environmentType, value)) return;
            OnPropertyChanged(nameof(EnvironmentSku));
            OnPropertyChanged(nameof(EnvironmentTypeDescription));
            RaiseWriteAllowlist();
        }
    }

    private EnvironmentSku? _rememberedSku;
    /// <summary>The last known type, saved with the tab so its colour shows before it connects.</summary>
    private EnvironmentSku? RememberedSku
    {
        get => _rememberedSku;
        set
        {
            if (_rememberedSku == value) return;
            _rememberedSku = value;
            OnPropertyChanged(nameof(EnvironmentTypeDescription));
            OnPropertyChanged(nameof(EnvironmentSku));
            RaiseWriteAllowlist();
        }
    }

    /// <summary>
    /// What the sidebar and badges show: the type read on this connection, else the one remembered
    /// from the last. Display only - the write guard reads the type afresh and never uses this.
    /// </summary>
    public EnvironmentSku EnvironmentSku =>
        EnvironmentType is { Sku: not EnvironmentSku.Unknown } type ? type.Sku
        : RememberedSku ?? EnvironmentSku.Unknown;

    /// <summary>The badge's tooltip: the type in words, and why it is Unknown when it is.</summary>
    public string EnvironmentTypeDescription
    {
        get
        {
            static string Label(EnvironmentSku sku) => new EnvironmentTypeInfo(sku, null, null).SkuLabel;

            if (EnvironmentType is { Sku: not EnvironmentSku.Unknown } read) return Label(read.Sku);

            var remembered = RememberedSku is { } sku and not EnvironmentSku.Unknown
                ? $"{Label(sku)}, remembered from the last connection."
                : null;

            if (EnvironmentType is null) return remembered ?? "Environment type not read yet - it is read after connecting.";

            var why = EnvironmentType.Detail ?? "The environment type could not be read.";
            return remembered is null ? why : $"{remembered} This time: {why}";
        }
    }

    public string? AccountName => _authContext.AccountName;

    // What separates the first name from the last in an account's local part.
    private static readonly char[] AccountNameSeparators = ['.', '_', '-', ' '];

    /// <summary>Two letters for the account avatar: "maria.lopez@contoso.com" is "ML".</summary>
    public string AccountInitials
    {
        get
        {
            var name = AccountName;
            if (string.IsNullOrWhiteSpace(name)) return "?";

            var local = name.Split('@')[0];
            var parts = local.Split(AccountNameSeparators, StringSplitOptions.RemoveEmptyEntries);

            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
                _ => string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0]))
            };
        }
    }

    public string? TenantId => _authContext.TenantId;

    /// <summary>Shared by every tab and remembered in settings.</summary>
    public bool IsDetailPaneOpen
    {
        get => _settings.IsDetailPaneOpen;
        set
        {
            if (_settings.IsDetailPaneOpen == value) return;
            _settings.IsDetailPaneOpen = value;
            _settings.Save();
            OnPropertyChanged();
            if (value) QueueSelectionInsight();
        }
    }

    /// <summary>Whether a filter or the search box narrows the list - shows the Clear button.</summary>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText) ||
        SelectedTypeFilter is { IsAll: false } ||
        SelectedSubTypeFilter is { IsAll: false } ||
        SelectedStateFilter is { IsAll: false } ||
        SelectedLayerFilter is { IsAll: false } ||
        FavouritesOnly;

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                RefreshLibrary();
                SaveSearchCommand?.RaiseCanExecuteChanged();
                OpenRecentCommand?.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
                CheckUnmanagedLayersCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                CopyTenantIdCommand.RaiseCanExecuteChanged();
                SolutionHistoryCommand.RaiseCanExecuteChanged();
                EntraTeamSyncCommand.RaiseCanExecuteChanged();
                QueueSyncCommand.RaiseCanExecuteChanged();
                EnvironmentAdminCommand.RaiseCanExecuteChanged();
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
            if (!SetProperty(ref _isBusy, value)) return;

            OnPropertyChanged(nameof(IsSolutionPickerEnabled));
            CheckUnmanagedLayersCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool IsSolutionPickerEnabled => IsConnected && !IsBusy;

    /// <summary>Sub type dropdown is pointless when the current selection has no sub types.</summary>
    public bool HasSubTypes => SubTypeFilters.Count > 1;

    private string _status = "Enter an environment URL and connect.";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private SolutionComponentItem? _selectedItem;
    /// <summary>
    /// The highlighted row. The row commands read it directly, because a context menu cannot
    /// hand them one - see <see cref="Target"/>.
    /// </summary>
    public SolutionComponentItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;

            OpenLinkCommand.RaiseCanExecuteChanged();
            ShowDetailsCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(DetailShortcuts));
            OnPropertyChanged(nameof(IsSelectedEnvironmentVariable));
            QueueSelectionInsight();
        }
    }

    // ---------------------------------------------------------------- detail pane: shortcuts and insight

    /// <summary>The detail pane's "Open in details" buttons for the selected object.</summary>
    public IReadOnlyList<DetailsShortcut> DetailShortcuts =>
        SelectedItem is { } item ? DetailsTabs.ShortcutsFor(item) : [];

    public bool IsSelectedEnvironmentVariable =>
        SelectedItem is { } item && DetailsTabs.KindOf(item) == ObjectKind.EnvironmentVariable;

    private readonly DispatcherTimer _insightDebounce;

    /// <summary>Bumped per selection; only the latest read may write its result.</summary>
    private int _insightRequest;

    /// <summary>What was read for each object, briefly - flicking between two rows should not re-read them.</summary>
    private readonly Dictionary<Guid, (DateTimeOffset At, LatestRunSummary? Run, EnvironmentVariableInfo? Variable, bool? SwitchedOn)> _insights = new();

    private static readonly TimeSpan InsightLifetime = TimeSpan.FromMinutes(2);

    /// <summary>How many of a flow's latest runs the card counts failures across.</summary>
    internal const int RecentRunCount = 20;

    private LatestRunSummary? _latestRun;
    /// <summary>The selected cloud flow's latest run; null for anything else, or while it is read.</summary>
    public LatestRunSummary? LatestRun
    {
        get => _latestRun;
        private set => SetProperty(ref _latestRun, value);
    }

    private EnvironmentVariableInfo? _selectedVariable;
    /// <summary>The selected environment variable as this environment resolves it.</summary>
    public EnvironmentVariableInfo? SelectedVariable
    {
        get => _selectedVariable;
        private set
        {
            if (!SetProperty(ref _selectedVariable, value)) return;
            OnPropertyChanged(nameof(SelectedVariableValue));
        }
    }

    /// <summary>"Not set" stands in where neither a value nor a default exists.</summary>
    public string? SelectedVariableValue => SelectedVariable is null
        ? null
        : SelectedVariable.EffectiveValue ?? "Not set";

    private void QueueSelectionInsight()
    {
        _insightRequest++;
        _insightDebounce.Stop();
        LatestRun = null;
        SelectedVariable = null;
        SelectedSwitchedOn = null;

        if (SelectedItem is not { } item || _client is null || !IsDetailPaneOpen) return;

        var kind = DetailsTabs.KindOf(item);
        if (kind is not (ObjectKind.CloudFlow or ObjectKind.EnvironmentVariable) && Switchable.KindOf(item) is null) return;

        if (_insights.TryGetValue(item.ObjectId, out var known) && DateTimeOffset.Now - known.At < InsightLifetime)
        {
            LatestRun = known.Run;
            SelectedVariable = known.Variable;
            SelectedSwitchedOn = known.SwitchedOn;
            return;
        }

        _insightDebounce.Start();
    }

    /// <summary>
    /// One small read for the selected object: a cloud flow's latest runs, an environment
    /// variable's value, and whether a flow, process or plug-in step is on - so the pane offers
    /// the one switch that applies. Failures leave the pane as it was - the details window says why.
    /// </summary>
    private async Task LoadSelectionInsightAsync()
    {
        var request = _insightRequest;
        if (SelectedItem is not { } item || _client is not { } client) return;

        try
        {
            LatestRunSummary? run = null;
            EnvironmentVariableInfo? variable = null;
            bool? switchedOn = null;

            switch (DetailsTabs.KindOf(item))
            {
                case ObjectKind.CloudFlow:
                    run = LatestRunSummary.From(await client.GetCloudFlowRunsAsync(item.ObjectId, CancellationToken.None, RecentRunCount));
                    break;
                case ObjectKind.EnvironmentVariable:
                    variable = await client.GetEnvironmentVariableAsync(item.ObjectId, item.ComponentType == 381, CancellationToken.None);
                    break;
            }

            if (Switchable.KindOf(item) is { } switchKind)
            {
                var states = await client.GetSwitchStatesAsync(switchKind, [item.ObjectId], CancellationToken.None);
                switchedOn = states.TryGetValue(item.ObjectId, out var on) ? on : null;
            }

            _insights[item.ObjectId] = (DateTimeOffset.Now, run, variable, switchedOn);
            if (request != _insightRequest) return;

            LatestRun = run;
            SelectedVariable = variable;
            SelectedSwitchedOn = switchedOn;
        }
        catch (Exception ex)
        {
            // A nicety: the details window reads the same and says what went wrong.
            Log.Warn("Selection insight could not be read", ex);
        }
    }

    /// <summary>
    /// The object a row command should act on. A MenuItem inside a ContextMenu invokes its command
    /// only after the popup has closed, and closing it tears down the popup's visual tree - which
    /// detaches any RelativeSource binding on CommandParameter and resets the parameter to null.
    /// So the parameter is trusted when one arrives (the Name column's hyperlink passes its own
    /// row), and the selection stands in for it when it does not.
    /// </summary>
    private SolutionComponentItem? Target(object? parameter) =>
        parameter as SolutionComponentItem ?? SelectedItem;

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
                OnPropertyChanged(nameof(SearchInsideLabel));
                OnPropertyChanged(nameof(SearchInsideTerm));
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
            if (!SetProperty(ref _selectedTypeFilter, value) || _suppressFilterRefresh) return;

            // Sub types are meaningful only within a type, so they follow the type selection.
            RebuildSubTypeFilters();
            ApplyFilter();
        }
    }

    private string _solutionSearchText = string.Empty;
    /// <summary>
    /// The solution picker's text: the selected solution's name, or a search being typed. Typing
    /// only narrows the list - it never changes which solution is loaded.
    /// </summary>
    public string SolutionSearchText
    {
        get => _solutionSearchText;
        set
        {
            if (SetProperty(ref _solutionSearchText, value ?? string.Empty)) SolutionsView.Refresh();
        }
    }

    /// <summary>
    /// The picker closed or lost focus without a pick: show the loaded solution again, and the
    /// whole list next time it opens.
    /// </summary>
    public void EndSolutionSearch()
    {
        SolutionSearchText = SelectedSolution?.DisplayLabel ?? string.Empty;
        OnPropertyChanged(nameof(SelectedSolution));
    }

    private string _typeFilterSearchText = string.Empty;
    public string TypeFilterSearchText
    {
        get => _typeFilterSearchText;
        set
        {
            if (SetProperty(ref _typeFilterSearchText, value))
            {
                TypeFiltersView.Refresh();
                
                // If the text no longer matches the selected item's label, the user is typing a new filter.
                // Clear the active grid filter so it doesn't stay stuck on the old selection.
                if (SelectedTypeFilter != null && value != SelectedTypeFilter.Label)
                {
                    SelectedTypeFilter = TypeFilters.FirstOrDefault(t => t.IsAll);
                }
            }
        }
    }

    public ICollectionView TypeFiltersView { get; }

    private TypeFilterOption? _selectedSubTypeFilter;
    public TypeFilterOption? SelectedSubTypeFilter
    {
        get => _selectedSubTypeFilter;
        set
        {
            if (SetProperty(ref _selectedSubTypeFilter, value) && !_suppressFilterRefresh) ApplyFilter();
        }
    }

    private TypeFilterOption? _selectedStateFilter;
    public TypeFilterOption? SelectedStateFilter
    {
        get => _selectedStateFilter;
        set
        {
            if (SetProperty(ref _selectedStateFilter, value) && !_suppressFilterRefresh) ApplyFilter();
        }
    }

    private TypeFilterOption? _selectedLayerFilter;
    public TypeFilterOption? SelectedLayerFilter
    {
        get => _selectedLayerFilter;
        set
        {
            if (SetProperty(ref _selectedLayerFilter, value) && !_suppressFilterRefresh) ApplyFilter();
        }
    }

    private SolutionInfo? _selectedSolution;
    public SolutionInfo? SelectedSolution
    {
        get => _selectedSolution;
        set
        {
            // Typing into the picker clears the ComboBox's own selection whenever the text matches no
            // solution. That is a search in progress, not a choice: the loaded solution stays, and
            // only picking one reloads. Clearing it for real (on disconnect) sets the field directly.
            if (value is null && !_suppressSolutionReload) return;

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
        SolutionUniqueName = _preferredSolutionUniqueName,
        EnvironmentType = RememberedSku
    };

    // ---------------------------------------------------------------- connect

    public async Task ConnectAsync(bool forceAccountPicker = false)
    {
        CancelBackgroundWork();
        _layerChecks.Clear();
        var cts = _loadCts = new CancellationTokenSource();

        try
        {
            IsBusy = true;

            if (_client is not null) CloseToolWindows();
            _client?.Dispose();
            
            var normalizedUrl = DataverseClient.NormalizeEnvironmentUrl(EnvironmentUrl);
            if (_client is not null && !string.Equals(_client.EnvironmentUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
            {
                _authContext = NewAuthContext();
                OnPropertyChanged(nameof(AccountName));
                OnPropertyChanged(nameof(AccountInitials));
            }

            _client = _newClient?.Invoke(_authContext, normalizedUrl) ?? new DataverseClient(_authContext, normalizedUrl);
            _environmentUrl = _client.EnvironmentUrl;
            OnPropertyChanged(nameof(EnvironmentUrl));

            Status = "Identifying tenant...";
            await _authContext.EnsureTenantAsync(_client.EnvironmentUrl, cts.Token);
            _authContext.ForceAccountPicker = forceAccountPicker;

            Status = "Signing in...";
            await _client.WhoAmIAsync(cts.Token);

            IsConnected = true;
            OnPropertyChanged(nameof(AccountName));
            OnPropertyChanged(nameof(AccountInitials));
            OnPropertyChanged(nameof(TenantId));

            Status = "Resolving environment...";
            var organization = await _client.RetrieveCurrentOrganizationAsync(cts.Token);

            Title = !string.IsNullOrWhiteSpace(organization?.FriendlyName)
                ? organization.FriendlyName
                : DeriveTitle(_client.EnvironmentUrl);

            var environmentId = _settings.GetEnvironmentId(_client.EnvironmentUrl)
                                ?? organization?.EnvironmentId
                                ?? await _client.GetEnvironmentIdFromDiscoveryAsync(cts.Token);

            // Only colours the tab and its badge, so it runs alongside the load rather than before it.
            _ = ReadEnvironmentTypeAsync(_client, environmentId);

            // Table metadata drives maker portal links: entity set names give each component
            // type its URL segment, and metadata ids let columns point at their parent table.
            var tables = await _client.GetTableMetadataAsync(cts.Token);

            _linkBuilder = new MakerPortalLinkBuilder(
                environmentId, _client.EnvironmentUrl, _settings.MakerLinkTemplates, tables);
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

    /// <summary>
    /// Separate from <see cref="_loadCts"/>: the solution load that starts straight after connecting
    /// cancels that one, and took an unfinished type lookup down with it.
    /// </summary>
    private CancellationTokenSource? _typeCts;

    private async Task ReadEnvironmentTypeAsync(DataverseClient client, string? environmentId)
    {
        var superseded = _typeCts;
        var cts = _typeCts = new CancellationTokenSource();
        if (superseded is not null) await superseded.CancelAsync();

        try
        {
            EnvironmentTypeInfo type;
            try
            {
                type = await client.GetEnvironmentTypeAsync(environmentId, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                type = new EnvironmentTypeInfo(EnvironmentSku.Unknown, null,
                    "Reading the environment type failed: " + ex.Message);
            }

            if (!ReferenceEquals(client, _client) || cts.IsCancellationRequested) return;

            EnvironmentType = type;

            // A failed read is not news about the environment, so it leaves the remembered type be.
            if (type.Sku != EnvironmentSku.Unknown && RememberedSku != type.Sku)
            {
                RememberedSku = type.Sku;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (ReferenceEquals(cts, _typeCts)) _typeCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Drops all loaded state and the signed-in account, e.g. after an app-wide sign out.</summary>
    public void Reset(string status)
    {
        CancelBackgroundWork();
        _layerChecks.Clear();
        SearchDropdownOpen = false;
        CloseToolWindows();
        _client?.Dispose();
        _client = null;
        _linkBuilder = null;
        EnvironmentType = null;
        _authContext = NewAuthContext();

        _allItems.Clear();
        _insights.Clear();
        Items.Clear();
        Solutions.Clear();
        TypeFilters.Clear();
        SubTypeFilters.Clear();
        StateFilters.Clear();
        LayerFilters.Clear();
        _selectedSolution = null;
        OnPropertyChanged(nameof(SelectedSolution));

        IsConnected = false;
        ResultSummary = string.Empty;
        Title = DeriveTitle(EnvironmentUrl);
        Status = status;
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountInitials));
        OnPropertyChanged(nameof(TenantId));
    }

    /// <summary>Stops a solution load, the environment type lookup and a layer check, whichever are running.</summary>
    private void CancelBackgroundWork()
    {
        _loadCts?.Cancel();
        _typeCts?.Cancel();
        _layerCts?.Cancel();
    }

    // ---------------------------------------------------------------- load

    /// <summary>
    /// Shows the cached copy straight away (if there is one) and refreshes it in the background,
    /// so a reconnect is usable immediately instead of after a full re-read.
    /// </summary>
    private async Task LoadSolutionComponentsAsync(bool useCache = true)
    {
        if (_client is null || SelectedSolution is null) return;

        var solution = SelectedSolution;
        var environmentUrl = _client.EnvironmentUrl;
        var superseded = _loadCts;
        var cts = _loadCts = new CancellationTokenSource();
        if (superseded is not null) await superseded.CancelAsync();
        if (_layerCts is { } layerCheck) await layerCheck.CancelAsync();

        try
        {
            IsBusy = true;

            // Reading and parsing a large cache is real work; it happens off the UI thread.
            var cached = useCache
                ? await Task.Run(() => ComponentCache.TryLoad(environmentUrl, solution.SolutionId), cts.Token)
                : null;

            if (!ReferenceEquals(cts, _loadCts)) return;

            ShowCachedOrClear(cached, solution);

            var verb = cached is not null ? "Refreshing" : "Loading";
            var progress = new Progress<int>(count => Status = $"{verb} '{solution.FriendlyName}'... {count:N0}");

            var timer = Stopwatch.StartNew();
            var components = await ReadComponentsAsync(solution, cached, progress, cts);
            if (components is null) return;

            timer.Stop();
            if (cts.IsCancellationRequested) return;

            Populate(components, solution);

            // Written in the background: serialising tens of thousands of rows would otherwise
            // hold the window up after the list is already on screen.
            _ = Task.Run(() => ComponentCache.Save(environmentUrl, solution.SolutionId, components), CancellationToken.None);

            Status = LoadedStatus(components, solution, timer.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer load
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(cts, _loadCts)) Status = "Load failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _loadCts)) IsBusy = false;
        }
    }

    /// <summary>The cached rows while the fresh read runs, or an empty list where there is no cache.</summary>
    private void ShowCachedOrClear(CachedComponents? cached, SolutionInfo solution)
    {
        if (cached is not null)
        {
            Populate(cached.Items, solution);
            Status = $"Showing {cached.Items.Count:N0} objects cached at {cached.LoadedAt:g} - refreshing...";
            return;
        }

        Items.Clear();
        _allItems.Clear();
        TypeFilters.Clear();
        SubTypeFilters.Clear();
        StateFilters.Clear();
        LayerFilters.Clear();
        ResultSummary = string.Empty;
        Status = $"Loading objects in '{solution.FriendlyName}'...";
    }

    /// <summary>
    /// The solution's components, read afresh. Null where the read failed with cached rows on
    /// screen; without a cache, the failure is the caller's to report.
    /// </summary>
    private async Task<IReadOnlyList<SolutionComponentItem>?> ReadComponentsAsync(
        SolutionInfo solution, CachedComponents? cached, IProgress<int> progress, CancellationTokenSource cts)
    {
        try
        {
            return await _client!.GetSolutionComponentsAsync(solution.SolutionId, progress, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && cached is not null)
        {
            // The cached rows are still on screen and still useful - keep them and say so. Unless
            // a newer load has taken over, whose status this would overwrite.
            if (ReferenceEquals(cts, _loadCts))
            {
                Status = $"Showing objects cached at {cached.LoadedAt:g}. Refresh failed: {ex.Message}";
            }

            return null;
        }
    }

    private string LoadedStatus(IReadOnlyList<SolutionComponentItem> components, SolutionInfo solution, TimeSpan took)
    {
        if (components is ComponentList { IsTruncated: true })
        {
            return $"Loaded the first {_allItems.Count:N0} objects from '{solution.FriendlyName}' - it has more " +
                   "than this tool reads, so an object not listed may still be in the solution.";
        }

        var elapsed = $"{took.TotalSeconds:0.0}s";
        return LinksAvailable
            ? $"Loaded {_allItems.Count:N0} objects from '{solution.FriendlyName}' in {elapsed}."
            : $"Loaded {_allItems.Count:N0} objects from '{solution.FriendlyName}' in {elapsed}. Maker portal " +
              "links are unavailable - the environment id could not be resolved (add it to \"EnvironmentIds\" " +
              "in settings.json).";
    }

    /// <summary>Swaps in a result set, preserving the user's current filter selections.</summary>
    private void Populate(IReadOnlyList<SolutionComponentItem> components, SolutionInfo solution)
    {
        var favourites = Library.For(EnvironmentUrl).Favourites;

        _allItems.Clear();
        _insights.Clear();

        foreach (var item in components)
        {
            item.MakerUrl = _linkBuilder?.Build(item, solution.SolutionId);
            if (item.HasUnmanagedLayer is null && _layerChecks.TryGetValue(item.ObjectId, out var layered))
            {
                item.HasUnmanagedLayer = layered;
            }

            item.IsFavourite = favourites.Contains(item.ObjectId);

            _allItems.Add(item);
        }

        _allItems.Sort((a, b) =>
        {
            var byType = string.Compare(a.ComponentTypeName, b.ComponentTypeName, StringComparison.CurrentCultureIgnoreCase);
            return byType != 0
                ? byType
                : string.Compare(a.PrimaryLabel, b.PrimaryLabel, StringComparison.CurrentCultureIgnoreCase);
        });

        RebuildFilters();

        // One reset rather than a change per row: the filtered view and the grid behind it would
        // otherwise do their work once for every object in the solution.
        Items.ReplaceAll(_allItems);

        ApplyFilter();
    }

    // ---------------------------------------------------------------- filters

    private void RebuildFilters()
    {
        _suppressFilterRefresh = true;

        var previousType = SelectedTypeFilter is { IsAll: false } t ? t.Name : null;
        var previousState = SelectedStateFilter is { IsAll: false } s ? s.Name : null;
        var previousLayer = SelectedLayerFilter is { IsAll: false } l ? l.Name : null;

        TypeFilters.Clear();
        TypeFilters.Add(new TypeFilterOption { Name = AllKey, Count = _allItems.Count, IsAll = true, AllLabel = "All types" });

        foreach (var group in _allItems.GroupBy(i => i.ComponentTypeName)
                                       .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            TypeFilters.Add(new TypeFilterOption { Name = group.Key, Count = group.Count() });
        }

        StateFilters.Clear();
        StateFilters.Add(new TypeFilterOption { Name = AllKey, Count = _allItems.Count, IsAll = true, AllLabel = "Managed and unmanaged" });
        StateFilters.Add(new TypeFilterOption { Name = "Unmanaged", Count = _allItems.Count(i => !i.IsManaged) });
        StateFilters.Add(new TypeFilterOption { Name = "Managed", Count = _allItems.Count(i => i.IsManaged) });

        LayerFilters.Clear();
        LayerFilters.Add(new TypeFilterOption { Name = AllKey, Count = _allItems.Count, IsAll = true, AllLabel = "Any / not checked" });
        LayerFilters.Add(new TypeFilterOption { Name = "Unmanaged layer", Count = _allItems.Count(i => i.HasUnmanagedLayer == true) });
        LayerFilters.Add(new TypeFilterOption { Name = "No unmanaged layer", Count = _allItems.Count(i => i.HasUnmanagedLayer == false) });
        LayerFilters.Add(new TypeFilterOption { Name = "Not checked", Count = _allItems.Count(i => i.HasUnmanagedLayer is null) });

        _selectedTypeFilter = TypeFilters.FirstOrDefault(f => f.Name == previousType) ?? TypeFilters[0];
        _selectedStateFilter = StateFilters.FirstOrDefault(f => f.Name == previousState) ?? StateFilters[0];
        _selectedLayerFilter = LayerFilters.FirstOrDefault(f => f.Name == previousLayer) ?? LayerFilters[0];
        OnPropertyChanged(nameof(SelectedTypeFilter));
        OnPropertyChanged(nameof(SelectedStateFilter));
        OnPropertyChanged(nameof(SelectedLayerFilter));

        RebuildSubTypeFilters();

        _suppressFilterRefresh = false;
    }

    private void RebuildSubTypeFilters()
    {
        var wasSuppressed = _suppressFilterRefresh;
        _suppressFilterRefresh = true;

        var previous = SelectedSubTypeFilter is { IsAll: false } f ? f.Name : null;
        var typeFilter = SelectedTypeFilter;

        // Only the sub types belonging to the selected type are offered.
        var scope = typeFilter is { IsAll: false }
            ? _allItems.Where(i => string.Equals(i.ComponentTypeName, typeFilter.Name, StringComparison.Ordinal))
            : _allItems;

        var withSubType = scope.Where(i => !string.IsNullOrWhiteSpace(i.SubType)).ToList();

        SubTypeFilters.Clear();
        SubTypeFilters.Add(new TypeFilterOption
        {
            Name = AllKey,
            Count = withSubType.Count,
            IsAll = true,
            AllLabel = "All sub types"
        });

        foreach (var group in withSubType.GroupBy(i => i.SubType!)
                                         .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            SubTypeFilters.Add(new TypeFilterOption { Name = group.Key, Count = group.Count() });
        }

        _selectedSubTypeFilter = SubTypeFilters.FirstOrDefault(o => o.Name == previous) ?? SubTypeFilters[0];
        OnPropertyChanged(nameof(SelectedSubTypeFilter));
        OnPropertyChanged(nameof(HasSubTypes));

        _suppressFilterRefresh = wasSuppressed;
    }

    private void ClearFilters()
    {
        _suppressFilterRefresh = true;

        if (TypeFilters.Count > 0) _selectedTypeFilter = TypeFilters[0];
        if (StateFilters.Count > 0) _selectedStateFilter = StateFilters[0];
        if (LayerFilters.Count > 0) _selectedLayerFilter = LayerFilters[0];
        OnPropertyChanged(nameof(SelectedTypeFilter));
        OnPropertyChanged(nameof(SelectedStateFilter));
        OnPropertyChanged(nameof(SelectedLayerFilter));

        RebuildSubTypeFilters();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        _favouritesOnly = false;
        OnPropertyChanged(nameof(FavouritesOnly));

        _suppressFilterRefresh = false;
        ApplyFilter();
    }

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

        ExportCsvCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private bool FilterItem(object obj)
    {
        if (obj is not SolutionComponentItem item) return false;

        if (FavouritesOnly && !item.IsFavourite) return false;

        if (SelectedTypeFilter is { IsAll: false } type &&
            !string.Equals(item.ComponentTypeName, type.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (SelectedSubTypeFilter is { IsAll: false } subType &&
            !string.Equals(item.SubType, subType.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (SelectedStateFilter is { IsAll: false } state &&
            !string.Equals(item.ManagedLabel, state.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (SelectedLayerFilter is { IsAll: false } layer)
        {
            var matches = layer.Name switch
            {
                "Unmanaged layer" => item.HasUnmanagedLayer == true,
                "No unmanaged layer" => item.HasUnmanagedLayer == false,
                "Not checked" => item.HasUnmanagedLayer is null,
                _ => true
            };

            if (!matches) return false;
        }

        return _searchTerms.All(term => item.SearchIndex.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>
    /// Space-separated keywords, every one of which must appear in the solution's display name,
    /// unique name, publisher or version - the same rule as the object search.
    /// </summary>
    private bool FilterSolution(object obj)
    {
        if (obj is not SolutionInfo solution) return false;

        var text = _solutionSearchText.Trim();

        // The box showing the loaded solution's own name is not a search: list everything.
        if (text.Length == 0 ||
            (SelectedSolution is not null &&
             string.Equals(text, SelectedSolution.DisplayLabel.Trim(), StringComparison.CurrentCultureIgnoreCase)))
        {
            return true;
        }

        var haystack = $"{solution.FriendlyName} {solution.UniqueName} {solution.PublisherName} {solution.Version}";

        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool FilterTypeOption(object obj)
    {
        if (string.IsNullOrWhiteSpace(_typeFilterSearchText)) return true;

        if (SelectedTypeFilter != null && 
            string.Equals(_typeFilterSearchText, SelectedTypeFilter.Label, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (obj is not TypeFilterOption option) return false;
        
        // Always show the "All types" catch-all option regardless of search text
        if (option.IsAll) return true;

        return option.Label.Contains(_typeFilterSearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    // ---------------------------------------------------------------- unmanaged layers

    /// <summary>
    /// Above this many uncheck objects, the user is asked to confirm - one Dataverse call per
    /// object adds up fast, and this is far more likely to be run against a large, unfiltered
    /// result set than the per-object Details view lookup is.
    /// </summary>
    private const int UnmanagedLayerCheckWarningThreshold = 300;

    /// <summary>
    /// Checks every object currently on screen (filters and all) that hasn't been checked yet.
    /// Opt-in and scoped to the visible rows, not the whole solution - see the class remarks on
    /// <see cref="DataverseClient.GetComponentLayersBulkAsync"/> for why this can't be part of the
    /// bulk object read.
    /// </summary>
    private async Task CheckUnmanagedLayersAsync(IReadOnlyList<SolutionComponentItem>? only = null)
    {
        if (_client is null) return;

        var targets = (only ?? ItemsView.Cast<SolutionComponentItem>())
            .Where(i => i.HasUnmanagedLayer is null && i.ObjectId != Guid.Empty)
            .ToList();

        if (targets.Count == 0)
        {
            Status = "Every object on screen has already been checked for an unmanaged layer.";
            return;
        }

        if (!ConfirmLayerCheck(targets.Count)) return;

        var superseded = _layerCts;
        var cts = _layerCts = new CancellationTokenSource();
        if (superseded is not null) await superseded.CancelAsync();

        try
        {
            IsCheckingLayers = true;
            var total = targets.Count;
            var progress = new Progress<int>(count => Status = $"Checking for unmanaged layers... {count:N0}/{total:N0}");

            var layers = await _client.GetComponentLayersBulkAsync(
                targets.Select(t => (t.ObjectId, t.ComponentType)).ToList(), progress, cts.Token);

            if (cts.IsCancellationRequested) return;

            var (unsupported, failed) = RecordLayerChecks(targets, layers);

            RebuildFilters();
            ApplyFilter();

            var checkedCount = targets.Count - unsupported - failed;
            var notes = new List<string>();
            if (unsupported > 0) notes.Add($"{unsupported:N0} of this type aren't supported by the layers check");
            if (failed > 0) notes.Add($"{failed:N0} could not be read - run the check again to retry them");

            Status = $"Checked {checkedCount:N0} object(s) for an unmanaged layer" +
                     (notes.Count == 0 ? "." : $" ({string.Join("; ", notes)}).");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(cts, _layerCts)) Status = "Layer check stopped.";
        }
        catch (Exception ex)
        {
            Status = "Unmanaged layer check failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _layerCts)) IsCheckingLayers = false;
        }
    }

    /// <summary>Past the warning threshold, asks before sending one request per object.</summary>
    private static bool ConfirmLayerCheck(int count)
    {
        if (count <= UnmanagedLayerCheckWarningThreshold) return true;

        var proceed = MessageBox.Show(
            $"This checks {count:N0} objects individually against Dataverse - one request each. " +
            "Narrowing the filters first is faster and gentler on the environment. Continue anyway?",
            MessageCaption, MessageBoxButton.YesNo, MessageBoxImage.Warning);

        return proceed == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Marks each checked object with whether it has an unmanaged layer, and counts those of a type
    /// the layers check does not support and those whose layers could not be read.
    /// </summary>
    private (int Unsupported, int Failed) RecordLayerChecks(
        List<SolutionComponentItem> targets, IReadOnlyDictionary<Guid, IReadOnlyList<ComponentLayer>?> layers)
    {
        var unsupported = 0;
        var failed = 0;
        foreach (var item in targets)
        {
            if (!layers.TryGetValue(item.ObjectId, out var componentLayers))
            {
                failed++;
                continue;
            }

            if (componentLayers is null)
            {
                unsupported++;
                continue;
            }

            item.HasUnmanagedLayer = _layerChecks[item.ObjectId] = componentLayers.Any(l => l.IsUnmanagedLayer);
        }

        return (unsupported, failed);
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

    private void ExportCsv(IReadOnlyList<SolutionComponentItem>? only = null)
    {
        var solution = SelectedSolution?.UniqueName ?? "objects";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"{Title}-{solution}.csv".Replace(' ', '-')
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // Exports what is on screen, filters and all - not the whole solution - or just the selection.
            var rows = only ?? ItemsView.Cast<SolutionComponentItem>().ToList();
            CsvExporter.Write(dialog.FileName, rows);
            Status = $"Exported {rows.Count:N0} objects to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, MessageCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowDetails(SolutionComponentItem? item, DetailsShortcut? openOn = null)
    {
        if (item is null || _client is null) return;

        var viewModel = new ObjectDetailsViewModel(_client, item, KnownItems(), _linkBuilder?.EnvironmentId, OpenUrl, this, openOn);
        var window = Track(new Views.ObjectDetailsWindow
        {
            DataContext = viewModel,
            Owner = Application.Current.MainWindow
        });

        // Several can be open at once; each steps on from the last rather than covering it.
        DetailsWindows.Show(window);

        if (item.ObjectId != Guid.Empty)
        {
            Library.AddRecent(EnvironmentUrl, new RecentObject
            {
                ObjectId = item.ObjectId,
                Label = item.PrimaryLabel,
                TypeName = item.ComponentTypeName,
                OpenedAt = DateTimeOffset.Now
            });
            RefreshLibrary();
        }
        _ = viewModel.LoadAsync();
    }

    /// <summary>Dependencies come back as bare ids; the loaded list turns most of them into names.</summary>
    private Dictionary<Guid, SolutionComponentItem> KnownItems()
    {
        var known = new Dictionary<Guid, SolutionComponentItem>();
        foreach (var loaded in _allItems)
        {
            if (loaded.ObjectId != Guid.Empty) known[loaded.ObjectId] = loaded;
        }

        return known;
    }

    private RelayCommand? _exploreDependenciesCommand;
    /// <summary>The selected object's dependency tree, from the palette rather than its details window.</summary>
    public RelayCommand ExploreDependenciesCommand => _exploreDependenciesCommand ??= new RelayCommand(_ =>
    {
        if (SelectedItem is not { } item || _client is null) return;

        new Views.DependencyExplorerWindow
        {
            DataContext = new DependencyExplorerViewModel(_client, item, KnownItems(), i => OpenDetails(i), this),
            Owner = Application.Current.MainWindow
        }.Show();
    }, _ => SelectedItem is not null && IsConnected);

    // ---------------------------------------------------------------- browser profile

    public RelayCommand SetBrowserProfileCommand { get; }

    /// <summary>The profile this environment's links and sign-ins open in; null for the default browser.</summary>
    public BrowserProfileSetting? BrowserProfile => _settings.GetBrowserProfile(EnvironmentUrl);

    /// <summary>The choices in the sidebar's "Open links in" menu, the current one ticked.</summary>
    public IReadOnlyList<BrowserProfileOption> BrowserProfileOptions =>
        BrowserProfileOption.Build(BrowserProfiles.Discover(), BrowserProfile);

    /// <summary>"Links open in Edge · Work" for the tooltip; empty for the default browser.</summary>
    public string BrowserProfileDescription => BrowserProfile is { } chosen
        ? "Links and sign-ins open in " +
          (BrowserProfiles.Discover().FirstOrDefault(chosen.Matches)?.Label ??
           $"{chosen.Browser} profile '{chosen.ProfileDirectory}' (not found on this machine - using the default browser)")
        : string.Empty;

    /// <summary>"Edge · Work", or "the default browser" - for the account menu.</summary>
    public string BrowserProfileLabel => BrowserProfile is { } chosen
        ? BrowserProfiles.Discover().FirstOrDefault(chosen.Matches)?.Label ?? $"{chosen.Browser} '{chosen.ProfileDirectory}' (not found)"
        : "the default browser";

    private void SetBrowserProfile(BrowserProfileOption? option)
    {
        if (option is null || string.IsNullOrWhiteSpace(EnvironmentUrl)) return;

        _settings.SetBrowserProfile(EnvironmentUrl, option.Setting);
        _settings.Save();
        RaiseBrowserProfile();
    }

    /// <summary>Re-reads the choice - after it changes, or the menu opens and profiles may have come and gone.</summary>
    public void RaiseBrowserProfile()
    {
        OnPropertyChanged(nameof(BrowserProfile));
        OnPropertyChanged(nameof(BrowserProfileOptions));
        OnPropertyChanged(nameof(BrowserProfileDescription));
        OnPropertyChanged(nameof(BrowserProfileLabel));
    }

    /// <summary>An auth context whose sign-in pages open in this environment's browser profile.</summary>
    private EnvironmentAuthContext NewAuthContext(string? accountId = null) => _newAuthContext?.Invoke() ?? new(_auth, null, accountId)
    {
        OpenBrowser = uri =>
        {
            LinkLauncher.Open(uri.AbsoluteUri, BrowserProfile);
            return Task.CompletedTask;
        }
    };

    /// <summary>Opens a link in this environment's browser profile, or the default browser.</summary>
    public void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            LinkLauncher.Open(url, BrowserProfile);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the link:\n{url}\n\n{ex.Message}",
                MessageCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyToClipboard(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            Status = $"Nothing to copy - this object has no {what}.";
            return;
        }

        Status = ClipboardText.TryCopy(text, out var failure)
            ? $"Copied {what}: {text}"
            : $"Could not copy the {what} - the clipboard is held by another application ({failure}).";
    }

    public void Dispose()
    {
        CancelBackgroundWork();
        _searchDebounce.Stop();
        _insightDebounce.Stop();
        CloseToolWindows();
        _client?.Dispose();
    }
}
