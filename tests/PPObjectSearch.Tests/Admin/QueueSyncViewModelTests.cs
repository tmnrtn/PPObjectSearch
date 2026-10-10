using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

/// <summary>Queue membership sync: a team previewed against a queue, and only what is confirmed written.</summary>
public sealed class QueueSyncViewModelTests : IDisposable
{
    private const string F = "@OData.Community.Display.V1.FormattedValue";

    private static readonly Guid DeskTeam = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid GroupTeam = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid DefaultTeam = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Support = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-0000-0000-000000000002");

    private static readonly Guid Alice = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid Carl = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Dora = Guid.Parse("30000000-0000-0000-0000-000000000004");
    private static readonly Guid FlowApp = Guid.Parse("30000000-0000-0000-0000-000000000005");
    private static readonly Guid MailBot = Guid.Parse("30000000-0000-0000-0000-000000000006");

    private readonly string _logFolder = Path.Combine(Path.GetTempPath(), "ppobjectsearch-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_logFolder, recursive: true); } catch (DirectoryNotFoundException) { /* Nothing was written. */ }
    }

    private static string Rows(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static Dictionary<string, object?> User(Guid id, string name, bool disabled = false, bool app = false) => new()
    {
        ["systemuserid"] = id.ToString(),
        ["fullname"] = name,
        ["domainname"] = name.ToLowerInvariant() + "@contoso.com",
        ["internalemailaddress"] = name.ToLowerInvariant() + "@contoso.com",
        ["isdisabled"] = disabled,
        ["accessmode" + F] = app ? "Non-interactive" : "Read-Write",
        ["applicationid"] = app ? Guid.NewGuid().ToString() : null
    };

    private static Dictionary<string, object?> Team(Guid id, string name, Guid? group = null, bool isDefault = false) => new()
    {
        ["teamid"] = id.ToString(), ["name"] = name, ["teamtype"] = group is null ? 0 : 2,
        ["teamtype" + F] = group is null ? "Owner" : "AAD Security Group",
        ["azureactivedirectoryobjectid"] = group?.ToString(), ["isdefault"] = isDefault,
        ["_businessunitid_value" + F] = "Contoso"
    };

    private static Dictionary<string, object?> Queue(Guid id, string name) => new()
    {
        ["queueid"] = id.ToString(), ["name"] = name, ["emailaddress"] = name.ToLowerInvariant() + "@contoso.com",
        ["queueviewtype" + F] = "Public", ["_ownerid_value" + F] = "Admin"
    };

    /// <summary>
    /// Service Desk has Alice, Bob, Dora (disabled) and Flow App (an application user); Support has
    /// Bob, Carl and Mail Bot (an application user). The queue's membership can be changed under it.
    /// </summary>
    private static FakeHttpHandler Handler(Func<HttpResponseMessage>? queueMembers = null) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "/teams?", Rows(
            Team(DeskTeam, "Service Desk"),
            Team(GroupTeam, "Sales RBAC", group: Guid.NewGuid()),
            Team(DefaultTeam, "Contoso", isDefault: true)))
        .OnJson(HttpMethod.Get, "/queues?", Rows(Queue(Support, "Support"), Queue(Billing, "Billing")))
        .OnJson(HttpMethod.Get, "teammembership_association", Rows(
            User(Alice, "Alice"), User(Bob, "Bob"), User(Dora, "Dora", disabled: true), User(FlowApp, "Flow App", app: true)))
        .On(HttpMethod.Get, "queuemembership_association", _ => queueMembers?.Invoke() ?? FakeHttpHandler.Json(Rows(
            User(Bob, "Bob"), User(Carl, "Carl"), User(MailBot, "Mail Bot", app: true))));

    private QueueSyncViewModel Sync(FakeHttpHandler handler, EnvironmentSessionViewModel? session = null) =>
        new(session ?? AdminSessions.Connected(handler), Fakes.Dataverse(handler)) { WriteLogFolder = _logFolder };

    private static async Task<QueueSyncViewModel> Previewed(QueueSyncViewModel sync)
    {
        await sync.LoadAsync();
        sync.SelectedTeam = sync.Teams.Single(t => t.TeamId == DeskTeam);
        sync.SelectedQueue = sync.Queues.Single(q => q.QueueId == Support);
        await sync.PreviewCommand.ExecuteAsync(null);
        return sync;
    }

    private static bool IsWrite(RecordedRequest r) => r.Method != HttpMethod.Get;

    // ---------------------------------------------------------------- loading and picking

    [Fact]
    public async Task Loading_lists_every_team_and_the_active_queues()
    {
        var handler = Handler();
        var sync = Sync(handler);

        await sync.LoadAsync();

        Assert.Equal(["Service Desk", "Sales RBAC", "Contoso"], sync.Teams.Select(t => t.Name));
        Assert.Equal(["Support", "Billing"], sync.Queues.Select(q => q.Name));
        Assert.Equal("3 teams and 2 active queues. Pick one of each, then Preview.", sync.Status);
        Assert.False(sync.IsBusy);
        Assert.DoesNotContain("$filter=azureactivedirectoryobjectid", handler.Requests[0].Url);
        Assert.StartsWith("Queue membership sync — ", sync.Title);
    }

    [Fact]
    public async Task A_failed_load_says_why()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "/teams?", HttpStatusCode.Forbidden, "No read on team.");
        var sync = Sync(handler);

        await sync.LoadAsync();

        Assert.StartsWith("Could not load teams and queues - ", sync.Status);
        Assert.Contains("No read on team.", sync.Status);
        Assert.Empty(sync.Teams);
    }

    [Fact]
    public async Task The_pickers_narrow_as_you_type()
    {
        var sync = Sync(Handler());
        await sync.LoadAsync();

        sync.TeamSearchText = "sales";
        sync.QueueSearchText = "bill";

        Assert.Equal("Sales RBAC", Assert.Single(sync.TeamsView.Cast<TeamInfo>()).Name);
        Assert.Equal("Billing", Assert.Single(sync.QueuesView.Cast<QueueInfo>()).Name);
    }

    [Fact]
    public async Task Preview_needs_both_a_team_and_a_queue()
    {
        var sync = Sync(Handler());
        await sync.LoadAsync();

        Assert.False(sync.CanPreview);
        sync.SelectedTeam = sync.Teams[0];
        Assert.False(sync.PreviewCommand.CanExecute(null));

        sync.SelectedQueue = sync.Queues[0];

        Assert.True(sync.CanPreview);
        Assert.True(sync.PreviewCommand.CanExecute(null));
        Assert.False(sync.HasPlan);
        Assert.Equal("Pick a team and a queue, then Preview.", sync.PlanHeading);
    }

    [Fact]
    public async Task Reloading_keeps_the_team_and_queue_that_were_picked()
    {
        var sync = Sync(Handler());
        await sync.LoadAsync();
        sync.SelectedTeam = sync.Teams[1];
        sync.SelectedQueue = sync.Queues[1];

        await sync.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(GroupTeam, sync.SelectedTeam?.TeamId);
        Assert.Equal(Billing, sync.SelectedQueue?.QueueId);
    }

    // ---------------------------------------------------------------- the plan

    [Fact]
    public async Task The_preview_adds_missing_team_members_and_removes_queue_members_not_in_the_team()
    {
        var handler = Handler();

        var sync = await Previewed(Sync(handler));

        Assert.True(sync.HasPlan);
        Assert.Equal("Service Desk  →  Support", sync.PlanHeading);
        Assert.Equal((6, 1, 2, 1, 2, 0), (sync.CountAll, sync.CountAdd, sync.CountRemove, sync.CountInBoth, sync.CountSkip, sync.CountKeep));

        var alice = sync.Rows.Single(r => r.Name == "Alice");
        Assert.Equal("Add", alice.StatusLabel);
        Assert.Equal("alice@contoso.com", alice.Upn);
        Assert.Equal("alice@contoso.com", alice.Email);
        Assert.Equal(false, alice.IsDisabled);
        Assert.Equal("Read-Write", alice.AccessMode);
        Assert.Equal("In the team, not in the queue.", alice.Detail);
        Assert.Equal("In both", sync.Rows.Single(r => r.Name == "Bob").StatusLabel);
        Assert.Equal("Skip", sync.Rows.Single(r => r.Name == "Dora").StatusLabel);
        Assert.Equal("Remove", sync.Rows.Single(r => r.Name == "Carl").StatusLabel);

        Assert.Contains("team 4, queue 3 members. Nothing is changed until you apply and confirm.", sync.Status);
        Assert.False(sync.HasWarnings);
        Assert.True(sync.ApplyCommand.CanExecute(null));
        Assert.DoesNotContain(handler.Requests, IsWrite);
    }

    [Theory]
    [InlineData(QueuePlanFilter.Add, new[] { "Alice" })]
    [InlineData(QueuePlanFilter.Remove, new[] { "Carl", "Mail Bot" })]
    [InlineData(QueuePlanFilter.InBoth, new[] { "Bob" })]
    [InlineData(QueuePlanFilter.Skip, new[] { "Dora", "Flow App" })]
    [InlineData(QueuePlanFilter.Keep, new string[0])]
    [InlineData(QueuePlanFilter.All, new[] { "Alice", "Carl", "Mail Bot", "Dora", "Flow App", "Bob" })]
    public async Task The_plan_filters_by_what_would_happen(QueuePlanFilter filter, string[] expected)
    {
        var sync = await Previewed(Sync(Handler()));

        sync.Filter = filter;

        Assert.Equal(expected, sync.RowsView.Cast<QueuePlanRowViewModel>().Select(r => r.Name));
    }

    [Fact]
    public async Task The_plan_can_be_searched()
    {
        var sync = await Previewed(Sync(Handler()));

        sync.SearchText = "carl@";

        Assert.Equal("Carl", Assert.Single(sync.RowsView.Cast<QueuePlanRowViewModel>()).Name);
    }

    [Fact]
    public async Task Additive_only_keeps_queue_members_rather_than_removing_them_without_reading_again()
    {
        var handler = Handler();
        var sync = await Previewed(Sync(handler));
        sync.Filter = QueuePlanFilter.Remove;
        var reads = handler.Requests.Count;

        sync.AdditiveOnly = true;

        Assert.Equal(QueuePlanFilter.All, sync.Filter);
        Assert.Equal((0, 2), (sync.CountRemove, sync.CountKeep));
        Assert.Equal("Keep", sync.Rows.Single(r => r.Name == "Carl").StatusLabel);
        Assert.Equal(reads, handler.Requests.Count);
    }

    [Fact]
    public async Task Changing_the_team_throws_the_plan_away()
    {
        var sync = await Previewed(Sync(Handler()));

        sync.SelectedTeam = sync.Teams.Single(t => t.TeamId == GroupTeam);

        Assert.False(sync.HasPlan);
        Assert.Empty(sync.Rows);
        Assert.Equal(0, sync.CountAll);
        Assert.False(sync.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_entra_group_team_and_a_default_team_each_come_with_a_warning()
    {
        var sync = Sync(Handler());
        await sync.LoadAsync();
        sync.SelectedQueue = sync.Queues[0];

        sync.SelectedTeam = sync.Teams.Single(t => t.TeamId == GroupTeam);
        await sync.PreviewCommand.ExecuteAsync(null);
        Assert.True(sync.HasWarnings);
        Assert.Contains("Entra group team", sync.Warnings);

        sync.SelectedTeam = sync.Teams.Single(t => t.TeamId == DefaultTeam);
        Assert.False(sync.HasWarnings);
        await sync.PreviewCommand.ExecuteAsync(null);
        Assert.Contains("default team", sync.Warnings);
    }

    [Fact]
    public async Task A_queue_that_already_matches_says_so()
    {
        var sync = await Previewed(Sync(Handler(() => FakeHttpHandler.Json(Rows(User(Alice, "Alice"), User(Bob, "Bob"))))));

        Assert.EndsWith("The queue already matches the team.", sync.Status);
        Assert.False(sync.ApplyCommand.CanExecute(null));

        sync.AdditiveOnly = true;
        await sync.PreviewCommand.ExecuteAsync(null);

        Assert.EndsWith("Every team member is already in the queue.", sync.Status);
    }

    [Fact]
    public async Task A_failed_preview_says_why_and_leaves_no_plan()
    {
        var handler = Handler(() => FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("Queue is locked."), HttpStatusCode.Conflict));

        var sync = await Previewed(Sync(handler));

        Assert.StartsWith("Could not preview - ", sync.Status);
        Assert.Contains("Queue is locked.", sync.Status);
        Assert.False(sync.HasPlan);
        Assert.False(sync.IsBusy);
    }

    // ---------------------------------------------------------------- confirm and apply

    [Fact]
    public async Task Nothing_is_written_until_the_changes_are_confirmed_and_applied()
    {
        var handler = Handler().OnEnvironmentType("Sandbox");
        var sync = await Previewed(Sync(handler));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = vm =>
        {
            shown = vm;
            return Task.CompletedTask;
        };

        await sync.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal("Sync queue members from team", shown.Operation);
        Assert.Equal("Support", shown.TargetName);
        Assert.Equal("Service Desk", shown.SourceName);
        Assert.False(shown.IsBlocked);
        Assert.Equal(["Alice", "Carl", "Mail Bot"], shown.Rows.Select(r => r.Name));
        Assert.False(shown.Rows.Single(r => r.Name == "Mail Bot").IsChecked);
        Assert.Equal("Nothing was changed.", sync.Status);
        Assert.DoesNotContain(handler.Requests, IsWrite);
    }

    [Fact]
    public async Task Confirmed_changes_add_and_remove_one_user_at_a_time_and_then_read_the_queue_again()
    {
        var queue = Rows(User(Bob, "Bob"), User(Carl, "Carl"), User(MailBot, "Mail Bot", app: true));
        var handler = Handler(() => FakeHttpHandler.Json(queue))
            .OnEnvironmentType("Sandbox")
            .OnStatus(HttpMethod.Post, "queuemembership_association/$ref", HttpStatusCode.NoContent)
            .OnStatus(HttpMethod.Delete, "queuemembership_association", HttpStatusCode.NoContent);
        var sync = await Previewed(Sync(handler));
        sync.ShowConfirmation = async vm =>
        {
            Assert.DoesNotContain(handler.Requests, IsWrite);
            Assert.False(vm.ApplyCommand.CanExecute(null));
            vm.RemoveAcknowledged = true;
            await vm.ApplyCommand.ExecuteAsync(null);
            queue = Rows(User(Alice, "Alice"), User(Bob, "Bob"), User(MailBot, "Mail Bot", app: true));
        };

        await sync.ApplyCommand.ExecuteAsync(null);

        var writes = handler.Requests.Where(IsWrite).ToList();
        Assert.Equal(2, writes.Count);
        Assert.Equal(HttpMethod.Post, writes[0].Method);
        Assert.Equal(Fakes.ApiRoot + $"queues({Support})/queuemembership_association/$ref", writes[0].Url);
        Assert.Contains($"systemusers({Alice})", writes[0].Body);
        Assert.Equal(HttpMethod.Delete, writes[1].Method);
        Assert.Equal(Fakes.ApiRoot + $"queues({Support})/queuemembership_association({Carl})/$ref", writes[1].Url);

        Assert.Equal("Done - 2 change(s) made to Support. The preview has been read again.", sync.Status);
        Assert.Equal((0, 1), (sync.CountAdd, sync.CountRemove));
        Assert.True(File.Exists(Assert.Single(Directory.GetFiles(_logFolder))));
    }

    [Fact]
    public async Task Additive_only_confirms_adds_alone_and_says_who_is_kept()
    {
        var handler = Handler()
            .OnEnvironmentType("Sandbox")
            .OnStatus(HttpMethod.Post, "queuemembership_association/$ref", HttpStatusCode.NoContent);
        var sync = await Previewed(Sync(handler));
        sync.AdditiveOnly = true;
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = async vm =>
        {
            shown = vm;
            await vm.ApplyCommand.ExecuteAsync(null);
        };

        await sync.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal("Add team members to queue", shown.Operation);
        Assert.Equal("Additive only: 2 queue member(s) not in the team are kept - nobody is removed.", shown.Note);
        Assert.Equal("Alice", Assert.Single(shown.Rows).Name);
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests, IsWrite).Method);
    }

    [Fact]
    public async Task The_warnings_go_into_the_confirmation_note()
    {
        var handler = Handler().OnEnvironmentType("Sandbox");
        var sync = Sync(handler);
        await sync.LoadAsync();
        sync.SelectedTeam = sync.Teams.Single(t => t.TeamId == DefaultTeam);
        sync.SelectedQueue = sync.Queues.Single(q => q.QueueId == Support);
        await sync.PreviewCommand.ExecuteAsync(null);
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = vm =>
        {
            shown = vm;
            return Task.CompletedTask;
        };

        await sync.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(sync.Warnings, shown?.Note);
    }

    [Fact]
    public async Task A_production_environment_is_refused_and_nothing_can_be_applied()
    {
        var handler = Handler().OnEnvironmentType("Production");
        var sync = await Previewed(Sync(handler));
        MembershipApplyViewModel? shown = null;
        sync.ShowConfirmation = async vm =>
        {
            shown = vm;
            vm.RemoveAcknowledged = true;
            Assert.False(vm.ApplyCommand.CanExecute(null));
            await vm.ApplyCommand.ExecuteAsync(null);
        };

        await sync.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.True(shown.IsBlocked);
        Assert.Equal(EnvironmentSku.Production, shown.Sku);
        Assert.Equal(shown.GuardMessage, sync.Status);
        Assert.Contains("production", sync.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(shown.AnyWritesAttempted);
        Assert.DoesNotContain(handler.Requests, IsWrite);
    }

    [Fact]
    public async Task Without_knowing_the_environment_type_there_is_no_confirmation()
    {
        var handler = Handler();
        var sync = await Previewed(Sync(handler, AdminSessions.Disconnected()));
        var shown = false;
        sync.ShowConfirmation = _ =>
        {
            shown = true;
            return Task.CompletedTask;
        };

        await sync.ApplyCommand.ExecuteAsync(null);

        Assert.False(shown);
        Assert.StartsWith("Could not prepare the changes - ", sync.Status);
        Assert.False(sync.IsBusy);
        Assert.DoesNotContain(handler.Requests, IsWrite);
    }
}
