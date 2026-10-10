using System.Net.Http;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ViewModels.DetailsHarness;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The details window's one-click changes ask the write guard and the person before anything is written.</summary>
[Collection(nameof(WriteConfirmation))]
public sealed class ObjectDetailsWriteTests : IDisposable
{
    private readonly Func<EnvironmentSessionViewModel, string, string, bool> _prompt = WriteConfirmation.Prompt;
    private readonly Action<string, string> _refuse = WriteConfirmation.Refuse;

    private readonly List<(string Title, string Action)> _asked = new();
    private readonly List<(string Message, string Title)> _refused = new();

    public ObjectDetailsWriteTests()
    {
        WriteConfirmation.Prompt = (_, title, action) =>
        {
            _asked.Add((title, action));
            return false;
        };
        WriteConfirmation.Refuse = (message, title) => _refused.Add((message, title));
    }

    public void Dispose()
    {
        WriteConfirmation.Prompt = _prompt;
        WriteConfirmation.Refuse = _refuse;
    }

    /// <summary>A tab signed in to the fake environment; allowlisted, it may be written to whatever its type.</summary>
    private static EnvironmentSessionViewModel Connected(FakeHttpHandler handler, bool allowlisted)
    {
        var settings = new AppSettings();
        if (allowlisted) settings.AllowProductionWrites = [Fakes.EnvironmentUrl];

        var session = new EnvironmentSessionViewModel(new AuthenticationService(), settings,
            new TabState { EnvironmentUrl = Fakes.EnvironmentUrl });
        session.UseConnectedClient(new DataverseClient(TestAuth.TokensFor(Fakes.EnvironmentUrl), Fakes.EnvironmentUrl, handler));
        return session;
    }

    private static FakeHttpHandler Step() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "organizations?$select=plugintracelogsetting", Empty)
        .OnJson(HttpMethod.Get, "plugintracelogs?", Empty)
        .OnJson(HttpMethod.Get, "sdkmessageprocessingsteps?", $$"""{"value":[{"sdkmessageprocessingstepid":"{{Id}}","statecode":0}]}""")
        .Quiet();

    private static FakeHttpHandler Variable() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Id})", """
            {"schemaname":"new_ApiUrl","type":100000000,"defaultvalue":"https://default",
             "environmentvariabledefinition_environmentvariablevalue":[{"environmentvariablevalueid":"ee000000-0000-0000-0000-000000000001","value":"https://current"}]}
            """)
        .Quiet();

    [Fact]
    public async Task Switching_in_an_environment_the_guard_blocks_is_refused_without_asking()
    {
        var handler = Step();
        var details = Details(handler, Item(92, name: "OnCreate"), session: Connected(handler, allowlisted: false));
        await details.LoadAsync();

        await details.ToggleSwitchCommand.ExecuteAsync(null);

        var (message, title) = Assert.Single(_refused);
        Assert.Equal("Disable", title);
        Assert.StartsWith("Writing here is blocked.", message);
        Assert.Empty(_asked);
        Assert.DoesNotContain(handler.Requests, r => r.Method != HttpMethod.Get);
        Assert.True(details.IsSwitchedOn);
    }

    [Fact]
    public async Task Switching_that_the_person_declines_writes_nothing()
    {
        var handler = Step();
        var details = Details(handler, Item(92, name: "OnCreate"), session: Connected(handler, allowlisted: true));
        await details.LoadAsync();

        await details.ToggleSwitchCommand.ExecuteAsync(null);

        Assert.Equal([("Disable", "Disable OnCreate?")], _asked);
        Assert.Empty(_refused);
        Assert.DoesNotContain(handler.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task A_tab_that_is_not_connected_cannot_be_checked_and_so_is_refused()
    {
        var details = Details(Step(), Item(92), session: Session());
        await details.LoadAsync();

        await details.ToggleSwitchCommand.ExecuteAsync(null);

        var (message, _) = Assert.Single(_refused);
        Assert.StartsWith("Could not check whether ", message);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Setting_a_value_names_the_variable_and_the_value()
    {
        var handler = Variable();
        var details = Details(handler, Item(380), session: Connected(handler, allowlisted: true));
        await details.LoadAsync();

        details.EnvironmentValueInput = "https://new";
        await details.SetEnvironmentValueCommand.ExecuteAsync(null);

        Assert.Equal([("Set value", "Set new_ApiUrl to:\n\nhttps://new")], _asked);
        Assert.DoesNotContain(handler.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task A_long_value_is_shortened_in_the_question()
    {
        var handler = Variable();
        var details = Details(handler, Item(380), session: Connected(handler, allowlisted: true));
        await details.LoadAsync();

        details.EnvironmentValueInput = new string('v', 500);
        await details.SetEnvironmentValueCommand.ExecuteAsync(null);

        var (_, action) = Assert.Single(_asked);
        Assert.EndsWith(new string('v', 400) + "...", action);
    }

    [Fact]
    public async Task Removing_the_value_says_the_default_will_apply()
    {
        var handler = Variable();
        var details = Details(handler, Item(380), session: Connected(handler, allowlisted: true));
        await details.LoadAsync();

        await details.ClearEnvironmentValueCommand.ExecuteAsync(null);

        Assert.Equal([("Remove value", "Remove the current value of new_ApiUrl, so its default applies?")], _asked);
        Assert.Equal("https://current", details.EnvironmentVariable?.CurrentValue);
    }
}
