using System.Net.Http;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The gate in front of every one-click write: the write guard, then the confirmation.</summary>
[Collection(nameof(WriteConfirmation))]
public sealed class WriteConfirmationTests : IDisposable
{
    private readonly Func<EnvironmentSessionViewModel, string, string, bool> _prompt = WriteConfirmation.Prompt;
    private readonly Action<string, string> _refuse = WriteConfirmation.Refuse;
    private readonly List<(string Message, string Title)> _refusals = new();
    private readonly List<(string Title, string Action)> _prompts = new();

    public WriteConfirmationTests()
    {
        WriteConfirmation.Refuse = (message, title) => _refusals.Add((message, title));
    }

    public void Dispose()
    {
        WriteConfirmation.Prompt = _prompt;
        WriteConfirmation.Refuse = _refuse;
    }

    private static FakeHttpHandler EnvironmentOfType(string sku) => new FakeHttpHandler().OnJson(HttpMethod.Get,
        "BusinessAppPlatform/environments?",
        "{\"value\":[{\"name\":\"env-1\",\"properties\":{\"displayName\":\"Contoso\",\"environmentSku\":\"" + sku + "\"," +
        "\"linkedEnvironmentMetadata\":{\"instanceApiUrl\":\"" + Fakes.EnvironmentUrl + "/\"}}}]}");

    private void Answer(bool yes) => WriteConfirmation.Prompt = (_, title, action) =>
    {
        _prompts.Add((title, action));
        return yes;
    };

    [Fact]
    public void The_tooltip_says_what_where_and_that_it_asks_first()
    {
        Assert.Equal("Turn on 3 flows in Contoso Dev. Asks first; recorded in the run log.",
            WriteConfirmation.ToolTip("Turn on 3 flows", "Contoso Dev"));
    }

    [Fact]
    public void Without_an_application_there_is_no_window_to_ask_over()
    {
        Assert.Null(System.Windows.Application.Current);
        Assert.Null(WriteConfirmation.ActiveWindow());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sandbox_asks_and_the_answer_decides(bool yes)
    {
        Answer(yes);
        var session = TestSessions.Connected(EnvironmentOfType("Sandbox"), []);

        var allowed = await WriteConfirmation.AskAsync(session, "Turn on", "Turn on 3 flows");

        Assert.Equal(yes, allowed);
        Assert.Equal(("Turn on", "Turn on 3 flows"), Assert.Single(_prompts));
        Assert.Empty(_refusals);
    }

    [Fact]
    public async Task Production_is_refused_by_the_guard_without_asking()
    {
        Answer(true);
        var session = TestSessions.Connected(EnvironmentOfType("Production"), []);

        var allowed = await WriteConfirmation.AskAsync(session, "Sync team", "Add 2 members");

        Assert.False(allowed);
        Assert.Empty(_prompts);
        var (message, title) = Assert.Single(_refusals);
        Assert.Equal("Sync team", title);
        Assert.Contains("production", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_tab_that_cannot_be_checked_is_refused_with_the_reason()
    {
        Answer(true);
        var session = TestSessions.Disconnected();

        var allowed = await WriteConfirmation.AskAsync(session, "Sync team", "Add 2 members");

        Assert.False(allowed);
        Assert.Empty(_prompts);
        Assert.Equal(
            ("Could not check whether contoso may be written to - The environment is not connected.", "Sync team"),
            Assert.Single(_refusals));
    }
}
