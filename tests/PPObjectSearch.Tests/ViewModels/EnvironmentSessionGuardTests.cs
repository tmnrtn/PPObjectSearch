using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// An environment tab's commands and properties at their edges: given nothing to act on, asked
/// twice, or used on a tab whose connection failed - each does nothing, or says why.
/// </summary>
public class EnvironmentSessionGuardTests
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_AccountName")]
    private static extern void SetAccountName(EnvironmentAuthContext context, string? name);

    /// <summary>A tab whose sign-in failed: it has a client, but is not connected.</summary>
    private static async Task<EnvironmentSessionViewModel> FailedAsync()
    {
        var env = new FakeEnvironment();
        env.Handler.OnError(HttpMethod.Get, "/WhoAmI", HttpStatusCode.Forbidden, "No access");
        var session = await env.ConnectedAsync();
        Assert.NotNull(session.Client);
        Assert.False(session.IsConnected);
        return session;
    }

    [Theory]
    [InlineData("maria.lopez@contoso.com", "ML")]
    [InlineData("admin@contoso.com", "AD")]
    [InlineData("x", "X")]
    [InlineData("first_middle-last", "FL")]
    [InlineData("...@contoso.com", "?")]
    [InlineData(null, "?")]
    public void The_avatar_shows_the_accounts_initials(string? account, string initials)
    {
        var auth = FakeEnvironment.Auth();
        SetAccountName(auth, account);
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(),
            new TabState { EnvironmentUrl = Fakes.EnvironmentUrl }, () => auth, (a, url) => new DataverseClient(a, url, new FakeHttpHandler()));

        Assert.Equal(account, session.AccountName);
        Assert.Equal(initials, session.AccountInitials);
    }

    [Fact]
    public void Commands_given_nothing_to_act_on_do_nothing()
    {
        var session = new FakeEnvironment().Session();
        var status = session.Status;

        session.ApplySavedSearchCommand.Execute(null);
        session.DeleteSavedSearchCommand.Execute(null);
        session.OpenRecentCommand.Execute(null);
        session.OpenLinkCommand.Execute(null);
        session.ShowDetailsCommand.Execute(null);
        session.ExploreDependenciesCommand.Execute(null);
        session.CancelLayerCheckCommand.Execute(null);
        session.EnvironmentAdminCommand.Execute(AdminTab.Queues);
        session.EnvironmentAdminCommand.Execute("not a tab");
        session.EnvironmentAdminCommand.Execute(null);
        session.OpenUrl("  ");
        session.SelectedSolution = null;

        Assert.Equal(status, session.Status);
        Assert.False(session.OpenLinkCommand.CanExecute(new SolutionComponentItem { Name = "x", ComponentTypeName = "Table" }));
        Assert.True(session.OpenLinkCommand.CanExecute(new SolutionComponentItem { Name = "x", ComponentTypeName = "Table", MakerUrl = "https://make.powerapps.com/x" }));
    }

    [Fact]
    public Task Checking_layers_without_a_connection_does_nothing() => EnvironmentSessionThread.Run(async () =>
    {
        var session = new FakeEnvironment().Session();
        var status = session.Status;

        await session.CheckUnmanagedLayersCommand.ExecuteAsync(null);
        await session.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(status, session.Status);
        Assert.False(session.IsCheckingLayers);
    });

    [Fact]
    public Task A_tab_whose_sign_in_failed_offers_no_tools() => EnvironmentSessionThread.Run(async () =>
    {
        var session = await FailedAsync();

        session.DocumentSolutionCommand.Execute(null);
        Assert.Equal("Choose a solution first - the documentation is of one solution.", session.Status);

        session.ContentSearchCommand.Execute(null);
        Assert.Equal("Connect first.", session.Status);

        await session.ExportDeploymentSettingsCommand.ExecuteAsync(null);
        Assert.Equal("Choose a solution first - the settings file is for one solution.", session.Status);

        session.RecentChangesCommand.Execute(null);
        session.FailuresCommand.Execute(null);
        session.SecurityLookupCommand.Execute(null);
        await session.TurnOnSolutionFlowsCommand.ExecuteAsync(null);
        Assert.Equal("Connect first.", session.Status);
    });

    [Fact]
    public Task Setting_a_value_it_already_has_changes_nothing() => EnvironmentSessionThread.Run(async () =>
    {
        var (env, session) = await EnvironmentSessionFilterTests.ConnectedAsync();
        var row = session.AllItems[0];
        session.SelectedItem = row;
        var (type, subType, state, layer, solution) = (session.SelectedTypeFilter, session.SelectedSubTypeFilter,
            session.SelectedStateFilter, session.SelectedLayerFilter, session.SelectedSolution);
        var changed = new List<string?>();
        session.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        session.SelectedItem = row;
        session.SearchText = string.Empty;
        session.EnvironmentUrl = env.Url;
        session.FavouritesOnly = false;
        session.SelectedTypeFilter = type;
        session.SelectedSubTypeFilter = subType;
        session.SelectedStateFilter = state;
        session.SelectedLayerFilter = layer;
        session.SelectedSolution = solution;
        session.TypeFilterSearchText = string.Empty;

        Assert.Empty(changed);
    });

    [Fact]
    public Task The_solution_picker_text_never_goes_null() => EnvironmentSessionThread.Run(() =>
    {
        var session = new FakeEnvironment().Session();

        session.SolutionSearchText = null!;
        Assert.Equal(string.Empty, session.SolutionSearchText);

        session.SolutionSearchText = "core";
        session.EndSolutionSearch();
        Assert.Equal(string.Empty, session.SolutionSearchText);

        session.TypeFilterSearchText = "tab";
        Assert.Null(session.SelectedTypeFilter);
        return Task.CompletedTask;
    });

    [Fact]
    public void Filters_ignore_what_is_not_theirs()
    {
        var session = new FakeEnvironment().Session();
        session.TypeFilterSearchText = "x";

        Assert.False(session.ItemsView.Filter("not a row"));
        Assert.False(session.SolutionsView.Filter("not a solution"));
        Assert.False(session.TypeFiltersView.Filter("not an option"));
    }

    [Fact]
    public Task Clearing_filters_on_an_empty_tab_is_harmless() => EnvironmentSessionThread.Run(() =>
    {
        var session = new FakeEnvironment().Session();

        session.ClearFiltersCommand.Execute(null);

        Assert.Null(session.SelectedTypeFilter);
        Assert.Equal("0 objects", session.ResultSummary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_layer_filter_it_does_not_know_lets_everything_through() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await EnvironmentSessionFilterTests.ConnectedAsync();

        session.SelectedLayerFilter = new TypeFilterOption { Name = "Something new", Count = 0 };

        Assert.Equal("5 objects", session.ResultSummary);
    });

    [Fact]
    public Task Only_a_sub_type_counts_as_an_active_filter() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await EnvironmentSessionFilterTests.ConnectedAsync();

        session.SelectedSubTypeFilter = session.SubTypeFilters.Single(f => f.Name == "Cloud Flow");

        Assert.True(session.HasActiveFilters);
        Assert.Equal("1 of 5 objects", session.ResultSummary);
    });

    [Fact]
    public Task Revoking_writes_that_were_never_allowed_changes_no_settings() => EnvironmentSessionThread.Run(async () =>
    {
        var settings = new AppSettings();
        var env = new FakeEnvironment();
        var session = await env.ConnectedAsync(settings);
        await EnvironmentSessionThread.Until(() => session.EnvironmentType is not null, "the environment type");

        // A sandbox needs no allowing; asking anyway does nothing.
        Assert.False(session.AllowWritesCommand.CanExecute(null));
        var status = session.Status;
        session.AllowWritesCommand.Execute(null);
        Assert.Equal(status, session.Status);

        session.RevokeWritesCommand.Execute(null);

        Assert.Null(settings.AllowProductionWrites);
        Assert.Equal($"Writes to {env.Host} are blocked again.", session.Status);
    });

    [Fact]
    public Task Without_a_link_environment_id_the_write_check_uses_the_one_in_settings() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment { WithOrganizationEnvironmentId = false };
        env.Handler.OnJson(HttpMethod.Get, "/api/discovery/v2.0/Instances", "{\"value\":[]}");
        var settings = new AppSettings();
        var session = await env.ConnectedAsync(settings);
        settings.EnvironmentIds = new() { [env.Host] = FakeEnvironment.EnvironmentId };

        var permission = await session.EvaluateWritePermissionAsync();

        Assert.True(permission.Allowed);
    });

    [Fact]
    public Task Starring_a_row_with_no_id_marks_it_without_keeping_it() => EnvironmentSessionThread.Run(async () =>
    {
        var (env, session) = await EnvironmentSessionFilterTests.ConnectedAsync();
        var draft = new SolutionComponentItem { Name = "draft", ComponentTypeName = "Table" };

        session.ToggleFavouriteCommand.Execute(draft);

        Assert.True(draft.IsFavourite);
        Assert.Equal("Added draft to favourites.", session.Status);
        Assert.Empty(UserLibrary.Shared.For(env.Url).Favourites);
    });

    [Fact]
    public Task A_tab_whose_sign_in_failed_still_asks_about_writes_by_the_id_in_settings() => EnvironmentSessionThread.Run(async () =>
    {
        var session = await FailedAsync();

        Assert.Null(session.EnvironmentId);
        Assert.True((await session.EvaluateWritePermissionAsync()).Allowed);
    });

    [Fact]
    public void A_tab_that_never_connected_resets_and_disposes_quietly()
    {
        var session = new FakeEnvironment().Session();

        session.Reset("Signed out.");
        session.Dispose();

        Assert.Equal("Signed out.", session.Status);
        Assert.Equal(string.Empty, session.TypeFilterSearchText);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public Task A_selection_read_due_after_a_disconnect_reads_nothing() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 29, "Notify owner", workflowCategory: 5);
        var session = await env.ConnectedAsync();
        session.SelectedItem = session.AllItems[0];
        var requests = env.Handler.Requests.Count;

        session.Reset("Disconnected.");
        EnvironmentSessionThread.Elapse(session, TimeSpan.FromMilliseconds(250));
        await Task.Delay(20);

        Assert.Equal(requests, env.Handler.Requests.Count);
        Assert.Null(session.LatestRun);
    });

    [Fact]
    public Task Reconnecting_after_a_layer_check_and_mid_type_read_starts_afresh() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 24, "Main form");
        var bap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        env.Handler.OnAsync(HttpMethod.Get, "BusinessAppPlatform/environments", async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) await bap.Task;
            return FakeHttpHandler.Json("{\"value\":[]}");
        });
        var session = await env.ConnectedAsync();
        await session.CheckUnmanagedLayersCommand.ExecuteAsync(null);
        Assert.Equal("Checked 0 object(s) for an unmanaged layer (1 of this type aren't supported by the layers check).", session.Status);

        await session.ConnectAsync();
        bap.SetResult();
        await EnvironmentSessionThread.Until(() => session.EnvironmentType is not null, "the second type read");

        Assert.True(session.IsConnected);
        Assert.Equal(EnvironmentSku.Unknown, session.EnvironmentType!.Sku);
    });

    [Theory]
    [InlineData("Default", EnvironmentSku.Default, true)]
    [InlineData("Developer", EnvironmentSku.Developer, false)]
    [InlineData("Trial", EnvironmentSku.Trial, false)]
    public Task Only_protected_types_offer_allowing_writes(string sku, EnvironmentSku expected, bool offered) => EnvironmentSessionThread.Run(async () =>
    {
        var session = await new FakeEnvironment(sku).ConnectedAsync();
        await EnvironmentSessionThread.Until(() => session.EnvironmentType is not null, "the environment type");

        Assert.Equal(expected, session.EnvironmentSku);
        Assert.Equal(offered, session.CanAllowWrites);
    });

    [Fact]
    public void A_remembered_unknown_type_is_no_better_than_none()
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(),
            new TabState { EnvironmentUrl = Fakes.EnvironmentUrl, EnvironmentType = EnvironmentSku.Unknown });

        Assert.Equal("Environment type not read yet - it is read after connecting.", session.EnvironmentTypeDescription);
        Assert.Equal(EnvironmentSku.Unknown, session.EnvironmentSku);
    }

    [Fact]
    public Task Without_the_organization_the_tab_falls_back_to_its_host_and_discovery() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.Handler.OnError(HttpMethod.Get, "RetrieveCurrentOrganization", HttpStatusCode.NotFound, "Not available on this version");

        var session = await env.ConnectedAsync();

        Assert.True(session.IsConnected);
        Assert.Equal(env.Host.Split('.')[0], session.Title);
        Assert.Equal(FakeEnvironment.EnvironmentId, session.EnvironmentId);
    });

    [Fact]
    public Task A_graph_client_signs_in_as_the_tab_does() => EnvironmentSessionThread.Run(async () =>
    {
        var session = await new FakeEnvironment().ConnectedAsync();

        using var graph = session.CreateGraphClient();

        Assert.IsType<Graph.GraphClient>(graph);
    });
}
