using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

public enum SecurityLookupTab
{
    WhoCan,
    Effective,
    SecuredColumn,
    CompareRoles
}

/// <summary>
/// The security questions the role editor does not answer: who can do something to a table, what
/// a user can actually do, who sees a secured column, and how two roles differ - in one
/// environment or across two. Read-only.
/// </summary>
public sealed class SecurityLookupViewModel : ObservableObject
{
    private readonly EnvironmentSessionViewModel _session;
    private readonly Dictionary<SecurityLookupTab, TabRead> _reads = new();
    private CancellationTokenSource? _cts;

    /// <summary>What a tab's last lookup covered, and what to say if it found nothing.</summary>
    private sealed record TabRead(string Summary, string EmptyHeading, string EmptyText);

    public SecurityLookupViewModel(EnvironmentSessionViewModel session, IEnumerable<EnvironmentSessionViewModel> sessions)
    {
        _session = session;
        Sessions = new ObservableCollection<EnvironmentSessionViewModel>(sessions.Where(s => s.IsConnected));
        if (!Sessions.Contains(session)) Sessions.Insert(0, session);

        RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => !IsBusy && CanRunTab);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ExportCommand = new RelayCommand(_ => Export(), _ => !IsBusy && HasResults);

        _leftSession = session;
        _rightSession = session;
    }

    public string Title => $"Security lookup — {_session.Title}";

    /// <summary>The tab the window was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;
    private DataverseClient Client => _session.Client ?? throw new InvalidOperationException("Not connected.");

    public ObservableCollection<EnvironmentSessionViewModel> Sessions { get; }
    public IReadOnlyList<string> Actions => SecurityLookup.Actions;

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ExportCommand { get; }

    private SecurityLookupTab _tab;
    public SecurityLookupTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value)) return;
            OnPropertyChanged(nameof(TabIndex));
            if (!IsBusy) Status = string.Empty;
            RaiseCommands();
            _ = EnsureListsAsync();
        }
    }

    /// <summary>The tab control's index, in the order of <see cref="SecurityLookupTab"/>.</summary>
    public int TabIndex
    {
        get => (int)Tab;
        set => Tab = (SecurityLookupTab)value;
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

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>"Read 3 roles granting prvDeleteAccount in 1.4 s - 12 users and 2 teams hold them", for the tab on screen.</summary>
    public string ReadSummary => _reads.TryGetValue(Tab, out var read) ? read.Summary : string.Empty;

    /// <summary>The status bar's left side: what is happening, else what the tab's last lookup covered.</summary>
    public string StatusLine => string.IsNullOrEmpty(Status) ? ReadSummary : Status;

    private string _notes = string.Empty;
    /// <summary>Lists that could not be read for the pickers - the warning banner.</summary>
    public string Notes
    {
        get => _notes;
        private set => SetProperty(ref _notes, value);
    }

    private void RaiseCommands()
    {
        RunCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ResultCountLabel));
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ReadSummary));
        OnPropertyChanged(nameof(StatusLine));
    }

    private bool CanRunTab => Tab switch
    {
        SecurityLookupTab.WhoCan => !string.IsNullOrWhiteSpace(TableName) && !string.IsNullOrWhiteSpace(Action),
        SecurityLookupTab.Effective => SelectedUser is not null,
        SecurityLookupTab.SecuredColumn => !string.IsNullOrWhiteSpace(TableName) && !string.IsNullOrWhiteSpace(ColumnName),
        _ => LeftRole is not null && RightRole is not null
    };

    /// <summary>The tab on screen has something to show.</summary>
    public bool HasResults => Tab switch
    {
        SecurityLookupTab.WhoCan => Holders.Count > 0 || Grants.Count > 0,
        SecurityLookupTab.Effective => EffectiveRows.Count > 0,
        SecurityLookupTab.SecuredColumn => FieldGrants.Count > 0,
        _ => Differences.Count > 0
    };

    /// <summary>Nothing to list on the tab on screen and nothing running - the empty state shows in the grid's place.</summary>
    public bool IsEmpty => !IsBusy && !HasResults;

    /// <summary>The status bar's right side: how many rows the tab on screen shows.</summary>
    public string ResultCountLabel => Tab switch
    {
        SecurityLookupTab.WhoCan => Plural(Holders.Count, "holder"),
        SecurityLookupTab.Effective => Plural(EffectiveRows.Count, "table"),
        SecurityLookupTab.SecuredColumn => Plural(FieldGrants.Count, "profile"),
        _ => Plural(Differences.Count, "difference")
    };

    /// <summary>Why the tab's list is empty and what to change - shown in its place.</summary>
    public string EmptyHeading => _reads.TryGetValue(Tab, out var read) ? read.EmptyHeading : Tab switch
    {
        SecurityLookupTab.WhoCan => "Who can do it?",
        SecurityLookupTab.Effective => "Choose a user",
        SecurityLookupTab.SecuredColumn => "Who sees a secured column?",
        _ => "Choose two roles"
    };

    public string EmptyText => _reads.TryGetValue(Tab, out var read) ? read.EmptyText : Tab switch
    {
        SecurityLookupTab.WhoCan => "Choose a privilege and a table above, then Look up.",
        SecurityLookupTab.Effective => "Pick a user above, then Show - the widest depth across every role they hold, directly and through their teams.",
        SecurityLookupTab.SecuredColumn => "Enter a table and a column above, then Look up.",
        _ => "Pick a role on each side - in one environment or two - then Compare."
    };

    private static string Plural(int count, string word) => $"{count:N0} {word}{(count == 1 ? string.Empty : "s")}";

    private static string Seconds(Stopwatch clock) => $"{clock.Elapsed.TotalSeconds:0.0} s";

    // ---------------------------------------------------------------- inputs

    public ObservableCollection<EntitySummary> Tables { get; } = new();
    public ObservableCollection<UserInfo> Users { get; } = new();
    public ObservableCollection<SecurityRoleInfo> LeftRoles { get; } = new();
    public ObservableCollection<SecurityRoleInfo> RightRoles { get; } = new();

    private string _tableName = string.Empty;
    /// <summary>A table's logical name, typed or picked.</summary>
    public string TableName
    {
        get => _tableName;
        set
        {
            if (SetProperty(ref _tableName, value)) RunCommand.RaiseCanExecuteChanged();
        }
    }

    private string _action = "Delete";
    public string Action
    {
        get => _action;
        set
        {
            if (SetProperty(ref _action, value)) RunCommand.RaiseCanExecuteChanged();
        }
    }

    private string _columnName = string.Empty;
    public string ColumnName
    {
        get => _columnName;
        set
        {
            if (SetProperty(ref _columnName, value)) RunCommand.RaiseCanExecuteChanged();
        }
    }

    private UserInfo? _selectedUser;
    public UserInfo? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (SetProperty(ref _selectedUser, value)) RunCommand.RaiseCanExecuteChanged();
        }
    }

    private EnvironmentSessionViewModel? _leftSession;
    public EnvironmentSessionViewModel? LeftSession
    {
        get => _leftSession;
        set
        {
            if (!SetProperty(ref _leftSession, value)) return;
            _ = LoadRolesAsync(value, LeftRoles);
        }
    }

    private EnvironmentSessionViewModel? _rightSession;
    public EnvironmentSessionViewModel? RightSession
    {
        get => _rightSession;
        set
        {
            if (!SetProperty(ref _rightSession, value)) return;
            _ = LoadRolesAsync(value, RightRoles);
        }
    }

    private SecurityRoleInfo? _leftRole;
    public SecurityRoleInfo? LeftRole
    {
        get => _leftRole;
        set
        {
            if (!SetProperty(ref _leftRole, value)) return;

            // The same role on the other side is the usual comparison across environments.
            if (value is not null && RightRole is null && !ReferenceEquals(LeftSession, RightSession))
            {
                RightRole = RightRoles.FirstOrDefault(r => string.Equals(r.Name, value.Name, StringComparison.OrdinalIgnoreCase));
            }

            RunCommand.RaiseCanExecuteChanged();
        }
    }

    private SecurityRoleInfo? _rightRole;
    public SecurityRoleInfo? RightRole
    {
        get => _rightRole;
        set
        {
            if (SetProperty(ref _rightRole, value)) RunCommand.RaiseCanExecuteChanged();
        }
    }

    // ---------------------------------------------------------------- results

    public ObservableCollection<RoleGrantRow> Grants { get; } = new();
    public ObservableCollection<AccessHolder> Holders { get; } = new();
    public ObservableCollection<EffectiveRow> EffectiveRows { get; } = new();
    public ObservableCollection<FieldPermissionGrant> FieldGrants { get; } = new();
    public ObservableCollection<RoleDifference> Differences { get; } = new();

    /// <summary>A role granting the privilege looked up, and how far.</summary>
    public sealed record RoleGrantRow(string Role, PrivilegeDepth Depth)
    {
        public string DepthLabel => PrivilegeMatrix.DepthLabel(Depth);
    }

    // ---------------------------------------------------------------- loading lists

    private bool _listsLoaded;

    /// <summary>Tables, users and roles for the pickers, read once when the window first needs them.</summary>
    public async Task EnsureListsAsync()
    {
        if (_listsLoaded || _session.Client is null) return;
        _listsLoaded = true;

        try
        {
            foreach (var table in (await Client.GetEntitiesAsync()).OrderBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                Tables.Add(table);
            }

            foreach (var user in (await Client.GetUsersAsync()).Where(u => !u.IsDisabled)) Users.Add(user);

            await LoadRolesAsync(LeftSession, LeftRoles);
            await LoadRolesAsync(RightSession, RightRoles);
        }
        catch (Exception ex)
        {
            Notes = "Could not read the tables and users for the pickers, so type a table's logical name instead - " + ex.Message;
        }
    }

    private static async Task LoadRolesAsync(EnvironmentSessionViewModel? session, ObservableCollection<SecurityRoleInfo> into)
    {
        into.Clear();
        if (session?.Client is null) return;

        try
        {
            foreach (var role in await session.Client.GetSecurityRolesAsync()) into.Add(role);
        }
        catch (Exception ex)
        {
            Log.Warn("Security roles could not be read for the lookup", ex);
        }
    }

    // ---------------------------------------------------------------- running

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        _reads.Remove(Tab);
        IsBusy = true;

        try
        {
            switch (Tab)
            {
                case SecurityLookupTab.WhoCan: await WhoCanAsync(_cts.Token); break;
                case SecurityLookupTab.Effective: await EffectiveAsync(_cts.Token); break;
                case SecurityLookupTab.SecuredColumn: await SecuredColumnAsync(_cts.Token); break;
                default: await CompareRolesAsync(_cts.Token); break;
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = "Could not look this up - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task WhoCanAsync(CancellationToken ct)
    {
        Grants.Clear();
        Holders.Clear();

        var table = TableName.Trim();
        Status = $"Reading {table}'s privileges...";
        var clock = Stopwatch.StartNew();
        var privilege = (await Client.GetTablePrivilegesAsync(table, ct)).FirstOrDefault(p => p.Action == Action);

        if (privilege is null)
        {
            _reads[Tab] = new TabRead($"Read {table}'s privileges in {Seconds(clock)}", $"No {Action} privilege",
                $"{table} has no {Action} privilege - it may be organization-owned or not a table. Try another table or privilege.");
            Status = string.Empty;
            RaiseCommands();
            return;
        }

        var held = new List<HeldRole>();
        var grants = await Client.GetPrivilegeGrantsAsync(privilege.PrivilegeId, ct);

        foreach (var grant in grants)
        {
            Status = $"Reading who holds {grant.RoleName}...";
            var depth = (await Client.GetRolePrivilegesAsync(grant.RootRoleId, ct))
                .FirstOrDefault(p => p.PrivilegeId == privilege.PrivilegeId)?.Depth ?? PrivilegeDepth.None;
            if (depth == PrivilegeDepth.None) continue;

            Grants.Add(new RoleGrantRow(grant.RoleName, depth));

            foreach (var holder in await Client.GetRoleHoldersAsync(grant.RootRoleId, ct))
            {
                held.Add(new HeldRole(grant.RoleName, depth, holder.Name, holder.IsTeam, holder.BusinessUnit, null));
                if (!holder.IsTeam) continue;

                foreach (var member in await Client.GetTeamMembersAsync(holder.Id, ct))
                {
                    held.Add(new HeldRole(grant.RoleName, depth, member.FullName, false, holder.BusinessUnit, holder.Name));
                }
            }
        }

        foreach (var holder in SecurityLookup.Holders(held)) Holders.Add(holder);

        _reads[Tab] = new TabRead(
            $"Read {Plural(Grants.Count, "role")} granting {privilege.Name} in {Seconds(clock)} - " +
            $"{Plural(Holders.Count(h => h.Kind == "User"), "user")} and {Plural(Holders.Count(h => h.Kind == "Team"), "team")} hold them",
            "No one can do it",
            $"No role grants {Action} on {table} at any depth. Try another privilege or table.");
        Status = string.Empty;
        RaiseCommands();
    }

    internal async Task EffectiveAsync(CancellationToken ct)
    {
        EffectiveRows.Clear();
        var user = SelectedUser!;

        Status = $"Reading {user.FullName}'s roles...";
        var clock = Stopwatch.StartNew();
        var assignments = new List<RoleAssignment>(await Client.GetUserRolesAsync(user.SystemUserId, ct));
        foreach (var team in await Client.GetUserTeamsAsync(user.SystemUserId, ct))
        {
            assignments.AddRange(await Client.GetTeamRolesAsync(team.TeamId, team.Name, ct));
        }

        var roles = new List<(string, IReadOnlyList<RolePrivilege>)>();
        var privileges = new Dictionary<Guid, IReadOnlyList<RolePrivilege>>();

        foreach (var assignment in assignments)
        {
            if (!privileges.TryGetValue(assignment.DefinitionId, out var granted))
            {
                Status = $"Reading {assignment.RoleName}...";
                granted = await Client.GetRolePrivilegesAsync(assignment.DefinitionId, ct);
                privileges[assignment.DefinitionId] = granted;
            }

            roles.Add((assignment.IsDirect ? assignment.RoleName : $"{assignment.RoleName} ({assignment.Via})", granted));
        }

        foreach (var row in SecurityLookup.Effective(roles)) EffectiveRows.Add(row);

        _reads[Tab] = new TabRead(
            $"Read {user.FullName}'s {Plural(assignments.Count, "role assignment")} ({assignments.Count(a => !a.IsDirect):N0} through teams) " +
            $"in {Seconds(clock)} - hover a cell for the roles that grant it",
            "No access",
            $"{user.FullName} holds no role that grants a table privilege - directly or through a team. Try another user.");
        Status = string.Empty;
        RaiseCommands();
    }

    internal async Task SecuredColumnAsync(CancellationToken ct)
    {
        FieldGrants.Clear();
        Status = "Reading field security profiles...";
        var clock = Stopwatch.StartNew();

        foreach (var grant in await Client.GetFieldPermissionGrantsAsync(TableName.Trim(), ColumnName.Trim(), ct)) FieldGrants.Add(grant);

        _reads[Tab] = new TabRead(
            $"Read the field security profiles for {TableName}.{ColumnName} in {Seconds(clock)}",
            "No profile grants it",
            $"No field security profile grants anything on {TableName}.{ColumnName} - if it is secured, only System Administrators see it.");
        Status = string.Empty;
        RaiseCommands();
    }

    internal async Task CompareRolesAsync(CancellationToken ct)
    {
        Differences.Clear();
        var left = LeftSession?.Client ?? Client;
        var right = RightSession?.Client ?? Client;

        Status = "Reading both roles...";
        var clock = Stopwatch.StartNew();
        var a = await left.GetRolePrivilegesAsync(LeftRole!.RoleId, ct);
        var b = await right.GetRolePrivilegesAsync(RightRole!.RoleId, ct);

        foreach (var difference in SecurityLookup.Diff(a, b)) Differences.Add(difference);

        _reads[Tab] = new TabRead(
            $"Read {Plural(a.Count + b.Count, "privilege")} of {LeftRole.Name} and {RightRole.Name} in {Seconds(clock)}",
            "No differences",
            $"{LeftRole.Name} and {RightRole.Name} grant exactly the same privileges. Try another pair, or the same role in another environment.");
        Status = string.Empty;
        RaiseCommands();
    }

    // ---------------------------------------------------------------- export

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"security-{Tab}-{_session.Title}.csv".Replace(' ', '-')
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            CsvExporter.WriteLines(dialog.FileName, ExportLines());
            Status = $"Exported to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not export - " + ex.Message;
        }
    }

    internal IReadOnlyList<string> ExportLines() => Tab switch
    {
        SecurityLookupTab.WhoCan =>
            Holders.Select(h => CsvExporter.Line(h.Name, h.Kind, h.DepthLabel, h.Via))
                .Prepend(CsvExporter.Line("Name", "Kind", $"{Action} depth on {TableName}", "Via")).ToList(),
        SecurityLookupTab.Effective =>
            EffectiveRows.Select(r => CsvExporter.Line(SecurityLookup.Actions.Select(a => r[a].Label).Prepend(r.Table)))
                .Prepend(CsvExporter.Line(SecurityLookup.Actions.Prepend("Table"))).ToList(),
        SecurityLookupTab.SecuredColumn =>
            FieldGrants.Select(g => CsvExporter.Line(g.ProfileName, Yes(g.CanRead), Yes(g.CanCreate), Yes(g.CanUpdate),
                    string.Join("; ", g.Users), string.Join("; ", g.Teams)))
                .Prepend(CsvExporter.Line("Profile", "Read", "Create", "Update", "Users", "Teams")).ToList(),
        _ =>
            Differences.Select(d => CsvExporter.Line(d.Table, d.Action, d.LeftLabel, d.RightLabel))
                .Prepend(CsvExporter.Line("Table", "Privilege", LeftRole?.Name ?? "Left", RightRole?.Name ?? "Right")).ToList()
    };

    private static string Yes(bool value) => value ? "Yes" : "No";
}
