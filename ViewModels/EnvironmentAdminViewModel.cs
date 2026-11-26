using System.Collections.ObjectModel;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Graph;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>The Environment admin window's tabs.</summary>
public enum AdminTab
{
    Users,
    Teams,
    Roles,
    Mailboxes,
    Queues
}

public enum UserStatusFilter { All, Enabled, Disabled }
public enum RoleManagedFilter { All, Managed, Unmanaged }
public enum MailboxOwnerFilter { All, Users, Queues, Other }
public enum QueueTypeFilter { All, Public, Private }
public enum TeamTypeFilter { All, Owner, Access, Entra }

/// <summary>What the selected user's pane lists below their properties.</summary>
public enum UserDetailView { Roles, Teams, FieldSecurity, Licenses, Groups }

/// <summary>What the selected team's pane lists below its properties.</summary>
public enum TeamDetailView { Members, Roles, FieldSecurity }

/// <summary>What the selected role's pane shows.</summary>
public enum RoleDetailView { Privileges, Other, HeldBy }

/// <summary>A place in the window: a tab and the row selected on it.</summary>
public sealed record AdminLocation(AdminTab Tab, Guid? Id, string? Label)
{
    public string Description => Label is { Length: > 0 } label ? $"{TabName(Tab)}: {label}" : TabName(Tab);

    internal static string TabName(AdminTab tab) => tab switch
    {
        AdminTab.Roles => "Security roles",
        _ => tab.ToString()
    };
}

/// <summary>
/// Users, teams, security roles, mailboxes and queues in one environment. Read-only: each tab
/// reads the first time it is opened, and links between them - a user's role, a team's members,
/// a queue's mailbox - move to the other tab with that row selected.
/// </summary>
public sealed class EnvironmentAdminViewModel : ObservableObject, IDisposable
{
    /// <summary>Loads started by selections and links, which nothing waits for - except tests.</summary>
    internal BackgroundWork Work { get; } = new();

    private readonly Func<GraphClient> _createGraph;
    private GraphClient? _graph;

    /// <param name="graph">For tests: the Graph client to read licences with, rather than the tab's.</param>
    public EnvironmentAdminViewModel(EnvironmentSessionViewModel session, DataverseClient client, AdminTab tab = AdminTab.Users,
        Func<GraphClient>? graph = null)
    {
        Session = session;
        _createGraph = graph ?? session.CreateGraphClient;
        Users = new AdminUsersViewModel(this, client);
        Teams = new AdminTeamsViewModel(this, client);
        Roles = new AdminRolesViewModel(this, client);
        Mailboxes = new AdminMailboxesViewModel(this, client);
        Queues = new AdminQueuesViewModel(this, client);
        _selectedTab = tab;

        BackCommand = new AsyncRelayCommand(_ => GoAsync(_back, _forward), _ => CanGoBack);
        ForwardCommand = new AsyncRelayCommand(_ => GoAsync(_forward, _back), _ => CanGoForward);
    }

    // ---------------------------------------------------------------- back and forward

    private readonly Stack<AdminLocation> _back = new();
    private readonly Stack<AdminLocation> _forward = new();

    /// <summary>Back to where the last link was followed from - Alt+Left, or the mouse's back button.</summary>
    public AsyncRelayCommand BackCommand { get; }
    public AsyncRelayCommand ForwardCommand { get; }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public string BackToolTip => _back.TryPeek(out var to) ? $"Back (Alt+Left) — to {to.Description}" : "Back (Alt+Left)";
    public string ForwardToolTip => _forward.TryPeek(out var to) ? $"Forward (Alt+Right) — to {to.Description}" : "Forward (Alt+Right)";

    /// <summary>Where the window is now: the tab on show and its selected row.</summary>
    public AdminLocation Here
    {
        get
        {
            var pane = Pane(SelectedTab);
            return new AdminLocation(SelectedTab, pane.SelectedId, pane.SelectedLabel);
        }
    }

    private async Task GoAsync(Stack<AdminLocation> from, Stack<AdminLocation> to)
    {
        if (!from.TryPop(out var target)) return;

        to.Push(Here);
        RaiseHistory();
        await MoveToAsync(target);
    }

    private async Task MoveToAsync(AdminLocation location)
    {
        SelectedTab = location.Tab;
        var pane = Pane(location.Tab);
        await pane.EnsureLoadedAsync();
        if (location.Id is { } id) pane.SelectById(id);
    }

    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(BackToolTip));
        OnPropertyChanged(nameof(ForwardToolTip));
        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
    }

    public EnvironmentSessionViewModel Session { get; }
    public string Title => $"Environment admin — {Session.Title}";

    /// <summary>The tab showing - users, teams, roles, mailboxes or queues - as the filters leave it, to a CSV file.</summary>
    public RelayCommand ExportCsvCommand => _exportCsvCommand ??= new RelayCommand(_ => ExportCsv());
    private RelayCommand? _exportCsvCommand;

    /// <summary>Asks where to save; replaced in tests. Null when cancelled.</summary>
    internal Func<string, string?> ChooseFile { get; set; } = suggested =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = suggested };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    private void ExportCsv()
    {
        var pane = Pane(SelectedTab);
        var lines = pane.CsvLines();
        if (ChooseFile($"{pane.ExportName}-{Session.EnvironmentHost}.csv") is not { } path) return;

        try
        {
            CsvExporter.WriteLines(path, lines);
            pane.ReportExport($"Exported {lines.Count - 1:N0} row(s) to {path}.");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            pane.ReportExport("Could not export - " + ex.Message);
        }
    }

    public AdminUsersViewModel Users { get; }
    public AdminTeamsViewModel Teams { get; }
    public AdminRolesViewModel Roles { get; }
    public AdminMailboxesViewModel Mailboxes { get; }
    public AdminQueuesViewModel Queues { get; }

    private AdminTab _selectedTab;
    public AdminTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value)) Work.Track(Pane(value).EnsureLoadedAsync());
        }
    }

    public AdminPaneViewModel Pane(AdminTab tab) => tab switch
    {
        AdminTab.Users => Users,
        AdminTab.Teams => Teams,
        AdminTab.Roles => Roles,
        AdminTab.Mailboxes => Mailboxes,
        _ => Queues
    };

    /// <summary>Reads the tab on show - called when the window opens.</summary>
    public Task LoadAsync() => Pane(SelectedTab).EnsureLoadedAsync();

    /// <summary>Microsoft Graph, in the environment's tenant - made the first time a user's licences or groups are asked for.</summary>
    internal GraphClient Graph => _graph ??= _createGraph();

    public void Dispose()
    {
        _graph?.Dispose();
        _graph = null;
    }

    /// <summary>Opens on one row, as the window's first place - nothing to go back to.</summary>
    internal Task OpenAtAsync(AdminTab tab, Guid id) => MoveToAsync(new AdminLocation(tab, id, null));

    /// <summary>
    /// Follows a link to another tab's row, reading the tab first if it has not been. Where it
    /// came from goes on the Back stack; a manual tab click does not.
    /// </summary>
    internal async Task ShowAsync(AdminTab tab, Guid id)
    {
        _back.Push(Here);
        _forward.Clear();
        RaiseHistory();
        await MoveToAsync(new AdminLocation(tab, id, null));
    }
}

/// <summary>What every tab has: a list read once, a search box, a busy flag and a status line.</summary>
public abstract class AdminPaneViewModel : ObservableObject
{
    private Task? _load;

    protected AdminPaneViewModel(EnvironmentAdminViewModel owner, DataverseClient client)
    {
        Owner = owner;
        Client = client;
        RefreshCommand = new AsyncRelayCommand(_ => ReloadAsync(), _ => !IsBusy);
    }

    protected EnvironmentAdminViewModel Owner { get; }
    protected DataverseClient Client { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>The file name the tab's export suggests, without the extension.</summary>
    public abstract string ExportName { get; }

    /// <summary>The rows the filters leave on show, as CSV lines under a header.</summary>
    public abstract IReadOnlyList<string> CsvLines();

    /// <summary>Says how the export went, where the status bar says what was read.</summary>
    internal void ReportExport(string message) => Status = message;

    protected static string? Date(DateTimeOffset? when) => when?.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (SetProperty(ref _isBusy, value)) RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = string.Empty;
    /// <summary>The status bar's left: what was read, and how long it took - or why it could not be.</summary>
    public string Status
    {
        get => _status;
        protected set => SetProperty(ref _status, value);
    }

    private string _showingSummary = string.Empty;
    /// <summary>The status bar's right: what the filters leave on show - "Showing 287 enabled".</summary>
    public string ShowingSummary
    {
        get => _showingSummary;
        protected set => SetProperty(ref _showingSummary, value);
    }

    /// <summary>"Showing 287 users", or "Showing 12 of 287 users" when the search or a filter narrows it.</summary>
    internal static string Showing(int shown, int of, string noun) =>
        shown == of ? $"Showing {shown:N0} {noun}" : $"Showing {shown:N0} of {of:N0} {noun}";

    /// <summary>"Read 312 users in 1.4 s".</summary>
    protected static string ReadSummary(int count, string noun, TimeSpan took) =>
        $"Read {count:N0} {noun} in {took.TotalSeconds:0.0} s";

    /// <summary>The selected row's id and name, for Back and Forward.</summary>
    public abstract Guid? SelectedId { get; }
    public abstract string? SelectedLabel { get; }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyFilter();
        }
    }

    /// <summary>Reads the list the first time only; Refresh reads it again.</summary>
    public Task EnsureLoadedAsync() => _load ??= ReloadAsync();

    public async Task ReloadAsync()
    {
        IsBusy = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await LoadCoreAsync();
            Status = ReadSummary(Count, Noun, clock.Elapsed);
        }
        catch (Exception ex)
        {
            Status = "Could not read - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected abstract Task LoadCoreAsync();
    protected abstract void ApplyFilter();

    /// <summary>How many were read, and what they are called - "users", "roles".</summary>
    protected abstract int Count { get; }
    protected abstract string Noun { get; }

    /// <summary>Selects the row with this id, clearing the filters if they hide it.</summary>
    public abstract void SelectById(Guid id);

    internal static bool Matches(string haystack, string search) =>
        string.IsNullOrWhiteSpace(search) ||
        Array.TrueForAll(
            search.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            term => haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>"All business units" first, then each value present, A to Z.</summary>
    internal static IReadOnlyList<string> Options(string all, IEnumerable<string?> values) =>
        new[] { all }
            .Concat(values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase))
            .ToList();

    /// <summary>Bumped per selection, so only the latest detail read writes its result.</summary>
    protected int DetailRequest;
}

// ==================================================================== users

public sealed class AdminUsersViewModel : AdminPaneViewModel
{
    public override string ExportName => "users";

    public override IReadOnlyList<string> CsvLines() =>
        View.Cast<UserInfo>()
            .Select(u => CsvExporter.Line(u.FullName, u.DomainName, u.Email, u.UserType, u.BusinessUnit, u.Title, u.StatusLabel,
                u.LicenseType, EntraLicenseNames(u), Date(u.CreatedOn), u.AadObjectId?.ToString(), u.SystemUserId.ToString()))
            .Prepend(CsvExporter.Line("Name", "User name", "Email", "Type", "Business unit", "Title", "Status",
                "Licence", "Entra licences", "Created", "Entra object id", "User id"))
            .ToList();

    /// <summary>The user's licences in Entra, once the licence filter has read them - empty until then.</summary>
    private string? EntraLicenseNames(UserInfo u) =>
        _entraLicenses?.TryGetValue(u.SystemUserId, out var held) == true
            ? string.Join("; ", held.Select(l => l.Name).Order(StringComparer.OrdinalIgnoreCase))
            : null;

    public const string AllTypes = "All user types";
    public const string AllUnits = "All business units";

    public AdminUsersViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is UserInfo u && Include(u);
        ShowRoleCommand = new RelayCommand(p => Owner.Work.Track(ShowRoleAsync(p as RoleAssignment)), p => p is RoleAssignment);
        ShowMailboxCommand = new RelayCommand(_ => Owner.Work.Track(ShowMailboxAsync()), _ => SelectedUser?.DefaultMailboxId is not null);
        ShowTeamCommand = new RelayCommand(p => Owner.Work.Track(ShowTeamAsync(p as TeamInfo)), p => p is TeamInfo);
        OwnedRecordsCommand = new RelayCommand(
            _ => { if (SelectedUser is { } user) Owner.Session.ShowOwnedRecords(user.SystemUserId, user.FullName, isTeam: false); },
            _ => SelectedUser is not null);
        EntraTeamSyncCommand = new RelayCommand(
            p => { if (p is TeamInfo { IsEntraGroupTeam: true } team) Owner.Session.ShowEntraTeamSync(team.TeamId); },
            p => p is TeamInfo { IsEntraGroupTeam: true });
        EffectiveAccessCommand = new RelayCommand(
            _ => { if (SelectedUser is { } user) Owner.Session.OpenSecurityLookup(user); },
            _ => SelectedUser is not null);
        ClearLicensesCommand = new RelayCommand(_ => ClearLicenseChoices());
    }

    /// <summary>
    /// Security lookup's effective access for the selected user: per table, the widest depth any
    /// of their roles grants, directly or through a team.
    /// </summary>
    public RelayCommand EffectiveAccessCommand { get; }

    /// <summary>A group team the user is in, opened in Entra team sync - which writes, so it asks there first.</summary>
    public RelayCommand EntraTeamSyncCommand { get; }

    public ObservableCollection<UserInfo> Items { get; } = new();
    public ListCollectionView View { get; }

    public RelayCommand ShowRoleCommand { get; }
    public RelayCommand ShowMailboxCommand { get; }
    public RelayCommand ShowTeamCommand { get; }

    /// <summary>The records the selected user owns themselves, counted table by table in a window of their own.</summary>
    public RelayCommand OwnedRecordsCommand { get; }

    private IReadOnlyList<string> _typeOptions = [AllTypes];
    public IReadOnlyList<string> TypeOptions { get => _typeOptions; private set => SetProperty(ref _typeOptions, value); }

    private IReadOnlyList<string> _unitOptions = [AllUnits];
    public IReadOnlyList<string> UnitOptions { get => _unitOptions; private set => SetProperty(ref _unitOptions, value); }

    private string _selectedType = AllTypes;
    public string SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetProperty(ref _selectedType, value ?? AllTypes)) ApplyFilter();
        }
    }

    private string _selectedUnit = AllUnits;
    public string SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (SetProperty(ref _selectedUnit, value ?? AllUnits)) ApplyFilter();
        }
    }

    private UserStatusFilter _statusFilter = UserStatusFilter.Enabled;
    /// <summary>Enabled users by default - most environments carry years of disabled ones.</summary>
    public UserStatusFilter StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetProperty(ref _statusFilter, value)) ApplyFilter();
        }
    }

    // ---------------------------------------------------------------- the licence filter

    /// <summary>
    /// Each licence the environment's people hold in Entra, with how many hold it. Ticking some
    /// leaves the people who hold none of them - Dataverse's own licence flag counts free and
    /// Microsoft 365 plans, so it cannot say who lacks a Power Apps or Dynamics 365 licence.
    /// </summary>
    public ObservableCollection<LicenseChoice> LicenseChoices { get; } = new();

    /// <summary>Every person's licences in Entra, by user id; null until the licence filter is first opened.</summary>
    private Dictionary<Guid, IReadOnlyList<EntraLicense>>? _entraLicenses;
    private readonly HashSet<string> _chosenSkus = new(StringComparer.OrdinalIgnoreCase);
    private bool _entraLicensesAsked;
    private int _entraLicenseRequest;
    private bool _choosingInBulk;

    public bool IsLicenseFilterOn => _chosenSkus.Count > 0;

    /// <summary>The filter button's value: "Any", "Without POWERAPPS_PER_USER", or "None of 3".</summary>
    public string LicenseFilterLabel => _chosenSkus.Count switch
    {
        0 => "Any",
        1 => "Without " + (LicenseChoices.FirstOrDefault(c => c.IsChosen)?.Name ?? "1 licence"),
        var n => $"None of {n}"
    };

    private bool _isLicensePickerOpen;
    /// <summary>The licence filter's list is open. The first time, every person's licences are read from Entra.</summary>
    public bool IsLicensePickerOpen
    {
        get => _isLicensePickerOpen;
        set
        {
            if (SetProperty(ref _isLicensePickerOpen, value) && value) EnsureEntraLicenses();
        }
    }

    private bool _isReadingLicenses;
    public bool IsReadingLicenses { get => _isReadingLicenses; private set => SetProperty(ref _isReadingLicenses, value); }

    private string _licenseReadStatus = string.Empty;
    /// <summary>Under the licence list: how many were read, how far it has got, or why it could not.</summary>
    public string LicenseReadStatus { get => _licenseReadStatus; private set => SetProperty(ref _licenseReadStatus, value); }

    public RelayCommand ClearLicensesCommand { get; }

    /// <summary>Reads every person's licences, once - a failed read is tried again the next time the list opens.</summary>
    private void EnsureEntraLicenses()
    {
        if (_entraLicensesAsked || Items.Count == 0) return;

        _entraLicensesAsked = true;
        Owner.Work.Track(LoadEntraLicensesAsync(++_entraLicenseRequest));
    }

    private async Task LoadEntraLicensesAsync(int request)
    {
        var people = Items.Where(u => !u.IsApplicationUser && u.AadObjectId is not null).ToList();
        var noObjectId = Items.Count(u => !u.IsApplicationUser && u.AadObjectId is null);

        IsReadingLicenses = true;
        LicenseReadStatus = "Reading licences from Entra…";
        var progress = new Progress<int>(n =>
        {
            if (request == _entraLicenseRequest && IsReadingLicenses)
                LicenseReadStatus = $"Reading licences from Entra… {n:N0} of {people.Count:N0} users";
        });

        try
        {
            var read = await Owner.Graph.GetUsersLicensesAsync(people.Select(u => u.AadObjectId!.Value.ToString()).ToList(), progress);
            if (request != _entraLicenseRequest) return;

            _entraLicenses = people
                .Where(u => read.ContainsKey(u.AadObjectId!.Value.ToString()))
                .ToDictionary(u => u.SystemUserId, u => read[u.AadObjectId!.Value.ToString()]);

            // Ticks survive a refresh, for the licences still held.
            LicenseChoices.Clear();
            foreach (var sku in _entraLicenses.Values.SelectMany(held => held.DistinctBy(l => l.SkuId))
                         .GroupBy(l => l.SkuId, StringComparer.OrdinalIgnoreCase)
                         .Select(g => (Id: g.Key, g.First().Name, Holders: g.Count()))
                         .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                LicenseChoices.Add(new LicenseChoice(sku.Id, sku.Name, sku.Holders, _chosenSkus.Contains(sku.Id), OnLicenseChoiceChanged));
            }
            _chosenSkus.IntersectWith(LicenseChoices.Where(c => c.IsChosen).Select(c => c.SkuId));

            var unread = people.Count - _entraLicenses.Count;
            LicenseReadStatus = $"Read {_entraLicenses.Count:N0} users' licences from Entra." +
                (unread > 0 ? $" {unread:N0} could not be read." : string.Empty) +
                (noObjectId > 0 ? $" {noObjectId:N0} {(noObjectId == 1 ? "has" : "have")} no Entra object id." : string.Empty) +
                (unread + noObjectId > 0 ? " Those are never listed as holding none." : string.Empty);
        }
        catch (Exception ex)
        {
            if (request != _entraLicenseRequest) return;
            _entraLicensesAsked = false;
            LicenseReadStatus = "Could not read licences from Entra - " + ex.Message;
        }
        finally
        {
            if (request == _entraLicenseRequest) IsReadingLicenses = false;
        }

        LicenseFilterChanged();
    }

    private void OnLicenseChoiceChanged()
    {
        if (_choosingInBulk) return;

        _chosenSkus.Clear();
        _chosenSkus.UnionWith(LicenseChoices.Where(c => c.IsChosen).Select(c => c.SkuId));
        LicenseFilterChanged();
    }

    private void ClearLicenseChoices()
    {
        _choosingInBulk = true;
        foreach (var choice in LicenseChoices) choice.IsChosen = false;
        _choosingInBulk = false;

        _chosenSkus.Clear();
        LicenseFilterChanged();
    }

    private void LicenseFilterChanged()
    {
        OnPropertyChanged(nameof(IsLicenseFilterOn));
        OnPropertyChanged(nameof(LicenseFilterLabel));
        ApplyFilter();
    }

    public int CountAll => Items.Count;
    public int CountEnabled => Items.Count(u => !u.IsDisabled);
    public int CountDisabled => Items.Count(u => u.IsDisabled);

    private bool Include(UserInfo u) => InStatus(u) && MatchesOtherFilters(u);

    private bool InStatus(UserInfo u) =>
        StatusFilter switch
        {
            UserStatusFilter.Enabled => !u.IsDisabled,
            UserStatusFilter.Disabled => u.IsDisabled,
            _ => true
        };

    private bool MatchesOtherFilters(UserInfo u) =>
        (SelectedType == AllTypes || string.Equals(u.UserType, SelectedType, StringComparison.CurrentCultureIgnoreCase)) &&
        (SelectedUnit == AllUnits || string.Equals(u.BusinessUnit, SelectedUnit, StringComparison.CurrentCultureIgnoreCase)) &&
        InLicense(u) &&
        Matches(u.SearchText, SearchText);

    /// <summary>
    /// With licences chosen, only people Entra says hold none of them. Application users need no
    /// licence, and a user whose licences could not be read is never counted as holding none.
    /// </summary>
    private bool InLicense(UserInfo u)
    {
        if (!IsLicenseFilterOn) return true;
        if (u.IsApplicationUser || _entraLicenses?.TryGetValue(u.SystemUserId, out var held) != true) return false;

        return !held.Any(l => _chosenSkus.Contains(l.SkuId));
    }

    protected override int Count => Items.Count;
    protected override string Noun => "users";
    public override Guid? SelectedId => SelectedUser?.SystemUserId;
    public override string? SelectedLabel => SelectedUser?.FullName;

    protected override void ApplyFilter()
    {
        View.Refresh();
        var (inStatus, noun) = StatusFilter switch
        {
            UserStatusFilter.Enabled => (CountEnabled, "enabled"),
            UserStatusFilter.Disabled => (CountDisabled, "disabled"),
            _ => (CountAll, "users")
        };
        ShowingSummary = Showing(View.Count, inStatus, noun) + HiddenByStatus();
    }

    /// <summary>
    /// " - 1 disabled also matches" when a search or the licence filter finds users the status
    /// filter hides: a search for someone's object id that shows nothing otherwise looks like they
    /// are not in the environment, and most unlicensed users are disabled.
    /// </summary>
    private string HiddenByStatus()
    {
        if (StatusFilter == UserStatusFilter.All || (string.IsNullOrWhiteSpace(SearchText) && !IsLicenseFilterOn))
            return string.Empty;

        var hidden = Items.Count(u => !InStatus(u) && MatchesOtherFilters(u));
        if (hidden == 0) return string.Empty;

        var other = StatusFilter == UserStatusFilter.Enabled ? "disabled" : "enabled";
        return $" - {hidden:N0} {other} also match{(hidden == 1 ? "es" : string.Empty)}";
    }

    private UserDetailView _detailView;
    /// <summary>Security roles, Teams, Field security, Licences or Entra groups, below the user's properties.</summary>
    public UserDetailView DetailView
    {
        get => _detailView;
        set
        {
            if (!SetProperty(ref _detailView, value)) return;
            OnPropertyChanged(nameof(IsRolesView));
            EnsureLicenses();
            EnsureGroups();
        }
    }

    public bool IsRolesView => DetailView == UserDetailView.Roles;

    protected override async Task LoadCoreAsync()
    {
        Status = "Reading users…";
        var selected = SelectedUser?.SystemUserId;
        var users = await Client.GetUsersAsync();

        Items.Clear();
        foreach (var user in users) Items.Add(user);

        TypeOptions = Options(AllTypes, users.Select(u => u.UserType));
        UnitOptions = Options(AllUnits, users.Select(u => u.BusinessUnit));
        if (!TypeOptions.Contains(SelectedType)) SelectedType = AllTypes;
        if (!UnitOptions.Contains(SelectedUnit)) SelectedUnit = AllUnits;

        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountEnabled));
        OnPropertyChanged(nameof(CountDisabled));
        ApplyFilter();

        // Licences read for the old list are read again for the new one, keeping what was ticked.
        if (_entraLicensesAsked)
        {
            _entraLicensesAsked = false;
            EnsureEntraLicenses();
        }

        if (selected is { } id) SelectedUser = Items.FirstOrDefault(u => u.SystemUserId == id);
    }

    public override void SelectById(Guid id)
    {
        if (Items.FirstOrDefault(u => u.SystemUserId == id) is not { } user) return;

        if (!View.Contains(user))
        {
            SearchText = string.Empty;
            SelectedType = AllTypes;
            SelectedUnit = AllUnits;
            ClearLicenseChoices();
            StatusFilter = UserStatusFilter.All;
        }

        SelectedUser = user;
    }

    // ---------------------------------------------------------------- the selected user

    private UserInfo? _selectedUser;
    public UserInfo? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (!SetProperty(ref _selectedUser, value)) return;
            ShowMailboxCommand.RaiseCanExecuteChanged();
            OwnedRecordsCommand.RaiseCanExecuteChanged();
            EffectiveAccessCommand.RaiseCanExecuteChanged();
            Owner.Work.Track(LoadDetailsAsync(value));
            ShowLicensesFor(value);
            ShowGroupsFor(value);
        }
    }

    public ObservableCollection<TeamInfo> Teams { get; } = new();

    /// <summary>Direct roles first, then each team's, by name.</summary>
    public ObservableCollection<RoleAssignment> RoleAssignments { get; } = new();

    /// <summary>Field security profiles, direct first, then each team's.</summary>
    public ObservableCollection<FieldProfileAssignment> FieldProfiles { get; } = new();

    private string _fieldProfileStatus = string.Empty;
    /// <summary>Why the field security list is empty - none held, or the read failed.</summary>
    public string FieldProfileStatus { get => _fieldProfileStatus; private set => SetProperty(ref _fieldProfileStatus, value); }

    private string _lastSignIn = string.Empty;
    /// <summary>"2026-11-14 09:12", "None recorded", "Not audited" - or a dash for an application user, or when it cannot be read.</summary>
    public string LastSignIn { get => _lastSignIn; private set => SetProperty(ref _lastSignIn, value); }

    private string _lastSignInToolTip = string.Empty;
    /// <summary>Where the sign-in comes from, or why there is none to show.</summary>
    public string LastSignInToolTip { get => _lastSignInToolTip; private set => SetProperty(ref _lastSignInToolTip, value); }

    private MailboxInfo? _mailbox;
    public MailboxInfo? Mailbox { get => _mailbox; private set => SetProperty(ref _mailbox, value); }

    private string _detailStatus = string.Empty;
    public string DetailStatus { get => _detailStatus; private set => SetProperty(ref _detailStatus, value); }

    private bool _isLoadingDetails;
    public bool IsLoadingDetails { get => _isLoadingDetails; private set => SetProperty(ref _isLoadingDetails, value); }

    public int DirectRoleCount => RoleAssignments.Count(r => r.IsDirect);
    public int TeamRoleCount => RoleAssignments.Count(r => !r.IsDirect);

    private async Task LoadDetailsAsync(UserInfo? user)
    {
        var request = ++DetailRequest;
        Teams.Clear();
        RoleAssignments.Clear();
        FieldProfiles.Clear();
        FieldProfileStatus = string.Empty;
        Mailbox = null;
        LastSignIn = string.Empty;
        LastSignInToolTip = string.Empty;
        RaiseRoleCounts();

        if (user is null)
        {
            DetailStatus = string.Empty;
            return;
        }

        _ = Owner.Work.Track(LoadLastSignInAsync(user, request));

        IsLoadingDetails = true;
        DetailStatus = "Reading teams and roles…";

        try
        {
            var teamsTask = Client.GetUserTeamsAsync(user.SystemUserId);
            var directTask = Client.GetUserRolesAsync(user.SystemUserId);
            var mailboxTask = user.DefaultMailboxId is { } mailboxId
                ? Client.GetMailboxAsync(mailboxId)
                : Task.FromResult<MailboxInfo?>(null);

            var teams = await teamsTask;
            var direct = await directTask;

            // Every team's roles, together - a user in a dozen teams should not wait for a dozen round trips in a row.
            var viaTeams = await Task.WhenAll(teams.Select(t => Client.GetTeamRolesAsync(t.TeamId, t.Name)));
            var profiles = await ReadFieldProfilesAsync(user.SystemUserId, teams);
            var mailbox = await ReadMailboxAsync(mailboxTask);

            if (request != DetailRequest) return;

            foreach (var team in teams.OrderBy(t => t.IsDefault).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Teams.Add(team);
            }

            foreach (var role in direct.OrderBy(r => r.RoleName, StringComparer.CurrentCultureIgnoreCase)) RoleAssignments.Add(role);
            foreach (var role in viaTeams.SelectMany(r => r)
                         .OrderBy(r => r.RoleName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.TeamName))
            {
                RoleAssignments.Add(role);
            }

            ShowFieldProfiles(profiles);

            Mailbox = mailbox;
            RaiseRoleCounts();

            DetailStatus = $"{Teams.Count:N0} team(s), {DirectRoleCount:N0} direct role(s), {TeamRoleCount:N0} through teams." +
                           (DirectRoleCount + TeamRoleCount == 0 ? " With no role, this user cannot sign in to apps." : string.Empty);
        }
        catch (Exception ex)
        {
            if (request == DetailRequest) DetailStatus = "Could not read this user's teams and roles - " + ex.Message;
        }
        finally
        {
            if (request == DetailRequest) IsLoadingDetails = false;
        }
    }

    /// <summary>Whether the environment logs sign-ins - read once, for the first user who has none.</summary>
    private Task<bool?>? _userAccessAudited;

    /// <summary>
    /// The user's last sign-in, from the audit log. Read on its own: reading the audit needs a
    /// privilege the roles and teams do not, and its absence should cost only this line.
    /// </summary>
    private async Task LoadLastSignInAsync(UserInfo user, int request)
    {
        if (user.IsApplicationUser)
        {
            LastSignIn = "—";
            LastSignInToolTip = "An application user is a service principal; it never signs in to the apps.";
            return;
        }

        LastSignIn = "…";
        LastSignInToolTip = "Reading the audit log…";

        try
        {
            var when = await Client.GetLastSignInAsync(user.SystemUserId);
            var audited = when is null ? await (_userAccessAudited ??= ReadUserAccessAuditedAsync()) : true;
            if (request != DetailRequest) return;

            (LastSignIn, LastSignInToolTip) = (when, audited) switch
            {
                ({ } at, _) => (at.ToString("yyyy-MM-dd HH:mm"),
                    "The latest User Access via Web entry in the audit log. Access through the Web API - integrations and " +
                    "background jobs running as the user - is not counted."),
                (null, false) => ("Not audited",
                    "This environment does not log user access, so there is no sign-in to show. It is turned on in the " +
                    "Power Platform admin center: Settings, Audit and logs, Audit settings, Log access."),
                _ => ("None recorded",
                    "No User Access via Web entry in the audit log - they have not signed in since user access logging was " +
                    "turned on, or not within the audit retention period.")
            };
        }
        catch (Exception ex)
        {
            if (request != DetailRequest) return;
            LastSignIn = "—";
            LastSignInToolTip = "Could not read the audit log - " + ex.Message;
        }
    }

    private async Task<bool?> ReadUserAccessAuditedAsync()
    {
        try
        {
            return await Client.IsUserAccessAuditedAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Audit settings could not be read", ex);
            return null;
        }
    }

    // ---------------------------------------------------------------- licences

    /// <summary>The user's licences in Entra, by name.</summary>
    public ObservableCollection<EntraLicense> Licenses { get; } = new();

    private string _licenseStatus = string.Empty;
    /// <summary>Why the licence list is empty - still reading, none assigned, no Entra user, or the read failed.</summary>
    public string LicenseStatus { get => _licenseStatus; private set => SetProperty(ref _licenseStatus, value); }

    /// <summary>The user whose licences are on show or being read; null until the Licences view is opened for them.</summary>
    private Guid? _licensesFor;
    private int _licenseRequest;

    /// <summary>Clears the licences for a new selection, and reads theirs at once if the Licences view is open.</summary>
    private void ShowLicensesFor(UserInfo? user)
    {
        _licenseRequest++;
        _licensesFor = null;
        Licenses.Clear();
        LicenseStatus = string.Empty;
        if (user is not null) EnsureLicenses();
    }

    /// <summary>
    /// Licences come from Microsoft Graph, which can ask the user to sign in again for it - so they
    /// are read only once the Licences view is opened, not for every user clicked past.
    /// </summary>
    private void EnsureLicenses()
    {
        if (DetailView != UserDetailView.Licenses || SelectedUser is not { } user || _licensesFor == user.SystemUserId) return;

        _licensesFor = user.SystemUserId;
        Owner.Work.Track(LoadLicensesAsync(user, ++_licenseRequest));
    }

    private async Task LoadLicensesAsync(UserInfo user, int request)
    {
        if (user.AadObjectId is not { } objectId)
        {
            LicenseStatus = user.IsApplicationUser
                ? "An application user is a service principal, which holds no licences."
                : "This user has no Entra object id, so their licences cannot be looked up.";
            return;
        }

        LicenseStatus = "Reading licences from Entra…";

        try
        {
            var licenses = await Owner.Graph.GetUserLicensesAsync(objectId.ToString());
            if (request != _licenseRequest) return;

            foreach (var license in licenses ?? []) Licenses.Add(license);

            LicenseStatus = licenses switch
            {
                null => "Entra has no user with this object id - they may have been deleted there.",
                { Count: 0 } => "No licence is assigned to this user in Entra, directly or through a group.",
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            if (request == _licenseRequest) LicenseStatus = "Could not read licences from Entra - " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- Entra groups

    /// <summary>The Entra groups the user is in, directly or nested, by name.</summary>
    public ObservableCollection<EntraUserGroup> Groups { get; } = new();

    private string _groupStatus = string.Empty;
    /// <summary>Why the group list is empty - still reading, in none, no Entra user, or the read failed.</summary>
    public string GroupStatus { get => _groupStatus; private set => SetProperty(ref _groupStatus, value); }

    /// <summary>The Dataverse team a group is linked to, shown on the Teams tab.</summary>
    public RelayCommand ShowGroupTeamCommand => _showGroupTeamCommand ??= new RelayCommand(
        p => Owner.Work.Track(ShowTeamAsync((p as EntraUserGroup)?.Team)), p => p is EntraUserGroup { Team: not null });
    private RelayCommand? _showGroupTeamCommand;

    /// <summary>The user whose groups are on show or being read; null until the Entra groups view is opened for them.</summary>
    private Guid? _groupsFor;
    private int _groupRequest;

    /// <summary>The environment's group teams, read once - to say which of the user's groups have a team here.</summary>
    private Task<IReadOnlyList<TeamInfo>>? _groupTeams;

    private void ShowGroupsFor(UserInfo? user)
    {
        _groupRequest++;
        _groupsFor = null;
        Groups.Clear();
        GroupStatus = string.Empty;
        if (user is not null) EnsureGroups();
    }

    /// <summary>Like licences, groups come from Microsoft Graph - so they are read only once their view is opened.</summary>
    private void EnsureGroups()
    {
        if (DetailView != UserDetailView.Groups || SelectedUser is not { } user || _groupsFor == user.SystemUserId) return;

        _groupsFor = user.SystemUserId;
        Owner.Work.Track(LoadGroupsAsync(user, ++_groupRequest));
    }

    private async Task LoadGroupsAsync(UserInfo user, int request)
    {
        if (user.AadObjectId is not { } objectId)
        {
            GroupStatus = user.IsApplicationUser
                ? "An application user is a service principal; its group memberships are not listed here."
                : "This user has no Entra object id, so their groups cannot be looked up.";
            return;
        }

        GroupStatus = "Reading groups from Entra…";

        try
        {
            var groupsTask = Owner.Graph.GetUserGroupsAsync(objectId.ToString());
            var teams = await ReadGroupTeamsAsync();
            var groups = await groupsTask;
            if (request != _groupRequest) return;

            var teamsByGroup = teams.Where(t => t.AadObjectId is not null).ToLookup(t => t.AadObjectId!.Value);
            foreach (var group in groups ?? [])
            {
                Groups.Add(Guid.TryParse(group.Id, out var id) && teamsByGroup.Contains(id)
                    ? group with { Teams = teamsByGroup[id].OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList() }
                    : group);
            }

            GroupStatus = groups switch
            {
                null => "Entra has no user with this object id - they may have been deleted there.",
                { Count: 0 } => "This user is not in any Entra group.",
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            if (request == _groupRequest) GroupStatus = "Could not read groups from Entra - " + ex.Message;
        }
    }

    /// <summary>The environment's group teams; none where they cannot be read, which costs only the links.</summary>
    private async Task<IReadOnlyList<TeamInfo>> ReadGroupTeamsAsync()
    {
        try
        {
            return await (_groupTeams ??= Client.GetTeamsAsync(entraGroupTeamsOnly: true));
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Group teams could not be read", ex);
            _groupTeams = null;
            return [];
        }
    }

    /// <summary>The user's mailbox, or none where it cannot be read.</summary>
    private static async Task<MailboxInfo?> ReadMailboxAsync(Task<MailboxInfo?> mailboxTask)
    {
        try
        {
            return await mailboxTask;
        }
        catch (Exception ex)
        {
            Services.Log.Warn("User mailbox could not be read", ex);
            // The mailbox is a nicety in this pane; the Mailboxes tab says what is wrong with it.
            return null;
        }
    }

    private void ShowFieldProfiles((IReadOnlyList<FieldProfileAssignment> Found, string? Error) profiles)
    {
        if (profiles.Error is { } error)
        {
            FieldProfileStatus = "Could not read field security profiles - " + error;
            return;
        }

        foreach (var profile in profiles.Found) FieldProfiles.Add(profile);
        if (FieldProfiles.Count == 0) FieldProfileStatus = "No field security profile, directly or through a team - so no access to secured columns.";
    }

    /// <summary>
    /// The user's field security profiles, direct and through every team. Read on its own, so a
    /// failure here - a privilege the reader lacks - does not cost the roles.
    /// </summary>
    private async Task<(IReadOnlyList<FieldProfileAssignment> Found, string? Error)> ReadFieldProfilesAsync(
        Guid systemUserId, IReadOnlyList<TeamInfo> teams)
    {
        try
        {
            var direct = Client.GetUserFieldProfilesAsync(systemUserId);
            var viaTeams = await Task.WhenAll(teams.Select(t => Client.GetTeamFieldProfilesAsync(t.TeamId, t.Name)));

            var found = (await direct).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Concat(viaTeams.SelectMany(p => p)
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.TeamName))
                .ToList();

            return (found, null);
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    private void RaiseRoleCounts()
    {
        OnPropertyChanged(nameof(DirectRoleCount));
        OnPropertyChanged(nameof(TeamRoleCount));
    }

    private Task ShowRoleAsync(RoleAssignment? role) =>
        role is null ? Task.CompletedTask : Owner.ShowAsync(AdminTab.Roles, role.DefinitionId);

    private Task ShowMailboxAsync() =>
        SelectedUser?.DefaultMailboxId is { } id ? Owner.ShowAsync(AdminTab.Mailboxes, id) : Task.CompletedTask;

    private Task ShowTeamAsync(TeamInfo? team) =>
        team is null ? Task.CompletedTask : Owner.ShowAsync(AdminTab.Teams, team.TeamId);
}

/// <summary>A licence in the Users tab's licence filter: how many of the environment's people hold it, and whether it is ticked.</summary>
public sealed class LicenseChoice : ObservableObject
{
    private readonly Action _changed;

    public LicenseChoice(string skuId, string name, int holders, bool isChosen, Action changed)
    {
        SkuId = skuId;
        Name = name;
        Holders = holders;
        _isChosen = isChosen;
        _changed = changed;
    }

    public string SkuId { get; }

    /// <summary>The SKU's part number - POWERAPPS_PER_USER, DYN365_ENTERPRISE_PLAN1.</summary>
    public string Name { get; }

    public int Holders { get; }

    private bool _isChosen;
    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (SetProperty(ref _isChosen, value)) _changed();
        }
    }
}

// ==================================================================== teams

public sealed class AdminTeamsViewModel : AdminPaneViewModel
{
    public override string ExportName => "teams";

    public override IReadOnlyList<string> CsvLines() =>
        View.Cast<TeamDetail>()
            .Select(t => CsvExporter.Line(t.Name, t.TypeDisplay, t.BusinessUnit, t.Administrator, t.EmailAddress, t.QueueName,
                t.AadObjectId?.ToString(), Date(t.CreatedOn), t.TeamId.ToString()))
            .Prepend(CsvExporter.Line("Team", "Type", "Business unit", "Administrator", "Email", "Queue", "Entra group",
                "Created", "Team id"))
            .ToList();

    public const string AllUnits = "All business units";

    public AdminTeamsViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is TeamDetail t && Include(t);
        ShowMemberCommand = new RelayCommand(p => Owner.Work.Track(ShowMemberAsync(p as MemberUser)), p => p is MemberUser);
        ShowRoleCommand = new RelayCommand(p => Owner.Work.Track(ShowRoleAsync(p as RoleAssignment)), p => p is RoleAssignment);
        ShowQueueCommand = new RelayCommand(_ => Owner.Work.Track(ShowQueueAsync()), _ => SelectedTeam?.QueueId is not null);
        OwnedRecordsCommand = new RelayCommand(
            _ => { if (SelectedTeam is { } team) Owner.Session.ShowOwnedRecords(team.TeamId, team.Name, isTeam: true); },
            _ => SelectedTeam is not null);
        EntraTeamSyncCommand = new RelayCommand(
            _ => { if (SelectedTeam is { IsEntraGroupTeam: true } team) Owner.Session.ShowEntraTeamSync(team.TeamId); },
            _ => SelectedTeam is { IsEntraGroupTeam: true });
    }

    public ObservableCollection<TeamDetail> Items { get; } = new();
    public ListCollectionView View { get; }

    public RelayCommand ShowMemberCommand { get; }
    public RelayCommand ShowRoleCommand { get; }
    public RelayCommand ShowQueueCommand { get; }

    /// <summary>The records the selected team owns, counted table by table in a window of their own.</summary>
    public RelayCommand OwnedRecordsCommand { get; }

    /// <summary>The selected group team, opened in Entra team sync - which writes, so it asks there first.</summary>
    public RelayCommand EntraTeamSyncCommand { get; }

    private TeamTypeFilter _typeFilter;
    public TeamTypeFilter TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (SetProperty(ref _typeFilter, value)) ApplyFilter();
        }
    }

    private IReadOnlyList<string> _unitOptions = [AllUnits];
    public IReadOnlyList<string> UnitOptions { get => _unitOptions; private set => SetProperty(ref _unitOptions, value); }

    private string _selectedUnit = AllUnits;
    public string SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (SetProperty(ref _selectedUnit, value ?? AllUnits)) ApplyFilter();
        }
    }

    public int CountAll => Items.Count;
    public int CountOwner => Items.Count(t => t.IsOwnerTeam);
    public int CountAccess => Items.Count(t => t.IsAccessTeam);
    public int CountEntra => Items.Count(t => t.IsEntraGroupTeam);

    private bool Include(TeamDetail t) =>
        TypeFilter switch
        {
            TeamTypeFilter.Owner => t.IsOwnerTeam,
            TeamTypeFilter.Access => t.IsAccessTeam,
            TeamTypeFilter.Entra => t.IsEntraGroupTeam,
            _ => true
        } &&
        (SelectedUnit == AllUnits || string.Equals(t.BusinessUnit, SelectedUnit, StringComparison.CurrentCultureIgnoreCase)) &&
        Matches(t.SearchText, SearchText);

    protected override int Count => Items.Count;
    protected override string Noun => "teams";
    public override Guid? SelectedId => SelectedTeam?.TeamId;
    public override string? SelectedLabel => SelectedTeam?.Name;

    protected override void ApplyFilter()
    {
        View.Refresh();
        ShowingSummary = Showing(View.Count, Items.Count, "teams");
    }

    private TeamDetailView _detailView;
    /// <summary>Members, Security roles or Field security, below the team's properties.</summary>
    public TeamDetailView DetailView { get => _detailView; set => SetProperty(ref _detailView, value); }

    protected override async Task LoadCoreAsync()
    {
        Status = "Reading teams…";
        var selected = SelectedTeam?.TeamId;
        var teams = await Client.GetTeamDetailsAsync();

        Items.Clear();
        foreach (var team in teams) Items.Add(team);

        UnitOptions = Options(AllUnits, teams.Select(t => t.BusinessUnit));
        if (!UnitOptions.Contains(SelectedUnit)) SelectedUnit = AllUnits;

        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountOwner));
        OnPropertyChanged(nameof(CountAccess));
        OnPropertyChanged(nameof(CountEntra));
        ApplyFilter();

        if (selected is { } id) SelectedTeam = Items.FirstOrDefault(t => t.TeamId == id);
    }

    public override void SelectById(Guid id)
    {
        if (Items.FirstOrDefault(t => t.TeamId == id) is not { } team) return;

        if (!View.Contains(team))
        {
            SearchText = string.Empty;
            TypeFilter = TeamTypeFilter.All;
            SelectedUnit = AllUnits;
        }

        SelectedTeam = team;
    }

    // ---------------------------------------------------------------- the selected team

    private TeamDetail? _selectedTeam;
    public TeamDetail? SelectedTeam
    {
        get => _selectedTeam;
        set
        {
            if (!SetProperty(ref _selectedTeam, value)) return;
            ShowQueueCommand.RaiseCanExecuteChanged();
            OwnedRecordsCommand.RaiseCanExecuteChanged();
            EntraTeamSyncCommand.RaiseCanExecuteChanged();
            Owner.Work.Track(LoadDetailsAsync(value));
        }
    }

    public ObservableCollection<MemberUser> Members { get; } = new();
    public ObservableCollection<RoleAssignment> Roles { get; } = new();
    public ObservableCollection<FieldProfileAssignment> FieldProfiles { get; } = new();

    private string _memberStatus = string.Empty;
    /// <summary>How many members and how many disabled - or why there are none.</summary>
    public string MemberStatus { get => _memberStatus; private set => SetProperty(ref _memberStatus, value); }

    private string _roleStatus = string.Empty;
    public string RoleStatus { get => _roleStatus; private set => SetProperty(ref _roleStatus, value); }

    private string _fieldProfileStatus = string.Empty;
    public string FieldProfileStatus { get => _fieldProfileStatus; private set => SetProperty(ref _fieldProfileStatus, value); }

    private bool _isLoadingDetails;
    public bool IsLoadingDetails { get => _isLoadingDetails; private set => SetProperty(ref _isLoadingDetails, value); }

    /// <summary>
    /// Members, roles and field security profiles, read together but each on its own - a privilege
    /// the reader lacks for one does not cost the others.
    /// </summary>
    private async Task LoadDetailsAsync(TeamDetail? team)
    {
        var request = ++DetailRequest;
        Members.Clear();
        Roles.Clear();
        FieldProfiles.Clear();
        MemberStatus = RoleStatus = FieldProfileStatus = string.Empty;

        if (team is null) return;

        IsLoadingDetails = true;
        MemberStatus = "Reading members…";
        RoleStatus = "Reading security roles…";
        FieldProfileStatus = "Reading field security profiles…";

        var membersTask = Client.GetTeamMembersAsync(team.TeamId);
        var rolesTask = Client.GetTeamRolesAsync(team.TeamId, team.Name);
        var profilesTask = Client.GetTeamFieldProfilesAsync(team.TeamId, team.Name);

        try
        {
            try
            {
                var members = await membersTask;
                if (request != DetailRequest) return;

                foreach (var member in members.OrderBy(m => m.FullName, StringComparer.CurrentCultureIgnoreCase)) Members.Add(member);
                MemberStatus = Members.Count == 0 ? NoMembersText(team) : MembersText();
            }
            catch (Exception ex)
            {
                if (request == DetailRequest) MemberStatus = "Could not read this team's members - " + ex.Message;
            }

            try
            {
                var roles = await rolesTask;
                if (request != DetailRequest) return;

                foreach (var role in roles.OrderBy(r => r.RoleName, StringComparer.CurrentCultureIgnoreCase)) Roles.Add(role);
                RoleStatus = Roles.Count == 0 ? NoRolesText(team) : $"{Roles.Count:N0} role(s). Every member holds them through this team.";
            }
            catch (Exception ex)
            {
                if (request == DetailRequest) RoleStatus = "Could not read this team's security roles - " + ex.Message;
            }

            try
            {
                var profiles = await profilesTask;
                if (request != DetailRequest) return;

                foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) FieldProfiles.Add(profile);
                FieldProfileStatus = FieldProfiles.Count == 0
                    ? "No field security profile - members get no access to secured columns through this team."
                    : string.Empty;
            }
            catch (Exception ex)
            {
                if (request == DetailRequest) FieldProfileStatus = "Could not read field security profiles - " + ex.Message;
            }
        }
        finally
        {
            if (request == DetailRequest) IsLoadingDetails = false;
        }
    }

    private static string NoMembersText(TeamDetail team) => team.IsEntraGroupTeam
        ? "No members yet. Group members join the team as they sign in to the environment; Entra team sync compares it with its group."
        : "No members.";

    private static string NoRolesText(TeamDetail team) => team.IsAccessTeam
        ? "No security role. An access team holds none: it grants access to the records shared with it."
        : "No security role - members get no privileges through this team.";

    /// <summary>"12 member(s), 2 disabled."</summary>
    private string MembersText()
    {
        var disabled = Members.Count(m => m.IsDisabled == true);
        return $"{Members.Count:N0} member(s)" + (disabled > 0 ? $", {disabled:N0} disabled." : ".");
    }

    private Task ShowMemberAsync(MemberUser? member) =>
        member is null ? Task.CompletedTask : Owner.ShowAsync(AdminTab.Users, member.SystemUserId);

    private Task ShowRoleAsync(RoleAssignment? role) =>
        role is null ? Task.CompletedTask : Owner.ShowAsync(AdminTab.Roles, role.DefinitionId);

    private Task ShowQueueAsync() =>
        SelectedTeam?.QueueId is { } id ? Owner.ShowAsync(AdminTab.Queues, id) : Task.CompletedTask;
}

// ==================================================================== security roles

public sealed class AdminRolesViewModel : AdminPaneViewModel
{
    public override string ExportName => "security-roles";

    public override IReadOnlyList<string> CsvLines() =>
        View.Cast<SecurityRoleInfo>()
            .Select(r => CsvExporter.Line(r.Name, r.BusinessUnit, r.IsManaged ? "Managed" : "Unmanaged", Date(r.ModifiedOn), r.RoleId.ToString()))
            .Prepend(CsvExporter.Line("Role", "Business unit", "State", "Modified", "Role id"))
            .ToList();

    public AdminRolesViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is SecurityRoleInfo r && Include(r);

        PrivilegesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Privileges);
        PrivilegesView.Filter = o => o is PrivilegeMatrixRow row &&
                                     Matches(row.Table, PrivilegeSearch) &&
                                     (!GrantedOnly || row.Widest > PrivilegeDepth.None);

        ShowHolderCommand = new RelayCommand(p => Owner.Work.Track(ShowHolderAsync(p as RoleHolder)), p => p is RoleHolder);
    }

    public ObservableCollection<SecurityRoleInfo> Items { get; } = new();
    public ListCollectionView View { get; }

    public RelayCommand ShowHolderCommand { get; }

    private RoleManagedFilter _managedFilter;
    public RoleManagedFilter ManagedFilter
    {
        get => _managedFilter;
        set
        {
            if (SetProperty(ref _managedFilter, value)) ApplyFilter();
        }
    }

    public int CountAll => Items.Count;
    public int CountManaged => Items.Count(r => r.IsManaged);
    public int CountUnmanaged => Items.Count(r => !r.IsManaged);

    private bool Include(SecurityRoleInfo r) =>
        ManagedFilter switch
        {
            RoleManagedFilter.Managed => r.IsManaged,
            RoleManagedFilter.Unmanaged => !r.IsManaged,
            _ => true
        } && Matches(r.SearchText, SearchText);

    protected override int Count => Items.Count;
    protected override string Noun => "roles";
    public override Guid? SelectedId => SelectedRole?.RoleId;
    public override string? SelectedLabel => SelectedRole?.Name;

    protected override void ApplyFilter()
    {
        View.Refresh();
        RaiseShowing();
    }

    /// <summary>"Showing 94 roles", or what the selected role grants on.</summary>
    private void RaiseShowing() =>
        ShowingSummary = SelectedRole is { } role && !IsLoadingDetails && Privileges.Count > 0
            ? $"{role.Name} grants on {Privileges.Count(p => p.Widest > PrivilegeDepth.None):N0} table(s)"
            : Showing(View.Count, Items.Count, "roles");

    private RoleDetailView _detailView;
    /// <summary>Table privileges, Other privileges or Held by - one at a time, at full height.</summary>
    public RoleDetailView DetailView
    {
        get => _detailView;
        set
        {
            if (SetProperty(ref _detailView, value)) OnPropertyChanged(nameof(IsPrivilegesView));
        }
    }

    public bool IsPrivilegesView => DetailView == RoleDetailView.Privileges;

    protected override async Task LoadCoreAsync()
    {
        Status = "Reading security roles…";
        var selected = SelectedRole?.RoleId;
        var roles = await Client.GetSecurityRolesAsync();

        Items.Clear();
        foreach (var role in roles) Items.Add(role);

        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountManaged));
        OnPropertyChanged(nameof(CountUnmanaged));
        ApplyFilter();

        if (selected is { } id) SelectedRole = Items.FirstOrDefault(r => r.RoleId == id);
    }

    public override void SelectById(Guid id)
    {
        if (Items.FirstOrDefault(r => r.RoleId == id) is not { } role) return;

        if (!View.Contains(role))
        {
            SearchText = string.Empty;
            ManagedFilter = RoleManagedFilter.All;
        }

        SelectedRole = role;
    }

    // ---------------------------------------------------------------- the selected role

    private SecurityRoleInfo? _selectedRole;
    public SecurityRoleInfo? SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (SetProperty(ref _selectedRole, value)) Owner.Work.Track(LoadDetailsAsync(value));
        }
    }

    public ObservableCollection<PrivilegeMatrixRow> Privileges { get; } = new();
    public ListCollectionView PrivilegesView { get; }
    public ObservableCollection<MiscPrivilege> MiscPrivileges { get; } = new();
    public ObservableCollection<RoleHolder> Holders { get; } = new();

    private string _privilegeSearch = string.Empty;
    /// <summary>Narrows the grid by table name - a role can carry privileges on hundreds of tables.</summary>
    public string PrivilegeSearch
    {
        get => _privilegeSearch;
        set
        {
            if (SetProperty(ref _privilegeSearch, value)) PrivilegesView.Refresh();
        }
    }

    private bool _grantedOnly = true;
    /// <summary>Only tables the role grants something on.</summary>
    public bool GrantedOnly
    {
        get => _grantedOnly;
        set
        {
            if (SetProperty(ref _grantedOnly, value)) PrivilegesView.Refresh();
        }
    }

    private string _detailStatus = string.Empty;
    public string DetailStatus { get => _detailStatus; private set => SetProperty(ref _detailStatus, value); }

    private string _holderStatus = string.Empty;
    public string HolderStatus { get => _holderStatus; private set => SetProperty(ref _holderStatus, value); }

    private bool _isLoadingDetails;
    public bool IsLoadingDetails { get => _isLoadingDetails; private set => SetProperty(ref _isLoadingDetails, value); }

    private async Task LoadDetailsAsync(SecurityRoleInfo? role)
    {
        var request = ++DetailRequest;
        Privileges.Clear();
        MiscPrivileges.Clear();
        Holders.Clear();
        DetailStatus = HolderStatus = string.Empty;

        if (role is null) return;

        IsLoadingDetails = true;
        DetailStatus = "Reading privileges…";
        HolderStatus = "Reading who holds this role…";

        var privilegesTask = Client.GetRolePrivilegesAsync(role.RoleId);
        var holdersTask = Client.GetRoleHoldersAsync(role.RoleId);

        try
        {
            var (tables, misc) = PrivilegeMatrix.Build(await privilegesTask);
            if (request != DetailRequest) return;

            foreach (var row in tables) Privileges.Add(row);
            foreach (var item in misc) MiscPrivileges.Add(item);
            DetailStatus = $"{tables.Count:N0} table(s), {misc.Count:N0} other privilege(s).";
        }
        catch (Exception ex)
        {
            if (request == DetailRequest) DetailStatus = "Could not read the privileges - " + ex.Message;
        }

        try
        {
            var holders = await holdersTask;
            if (request != DetailRequest) return;

            foreach (var holder in holders) Holders.Add(holder);
            var users = holders.Count(h => !h.IsTeam);
            HolderStatus = holders.Count == 0
                ? "Nobody holds this role directly, and no team has it."
                : $"{users:N0} user(s) and {holders.Count - users:N0} team(s). Team members hold it through their team.";
        }
        catch (Exception ex)
        {
            if (request == DetailRequest) HolderStatus = "Could not read who holds this role - " + ex.Message;
        }
        finally
        {
            if (request == DetailRequest)
            {
                IsLoadingDetails = false;
                RaiseShowing();
            }
        }
    }

    private Task ShowHolderAsync(RoleHolder? holder) =>
        holder is null ? Task.CompletedTask : Owner.ShowAsync(holder.IsTeam ? AdminTab.Teams : AdminTab.Users, holder.Id);
}

// ==================================================================== mailboxes

public sealed class AdminMailboxesViewModel : AdminPaneViewModel
{
    public override string ExportName => "mailboxes";

    public override IReadOnlyList<string> CsvLines() =>
        View.Cast<MailboxInfo>()
            .Select(m => CsvExporter.Line(m.Name, m.EmailAddress, m.RegardingName, m.RegardingType, m.ApprovalLabel,
                m.IncomingStatusLabel, m.OutgoingStatusLabel, m.AppointmentsStatusLabel, Date(m.TestCompletedOn), m.MailboxId.ToString()))
            .Prepend(CsvExporter.Line("Mailbox", "Email", "Regarding", "Regarding type", "Approval", "Incoming", "Outgoing",
                "Appointments", "Last tested", "Mailbox id"))
            .ToList();

    public const string AnyApproval = "Any approval";
    public const string AnyTest = "Any test result";

    public AdminMailboxesViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is MailboxInfo m && Include(m);
        ShowOwnerCommand = new RelayCommand(
            _ => Owner.Work.Track(ShowOwnerAsync()),
            _ => SelectedMailbox is { RegardingId: not null, OwnerKind: MailboxInfo.UserOwner or MailboxInfo.QueueOwner });
    }

    public ObservableCollection<MailboxInfo> Items { get; } = new();
    public ListCollectionView View { get; }

    public RelayCommand ShowOwnerCommand { get; }

    private MailboxOwnerFilter _ownerFilter;
    public MailboxOwnerFilter OwnerFilter
    {
        get => _ownerFilter;
        set
        {
            if (SetProperty(ref _ownerFilter, value)) ApplyFilter();
        }
    }

    private IReadOnlyList<string> _approvalOptions = [AnyApproval];
    public IReadOnlyList<string> ApprovalOptions { get => _approvalOptions; private set => SetProperty(ref _approvalOptions, value); }

    private string _selectedApproval = AnyApproval;
    public string SelectedApproval
    {
        get => _selectedApproval;
        set
        {
            if (SetProperty(ref _selectedApproval, value ?? AnyApproval)) ApplyFilter();
        }
    }

    private IReadOnlyList<string> _testOptions = [AnyTest];
    public IReadOnlyList<string> TestOptions { get => _testOptions; private set => SetProperty(ref _testOptions, value); }

    private string _selectedTest = AnyTest;
    public string SelectedTest
    {
        get => _selectedTest;
        set
        {
            if (SetProperty(ref _selectedTest, value ?? AnyTest)) ApplyFilter();
        }
    }

    public int CountAll => Items.Count;
    public int CountUsers => Items.Count(m => m.OwnerKind == MailboxInfo.UserOwner);
    public int CountQueues => Items.Count(m => m.OwnerKind == MailboxInfo.QueueOwner);
    public int CountOther => Items.Count(m => !HasUserOrQueueOwner(m));

    private static bool HasUserOrQueueOwner(MailboxInfo m) => m.OwnerKind is MailboxInfo.UserOwner or MailboxInfo.QueueOwner;

    private bool Include(MailboxInfo m) =>
        OwnerFilter switch
        {
            MailboxOwnerFilter.Users => m.OwnerKind == MailboxInfo.UserOwner,
            MailboxOwnerFilter.Queues => m.OwnerKind == MailboxInfo.QueueOwner,
            MailboxOwnerFilter.Other => !HasUserOrQueueOwner(m),
            _ => true
        } &&
        (SelectedApproval == AnyApproval || string.Equals(m.ApprovalLabel ?? "Empty", SelectedApproval, StringComparison.CurrentCultureIgnoreCase)) &&
        (SelectedTest == AnyTest || m.TestLabel == SelectedTest) &&
        Matches(m.SearchText, SearchText);

    protected override int Count => Items.Count;
    protected override string Noun => "mailboxes";
    public override Guid? SelectedId => SelectedMailbox?.MailboxId;
    public override string? SelectedLabel => SelectedMailbox?.Name;

    protected override void ApplyFilter()
    {
        View.Refresh();
        ShowingSummary = Showing(View.Count, Items.Count, "mailboxes");
    }

    protected override async Task LoadCoreAsync()
    {
        Status = "Reading mailboxes…";
        var selected = SelectedMailbox?.MailboxId;
        var mailboxes = await Client.GetMailboxesAsync();

        Items.Clear();
        foreach (var mailbox in mailboxes) Items.Add(mailbox);

        ApprovalOptions = Options(AnyApproval, mailboxes.Select(m => m.ApprovalLabel ?? "Empty"));
        TestOptions = Options(AnyTest, mailboxes.Select(m => m.TestLabel));
        if (!ApprovalOptions.Contains(SelectedApproval)) SelectedApproval = AnyApproval;
        if (!TestOptions.Contains(SelectedTest)) SelectedTest = AnyTest;

        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountUsers));
        OnPropertyChanged(nameof(CountQueues));
        OnPropertyChanged(nameof(CountOther));
        ApplyFilter();

        if (selected is { } id) SelectedMailbox = Items.FirstOrDefault(m => m.MailboxId == id);
    }

    public override void SelectById(Guid id)
    {
        if (Items.FirstOrDefault(m => m.MailboxId == id) is not { } mailbox) return;

        if (!View.Contains(mailbox))
        {
            SearchText = string.Empty;
            OwnerFilter = MailboxOwnerFilter.All;
            SelectedApproval = AnyApproval;
            SelectedTest = AnyTest;
        }

        SelectedMailbox = mailbox;
    }

    private MailboxInfo? _selectedMailbox;
    public MailboxInfo? SelectedMailbox
    {
        get => _selectedMailbox;
        set
        {
            if (SetProperty(ref _selectedMailbox, value)) ShowOwnerCommand.RaiseCanExecuteChanged();
        }
    }

    private Task ShowOwnerAsync() => SelectedMailbox switch
    {
        { RegardingId: { } id, OwnerKind: MailboxInfo.UserOwner } => Owner.ShowAsync(AdminTab.Users, id),
        { RegardingId: { } id, OwnerKind: MailboxInfo.QueueOwner } => Owner.ShowAsync(AdminTab.Queues, id),
        _ => Task.CompletedTask
    };
}

// ==================================================================== queues

public sealed class AdminQueuesViewModel : AdminPaneViewModel
{
    public override string ExportName => "queues";

    public override IReadOnlyList<string> CsvLines() =>
        View.Cast<QueueDetail>()
            .Select(q => CsvExporter.Line(q.Name, q.EmailAddress, q.ViewTypeLabel, q.Owner, q.OwnerType, q.BusinessUnit,
                q.MailboxName, q.QueueId.ToString()))
            .Prepend(CsvExporter.Line("Queue", "Email", "Type", "Owner", "Owner type", "Business unit", "Mailbox", "Queue id"))
            .ToList();

    public AdminQueuesViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is QueueDetail q && Include(q);
        ShowMailboxCommand = new RelayCommand(_ => Owner.Work.Track(ShowMailboxAsync()), _ => SelectedQueue?.MailboxId is not null);
        ShowMemberCommand = new RelayCommand(p => Owner.Work.Track(ShowMemberAsync(p as MemberUser)), p => p is MemberUser);
        QueueSyncCommand = new RelayCommand(
            _ => { if (SelectedQueue is { } queue) Owner.Session.ShowQueueSync(queue.QueueId); },
            _ => SelectedQueue is not null);
    }

    /// <summary>The selected queue, opened in Queue membership sync - which writes, so it asks there first.</summary>
    public RelayCommand QueueSyncCommand { get; }

    public ObservableCollection<QueueDetail> Items { get; } = new();
    public ListCollectionView View { get; }

    public RelayCommand ShowMailboxCommand { get; }
    public RelayCommand ShowMemberCommand { get; }

    private QueueTypeFilter _typeFilter;
    public QueueTypeFilter TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (SetProperty(ref _typeFilter, value)) ApplyFilter();
        }
    }

    private bool _activeOnly = true;
    public bool ActiveOnly
    {
        get => _activeOnly;
        set
        {
            if (SetProperty(ref _activeOnly, value)) ApplyFilter();
        }
    }

    public int CountAll => Items.Count(q => !ActiveOnly || q.IsActive);
    public int CountPublic => Items.Count(q => (!ActiveOnly || q.IsActive) && !q.IsPrivate);
    public int CountPrivate => Items.Count(q => (!ActiveOnly || q.IsActive) && q.IsPrivate);

    private bool Include(QueueDetail q) =>
        (!ActiveOnly || q.IsActive) &&
        TypeFilter switch
        {
            QueueTypeFilter.Public => !q.IsPrivate,
            QueueTypeFilter.Private => q.IsPrivate,
            _ => true
        } && Matches(q.SearchText, SearchText);

    protected override void ApplyFilter()
    {
        View.Refresh();
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountPublic));
        OnPropertyChanged(nameof(CountPrivate));
        ShowingSummary = Showing(View.Count, CountAll, ActiveOnly ? "active queues" : "queues");
    }

    protected override int Count => Items.Count;
    protected override string Noun => "queues";
    public override Guid? SelectedId => SelectedQueue?.QueueId;
    public override string? SelectedLabel => SelectedQueue?.Name;

    protected override async Task LoadCoreAsync()
    {
        Status = "Reading queues…";
        var selected = SelectedQueue?.QueueId;
        var queues = await Client.GetQueueDetailsAsync();

        Items.Clear();
        foreach (var queue in queues) Items.Add(queue);
        ApplyFilter();

        if (selected is { } id) SelectedQueue = Items.FirstOrDefault(q => q.QueueId == id);
    }

    public override void SelectById(Guid id)
    {
        if (Items.FirstOrDefault(q => q.QueueId == id) is not { } queue) return;

        if (!View.Contains(queue))
        {
            SearchText = string.Empty;
            TypeFilter = QueueTypeFilter.All;
            ActiveOnly = false;
        }

        SelectedQueue = queue;
    }

    // ---------------------------------------------------------------- the selected queue

    private QueueDetail? _selectedQueue;
    public QueueDetail? SelectedQueue
    {
        get => _selectedQueue;
        set
        {
            if (!SetProperty(ref _selectedQueue, value)) return;
            ShowMailboxCommand.RaiseCanExecuteChanged();
            QueueSyncCommand.RaiseCanExecuteChanged();
            Owner.Work.Track(LoadDetailsAsync(value));
        }
    }

    public ObservableCollection<MemberUser> Members { get; } = new();

    private MailboxInfo? _mailbox;
    public MailboxInfo? Mailbox { get => _mailbox; private set => SetProperty(ref _mailbox, value); }

    private string _detailStatus = string.Empty;
    public string DetailStatus { get => _detailStatus; private set => SetProperty(ref _detailStatus, value); }

    private bool _isLoadingDetails;
    public bool IsLoadingDetails { get => _isLoadingDetails; private set => SetProperty(ref _isLoadingDetails, value); }

    private async Task LoadDetailsAsync(QueueDetail? queue)
    {
        var request = ++DetailRequest;
        Members.Clear();
        Mailbox = null;

        if (queue is null)
        {
            DetailStatus = string.Empty;
            return;
        }

        IsLoadingDetails = true;
        DetailStatus = "Reading members and mailbox…";

        try
        {
            var membersTask = Client.GetQueueMembersAsync(queue.QueueId);
            MailboxInfo? mailbox = null;
            if (queue.MailboxId is { } mailboxId)
            {
                try
                {
                    mailbox = await Client.GetMailboxAsync(mailboxId);
                }
                catch (Exception ex)
                {
                    Services.Log.Warn("Queue mailbox could not be read", ex);
                    // Shown as unknown; the Mailboxes tab says what is wrong.
                }
            }

            var members = await membersTask;
            if (request != DetailRequest) return;

            foreach (var member in members.OrderBy(m => m.FullName, StringComparer.CurrentCultureIgnoreCase)) Members.Add(member);
            Mailbox = mailbox;

            DetailStatus = Members.Count == 0 ? NoMembersText(queue) : MembersText();
        }
        catch (Exception ex)
        {
            if (request == DetailRequest) DetailStatus = "Could not read this queue's members - " + ex.Message;
        }
        finally
        {
            if (request == DetailRequest) IsLoadingDetails = false;
        }
    }

    private static string NoMembersText(QueueDetail queue) => queue.IsPrivate
        ? "This private queue has no members, so nobody can see its items."
        : "No members. A public queue is open to everyone with access to queues.";

    /// <summary>"12 member(s), 2 disabled."</summary>
    private string MembersText()
    {
        var disabled = Members.Count(m => m.IsDisabled == true);
        return $"{Members.Count:N0} member(s)" + (disabled > 0 ? $", {disabled:N0} disabled." : ".");
    }

    private Task ShowMailboxAsync() =>
        SelectedQueue?.MailboxId is { } id ? Owner.ShowAsync(AdminTab.Mailboxes, id) : Task.CompletedTask;

    private Task ShowMemberAsync(MemberUser? member) =>
        member is null ? Task.CompletedTask : Owner.ShowAsync(AdminTab.Users, member.SystemUserId);
}
