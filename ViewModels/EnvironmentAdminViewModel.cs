using System.Collections.ObjectModel;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>The Environment admin window's tabs.</summary>
public enum AdminTab
{
    Users,
    Roles,
    Mailboxes,
    Queues
}

public enum UserStatusFilter { All, Enabled, Disabled }
public enum RoleManagedFilter { All, Managed, Unmanaged }
public enum MailboxOwnerFilter { All, Users, Queues, Other }
public enum QueueTypeFilter { All, Public, Private }

/// <summary>What the selected user's pane lists below their properties.</summary>
public enum UserDetailView { Roles, Teams, FieldSecurity }

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
/// Users, security roles, mailboxes and queues in one environment. Read-only: each tab reads
/// the first time it is opened, and links between them - a user's role, a queue's mailbox -
/// move to the other tab with that row selected.
/// </summary>
public sealed class EnvironmentAdminViewModel : ObservableObject
{
    /// <summary>Loads started by selections and links, which nothing waits for - except tests.</summary>
    internal BackgroundWork Work { get; } = new();

    public EnvironmentAdminViewModel(EnvironmentSessionViewModel session, DataverseClient client, AdminTab tab = AdminTab.Users)
    {
        Session = session;
        Users = new AdminUsersViewModel(this, client);
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

    public AdminUsersViewModel Users { get; }
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
        AdminTab.Roles => Roles,
        AdminTab.Mailboxes => Mailboxes,
        _ => Queues
    };

    /// <summary>Reads the tab on show - called when the window opens.</summary>
    public Task LoadAsync() => Pane(SelectedTab).EnsureLoadedAsync();

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
        search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase));

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
    public const string AllTypes = "All user types";
    public const string AllUnits = "All business units";

    public AdminUsersViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is UserInfo u && Include(u);
        ShowRoleCommand = new RelayCommand(p => Owner.Work.Track(ShowRoleAsync(p as RoleAssignment)), p => p is RoleAssignment);
        ShowMailboxCommand = new RelayCommand(_ => Owner.Work.Track(ShowMailboxAsync()), _ => SelectedUser?.DefaultMailboxId is not null);
    }

    public ObservableCollection<UserInfo> Items { get; } = new();
    public ListCollectionView View { get; }

    public RelayCommand ShowRoleCommand { get; }
    public RelayCommand ShowMailboxCommand { get; }

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

    public int CountAll => Items.Count;
    public int CountEnabled => Items.Count(u => !u.IsDisabled);
    public int CountDisabled => Items.Count(u => u.IsDisabled);

    private bool Include(UserInfo u) =>
        StatusFilter switch
        {
            UserStatusFilter.Enabled => !u.IsDisabled,
            UserStatusFilter.Disabled => u.IsDisabled,
            _ => true
        } &&
        (SelectedType == AllTypes || string.Equals(u.UserType, SelectedType, StringComparison.CurrentCultureIgnoreCase)) &&
        (SelectedUnit == AllUnits || string.Equals(u.BusinessUnit, SelectedUnit, StringComparison.CurrentCultureIgnoreCase)) &&
        Matches(u.SearchText, SearchText);

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
        ShowingSummary = Showing(View.Count, inStatus, noun);
    }

    private UserDetailView _detailView;
    /// <summary>Security roles or Teams, below the user's properties.</summary>
    public UserDetailView DetailView
    {
        get => _detailView;
        set
        {
            if (SetProperty(ref _detailView, value)) OnPropertyChanged(nameof(IsRolesView));
        }
    }

    public bool IsRolesView => DetailView == UserDetailView.Roles;

    protected override async Task LoadCoreAsync()
    {
        Status = "Reading users...";
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
            Owner.Work.Track(LoadDetailsAsync(value));
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
        RaiseRoleCounts();

        if (user is null)
        {
            DetailStatus = string.Empty;
            return;
        }

        IsLoadingDetails = true;
        DetailStatus = "Reading teams and roles...";

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
            MailboxInfo? mailbox = null;
            try
            {
                mailbox = await mailboxTask;
            }
            catch (Exception ex)
            {
                Services.Log.Warn("User mailbox could not be read", ex);
                // The mailbox is a nicety in this pane; the Mailboxes tab says what is wrong with it.
            }

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

            if (profiles.Error is { } error)
            {
                FieldProfileStatus = "Could not read field security profiles - " + error;
            }
            else
            {
                foreach (var profile in profiles.Found) FieldProfiles.Add(profile);
                if (FieldProfiles.Count == 0) FieldProfileStatus = "No field security profile, directly or through a team - so no access to secured columns.";
            }

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
}

// ==================================================================== security roles

public sealed class AdminRolesViewModel : AdminPaneViewModel
{
    public AdminRolesViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is SecurityRoleInfo r && Include(r);

        PrivilegesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Privileges);
        PrivilegesView.Filter = o => o is PrivilegeMatrixRow row &&
                                     Matches(row.Table, PrivilegeSearch) &&
                                     (!GrantedOnly || row.Widest > PrivilegeDepth.None);

        ShowHolderCommand = new RelayCommand(p => Owner.Work.Track(ShowHolderAsync(p as RoleHolder)), p => p is RoleHolder { IsTeam: false });
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
        Status = "Reading security roles...";
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
        DetailStatus = "Reading privileges...";
        HolderStatus = "Reading who holds this role...";

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
        holder is { IsTeam: false } ? Owner.ShowAsync(AdminTab.Users, holder.Id) : Task.CompletedTask;
}

// ==================================================================== mailboxes

public sealed class AdminMailboxesViewModel : AdminPaneViewModel
{
    public const string AnyApproval = "Any approval";
    public const string AnyTest = "Any test result";

    public AdminMailboxesViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is MailboxInfo m && Include(m);
        ShowOwnerCommand = new RelayCommand(
            _ => Owner.Work.Track(ShowOwnerAsync()),
            _ => SelectedMailbox is { RegardingId: not null, OwnerKind: "User" or "Queue" });
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
    public int CountUsers => Items.Count(m => m.OwnerKind == "User");
    public int CountQueues => Items.Count(m => m.OwnerKind == "Queue");
    public int CountOther => Items.Count(m => m.OwnerKind is not ("User" or "Queue"));

    private bool Include(MailboxInfo m) =>
        OwnerFilter switch
        {
            MailboxOwnerFilter.Users => m.OwnerKind == "User",
            MailboxOwnerFilter.Queues => m.OwnerKind == "Queue",
            MailboxOwnerFilter.Other => m.OwnerKind is not ("User" or "Queue"),
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
        Status = "Reading mailboxes...";
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
        { RegardingId: { } id, OwnerKind: "User" } => Owner.ShowAsync(AdminTab.Users, id),
        { RegardingId: { } id, OwnerKind: "Queue" } => Owner.ShowAsync(AdminTab.Queues, id),
        _ => Task.CompletedTask
    };
}

// ==================================================================== queues

public sealed class AdminQueuesViewModel : AdminPaneViewModel
{
    public AdminQueuesViewModel(EnvironmentAdminViewModel owner, DataverseClient client) : base(owner, client)
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is QueueDetail q && Include(q);
        ShowMailboxCommand = new RelayCommand(_ => Owner.Work.Track(ShowMailboxAsync()), _ => SelectedQueue?.MailboxId is not null);
        ShowMemberCommand = new RelayCommand(p => Owner.Work.Track(ShowMemberAsync(p as MemberUser)), p => p is MemberUser);
    }

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
        Status = "Reading queues...";
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
        DetailStatus = "Reading members and mailbox...";

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
