using System.Collections.ObjectModel;
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
    private CancellationTokenSource? _cts;

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

    private string _status = "Choose what to look up.";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private void RaiseCommands()
    {
        RunCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private bool CanRunTab => Tab switch
    {
        SecurityLookupTab.WhoCan => !string.IsNullOrWhiteSpace(TableName) && !string.IsNullOrWhiteSpace(Action),
        SecurityLookupTab.Effective => SelectedUser is not null,
        SecurityLookupTab.SecuredColumn => !string.IsNullOrWhiteSpace(TableName) && !string.IsNullOrWhiteSpace(ColumnName),
        _ => LeftRole is not null && RightRole is not null
    };

    private bool HasResults => Tab switch
    {
        SecurityLookupTab.WhoCan => Holders.Count > 0 || Grants.Count > 0,
        SecurityLookupTab.Effective => EffectiveRows.Count > 0,
        SecurityLookupTab.SecuredColumn => FieldGrants.Count > 0,
        _ => Differences.Count > 0
    };

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
            Status = "Could not read the lists - " + ex.Message;
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
        var privilege = (await Client.GetTablePrivilegesAsync(table, ct)).FirstOrDefault(p => p.Action == Action);

        if (privilege is null)
        {
            Status = $"{table} has no {Action} privilege - it may be organization-owned or not a table.";
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

        Status = $"{Grants.Count:N0} role(s) grant {privilege.Name}; {Holders.Count(h => h.Kind == "User"):N0} user(s) and " +
                 $"{Holders.Count(h => h.Kind == "Team"):N0} team(s) hold them.";
        RaiseCommands();
    }

    internal async Task EffectiveAsync(CancellationToken ct)
    {
        EffectiveRows.Clear();
        var user = SelectedUser!;

        Status = $"Reading {user.FullName}'s roles...";
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

        Status = $"{user.FullName} holds {assignments.Count:N0} role assignment(s) ({assignments.Count(a => !a.IsDirect):N0} through teams), " +
                 $"reaching {EffectiveRows.Count:N0} table(s). Hover a cell for the roles that grant it.";
        RaiseCommands();
    }

    internal async Task SecuredColumnAsync(CancellationToken ct)
    {
        FieldGrants.Clear();
        Status = "Reading field security profiles...";

        foreach (var grant in await Client.GetFieldPermissionGrantsAsync(TableName.Trim(), ColumnName.Trim(), ct)) FieldGrants.Add(grant);

        Status = FieldGrants.Count == 0
            ? $"No field security profile grants anything on {TableName}.{ColumnName} - if it is secured, only System Administrators see it."
            : $"{FieldGrants.Count:N0} profile(s) grant access to {TableName}.{ColumnName}.";
        RaiseCommands();
    }

    internal async Task CompareRolesAsync(CancellationToken ct)
    {
        Differences.Clear();
        var left = LeftSession?.Client ?? Client;
        var right = RightSession?.Client ?? Client;

        Status = "Reading both roles...";
        var a = await left.GetRolePrivilegesAsync(LeftRole!.RoleId, ct);
        var b = await right.GetRolePrivilegesAsync(RightRole!.RoleId, ct);

        foreach (var difference in SecurityLookup.Diff(a, b)) Differences.Add(difference);

        Status = Differences.Count == 0
            ? $"{LeftRole.Name} and {RightRole.Name} grant exactly the same privileges."
            : $"{Differences.Count:N0} privilege(s) differ.";
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
