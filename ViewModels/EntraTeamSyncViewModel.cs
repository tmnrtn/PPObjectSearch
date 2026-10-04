using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Graph;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

public enum EntraMatchFilter
{
    All,
    Both,
    DataverseOnly,
    EntraOnly
}

/// <summary>A comparison row, plus the diagnosis once one has been run for it.</summary>
public sealed class EntraMatchRowViewModel : ObservableObject
{
    public EntraMatchRowViewModel(EntraMatchRow row) => Row = row;

    public EntraMatchRow Row { get; }

    public EntraMatchStatus Status => Row.Status;
    public string StatusLabel => Status switch
    {
        EntraMatchStatus.Both => "Both",
        EntraMatchStatus.DataverseOnly => "Team only",
        _ => "Group only"
    };

    public string Name => Row.Name;
    public string? Upn => Row.Upn;
    public string? Email => Row.Email;
    public string? MatchedOn => Row.MatchedOn;
    public string? EntraObjectId => Row.EntraObjectId;
    public bool? EntraEnabled => Row.Entra?.AccountEnabled;
    public bool? DataverseDisabled => Row.Dataverse?.IsDisabled;
    public string? AccessMode => Row.Dataverse?.AccessMode;

    private Diagnosis? _diagnosis;
    public Diagnosis? Diagnosis
    {
        get => _diagnosis;
        set
        {
            if (!SetProperty(ref _diagnosis, value)) return;
            OnPropertyChanged(nameof(DiagnosisLabel));
            OnPropertyChanged(nameof(DiagnosisDetail));
            OnPropertyChanged(nameof(AccountFlags));
        }
    }

    private GroupOnlyDiagnosis? _groupDiagnosis;
    /// <summary>For a 'group only' user: what Dataverse holds for them.</summary>
    public GroupOnlyDiagnosis? GroupDiagnosis
    {
        get => _groupDiagnosis;
        set
        {
            if (!SetProperty(ref _groupDiagnosis, value)) return;
            OnPropertyChanged(nameof(DiagnosisLabel));
            OnPropertyChanged(nameof(DiagnosisDetail));
            OnPropertyChanged(nameof(AccountFlags));
        }
    }

    public string? DiagnosisLabel => Diagnosis?.Label ?? GroupDiagnosis?.Label;
    public string? DiagnosisDetail => Diagnosis?.Detail ?? GroupDiagnosis?.Detail;

    /// <summary>
    /// What is wrong with the account, if anything: "Entra disabled", "DV disabled", "Not in
    /// Entra". Empty for a healthy account - the column only speaks up when there is a problem.
    /// </summary>
    public IReadOnlyList<string> AccountFlags
    {
        get
        {
            var flags = new List<string>();

            if (Diagnosis?.Category is DiagnosisCategory.EntraNotFound or DiagnosisCategory.StaleObjectId)
            {
                flags.Add("Not in Entra");
            }
            else if (EntraEnabled == false || Diagnosis?.EntraUser?.AccountEnabled == false ||
                     Diagnosis?.Category == DiagnosisCategory.EntraDisabled ||
                     GroupDiagnosis?.Category == GroupOnlyCategory.EntraDisabled)
            {
                flags.Add("Entra disabled");
            }

            if (DataverseDisabled == true || GroupDiagnosis?.SystemUser?.IsDisabled == true ||
                Diagnosis?.Category == DiagnosisCategory.DataverseDisabled ||
                GroupDiagnosis?.Category == GroupOnlyCategory.DataverseDisabled)
            {
                flags.Add("DV disabled");
            }

            return flags;
        }
    }

    /// <summary>The detail the grid leaves out: how the two sides were matched, and the Entra object id.</summary>
    public string RowToolTip => string.Join(Environment.NewLine, new[]
    {
        DiagnosisDetail,
        MatchedOn is { Length: > 0 } on ? "Matched on " + on : null,
        EntraObjectId is { Length: > 0 } id ? "Entra object id " + id : null,
        AccessMode is { Length: > 0 } mode ? "Access mode " + mode : null
    }.Where(line => !string.IsNullOrEmpty(line)));
}

/// <summary>
/// An Entra group team next to its Entra (RBAC) group: who is in both, who is only in the team,
/// who is only in the group. Two ways to close the gap, each previewed and confirmed first -
/// Dataverse's own SyncGroupMembersToTeam, and removing by hand the users that sync leaves behind.
/// </summary>
public sealed class EntraTeamSyncViewModel : ObservableObject, IDisposable
{
    private readonly DataverseClient _client;
    private readonly GraphClient _graph;
    private CancellationTokenSource? _loadCts;
    private IReadOnlyList<MemberUser> _teamMembers = Array.Empty<MemberUser>();
    private IReadOnlyList<EntraUser> _groupUsers = Array.Empty<EntraUser>();

    public EntraTeamSyncViewModel(EnvironmentSessionViewModel session, DataverseClient client)
    {
        Session = session;
        _client = client;
        _graph = session.CreateGraphClient();

        TeamsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Teams);
        TeamsView.Filter = o => o is TeamInfo t && Matches(t.SearchText, TeamSearchText);

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;

        RefreshTeamsCommand = new AsyncRelayCommand(_ => LoadTeamsAsync());
        ReloadCommand = new AsyncRelayCommand(_ => LoadComparisonAsync(), _ => SelectedTeam is not null && !IsBusy);
        DiagnoseCommand = new AsyncRelayCommand(_ => DiagnoseAsync(), _ => HasComparison && CountDataverseOnly + CountEntraOnly > 0 && !IsBusy);
        CopyUserSyncScriptCommand = new RelayCommand(_ => CopyUserSyncScript(), _ => NeedsUserSyncCount > 0);
        SyncCommand = new AsyncRelayCommand(_ => PreviewSyncAsync(), _ => HasComparison && Group is not null && !IsBusy);
        RemoveLeftoversCommand = new AsyncRelayCommand(_ => PreviewRemovalAsync(), _ => HasComparison && CountDataverseOnly > 0 && !IsBusy);
        PullInCommand = new AsyncRelayCommand(_ => PreviewPullInAsync(), _ => HasComparison && Group is not null && CountEntraOnly > 0 && !IsBusy);
    }

    public EnvironmentSessionViewModel Session { get; }
    public string Title => $"Entra team sync — {Session.Title}";

    public ObservableCollection<TeamInfo> Teams { get; } = new();
    public ListCollectionView TeamsView { get; }

    public ObservableCollection<EntraMatchRowViewModel> Rows { get; } = new();
    public ListCollectionView RowsView { get; }

    public AsyncRelayCommand RefreshTeamsCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand DiagnoseCommand { get; }
    public AsyncRelayCommand SyncCommand { get; }
    public AsyncRelayCommand RemoveLeftoversCommand { get; }
    public AsyncRelayCommand PullInCommand { get; }
    public RelayCommand CopyUserSyncScriptCommand { get; }

    /// <summary>'Group only' users diagnosed as needing provisioning into the environment.</summary>
    public int NeedsUserSyncCount =>
        Rows.Count(r => r.GroupDiagnosis is { } d && MembershipPlanner.NeedsUserSync(d.Category));

    private string _teamSearchText = string.Empty;
    public string TeamSearchText
    {
        get => _teamSearchText;
        set
        {
            if (SetProperty(ref _teamSearchText, value)) TeamsView.Refresh();
        }
    }

    private TeamInfo? _selectedTeam;
    public TeamInfo? SelectedTeam
    {
        get => _selectedTeam;
        set
        {
            if (!SetProperty(ref _selectedTeam, value)) return;
            OnPropertyChanged(nameof(HasTeam));
            _ = LoadComparisonAsync();
        }
    }

    public bool HasTeam => SelectedTeam is not null;

    private DateTimeOffset? _lastRead;
    private System.Windows.Threading.DispatcherTimer? _ageTimer;

    /// <summary>"Read 2 min ago" beside the refresh button - how stale the comparison is.</summary>
    public string LastReadLabel => _lastRead is { } at ? "Read " + Ago(DateTimeOffset.Now - at) : string.Empty;

    internal static string Ago(TimeSpan age) => age.TotalSeconds < 60 ? "just now"
        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
        : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
        : "over a day ago";

    private void MarkRead()
    {
        _lastRead = DateTimeOffset.Now;
        OnPropertyChanged(nameof(LastReadLabel));

        if (_ageTimer is null)
        {
            _ageTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _ageTimer.Tick += (_, _) => OnPropertyChanged(nameof(LastReadLabel));
            _ageTimer.Start();
        }
    }

    // ---------------------------------------------------------------- the write buttons' counts

    /// <summary>
    /// How many users Remove leftovers would offer: every 'team only' user until a diagnosis has
    /// said which of them the sync really leaves behind.
    /// </summary>
    public int LeftoverCount => IsDiagnosed
        ? Rows.Count(r => r.Status == EntraMatchStatus.DataverseOnly && r.Diagnosis is { } d && MembershipPlanner.CanRemove(d.Category))
        : CountDataverseOnly;

    /// <summary>How many Pull in group members would offer, on the same terms.</summary>
    public int PullInCount => IsGroupDiagnosed
        ? Rows.Count(r => r.Status == EntraMatchStatus.EntraOnly && r.GroupDiagnosis is { } d && MembershipPlanner.CanPullIn(d.Category))
        : CountEntraOnly;

    public string RemoveLeftoversLabel => HasComparison ? $"Remove leftovers ({LeftoverCount:N0})…" : "Remove leftovers…";
    public string PullInLabel => HasComparison ? $"Pull in group members ({PullInCount:N0})…" : "Pull in group members…";

    private EntraGroup? _group;
    public EntraGroup? Group
    {
        get => _group;
        private set
        {
            if (!SetProperty(ref _group, value)) return;
            OnPropertyChanged(nameof(GroupLabel));
        }
    }

    public string GroupLabel => Group?.DisplayName ?? (SelectedTeam?.AadObjectId is { } id ? $"{id} (not found in Entra)" : "-");

    private bool _hasComparison;
    public bool HasComparison
    {
        get => _hasComparison;
        private set
        {
            if (SetProperty(ref _hasComparison, value)) RaiseCommands();
        }
    }

    private EntraMatchFilter _filter;
    public EntraMatchFilter Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) RowsView.Refresh();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) RowsView.Refresh();
        }
    }

    private int _countAll, _countBoth, _countDataverseOnly, _countEntraOnly;
    public int CountAll { get => _countAll; private set => SetProperty(ref _countAll, value); }
    public int CountBoth { get => _countBoth; private set => SetProperty(ref _countBoth, value); }
    public int CountDataverseOnly { get => _countDataverseOnly; private set => SetProperty(ref _countDataverseOnly, value); }
    public int CountEntraOnly { get => _countEntraOnly; private set => SetProperty(ref _countEntraOnly, value); }

    private string _warnings = string.Empty;
    /// <summary>Anything that makes the comparison less than straightforward, said out loud.</summary>
    public string Warnings
    {
        get => _warnings;
        private set
        {
            if (SetProperty(ref _warnings, value)) OnPropertyChanged(nameof(HasWarnings));
        }
    }

    public bool HasWarnings => !string.IsNullOrEmpty(Warnings);

    private bool _isDiagnosed;
    public bool IsDiagnosed
    {
        get => _isDiagnosed;
        private set => SetProperty(ref _isDiagnosed, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaiseCommands();
        }
    }

    private string _status = "Loading Entra group teams...";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // ---------------------------------------------------------------- loading

    public async Task LoadTeamsAsync()
    {
        IsBusy = true;
        Status = "Loading Entra group teams...";

        try
        {
            var selectedId = SelectedTeam?.TeamId;
            var teams = await _client.GetTeamsAsync(entraGroupTeamsOnly: true);

            Teams.Clear();
            foreach (var team in teams) Teams.Add(team);

            Status = teams.Count == 0
                ? "No teams in this environment are linked to an Entra group."
                : $"{teams.Count:N0} Entra group team(s). Pick one to compare it with its group.";

            if (selectedId is { } id) _selectedTeam = Teams.FirstOrDefault(t => t.TeamId == id);
            OnPropertyChanged(nameof(SelectedTeam));
        }
        catch (Exception ex)
        {
            Status = "Could not load teams - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reads both sides afresh - the team from Dataverse, the group from Graph.</summary>
    private async Task LoadComparisonAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;

        var team = SelectedTeam;
        Rows.Clear();
        HasComparison = false;
        IsDiagnosed = false;
        IsGroupDiagnosed = false;
        Group = null;
        Warnings = string.Empty;
        RecountRows();

        if (team?.AadObjectId is not { } groupId)
        {
            IsBusy = false;
            return;
        }

        IsBusy = true;
        Status = $"Reading {team.Name} and its Entra group...";

        try
        {
            var membersTask = _client.GetTeamMembersAsync(team.TeamId, ct);
            var group = await _graph.GetGroupAsync(groupId.ToString(), ct);

            IReadOnlyList<EntraUser> groupUsers = Array.Empty<EntraUser>();
            IReadOnlyDictionary<string, int> nonUsers = new Dictionary<string, int>();

            if (group is not null)
            {
                var usersTask = _graph.GetGroupTransitiveUsersAsync(group.Id, ct);
                var nonUsersTask = _graph.GetNonUserMemberCountsAsync(group.Id, ct);
                groupUsers = await usersTask;
                nonUsers = await nonUsersTask;
            }

            var members = await membersTask;
            if (ct.IsCancellationRequested) return;

            Group = group;
            _teamMembers = members;
            _groupUsers = groupUsers;
            Rebuild();

            var warnings = new List<string>();
            if (group is null)
            {
                warnings.Add($"Entra group {groupId} was not found - deleted, in another tenant, or not readable by this account. " +
                             "Every team member shows as 'team only', and syncing is unavailable because it could empty the team.");
            }

            if (team.MembershipType is { } mt && mt != 0)
            {
                warnings.Add($"The team's membership type is '{team.MembershipTypeLabel ?? mt.ToString()}', so Dataverse only " +
                             "syncs that part of the group - some differences are expected.");
            }

            if (nonUsers.Count > 0)
            {
                warnings.Add("Ignored non-user members of the group: " +
                             string.Join(", ", nonUsers.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value:N0}")) + ".");
            }

            Warnings = string.Join("  ", warnings);
            HasComparison = true;
            MarkRead();

            Status = $"Read at {DateTime.Now:T}. Dataverse syncs group teams lazily, on sign-in: 'group only' is usually someone who " +
                     "has not used the environment since joining; 'team only' is usually someone removed but not yet re-synced.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer selection took over.
        }
        catch (Exception ex)
        {
            Status = "Could not compare - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _loadCts)) IsBusy = false;
        }
    }

    private void Rebuild()
    {
        var diagnoses = Rows
            .Where(r => r.Diagnosis is not null && r.Row.Dataverse is not null)
            .ToDictionary(r => r.Row.Dataverse!.SystemUserId, r => r.Diagnosis);
        var groupDiagnoses = Rows
            .Where(r => r.GroupDiagnosis is not null && r.Row.Entra is not null && r.Status == EntraMatchStatus.EntraOnly)
            .ToDictionary(r => r.Row.Entra!.Id, r => r.GroupDiagnosis, StringComparer.OrdinalIgnoreCase);

        Rows.Clear();
        foreach (var row in MembershipPlanner.MatchEntra(_teamMembers, _groupUsers))
        {
            var vm = new EntraMatchRowViewModel(row);

            // A re-read keeps the diagnosis of anyone still left over; it is still true of them.
            if (row.Status == EntraMatchStatus.DataverseOnly && diagnoses.TryGetValue(row.Dataverse!.SystemUserId, out var d))
            {
                vm.Diagnosis = d;
            }
            else if (row.Status == EntraMatchStatus.EntraOnly && groupDiagnoses.TryGetValue(row.Entra!.Id, out var g))
            {
                vm.GroupDiagnosis = g;
            }

            Rows.Add(vm);
        }

        IsDiagnosed = Rows.Where(r => r.Status == EntraMatchStatus.DataverseOnly).All(r => r.Diagnosis is not null) &&
                      Rows.Any(r => r.Status == EntraMatchStatus.DataverseOnly);
        IsGroupDiagnosed = Rows.Where(r => r.Status == EntraMatchStatus.EntraOnly).All(r => r.GroupDiagnosis is not null) &&
                           Rows.Any(r => r.Status == EntraMatchStatus.EntraOnly);
        RecountRows();
    }

    private void RecountRows()
    {
        CountAll = Rows.Count;
        CountBoth = Rows.Count(r => r.Status == EntraMatchStatus.Both);
        CountDataverseOnly = Rows.Count(r => r.Status == EntraMatchStatus.DataverseOnly);
        CountEntraOnly = Rows.Count(r => r.Status == EntraMatchStatus.EntraOnly);
        RowsView.Refresh();
        RaiseCommands();
    }

    private bool FilterRow(object o)
    {
        if (o is not EntraMatchRowViewModel row) return false;

        var statusOk = Filter switch
        {
            EntraMatchFilter.Both => row.Status == EntraMatchStatus.Both,
            EntraMatchFilter.DataverseOnly => row.Status == EntraMatchStatus.DataverseOnly,
            EntraMatchFilter.EntraOnly => row.Status == EntraMatchStatus.EntraOnly,
            _ => true
        };

        return statusOk && Matches($"{row.Name} {row.Upn} {row.Email} {row.EntraObjectId} {row.DiagnosisLabel}", SearchText);
    }

    // ---------------------------------------------------------------- diagnosis

    /// <summary>Diagnoses both sides: why 'team only' users are still in the team, and why 'group only' users are not.</summary>
    private async Task DiagnoseAsync()
    {
        var parts = new List<string>();

        if (CountDataverseOnly > 0 && await DiagnoseTeamOnlyAsync() is { } teamOnly) parts.Add(teamOnly);
        if (CountEntraOnly > 0 && await DiagnoseGroupOnlyAsync() is { } groupOnly) parts.Add(groupOnly);

        if (parts.Count == 0) return;

        Status = string.Join("  ", parts);
        Filter = (CountDataverseOnly > 0, CountEntraOnly > 0) switch
        {
            (true, false) => EntraMatchFilter.DataverseOnly,
            (false, true) => EntraMatchFilter.EntraOnly,
            _ => EntraMatchFilter.All
        };
    }

    /// <summary>
    /// Asks Entra about each 'team only' user - by object id, then by UPN, then whether it thinks
    /// they are in the group - to say why they are still in the team. The summary, or null if it failed.
    /// </summary>
    private async Task<string?> DiagnoseTeamOnlyAsync()
    {
        var targets = Rows.Where(r => r.Status == EntraMatchStatus.DataverseOnly).ToList();
        if (targets.Count == 0) return null;

        IsBusy = true;
        var done = 0;
        var groupId = Group?.Id;

        try
        {
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (row, ct) =>
            {
                var dv = row.Row.Dataverse!;

                var byId = dv.AadObjectId is { } oid ? await _graph.TryGetUserAsync(oid.ToString(), ct) : null;

                // Guest UPNs carry #EXT#, which Graph will not take as a path key.
                var byUpn = byId is null && !string.IsNullOrEmpty(dv.DomainName) && !dv.DomainName.Contains('#')
                    ? await _graph.TryGetUserAsync(dv.DomainName, ct)
                    : null;

                var entra = byId ?? byUpn;
                bool? saysMember = entra is not null && groupId is not null
                    ? await _graph.IsTransitiveMemberAsync(entra.Id, groupId, ct)
                    : null;

                var diagnosis = MembershipPlanner.Diagnose(dv, byId, byUpn, saysMember);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    row.Diagnosis = diagnosis;
                    Status = $"Checking 'team only' users in Entra... {++done}/{targets.Count}";
                });
            });

            IsDiagnosed = true;
            RowsView.Refresh();

            return $"Team only ({targets.Count:N0}): {Summarise(targets)}.";
        }
        catch (Exception ex)
        {
            Status = "Diagnosing 'team only' users failed - " + ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Looks each 'group only' user up as a Dataverse user - by object id, then UPN - to say why
    /// the sync has not added them. The summary, or null if it failed.
    /// </summary>
    private async Task<string?> DiagnoseGroupOnlyAsync()
    {
        var targets = Rows.Where(r => r.Status == EntraMatchStatus.EntraOnly).ToList();
        if (targets.Count == 0) return null;

        IsBusy = true;
        Status = $"Looking up {targets.Count:N0} 'group only' user(s) in Dataverse...";

        try
        {
            var users = await _client.FindUsersForEntraUsersAsync(targets.Select(r => r.Row.Entra!));

            var byObjectId = users
                .Where(u => u.AadObjectId is not null)
                .GroupBy(u => u.AadObjectId!.Value.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var byUpn = users
                .Where(u => !string.IsNullOrEmpty(u.DomainName))
                .GroupBy(u => u.DomainName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var row in targets)
            {
                var entra = row.Row.Entra!;
                var idMatch = byObjectId.GetValueOrDefault(entra.Id);
                var upnMatch = idMatch is null && entra.Upn is { } upn ? byUpn.GetValueOrDefault(upn) : null;

                row.GroupDiagnosis = MembershipPlanner.DiagnoseGroupOnly(entra, idMatch, upnMatch, SelectedTeam?.MembershipType);
            }

            IsGroupDiagnosed = true;
            RowsView.Refresh();
            RaiseCommands();

            return $"Group only ({targets.Count:N0}): {Summarise(targets)}.";
        }
        catch (Exception ex)
        {
            Status = "Diagnosing 'group only' users failed - " + ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Summarise(IEnumerable<EntraMatchRowViewModel> rows) =>
        string.Join(" · ", rows
            .GroupBy(r => r.DiagnosisLabel)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}"));

    private bool _isGroupDiagnosed;
    /// <summary>Every 'group only' user has been looked up in Dataverse.</summary>
    public bool IsGroupDiagnosed
    {
        get => _isGroupDiagnosed;
        private set => SetProperty(ref _isGroupDiagnosed, value);
    }

    /// <summary>
    /// The Add-AdminPowerAppsSyncUser script for everyone who needs provisioning, for a Power
    /// Platform admin to run where pulling them in is not possible.
    /// </summary>
    private void CopyUserSyncScript()
    {
        var users = Rows
            .Where(r => r.GroupDiagnosis is { } d && MembershipPlanner.NeedsUserSync(d.Category))
            .Select(r => (r.Row.Entra!.Id, r.Name, r.DiagnosisLabel ?? string.Empty))
            .ToList();

        var script = MembershipPlanner.BuildUserSyncScript(users, Session.EnvironmentId);

        Status = ClipboardText.TryCopy(script, out var failure)
            ? $"Copied a PowerShell script provisioning {users.Count:N0} user(s). A Power Platform admin can run it."
            : "Could not copy the script - " + failure;
    }

    // ---------------------------------------------------------------- writes

    /// <summary>
    /// Previews SyncGroupMembersToTeam: removals are the 'team only' users; additions are the
    /// 'group only' users who already have a Dataverse user record, the only ones it can add.
    /// </summary>
    private async Task PreviewSyncAsync()
    {
        if (SelectedTeam is not { } team || Group is not { } group) return;

        IsBusy = true;
        Status = "Preparing the sync preview...";

        WritePermission permission;
        List<MembershipChange> changes;
        int cannotAdd;

        try
        {
            permission = await EnsurePermissionAsync();

            var groupOnly = Rows.Where(r => r.Status == EntraMatchStatus.EntraOnly).ToList();
            var existing = await _client.GetUsersByAadObjectIdsAsync(groupOnly.Select(r => r.Row.Entra!.Id));
            var byObjectId = existing
                .Where(u => u.AadObjectId is not null)
                .GroupBy(u => u.AadObjectId!.Value.ToString())
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            changes = new List<MembershipChange>();

            foreach (var row in groupOnly)
            {
                if (!byObjectId.TryGetValue(row.Row.Entra!.Id, out var user)) continue;

                changes.Add(new MembershipChange(MembershipChangeKind.Add, user.SystemUserId, row.Name, row.Upn,
                    user.IsDisabled == true
                        ? "In the group; has a Dataverse user, but it is disabled."
                        : "In the group, and has a Dataverse user."));
            }

            cannotAdd = groupOnly.Count - changes.Count;

            foreach (var row in Rows.Where(r => r.Status == EntraMatchStatus.DataverseOnly))
            {
                changes.Add(new MembershipChange(MembershipChangeKind.Remove, row.Row.Dataverse!.SystemUserId, row.Name, row.Upn,
                    row.Diagnosis is { } d ? $"{d.Label}: {d.Detail}" : "In the team, not in the Entra group."));
            }
        }
        catch (Exception ex)
        {
            Status = "Could not prepare the sync - " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var notes = new List<string>();
        if (cannotAdd > 0)
        {
            notes.Add($"{cannotAdd:N0} group member(s) have no Dataverse user yet, so the sync cannot add them - they are added " +
                      "when they first sign in to the environment.");
        }

        if (team.MembershipType is { } mt && mt != 0)
        {
            notes.Add($"Membership type '{team.MembershipTypeLabel ?? mt.ToString()}' limits what the sync takes from the group, " +
                      "so the forecast may overstate it.");
        }

        if (!IsDiagnosed && CountDataverseOnly > 0)
        {
            notes.Add("Some users the sync is expected to remove may be left behind (deleted or disabled in Entra). " +
                      "Run Diagnose afterwards, and remove those by hand.");
        }

        await ConfirmAsync(new MembershipApplyRequest
        {
            Operation = "Sync team from Entra group",
            TargetKind = "team",
            TargetName = team.Name,
            SourceKind = "Entra group",
            SourceName = group.DisplayName ?? group.Id,
            EnvironmentName = Session.Title,
            EnvironmentHost = Session.EnvironmentHost,
            Account = Session.AccountName,
            Permission = permission,
            Changes = changes,
            ApplyAll = ct => _client.SyncGroupMembersToTeamAsync(team.TeamId, ct),
            ReadMemberIds = ct => ReadTeamMemberIdsAsync(team.TeamId, ct),
            Note = notes.Count == 0 ? null : string.Join(" ", notes)
        });
    }

    /// <summary>
    /// Previews removing, one at a time, the 'team only' users the sync leaves behind. Diagnoses
    /// first if that has not been done, because the diagnosis decides who is offered and ticked.
    /// </summary>
    private async Task PreviewRemovalAsync()
    {
        if (SelectedTeam is not { } team) return;

        if (!IsDiagnosed) await DiagnoseTeamOnlyAsync();
        if (!IsDiagnosed) return;

        var offered = Rows
            .Where(r => r.Status == EntraMatchStatus.DataverseOnly && r.Diagnosis is { } d && MembershipPlanner.CanRemove(d.Category))
            .ToList();

        var withheld = Rows.Count(r => r.Status == EntraMatchStatus.DataverseOnly) - offered.Count;

        if (offered.Count == 0)
        {
            Status = "Nobody to remove: every 'team only' user is one Entra says is a member, or could not be checked.";
            return;
        }

        WritePermission permission;
        try
        {
            IsBusy = true;
            Status = "Checking what kind of environment this is...";
            permission = await EnsurePermissionAsync();
        }
        catch (Exception ex)
        {
            Status = "Could not prepare the removal - " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var changes = offered
            .Select(r => new MembershipChange(
                MembershipChangeKind.Remove, r.Row.Dataverse!.SystemUserId, r.Name, r.Upn,
                $"{r.DiagnosisLabel}: {r.DiagnosisDetail}",
                IncludedByDefault: MembershipPlanner.IsRemovableByDefault(r.Diagnosis!.Category)))
            .ToList();

        await ConfirmAsync(new MembershipApplyRequest
        {
            Operation = "Remove users the sync left behind",
            TargetKind = "team",
            TargetName = team.Name,
            SourceKind = "Entra group",
            SourceName = Group?.DisplayName ?? GroupLabel,
            EnvironmentName = Session.Title,
            EnvironmentHost = Session.EnvironmentHost,
            Account = Session.AccountName,
            Permission = permission,
            Changes = changes,
            ApplyEach = (change, ct) => _client.RemoveTeamMemberAsync(team.TeamId, change.SystemUserId, ct),
            ReadMemberIds = ct => ReadTeamMemberIdsAsync(team.TeamId, ct),
            Note = "Ticked: users deleted or disabled in Entra, or whose Entra object id is stale - the ones the sync does not " +
                   "remove. Others are listed unticked; tick them only if you are sure." +
                   (withheld > 0 ? $" {withheld:N0} user(s) Entra says ARE members, or could not be checked, are not offered." : string.Empty)
        });
    }

    /// <summary>
    /// Previews pulling 'group only' users into Dataverse: a WhoAmI made as each of them
    /// (CallerObjectId) triggers Dataverse's just-in-time user sync - the on-demand alternative to
    /// them signing in - creating or re-evaluating their user record. Only users with no record or
    /// a disabled one are offered, as that is what this fixes. Then SyncGroupMembersToTeam runs so
    /// the new users join the team - but only when the team has nobody the sync would remove;
    /// otherwise that is left to Sync from Entra, whose preview shows the removals.
    /// </summary>
    private async Task PreviewPullInAsync()
    {
        if (SelectedTeam is not { } team || Group is not { } group) return;

        if (!IsGroupDiagnosed && await DiagnoseGroupOnlyAsync() is { } summary) Status = summary;
        if (!IsGroupDiagnosed) return;

        var groupOnly = Rows.Where(r => r.Status == EntraMatchStatus.EntraOnly).ToList();
        var offered = groupOnly
            .Where(r => r.GroupDiagnosis is { } d && MembershipPlanner.CanPullIn(d.Category) && Guid.TryParse(r.Row.Entra!.Id, out _))
            .ToList();

        if (offered.Count == 0)
        {
            Status = "Nobody to pull in: no 'group only' user is missing a Dataverse user or has a disabled one. " +
                     "The Diagnosis column says why each is not in the team.";
            return;
        }

        WritePermission permission;
        try
        {
            IsBusy = true;
            Status = "Checking what kind of environment this is...";
            permission = await EnsurePermissionAsync();
        }
        catch (Exception ex)
        {
            Status = "Could not prepare the pull-in - " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // The change's id is the Entra object id: these users may have no systemuserid yet.
        var changes = offered
            .Select(r => new MembershipChange(MembershipChangeKind.Add, Guid.Parse(r.Row.Entra!.Id), r.Name, r.Upn,
                $"{r.DiagnosisLabel}: {r.DiagnosisDetail}"))
            .ToList();

        // The sync removes as well as adds, so it only follows on when there is nobody for it to remove.
        var syncAfter = CountDataverseOnly == 0;

        var notes = new List<string>
        {
            "Each ticked user gets one WhoAmI made as them (CallerObjectId), which makes Dataverse sync their user record " +
            "just in time, as their own sign-in would. You need the Act on Behalf of Another User privilege (Delegate role, " +
            "or System Administrator). Users without a licence or outside the environment's security group still will not be added.",
            syncAfter
                ? "Then SyncGroupMembersToTeam runs, so the new users join the team - the team has nobody for it to remove."
                : $"SyncGroupMembersToTeam is NOT run afterwards: the team has {CountDataverseOnly:N0} 'team only' user(s) it would " +
                  "remove. Use Sync from Entra next - its preview shows who it removes."
        };

        var others = groupOnly.Count - offered.Count;
        if (others > 0)
        {
            notes.Add($"{others:N0} other 'group only' user(s) are not offered - pulling them in would not help " +
                      "(see the Diagnosis column; Copy provisioning script covers users linked to another Entra id).");
        }

        var ran = await ConfirmAsync(new MembershipApplyRequest
        {
            Operation = "Pull in group members",
            TargetKind = "team",
            TargetName = team.Name,
            SourceKind = "Entra group",
            SourceName = group.DisplayName ?? group.Id,
            EnvironmentName = Session.Title,
            EnvironmentHost = Session.EnvironmentHost,
            Account = Session.AccountName,
            Permission = permission,
            Changes = changes,
            AddVerb = "Pull in",
            AddedResult = "User record synced",
            ApplyEach = (change, ct) => _client.WhoAmIAsAsync(change.SystemUserId, ct),
            AfterAll = syncAfter ? ct => _client.SyncGroupMembersToTeamAsync(team.TeamId, ct) : null,
            AfterAllLabel = "SyncGroupMembersToTeam",
            ReadMemberIds = ct => ReadTeamMemberIdsAsync(team.TeamId, ct),
            Note = string.Join(" ", notes)
        });

        if (!ran) return;

        var pulled = changes.Select(c => c.SystemUserId.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nowInTeam = Rows.Count(r => r.Status == EntraMatchStatus.Both && r.Row.Entra is { } e && pulled.Contains(e.Id));

        Status += $" {nowInTeam:N0} of {changes.Count:N0} pulled-in user(s) are now in the team." +
                  (nowInTeam < changes.Count
                      ? syncAfter
                          ? " The rest may land in a few minutes - Re-read, then Diagnose to see why any remain."
                          : " Run Sync from Entra to add the rest."
                      : string.Empty);
    }

    /// <summary>Shows the confirmation; true if anything was run, after which the team has been re-read.</summary>
    private async Task<bool> ConfirmAsync(MembershipApplyRequest request)
    {
        var viewModel = new MembershipApplyViewModel(request);
        var window = new Views.MembershipApplyWindow { DataContext = viewModel, Owner = OwnerWindow() };
        window.ShowDialog();
        var changed = viewModel.AnyWritesAttempted;

        if (!changed)
        {
            Status = request.Permission.Allowed ? "Nothing was changed." : request.Permission.Reason;
            return false;
        }

        var outcome = viewModel.Status;
        if (SelectedTeam is not { } team) return true;

        // The team's side is all that can have changed; the group is not re-read.
        try
        {
            IsBusy = true;
            _teamMembers = await _client.GetTeamMembersAsync(team.TeamId);
            Rebuild();
        }
        catch (Exception ex)
        {
            outcome += " Re-reading the team failed - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        Status = outcome;
        return true;
    }

    private async Task<IReadOnlySet<Guid>> ReadTeamMemberIdsAsync(Guid teamId, CancellationToken ct) =>
        (await _client.GetTeamMembersAsync(teamId, ct)).Select(u => u.SystemUserId).ToHashSet();

    /// <summary>Asked afresh for every preview, so allowing or blocking writes from the sidebar
    /// applies to a window that is already open.</summary>
    private Task<WritePermission> EnsurePermissionAsync() => Session.EvaluateWritePermissionAsync();

    // ---------------------------------------------------------------- button hints

    /// <summary>What each footer button does - or, while it is disabled, why.</summary>
    public string DiagnoseHint =>
        !HasComparison ? "Pick a team to compare first."
        : CountDataverseOnly + CountEntraOnly == 0 ? "Nothing to diagnose: the team and the group match."
        : "Say why each 'team only' user is still in the team (asks Entra) and why each 'group only' user is not " +
          "(looks up their Dataverse user). Read-only.";

    public string RemoveLeftoversHint =>
        !HasComparison ? "Pick a team to compare first."
        : CountDataverseOnly == 0 ? "Nothing to remove: nobody is in the team without being in the Entra group."
        : "Preview removing the 'team only' users the sync leaves behind. Nothing changes until you confirm.";

    public string PullInHint =>
        !HasComparison ? "Pick a team to compare first."
        : Group is null ? "Unavailable: the Entra group was not found."
        : CountEntraOnly == 0 ? "Nobody to pull in: every group member is already in the team."
        : "Preview provisioning 'group only' users who have no Dataverse user, or a disabled one, with a WhoAmI made as " +
          "each of them - then a team sync. Nothing changes until you confirm.";

    public string CopyScriptHint => NeedsUserSyncCount > 0
        ? $"Copy an Add-AdminPowerAppsSyncUser PowerShell script for the {NeedsUserSyncCount:N0} user(s) who need provisioning, for a Power Platform admin to run."
        : "Diagnose first: the script covers 'group only' users who need provisioning into the environment.";

    public string SyncHint =>
        !HasComparison ? "Pick a team to compare first."
        : Group is null ? "Unavailable: the Entra group was not found, and syncing against a missing group could empty the team."
        : "Preview Dataverse's SyncGroupMembersToTeam for this team. It can only add group members who already " +
          "have a Dataverse user. Nothing changes until you confirm.";

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(DiagnoseHint));
        OnPropertyChanged(nameof(RemoveLeftoversHint));
        OnPropertyChanged(nameof(SyncHint));
        OnPropertyChanged(nameof(PullInHint));
        OnPropertyChanged(nameof(CopyScriptHint));
        OnPropertyChanged(nameof(NeedsUserSyncCount));
        OnPropertyChanged(nameof(LeftoverCount));
        OnPropertyChanged(nameof(PullInCount));
        OnPropertyChanged(nameof(RemoveLeftoversLabel));
        OnPropertyChanged(nameof(PullInLabel));
        CopyUserSyncScriptCommand.RaiseCanExecuteChanged();

        ReloadCommand.RaiseCanExecuteChanged();
        DiagnoseCommand.RaiseCanExecuteChanged();
        SyncCommand.RaiseCanExecuteChanged();
        RemoveLeftoversCommand.RaiseCanExecuteChanged();
        PullInCommand.RaiseCanExecuteChanged();
    }

    private Window? OwnerWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => ReferenceEquals(w.DataContext, this))
        ?? Application.Current?.MainWindow;

    internal static bool Matches(string haystack, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        return search
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    public void Dispose()
    {
        _ageTimer?.Stop();
        _loadCts?.Cancel();
        _graph.Dispose();
    }
}
