using System.Net;
using System.Net.Http;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>An environment tab connecting: signing in, reading the environment and its type, and disconnecting again.</summary>
public class EnvironmentSessionConnectTests
{
    private const string BapList = "BusinessAppPlatform/environments";

    [Fact]
    public Task Connecting_lists_the_solutions_default_first_and_loads_the_remembered_one() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var core = env.AddSolution("ECT Core", "ect_core");
        env.AddSolution("Accounts", "accounts");
        env.AddSolution("Default Solution", "Default");
        env.AddComponent(core, 1, "account", "Account");
        env.AddComponent(core, 29, "Notify owner", workflowCategory: 5);
        var session = env.Session(solution: "ECT_CORE");
        var stateChanges = 0;
        session.StateChanged += (_, _) => stateChanges++;

        await session.ConnectAsync();

        Assert.True(session.IsConnected);
        Assert.True(session.IsSolutionPickerEnabled);
        Assert.False(session.IsBusy);
        Assert.Equal("Contoso Dev", session.Title);
        Assert.Equal(["Default Solution", "Accounts", "ECT Core"], session.Solutions.Select(s => s.FriendlyName));
        Assert.Equal("ect_core", session.SelectedSolution?.UniqueName);
        Assert.Equal(2, session.Items.Count);
        Assert.Equal("2 objects", session.ResultSummary);
        Assert.StartsWith("Loaded 2 objects from 'ECT Core' in ", session.Status);
        Assert.True(session.LinksAvailable);
        Assert.Equal(FakeEnvironment.EnvironmentId, session.EnvironmentId);
        Assert.Equal(env.Url, session.Client!.EnvironmentUrl);
        Assert.True(stateChanges >= 1);

        var state = session.ToState();
        Assert.Equal(env.Url, state.EnvironmentUrl);
        Assert.Equal("ect_core", state.SolutionUniqueName);
        Assert.Equal("22222222-0000-0000-0000-000000000002", state.TenantId);
        Assert.Equal("22222222-0000-0000-0000-000000000002", session.TenantId);

        // Every Dataverse request carries the environment's token.
        Assert.All(env.Handler.Requests.Where(r => r.Url.StartsWith(env.Url, StringComparison.Ordinal)),
            r => Assert.Equal("Bearer token:" + env.Url, r.Authorization));
    });

    [Fact]
    public Task Without_a_remembered_solution_the_default_solution_is_loaded() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.AddSolution("ECT Core", "ect_core");
        var fallback = env.AddSolution("Default Solution", "Default");
        env.AddComponent(fallback, 61, "new_script.js");

        var session = await env.ConnectedAsync(solution: "no_longer_there");

        Assert.Equal("Default", session.SelectedSolution?.UniqueName);
        Assert.Equal("new_script.js", Assert.Single(session.Items).Name);
        Assert.Equal("Default", session.ToState().SolutionUniqueName);
    });

    [Fact]
    public Task Without_a_default_solution_the_first_listed_is_loaded() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.AddSolution("Zeta", "zeta");
        env.AddSolution("Alpha", "alpha");

        var session = await env.ConnectedAsync();

        Assert.Equal("alpha", session.SelectedSolution?.UniqueName);
        Assert.Empty(session.Items);
        Assert.StartsWith("Loaded 0 objects from 'Alpha'", session.Status);
    });

    [Fact]
    public Task An_environment_with_no_solutions_says_so() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();

        var session = await env.ConnectedAsync();

        Assert.True(session.IsConnected);
        Assert.Null(session.SelectedSolution);
        Assert.Equal("Connected, but no solutions were returned.", session.Status);
        Assert.DoesNotContain(env.Handler.Requests, r => r.Url.Contains("msdyn_solutioncomponentsummaries", StringComparison.Ordinal));
    });

    [Fact]
    public Task Without_a_friendly_name_the_tab_is_named_after_the_host() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment { FriendlyName = null };

        var session = await env.ConnectedAsync();

        Assert.Equal(env.Host.Split('.')[0], session.Title);
    });

    [Fact]
    public Task The_environment_id_comes_from_discovery_when_the_organization_does_not_carry_it() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment { WithOrganizationEnvironmentId = false };
        env.AddSolution("Default Solution", "Default");

        var session = await env.ConnectedAsync();

        Assert.Equal(FakeEnvironment.EnvironmentId, session.EnvironmentId);
        Assert.True(session.LinksAvailable);
        Assert.Contains(env.Handler.Requests, r => r.Url.EndsWith("/api/discovery/v2.0/Instances", StringComparison.Ordinal));
    });

    [Fact]
    public Task An_environment_id_in_settings_is_used_before_asking_anyone() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment { WithOrganizationEnvironmentId = false };
        env.AddSolution("Default Solution", "Default");
        var settings = new AppSettings { EnvironmentIds = new() { [env.Host] = "from-settings" } };

        var session = await env.ConnectedAsync(settings);

        Assert.Equal("from-settings", session.EnvironmentId);
        Assert.DoesNotContain(env.Handler.Requests, r => r.Url.Contains("/api/discovery/", StringComparison.Ordinal));
    });

    [Fact]
    public Task Without_an_environment_id_the_status_says_maker_links_are_unavailable() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment { WithOrganizationEnvironmentId = false };
        env.Handler.OnJson(HttpMethod.Get, "/api/discovery/v2.0/Instances", "{\"value\":[]}");
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 1, "account", "Account");

        var session = await env.ConnectedAsync();

        Assert.False(session.LinksAvailable);
        Assert.Null(session.EnvironmentId);
        Assert.Contains("Maker portal links are unavailable", session.Status);
        Assert.Null(Assert.Single(session.Items).MakerUrl);
    });

    [Fact]
    public Task A_failed_sign_in_leaves_the_tab_disconnected_with_the_reason() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.Handler.OnError(HttpMethod.Get, "/WhoAmI", HttpStatusCode.Unauthorized, "The user is not a member of the organization.");

        var session = await env.ConnectedAsync();

        Assert.False(session.IsConnected);
        Assert.False(session.IsBusy);
        Assert.StartsWith("Connection failed: ", session.Status);
        Assert.Contains("The user is not a member of the organization.", session.Status);
        Assert.False(session.RefreshCommand.CanExecute(null));
    });

    [Fact]
    public Task An_empty_url_fails_to_connect_with_a_hint() => EnvironmentSessionThread.Run(async () =>
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings());

        await session.ConnectAsync();

        Assert.False(session.IsConnected);
        Assert.Equal("Connection failed: Enter an environment URL, e.g. https://contoso.crm11.dynamics.com", session.Status);
    });

    [Fact]
    public Task Connecting_again_while_signing_in_cancels_the_first_attempt() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.AddSolution("Default Solution", "Default");
        var firstWhoAmI = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        env.Handler.OnAsync(HttpMethod.Get, "/WhoAmI", async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) await firstWhoAmI.Task;
            return FakeHttpHandler.Json("{\"UserId\":\"11111111-0000-0000-0000-000000000001\"}");
        });
        var session = env.Session();
        var statuses = new List<string>();
        session.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(session.Status)) statuses.Add(session.Status); };

        var first = session.ConnectAsync();
        await EnvironmentSessionThread.Until(() => calls == 1, "the first sign-in");
        var second = session.ConnectAsync();
        firstWhoAmI.SetResult();
        await Task.WhenAll(first, second);

        Assert.Contains("Cancelled.", statuses);
        Assert.Contains(statuses, s => s.StartsWith("Loaded 0 objects", StringComparison.Ordinal));
        Assert.True(session.IsConnected);
        Assert.Equal("Default", session.SelectedSolution?.UniqueName);
    });

    [Fact]
    public Task Switching_account_connects_again() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.AddSolution("Default Solution", "Default");
        var session = await env.ConnectedAsync();
        var whoAmIs = env.Handler.Requests.Count(r => r.Url.EndsWith("/WhoAmI", StringComparison.Ordinal));

        await session.SwitchAccountCommand.ExecuteAsync(null);

        Assert.True(session.IsConnected);
        Assert.Equal(whoAmIs + 1, env.Handler.Requests.Count(r => r.Url.EndsWith("/WhoAmI", StringComparison.Ordinal)));
    });

    [Fact]
    public Task Connecting_to_a_new_url_reaches_the_new_environment() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.AddSolution("Default Solution", "Default");
        var session = await env.ConnectedAsync();
        var other = $"https://other{Guid.NewGuid():N}.crm4.dynamics.com";

        session.EnvironmentUrl = other + "/main.aspx";

        // Still connected to the first: the title stays until the new one is reached.
        Assert.Equal("Contoso Dev", session.Title);
        Assert.Equal(new Uri(other).Host, session.EnvironmentHost);

        await session.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(other, session.Client!.EnvironmentUrl);
        Assert.Equal(other, session.EnvironmentUrl);
        Assert.Contains(env.Handler.Requests, r => r.Url.StartsWith(other + "/api/data/v9.2/WhoAmI", StringComparison.Ordinal));
    });

    [Fact]
    public Task Disconnecting_clears_the_tab() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 1, "account", "Account");
        var session = await env.ConnectedAsync();
        await EnvironmentSessionThread.Until(() => session.EnvironmentType is not null, "the environment type");
        Assert.True(session.DisconnectCommand.CanExecute(null));

        session.DisconnectCommand.Execute(null);

        Assert.False(session.IsConnected);
        Assert.Null(session.Client);
        Assert.Null(session.SelectedSolution);
        Assert.Empty(session.Items);
        Assert.Empty(session.AllItems);
        Assert.Empty(session.Solutions);
        Assert.Empty(session.TypeFilters);
        Assert.Empty(session.StateFilters);
        Assert.Equal(string.Empty, session.ResultSummary);
        Assert.Null(session.EnvironmentType);
        Assert.Equal("Disconnected.", session.Status);
        Assert.Equal(env.Host.Split('.')[0], session.Title);
        Assert.Equal("?", session.AccountInitials);
        Assert.False(session.DisconnectCommand.CanExecute(null));

        session.Dispose();
    });

    [Fact]
    public Task Disposing_a_connected_tab_stops_its_work() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.AddSolution("Default Solution", "Default");
        var session = await env.ConnectedAsync();
        var client = session.Client!;

        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.WhoAmIAsync());
    });

    // ---------------------------------------------------------------- environment type

    [Fact]
    public Task The_environment_type_is_read_after_connecting_and_remembered_with_the_tab() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment("Production");
        var session = env.Session();
        var stateChanges = 0;
        session.StateChanged += (_, _) => stateChanges++;
        Assert.Equal("Environment type not read yet - it is read after connecting.", session.EnvironmentTypeDescription);

        await session.ConnectAsync();
        await EnvironmentSessionThread.Until(() => session.EnvironmentType is not null, "the environment type");

        Assert.Equal(EnvironmentSku.Production, session.EnvironmentSku);
        Assert.Equal("Production", session.EnvironmentTypeDescription);
        Assert.Equal(EnvironmentSku.Production, session.ToState().EnvironmentType);
        Assert.True(stateChanges >= 2);
        Assert.True(session.CanAllowWrites);
        Assert.True(session.AllowWritesCommand.CanExecute(null));
        Assert.False(session.IsWriteAllowlisted);
        Assert.Equal(string.Empty, session.WriteAccessDescription);
    });

    [Fact]
    public Task A_remembered_type_shows_before_connecting_and_through_a_failed_read() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.Handler.OnStatus(HttpMethod.Get, BapList, HttpStatusCode.Forbidden);
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(),
            new TabState { EnvironmentUrl = env.Url, EnvironmentType = EnvironmentSku.Sandbox },
            FakeEnvironment.Auth, (auth, url) => new DataverseClient(auth, url, env.Routes()));

        Assert.Equal(EnvironmentSku.Sandbox, session.EnvironmentSku);
        Assert.Equal("Sandbox, remembered from the last connection.", session.EnvironmentTypeDescription);
        Assert.False(session.CanAllowWrites);

        await session.ConnectAsync();
        await EnvironmentSessionThread.Until(() => session.EnvironmentType is not null, "the environment type");

        Assert.Equal(EnvironmentSku.Unknown, session.EnvironmentType!.Sku);
        Assert.Equal(EnvironmentSku.Sandbox, session.EnvironmentSku);
        Assert.StartsWith("Sandbox, remembered from the last connection. This time: This environment was not found",
            session.EnvironmentTypeDescription);
        Assert.Equal(EnvironmentSku.Sandbox, session.ToState().EnvironmentType);
    });

    [Fact]
    public Task A_failed_type_read_with_nothing_remembered_says_why() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.Handler.OnStatus(HttpMethod.Get, BapList, HttpStatusCode.Forbidden);

        var session = await env.ConnectedAsync();
        await EnvironmentSessionThread.Until(() => session.EnvironmentType is not null, "the environment type");

        Assert.Equal(EnvironmentSku.Unknown, session.EnvironmentSku);
        Assert.Contains("HTTP 403", session.EnvironmentTypeDescription);
        Assert.True(session.CanAllowWrites);
    });

    [Fact]
    public Task Changing_the_url_before_connecting_forgets_the_remembered_type() => EnvironmentSessionThread.Run(() =>
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(),
            new TabState { EnvironmentUrl = "https://contoso.crm11.dynamics.com", EnvironmentType = EnvironmentSku.Production });
        Assert.Equal("contoso", session.Title);
        Assert.Equal(EnvironmentSku.Production, session.EnvironmentSku);

        session.EnvironmentUrl = "fabrikam.crm4.dynamics.com";

        Assert.Equal(EnvironmentSku.Unknown, session.EnvironmentSku);
        Assert.Equal("fabrikam", session.Title);
        Assert.Equal("fabrikam.crm4.dynamics.com", session.EnvironmentHost);
        Assert.Null(session.ToState().EnvironmentType);
        Assert.Null(session.AccountName);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("", "New environment", "Not connected")]
    [InlineData("https://contoso.crm11.dynamics.com/", "contoso", "contoso.crm11.dynamics.com")]
    [InlineData("  bad url  ", "bad url", "  bad url  ")]
    public void The_title_and_host_come_from_the_url(string url, string title, string host)
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(), new TabState { EnvironmentUrl = url });

        Assert.Equal(title, session.Title);
        Assert.Equal(host, session.EnvironmentHost);
        Assert.Equal(host != "Not connected", session.CanAllowWrites);
        Assert.False(session.IsWriteAllowlisted);
    }

    [Fact]
    public Task A_type_read_that_finishes_after_a_disconnect_is_dropped() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment("Production");
        var bap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Handler.OnAsync(HttpMethod.Get, BapList, async _ =>
        {
            await bap.Task;
            return FakeHttpHandler.Json("{\"value\":[]}");
        });
        var session = await env.ConnectedAsync();
        await EnvironmentSessionThread.Until(() => env.Handler.Requests.Any(r => r.Url.Contains(BapList, StringComparison.Ordinal)),
            "the environment type to be asked");

        session.Reset("Signed out.");
        bap.SetResult();
        await Task.Delay(100);

        Assert.Null(session.EnvironmentType);
        Assert.Equal("Signed out.", session.Status);
    });

    // ---------------------------------------------------------------- writes

    [Fact]
    public Task Writes_are_permitted_where_the_live_type_allows_them() => EnvironmentSessionThread.Run(async () =>
    {
        var sandbox = await new FakeEnvironment("Sandbox").ConnectedAsync();
        var production = await new FakeEnvironment("Production").ConnectedAsync();

        Assert.True((await sandbox.EvaluateWritePermissionAsync()).Allowed);
        Assert.False((await production.EvaluateWritePermissionAsync()).Allowed);
    });

    [Fact]
    public Task Asking_about_writes_before_connecting_is_an_error() => EnvironmentSessionThread.Run(async () =>
    {
        var session = new FakeEnvironment().Session();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EvaluateWritePermissionAsync());
    });

    [Fact]
    public Task An_allowlisted_production_environment_can_have_its_writes_revoked() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment("Production");
        var settings = new AppSettings { AllowProductionWrites = [env.Url] };
        var session = await env.ConnectedAsync(settings);
        await EnvironmentSessionThread.Until(() => session.EnvironmentType is not null, "the environment type");
        Assert.True((await session.EvaluateWritePermissionAsync()).Allowed);
        Assert.True(session.IsWriteAllowlisted);
        Assert.False(session.CanAllowWrites);
        Assert.Equal("Writes allowed - this environment is in AllowProductionWrites.", session.WriteAccessDescription);
        var changed = 0;
        session.WriteAllowlistChanged += (_, _) => changed++;

        Assert.True(session.RevokeWritesCommand.CanExecute(null));
        session.RevokeWritesCommand.Execute(null);

        Assert.False(session.IsWriteAllowlisted);
        Assert.True(session.CanAllowWrites);
        Assert.Empty(settings.AllowProductionWrites);
        Assert.Equal(1, changed);
        Assert.Equal($"Writes to {env.Host} are blocked again.", session.Status);
        Assert.False((await session.EvaluateWritePermissionAsync()).Allowed);
    });
}
