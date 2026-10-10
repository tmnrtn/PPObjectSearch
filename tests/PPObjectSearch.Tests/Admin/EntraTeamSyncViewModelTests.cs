using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

/// <summary>Entra team sync: an Entra group team beside its group, the diagnoses, and the confirmed writes that close the gap.</summary>
public sealed class EntraTeamSyncViewModelTests : IDisposable
{
    private const string F = "@OData.Community.Display.V1.FormattedValue";

    private static readonly Guid SalesTeam = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OrphanTeam = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private const string Group = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string MissingGroup = "aaaaaaaa-0000-0000-0000-000000000002";

    // Dataverse user ids, and Entra object ids.
    private static readonly Guid AliceDv = Guid.Parse("30000000-0000-0000-0000-00000000000a");
    private static readonly Guid BobDv = Guid.Parse("30000000-0000-0000-0000-00000000000b");
    private static readonly Guid CarolDv = Guid.Parse("30000000-0000-0000-0000-00000000000c");
    private static readonly Guid DanDv = Guid.Parse("30000000-0000-0000-0000-00000000000d");
    private static readonly Guid ErinDv = Guid.Parse("30000000-0000-0000-0000-00000000000e");
    private static readonly Guid FrankDv = Guid.Parse("30000000-0000-0000-0000-00000000000f");
    private const string AliceId = "e0000000-0000-0000-0000-00000000000a";
    private const string BobId = "e0000000-0000-0000-0000-00000000000b";
    private const string CarolId = "e0000000-0000-0000-0000-00000000000c";
    private const string DanId = "e0000000-0000-0000-0000-00000000000d";
    private const string ErinId = "e0000000-0000-0000-0000-00000000000e";
    private const string FrankId = "e0000000-0000-0000-0000-00000000000f";

    private readonly string _logFolder = Path.Combine(Path.GetTempPath(), "ppobjectsearch-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_logFolder, recursive: true); } catch (DirectoryNotFoundException) { /* Nothing was written. */ }
    }

    private static string Rows(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static Dictionary<string, object?> DvUser(Guid id, string name, string? oid, bool disabled = false) => new()
    {
        ["systemuserid"] = id.ToString(),
        ["fullname"] = name,
        ["domainname"] = name.ToLowerInvariant() + "@contoso.com",
        ["internalemailaddress"] = name.ToLowerInvariant() + "@contoso.com",
        ["azureactivedirectoryobjectid"] = oid,
        ["isdisabled"] = disabled,
        ["accessmode" + F] = "Read-Write"
    };

    private static object EntraUser(string id, string name, bool enabled = true) => new
    {
        id, displayName = name, userPrincipalName = name.ToLowerInvariant() + "@contoso.com",
        mail = name.ToLowerInvariant() + "@contoso.com", accountEnabled = enabled, userType = "Member"
    };

    private static Dictionary<string, object?> Alice => DvUser(AliceDv, "Alice", AliceId);
    private static Dictionary<string, object?> Bob => DvUser(BobDv, "Bob", BobId);
    private static Dictionary<string, object?> Carol => DvUser(CarolDv, "Carol", null);
    private static Dictionary<string, object?> Frank => DvUser(FrankDv, "Frank", FrankId);
    private static Dictionary<string, object?> ErinDisabled => DvUser(ErinDv, "Erin", ErinId, disabled: true);

    /// <summary>The fake environment and tenant. The team's membership is whatever the test says it is at the time.</summary>
    private sealed class World
    {
        public object[] Members { get; set; } = [Alice, Bob, Carol, Frank];
        public object[] GroupUsers { get; set; } = [EntraUser(AliceId, "Alice"), EntraUser(DanId, "Dan"), EntraUser(ErinId, "Erin")];
        public bool FailTeamReads { get; set; }
        public object[] DataverseUsers { get; set; } = [ErinDisabled];
        public object[] TeamAfterSync { get; set; } = [];

        public FakeHttpHandler Handler { get; } = new();

        private static Dictionary<string, object?> TeamRow(Guid id, string name, string group, int membershipType = 0, string? membershipLabel = null) => new()
        {
            ["teamid"] = id.ToString(), ["name"] = name, ["teamtype"] = 2, ["teamtype" + F] = "AAD Security Group",
            ["azureactivedirectoryobjectid"] = group, ["membershiptype"] = membershipType,
            ["membershiptype" + F] = membershipLabel ?? "Members and guests", ["isdefault"] = false,
            ["_businessunitid_value" + F] = "Contoso"
        };

        public World(string sku = "Sandbox", int membershipType = 0, string? membershipLabel = null)
        {
            Handler
                .OnEnvironmentType(sku)
                .OnJson(HttpMethod.Get, "/teams?", Rows(
                    TeamRow(SalesTeam, "Sales", Group, membershipType, membershipLabel),
                    TeamRow(OrphanTeam, "Orphans", MissingGroup)))
                .On(HttpMethod.Get, "teammembership_association", _ => FailTeamReads
                    ? FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("Try later."), HttpStatusCode.ServiceUnavailable)
                    : FakeHttpHandler.Json(Rows(Members)))
                .On(HttpMethod.Get, "/systemusers", _ => FakeHttpHandler.Json(Rows(DataverseUsers)))
                .OnStatus(HttpMethod.Post, "RemoveMembersTeam", HttpStatusCode.NoContent)
                .On(HttpMethod.Post, "SyncGroupMembersToTeam", _ =>
                {
                    Members = TeamAfterSync;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                })
                .On(HttpMethod.Get, "/WhoAmI", _ => FakeHttpHandler.Json(JsonSerializer.Serialize(new { UserId = Guid.NewGuid() })))
                // Graph
                .OnJson(HttpMethod.Get, $"groups/{Group}?", JsonSerializer.Serialize(new { id = Group, displayName = "Sales RBAC", securityEnabled = true, mailEnabled = false }))
                .OnError(HttpMethod.Get, $"groups/{MissingGroup}?", HttpStatusCode.NotFound, "Resource does not exist.")
                .On(HttpMethod.Get, "transitiveMembers/microsoft.graph.user", _ => FakeHttpHandler.Json(Rows(GroupUsers)))
                .OnJson(HttpMethod.Get, "transitiveMembers?", JsonSerializer.Serialize(new
                {
                    value = new object[]
                    {
                        new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = AliceId },
                        new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "nested" }
                    }
                }))
                .OnStatus(HttpMethod.Get, $"users/{BobId}?", HttpStatusCode.NotFound)
                .OnStatus(HttpMethod.Get, "users/bob@contoso.com?", HttpStatusCode.NotFound)
                .OnJson(HttpMethod.Get, "users/carol@contoso.com?", JsonSerializer.Serialize(EntraUser(CarolId, "Carol")))
                .OnJson(HttpMethod.Get, $"users/{FrankId}?", JsonSerializer.Serialize(EntraUser(FrankId, "Frank")))
                .OnJson(HttpMethod.Post, $"users/{CarolId}/checkMemberGroups", """{"value":[]}""")
                .OnJson(HttpMethod.Post, $"users/{FrankId}/checkMemberGroups", $$"""{"value":["{{Group}}"]}""");
        }
    }

    private EntraTeamSyncViewModel Sync(World world, EnvironmentSessionViewModel? session = null) =>
        new(session ?? AdminSessions.Connected(world.Handler), Fakes.Dataverse(world.Handler), Fakes.Graph(world.Handler))
        {
            WriteLogFolder = _logFolder
        };

    private static async Task<EntraTeamSyncViewModel> Compared(EntraTeamSyncViewModel sync, Guid? team = null)
    {
        await sync.LoadTeamsAsync();
        sync.SelectedTeam = sync.Teams.Single(t => t.TeamId == (team ?? SalesTeam));
        await AdminSessions.Until(() => !sync.IsBusy);
        return sync;
    }

    private static EntraMatchRowViewModel Row(EntraTeamSyncViewModel sync, string name) => sync.Rows.Single(r => r.Name == name);

    /// <summary>A change to Dataverse - not a read, and not Graph's checkMemberGroups, which only asks.</summary>
    private static bool IsWrite(RecordedRequest r) => r.Method != HttpMethod.Get && r.Url.StartsWith(Fakes.ApiRoot);

    private static Func<MembershipApplyViewModel, Task> Capture(Action<MembershipApplyViewModel> seen) => vm =>
    {
        seen(vm);
        return Task.CompletedTask;
    };

    // ---------------------------------------------------------------- loading

    [Fact]
    public void Only_entra_group_teams_are_listed() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = Sync(world);

        await sync.LoadTeamsAsync();

        Assert.Equal(["Sales", "Orphans"], sync.Teams.Select(t => t.Name));
        Assert.Contains("$filter=azureactivedirectoryobjectid ne null", world.Handler.Requests.Single(r => r.Url.Contains("/teams?")).Url);
        Assert.Equal("2 Entra group team(s). Pick one to compare it with its group.", sync.Status);
        Assert.False(sync.HasTeam);
        Assert.Equal("-", sync.GroupLabel);
        Assert.StartsWith("Entra team sync — ", sync.Title);
    });

    [Fact]
    public void An_environment_without_group_teams_says_so() => AdminUiThread.Run(async () =>
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/teams?", Rows());
        using var sync = new EntraTeamSyncViewModel(AdminSessions.Connected(handler), Fakes.Dataverse(handler), Fakes.Graph(handler));

        await sync.RefreshTeamsCommand.ExecuteAsync(null);

        Assert.Equal("No teams in this environment are linked to an Entra group.", sync.Status);
    });

    [Fact]
    public void A_failed_team_read_says_why() => AdminUiThread.Run(async () =>
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "/teams?", HttpStatusCode.Forbidden, "No read on team.");
        using var sync = new EntraTeamSyncViewModel(AdminSessions.Connected(handler), Fakes.Dataverse(handler), Fakes.Graph(handler));

        await sync.LoadTeamsAsync();

        Assert.StartsWith("Could not load teams - ", sync.Status);
        Assert.Contains("No read on team.", sync.Status);
        Assert.False(sync.IsBusy);
    });

    [Fact]
    public void The_team_picker_narrows_as_you_type() => AdminUiThread.Run(async () =>
    {
        using var sync = Sync(new World());
        await sync.LoadTeamsAsync();

        sync.TeamSearchText = "orph";

        Assert.Equal("Orphans", Assert.Single(sync.TeamsView.Cast<TeamInfo>()).Name);
    });

    [Fact]
    public void Refreshing_the_teams_keeps_the_one_picked() => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World()));

        await sync.LoadTeamsAsync();

        Assert.Equal(SalesTeam, sync.SelectedTeam?.TeamId);
    });

    // ---------------------------------------------------------------- the comparison

    [Fact]
    public void Picking_a_team_compares_it_with_its_group() => AdminUiThread.Run(async () =>
    {
        var world = new World();

        using var sync = await Compared(Sync(world));

        Assert.True(sync.HasComparison);
        Assert.True(sync.HasTeam);
        Assert.Equal("Sales RBAC", sync.GroupLabel);
        Assert.Equal(["Alice", "Bob", "Carol", "Dan", "Erin", "Frank"], sync.Rows.Select(r => r.Name));
        Assert.Equal((6, 1, 3, 2), (sync.CountAll, sync.CountBoth, sync.CountDataverseOnly, sync.CountEntraOnly));
        Assert.Equal("Ignored non-user members of the group: group 1.", sync.Warnings);
        Assert.True(sync.HasWarnings);
        Assert.StartsWith("Read at ", sync.Status);
        Assert.Equal("Read just now", sync.LastReadLabel);
        Assert.False(sync.IsDiagnosed);
        Assert.DoesNotContain(world.Handler.Requests, IsWrite);
    });

    [Fact]
    public void Each_row_says_which_side_it_is_on_and_how_it_was_matched() => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World()));

        var alice = Row(sync, "Alice");
        Assert.Equal("Both", alice.StatusLabel);
        Assert.Equal("Object id", alice.MatchedOn);
        Assert.Equal("alice@contoso.com", alice.Upn);
        Assert.Equal("alice@contoso.com", alice.Email);
        Assert.Equal(AliceId, alice.EntraObjectId);
        Assert.Equal(true, alice.EntraEnabled);
        Assert.Equal(false, alice.DataverseDisabled);
        Assert.Equal("Read-Write", alice.AccessMode);
        Assert.Empty(alice.AccountFlags);
        Assert.Equal(
            string.Join(Environment.NewLine, "Matched on Object id", "Entra object id " + AliceId, "Access mode Read-Write"),
            alice.RowToolTip);

        Assert.Equal("Team only", Row(sync, "Bob").StatusLabel);
        Assert.Equal("Group only", Row(sync, "Dan").StatusLabel);
        Assert.Null(Row(sync, "Dan").AccessMode);
    });

    [Theory]
    [InlineData(EntraMatchFilter.Both, new[] { "Alice" })]
    [InlineData(EntraMatchFilter.DataverseOnly, new[] { "Bob", "Carol", "Frank" })]
    [InlineData(EntraMatchFilter.EntraOnly, new[] { "Dan", "Erin" })]
    [InlineData(EntraMatchFilter.All, new[] { "Alice", "Bob", "Carol", "Dan", "Erin", "Frank" })]
    public void The_rows_filter_by_side(EntraMatchFilter filter, string[] expected) => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World()));

        sync.Filter = filter;

        Assert.Equal(expected, sync.RowsView.Cast<EntraMatchRowViewModel>().Select(r => r.Name));
    });

    [Fact]
    public void The_rows_can_be_searched_by_any_of_their_names() => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World()));

        sync.SearchText = "erin@contoso";

        Assert.Equal("Erin", Assert.Single(sync.RowsView.Cast<EntraMatchRowViewModel>()).Name);
    });

    [Fact]
    public void The_buttons_say_what_they_would_do_before_a_diagnosis() => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World()));

        Assert.Equal("Remove leftovers (3)…", sync.RemoveLeftoversLabel);
        Assert.Equal("Pull in group members (2)…", sync.PullInLabel);
        Assert.Contains("Read-only", sync.DiagnoseHint);
        Assert.StartsWith("Preview removing", sync.RemoveLeftoversHint);
        Assert.StartsWith("Preview provisioning", sync.PullInHint);
        Assert.StartsWith("Preview Dataverse's SyncGroupMembersToTeam", sync.SyncHint);
        Assert.StartsWith("Diagnose first", sync.CopyScriptHint);
        Assert.True(sync.DiagnoseCommand.CanExecute(null));
        Assert.True(sync.SyncCommand.CanExecute(null));
        Assert.True(sync.RemoveLeftoversCommand.CanExecute(null));
        Assert.True(sync.PullInCommand.CanExecute(null));
        Assert.True(sync.ReloadCommand.CanExecute(null));
        Assert.False(sync.CopyUserSyncScriptCommand.CanExecute(null));
    });

    [Fact]
    public void Before_a_team_is_picked_every_button_asks_for_one()
    {
        using var sync = Sync(new World());

        Assert.Equal("Remove leftovers…", sync.RemoveLeftoversLabel);
        Assert.Equal("Pull in group members…", sync.PullInLabel);
        Assert.All(new[] { sync.DiagnoseHint, sync.RemoveLeftoversHint, sync.PullInHint, sync.SyncHint },
            hint => Assert.Equal("Pick a team to compare first.", hint));
        Assert.False(sync.ReloadCommand.CanExecute(null));
        Assert.False(sync.SyncCommand.CanExecute(null));
        Assert.Equal(string.Empty, sync.LastReadLabel);
    }

    [Fact]
    public void A_team_that_matches_its_group_has_nothing_to_diagnose_remove_or_pull_in() => AdminUiThread.Run(async () =>
    {
        var world = new World { Members = [Alice], GroupUsers = [EntraUser(AliceId, "Alice")] };

        using var sync = await Compared(Sync(world));

        Assert.Equal("Nothing to diagnose: the team and the group match.", sync.DiagnoseHint);
        Assert.StartsWith("Nothing to remove", sync.RemoveLeftoversHint);
        Assert.StartsWith("Nobody to pull in", sync.PullInHint);
        Assert.False(sync.DiagnoseCommand.CanExecute(null));
        Assert.False(sync.RemoveLeftoversCommand.CanExecute(null));
        Assert.False(sync.PullInCommand.CanExecute(null));
        Assert.True(sync.SyncCommand.CanExecute(null));
    });

    [Fact]
    public void A_group_that_cannot_be_found_leaves_everyone_team_only_and_sync_unavailable() => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World()), OrphanTeam);

        Assert.Null(sync.Group);
        Assert.Equal($"{MissingGroup} (not found in Entra)", sync.GroupLabel);
        Assert.Contains($"Entra group {MissingGroup} was not found", sync.Warnings);
        Assert.Equal(4, sync.CountDataverseOnly);
        Assert.False(sync.SyncCommand.CanExecute(null));
        Assert.False(sync.PullInCommand.CanExecute(null));
        Assert.StartsWith("Unavailable", sync.SyncHint);
        Assert.Equal("Unavailable: the Entra group was not found.", sync.PullInHint);
    });

    [Fact]
    public void A_membership_type_other_than_members_and_guests_is_warned_about() => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World(membershipType: 1, membershipLabel: "Members")));

        Assert.Contains("The team's membership type is 'Members'", sync.Warnings);
    });

    [Fact]
    public void A_failed_comparison_says_why() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "teammembership_association", HttpStatusCode.InternalServerError, "Team is locked.");
        using var sync = new EntraTeamSyncViewModel(AdminSessions.Connected(handler), Fakes.Dataverse(handler), Fakes.Graph(world.Handler));
        sync.Teams.Add(new TeamInfo(SalesTeam, "Sales", 2, null, null, Guid.Parse(Group), 0, null, false));

        sync.SelectedTeam = sync.Teams[0];
        await AdminSessions.Until(() => !sync.IsBusy);

        Assert.StartsWith("Could not compare - ", sync.Status);
        Assert.Contains("Team is locked.", sync.Status);
        Assert.False(sync.HasComparison);
    });

    [Fact]
    public void Picking_another_team_mid_read_drops_the_first_reading() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new FakeHttpHandler()
            .OnAsync(HttpMethod.Get, $"teams({SalesTeam})/teammembership_association", _ => gate.Task)
            .OnJson(HttpMethod.Get, "teammembership_association", Rows(Bob));
        using var sync = new EntraTeamSyncViewModel(AdminSessions.Connected(handler), Fakes.Dataverse(handler), Fakes.Graph(world.Handler));
        sync.Teams.Add(new TeamInfo(SalesTeam, "Sales", 2, null, null, Guid.Parse(Group), 0, null, false));
        sync.Teams.Add(new TeamInfo(OrphanTeam, "Orphans", 2, null, null, Guid.Parse(MissingGroup), 0, null, false));

        sync.SelectedTeam = sync.Teams[0];
        Assert.True(sync.IsBusy);
        sync.SelectedTeam = sync.Teams[1];
        await AdminSessions.Until(() => !sync.IsBusy);
        gate.SetResult(FakeHttpHandler.Json(Rows(Alice, Carol)));

        Assert.True(sync.HasComparison);
        Assert.Null(sync.Group);
        Assert.Equal("Bob", Assert.Single(sync.Rows).Name);
        Assert.Contains(MissingGroup, sync.Warnings);
    });

    [Fact]
    public void A_team_without_a_group_shows_no_comparison() => AdminUiThread.Run(async () =>
    {
        using var sync = Sync(new World());
        sync.Teams.Add(new TeamInfo(OrphanTeam, "Unlinked", 0, null, null, null, null, null, false));

        sync.SelectedTeam = sync.Teams[0];
        await AdminSessions.Until(() => !sync.IsBusy);

        Assert.False(sync.HasComparison);
        Assert.Empty(sync.Rows);
        Assert.Equal("-", sync.GroupLabel);
    });

    // ---------------------------------------------------------------- diagnosis

    [Fact]
    public void Diagnosing_says_why_each_team_only_and_group_only_user_is_there() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world));

        await sync.DiagnoseCommand.ExecuteAsync(null);

        Assert.True(sync.IsDiagnosed);
        Assert.True(sync.IsGroupDiagnosed);
        Assert.Equal("Deleted from Entra", Row(sync, "Bob").DiagnosisLabel);
        Assert.Equal("No Entra object id", Row(sync, "Carol").DiagnosisLabel);
        Assert.Equal("Entra says member", Row(sync, "Frank").DiagnosisLabel);
        Assert.Equal("No Dataverse user", Row(sync, "Dan").DiagnosisLabel);
        Assert.Equal("Dataverse user disabled", Row(sync, "Erin").DiagnosisLabel);
        Assert.Equal("The user no longer exists in Entra. The sync does not appear to remove deleted users.", Row(sync, "Bob").DiagnosisDetail);

        Assert.StartsWith("Team only (3): ", sync.Status);
        Assert.Contains("Group only (2): ", sync.Status);
        Assert.Equal(EntraMatchFilter.All, sync.Filter);

        Assert.Equal(2, sync.LeftoverCount);
        Assert.Equal(2, sync.PullInCount);
        Assert.Equal(2, sync.NeedsUserSyncCount);
        Assert.True(sync.CopyUserSyncScriptCommand.CanExecute(null));
        Assert.Contains("for the 2 user(s) who need provisioning", sync.CopyScriptHint);
        Assert.DoesNotContain(world.Handler.Requests, IsWrite);
    });

    [Fact]
    public void A_diagnosis_flags_what_is_wrong_with_each_account() => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World()));

        await sync.DiagnoseCommand.ExecuteAsync(null);

        Assert.Equal(["Not in Entra"], Row(sync, "Bob").AccountFlags);
        Assert.Empty(Row(sync, "Carol").AccountFlags);
        Assert.Equal(["DV disabled"], Row(sync, "Erin").AccountFlags);
        Assert.StartsWith(Row(sync, "Bob").DiagnosisDetail!, Row(sync, "Bob").RowToolTip);
    });

    [Fact]
    public void Only_team_only_users_filters_to_them_after_a_diagnosis() => AdminUiThread.Run(async () =>
    {
        var world = new World { GroupUsers = [EntraUser(AliceId, "Alice")] };
        using var sync = await Compared(Sync(world));

        await sync.DiagnoseCommand.ExecuteAsync(null);

        Assert.Equal(EntraMatchFilter.DataverseOnly, sync.Filter);
        Assert.False(sync.IsGroupDiagnosed);
    });

    [Fact]
    public void Only_group_only_users_filters_to_them_after_a_diagnosis() => AdminUiThread.Run(async () =>
    {
        var world = new World { Members = [Alice] };
        using var sync = await Compared(Sync(world));

        await sync.DiagnoseCommand.ExecuteAsync(null);

        Assert.Equal(EntraMatchFilter.EntraOnly, sync.Filter);
        Assert.Equal("Group only (2): No Dataverse user 1 · Dataverse user disabled 1.", sync.Status);
    });

    [Fact]
    public void Reading_the_team_again_starts_the_diagnosis_afresh() => AdminUiThread.Run(async () =>
    {
        using var sync = await Compared(Sync(new World()));
        await sync.DiagnoseCommand.ExecuteAsync(null);

        await sync.ReloadCommand.ExecuteAsync(null);

        Assert.False(sync.IsDiagnosed);
        Assert.False(sync.IsGroupDiagnosed);
        Assert.Null(Row(sync, "Bob").DiagnosisLabel);
        Assert.Equal(3, sync.LeftoverCount);
    });

    [Fact]
    public void A_failed_team_only_diagnosis_says_why() => AdminUiThread.Run(async () =>
    {
        var world = new World { GroupUsers = [EntraUser(AliceId, "Alice")] };
        var graph = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "users/", HttpStatusCode.Forbidden, "Insufficient privileges.")
            .OnJson(HttpMethod.Get, $"groups/{Group}?", JsonSerializer.Serialize(new { id = Group, displayName = "Sales RBAC" }))
            .OnJson(HttpMethod.Get, "transitiveMembers/microsoft.graph.user", Rows(EntraUser(AliceId, "Alice")))
            .OnJson(HttpMethod.Get, "transitiveMembers?", Rows());
        using var sync = new EntraTeamSyncViewModel(AdminSessions.Connected(world.Handler), Fakes.Dataverse(world.Handler), Fakes.Graph(graph));
        await Compared(sync);

        await sync.DiagnoseCommand.ExecuteAsync(null);

        Assert.StartsWith("Diagnosing 'team only' users failed - ", sync.Status);
        Assert.False(sync.IsDiagnosed);
        Assert.False(sync.IsBusy);
    });

    [Fact]
    public void A_failed_group_only_diagnosis_says_why() => AdminUiThread.Run(async () =>
    {
        var world = new World { Members = [Alice] };
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "/systemusers", HttpStatusCode.InternalServerError, "Query timed out.")
            .On(HttpMethod.Get, "teammembership_association", _ => FakeHttpHandler.Json(Rows(Alice)));
        using var sync = new EntraTeamSyncViewModel(AdminSessions.Connected(handler), Fakes.Dataverse(handler), Fakes.Graph(world.Handler));
        sync.Teams.Add(new TeamInfo(SalesTeam, "Sales", 2, null, null, Guid.Parse(Group), 0, null, false));
        sync.SelectedTeam = sync.Teams[0];
        await AdminSessions.Until(() => !sync.IsBusy);

        await sync.DiagnoseCommand.ExecuteAsync(null);

        Assert.StartsWith("Diagnosing 'group only' users failed - ", sync.Status);
        Assert.Contains("Query timed out.", sync.Status);
        Assert.False(sync.IsGroupDiagnosed);
    });

    // ---------------------------------------------------------------- sync from Entra

    [Fact]
    public void The_sync_preview_forecasts_its_adds_and_removals_and_writes_nothing() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = Capture(vm => shown = vm);

        await sync.SyncCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.True(shown.IsForecast);
        Assert.Equal("Sync team from Entra group", shown.Operation);
        Assert.Equal("Sales RBAC", shown.SourceName);
        Assert.Equal(["Erin", "Bob", "Carol", "Frank"], shown.Rows.Select(r => r.Name));
        Assert.Equal("In the group; has a Dataverse user, but it is disabled.", shown.Rows[0].Reason);
        Assert.Equal("In the team, not in the Entra group.", shown.Rows[1].Reason);
        Assert.Contains("1 group member(s) have no Dataverse user yet", shown.Note);
        Assert.Contains("Run Diagnose afterwards", shown.Note);
        Assert.Equal("Nothing was changed.", sync.Status);
        Assert.DoesNotContain(world.Handler.Requests, IsWrite);
    });

    [Fact]
    public void A_confirmed_sync_runs_SyncGroupMembersToTeam_and_reports_what_landed() => AdminUiThread.Run(async () =>
    {
        var world = new World { TeamAfterSync = [Alice, ErinDisabled] };
        using var sync = await Compared(Sync(world));
        sync.ShowConfirmation = async vm =>
        {
            Assert.DoesNotContain(world.Handler.Requests, IsWrite);
            Assert.False(vm.ApplyCommand.CanExecute(null));
            vm.RemoveAcknowledged = true;
            await vm.ApplyCommand.ExecuteAsync(null);
        };

        await sync.SyncCommand.ExecuteAsync(null);

        var write = Assert.Single(world.Handler.Requests, IsWrite);
        Assert.Equal(Fakes.ApiRoot + $"teams({SalesTeam})/Microsoft.Dynamics.CRM.SyncGroupMembersToTeam", write.Url);
        Assert.Equal("Sync ran: 4 → 2 members (1 added, 3 removed). Every expected change has landed.", sync.Status);
        Assert.Equal((2, 0), (sync.CountBoth, sync.CountDataverseOnly));
        Assert.Single(Directory.GetFiles(_logFolder));
    });

    [Fact]
    public void A_diagnosed_sync_preview_gives_each_removal_its_diagnosis() => AdminUiThread.Run(async () =>
    {
        var world = new World(membershipType: 1, membershipLabel: "Members");
        using var sync = await Compared(Sync(world));
        await sync.DiagnoseCommand.ExecuteAsync(null);
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = Capture(vm => shown = vm);

        await sync.SyncCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.StartsWith("Deleted from Entra: ", shown.Rows.Single(r => r.Name == "Bob").Reason);
        Assert.Contains("Membership type 'Members' limits what the sync takes", shown.Note);
        Assert.DoesNotContain("Run Diagnose afterwards", shown.Note);
    });

    [Fact]
    public void Production_refuses_the_sync_and_nothing_can_be_applied() => AdminUiThread.Run(async () =>
    {
        var world = new World(sku: "Production");
        using var sync = await Compared(Sync(world));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = async vm =>
        {
            shown = vm;
            vm.RemoveAcknowledged = true;
            await vm.ApplyCommand.ExecuteAsync(null);
        };

        await sync.SyncCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.True(shown.IsBlocked);
        Assert.False(shown.CanAcknowledge);
        Assert.StartsWith("Writing here is blocked. This is a production environment.", sync.Status);
        Assert.DoesNotContain(world.Handler.Requests, IsWrite);
    });

    [Fact]
    public void Without_the_environment_type_the_sync_is_not_offered() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world, AdminSessions.Disconnected()));
        var shown = false;
        sync.ShowConfirmation = Capture(_ => shown = true);

        await sync.SyncCommand.ExecuteAsync(null);

        Assert.False(shown);
        Assert.Equal("Could not prepare the sync - The environment is not connected.", sync.Status);
        Assert.False(sync.IsBusy);
    });

    // ---------------------------------------------------------------- remove leftovers

    [Fact]
    public void Removing_leftovers_diagnoses_first_and_offers_only_those_the_sync_leaves_behind() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = Capture(vm => shown = vm);

        await sync.RemoveLeftoversCommand.ExecuteAsync(null);

        Assert.True(sync.IsDiagnosed);
        Assert.NotNull(shown);
        Assert.False(shown.IsForecast);
        Assert.Equal(["Bob", "Carol"], shown.Rows.Select(r => r.Name));
        Assert.True(shown.Rows[0].IsChecked);
        Assert.False(shown.Rows[1].IsChecked);
        Assert.Contains("1 user(s) Entra says ARE members, or could not be checked, are not offered.", shown.Note);
        Assert.DoesNotContain(world.Handler.Requests, IsWrite);
    });

    [Fact]
    public void Confirmed_removals_go_one_user_at_a_time_and_the_team_is_read_again() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world));
        sync.ShowConfirmation = async vm =>
        {
            vm.RemoveAcknowledged = true;
            await vm.ApplyCommand.ExecuteAsync(null);
            world.Members = [Alice, Carol, Frank];
        };

        await sync.RemoveLeftoversCommand.ExecuteAsync(null);

        var write = Assert.Single(world.Handler.Requests, IsWrite);
        Assert.Equal(Fakes.ApiRoot + $"teams({SalesTeam})/Microsoft.Dynamics.CRM.RemoveMembersTeam", write.Url);
        Assert.Contains(BobDv.ToString(), write.Body);
        Assert.Equal("Done - 1 change(s) made to Sales.", sync.Status);
        Assert.Equal(2, sync.CountDataverseOnly);
        Assert.True(sync.IsDiagnosed);
    });

    [Fact]
    public void With_nobody_the_sync_leaves_behind_there_is_nothing_to_remove() => AdminUiThread.Run(async () =>
    {
        var world = new World { Members = [Alice, Frank] };
        using var sync = await Compared(Sync(world));
        var shown = false;
        sync.ShowConfirmation = Capture(_ => shown = true);

        await sync.RemoveLeftoversCommand.ExecuteAsync(null);

        Assert.False(shown);
        Assert.StartsWith("Nobody to remove", sync.Status);
        Assert.Equal(0, sync.LeftoverCount);
    });

    [Fact]
    public void A_removal_in_production_is_refused() => AdminUiThread.Run(async () =>
    {
        var world = new World(sku: "Production");
        using var sync = await Compared(Sync(world));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = Capture(vm => shown = vm);

        await sync.RemoveLeftoversCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.True(shown.IsBlocked);
        Assert.False(shown.ApplyCommand.CanExecute(null));
        Assert.Equal(shown.GuardMessage, sync.Status);
        Assert.DoesNotContain(world.Handler.Requests, IsWrite);
    });

    [Fact]
    public void A_removal_that_cannot_check_the_environment_is_not_offered() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world, AdminSessions.Disconnected()));
        var shown = false;
        sync.ShowConfirmation = Capture(_ => shown = true);

        await sync.RemoveLeftoversCommand.ExecuteAsync(null);

        Assert.False(shown);
        Assert.Equal("Could not prepare the removal - The environment is not connected.", sync.Status);
    });

    // ---------------------------------------------------------------- pull in

    [Fact]
    public void Pulling_in_offers_users_with_no_or_a_disabled_dataverse_user_and_writes_nothing_until_confirmed() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = Capture(vm => shown = vm);

        await sync.PullInCommand.ExecuteAsync(null);

        Assert.True(sync.IsGroupDiagnosed);
        Assert.NotNull(shown);
        Assert.Equal("Pull in group members", shown.Operation);
        Assert.Equal("Pull in", shown.AddVerb);
        Assert.Equal(["Dan", "Erin"], shown.Rows.Select(r => r.Name));
        Assert.Equal(Guid.Parse(DanId), shown.Rows[0].Change.SystemUserId);
        Assert.Contains("SyncGroupMembersToTeam is NOT run afterwards: the team has 3 'team only' user(s)", shown.Note);
        Assert.Equal("Pull in 2", shown.ApplyLabel);
        Assert.DoesNotContain(world.Handler.Requests, r => r.Url.Contains("/WhoAmI"));
    });

    [Fact]
    public void Confirmed_pull_ins_make_one_whoami_as_each_user_and_skip_the_sync_while_it_would_remove_people() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world));
        sync.ShowConfirmation = vm => vm.ApplyCommand.ExecuteAsync(null);

        await sync.PullInCommand.ExecuteAsync(null);

        var whoAmIs = world.Handler.Requests.Where(r => r.Url.Contains("/WhoAmI")).ToList();
        Assert.Equal([DanId, ErinId], whoAmIs.Select(r => r.Header("CallerObjectId")));
        Assert.DoesNotContain(world.Handler.Requests, r => r.Url.Contains("SyncGroupMembersToTeam"));
        Assert.EndsWith("0 of 2 pulled-in user(s) are now in the team. Run Sync from Entra to add the rest.", sync.Status);
    });

    [Fact]
    public void With_nobody_to_remove_a_pull_in_is_followed_by_a_sync_that_brings_them_into_the_team() => AdminUiThread.Run(async () =>
    {
        var world = new World
        {
            Members = [Alice],
            GroupUsers = [EntraUser(AliceId, "Alice"), EntraUser(DanId, "Dan")],
            DataverseUsers = [],
            TeamAfterSync = [Alice, DvUser(DanDv, "Dan", DanId)]
        };
        using var sync = await Compared(Sync(world));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = async vm =>
        {
            shown = vm;
            await vm.ApplyCommand.ExecuteAsync(null);
        };

        await sync.PullInCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Contains("Then SyncGroupMembersToTeam runs", shown.Note);
        Assert.Equal(
            ["/WhoAmI", "SyncGroupMembersToTeam"],
            world.Handler.Requests.Where(r => r.Url.Contains("/WhoAmI") || r.Url.Contains("SyncGroupMembersToTeam"))
                .Select(r => r.Url.Contains("/WhoAmI") ? "/WhoAmI" : "SyncGroupMembersToTeam"));
        Assert.Contains("Then SyncGroupMembersToTeam succeeded.", sync.Status);
        Assert.EndsWith("1 of 1 pulled-in user(s) are now in the team.", sync.Status);
        Assert.Equal(2, sync.CountBoth);
    });

    [Fact]
    public void A_pull_in_that_lands_nobody_says_the_rest_may_follow() => AdminUiThread.Run(async () =>
    {
        var world = new World
        {
            Members = [Alice],
            GroupUsers = [EntraUser(AliceId, "Alice"), EntraUser(DanId, "Dan")],
            DataverseUsers = [],
            TeamAfterSync = [Alice]
        };
        using var sync = await Compared(Sync(world));
        sync.ShowConfirmation = vm => vm.ApplyCommand.ExecuteAsync(null);

        await sync.PullInCommand.ExecuteAsync(null);

        Assert.EndsWith("The rest may land in a few minutes - Re-read, then Diagnose to see why any remain.", sync.Status);
    });

    [Fact]
    public void With_no_one_a_whoami_would_help_there_is_nobody_to_pull_in() => AdminUiThread.Run(async () =>
    {
        var world = new World
        {
            Members = [Alice],
            GroupUsers = [EntraUser(AliceId, "Alice"), EntraUser(ErinId, "Erin", enabled: false)],
            DataverseUsers = []
        };
        using var sync = await Compared(Sync(world));
        var shown = false;
        sync.ShowConfirmation = Capture(_ => shown = true);

        await sync.PullInCommand.ExecuteAsync(null);

        Assert.False(shown);
        Assert.StartsWith("Nobody to pull in", sync.Status);
        Assert.Equal("Disabled in Entra", Row(sync, "Erin").DiagnosisLabel);
        Assert.Equal(["Entra disabled"], Row(sync, "Erin").AccountFlags);
    });

    [Fact]
    public void Users_not_offered_for_pull_in_are_counted_in_the_note() => AdminUiThread.Run(async () =>
    {
        var world = new World { GroupUsers = [EntraUser(AliceId, "Alice"), EntraUser(DanId, "Dan"), EntraUser(ErinId, "Erin", enabled: false)] };
        using var sync = await Compared(Sync(world));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = Capture(vm => shown = vm);

        await sync.PullInCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Contains("1 other 'group only' user(s) are not offered", shown.Note);
        Assert.Equal("Nothing was changed.", sync.Status);
    });

    [Fact]
    public void A_pull_in_that_cannot_check_the_environment_is_not_offered() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world, AdminSessions.Disconnected()));
        var shown = false;
        sync.ShowConfirmation = Capture(_ => shown = true);

        await sync.PullInCommand.ExecuteAsync(null);

        Assert.False(shown);
        Assert.Equal("Could not prepare the pull-in - The environment is not connected.", sync.Status);
    });

    [Fact]
    public void A_failed_re_read_after_writing_is_added_to_the_outcome() => AdminUiThread.Run(async () =>
    {
        var world = new World();
        using var sync = await Compared(Sync(world));
        sync.ShowConfirmation = async vm =>
        {
            vm.RemoveAcknowledged = true;
            await vm.ApplyCommand.ExecuteAsync(null);
            world.FailTeamReads = true;
        };

        await sync.RemoveLeftoversCommand.ExecuteAsync(null);

        Assert.StartsWith("Done - 1 change(s) made to Sales. Re-reading the team failed - ", sync.Status);
        Assert.Contains("Try later.", sync.Status);
        Assert.False(sync.IsBusy);
    });

    // ---------------------------------------------------------------- the small parts

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(30 * 3600, "over a day ago")]
    public void The_age_of_a_reading_is_said_roughly(int seconds, string expected)
    {
        Assert.Equal(expected, EntraTeamSyncViewModel.Ago(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData("Sales RBAC Contoso", "", true)]
    [InlineData("Sales RBAC Contoso", "rbac sales", true)]
    [InlineData("Sales RBAC Contoso", "rbac fabrikam", false)]
    public void A_search_matches_when_every_word_is_found(string haystack, string search, bool expected)
    {
        Assert.Equal(expected, EntraTeamSyncViewModel.Matches(haystack, search));
    }

    [Fact]
    public void A_stale_object_id_is_flagged_as_not_in_entra_and_a_disabled_dataverse_user_as_such()
    {
        var dv = new MemberUser(Guid.NewGuid(), "Gus", "gus@contoso.com", null, Guid.NewGuid(), true, null, null);
        var row = new EntraMatchRowViewModel(new EntraMatchRow(EntraMatchStatus.DataverseOnly, null, dv, null))
        {
            Diagnosis = MembershipPlanner.Diagnose(dv, null, new EntraUser("other", "Gus", "gus@contoso.com", null, true), null)
        };

        Assert.Equal(DiagnosisCategory.StaleObjectId, row.Diagnosis.Category);
        Assert.Equal(["Not in Entra", "DV disabled"], row.AccountFlags);
    }

    [Fact]
    public void A_group_only_user_disabled_in_dataverse_is_flagged_from_their_user_record()
    {
        var entra = new EntraUser("e1", "Hal", "hal@contoso.com", null, true);
        var record = new MemberUser(Guid.NewGuid(), "Hal", "hal@contoso.com", null, null, true, null, null);
        var row = new EntraMatchRowViewModel(new EntraMatchRow(EntraMatchStatus.EntraOnly, null, null, entra))
        {
            GroupDiagnosis = new GroupOnlyDiagnosis(GroupOnlyCategory.ObjectIdMismatch, "Linked elsewhere.", "UPN", record)
        };

        Assert.Equal("Linked to another Entra id", row.DiagnosisLabel);
        Assert.Equal("Linked elsewhere.", row.DiagnosisDetail);
        Assert.Equal(["DV disabled"], row.AccountFlags);
    }
}
