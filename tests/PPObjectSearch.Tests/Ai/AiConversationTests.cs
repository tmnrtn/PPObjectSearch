using System.IO;
using System.Text.Json;
using PPObjectSearch.Ai;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Ai;

/// <summary>A provider that answers from a script, and keeps every request it was sent.</summary>
internal sealed class ScriptedClient : IAiChatClient
{
    private readonly Queue<Func<AiRequest, AiReply>> _replies = new();

    public List<AiRequest> Requests { get; } = new();

    /// <summary>The conversation as each request carried it - the live list grows after the call.</summary>
    public List<List<AiMessage>> Sent { get; } = new();

    public string Label => "Scripted";
    public bool SupportsTools { get; init; } = true;

    public ScriptedClient Then(string text) => Then(_ => new AiReply { Message = new AiMessage { Role = AiRole.Assistant, Text = text } });

    public ScriptedClient ThenTool(string name, object arguments, string id = "call_1") => Then(_ => new AiReply
    {
        Stop = AiStop.ToolUse,
        Message = new AiMessage
        {
            Role = AiRole.Assistant,
            ToolCalls = [new AiToolCall(id, name, JsonSerializer.SerializeToElement(arguments))]
        }
    });

    public ScriptedClient Then(Func<AiRequest, AiReply> reply)
    {
        _replies.Enqueue(reply);
        return this;
    }

    public Task<AiReply> SendAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Sent.Add(request.Messages.ToList());
        var reply = _replies.Dequeue()(request);
        if (reply.Message.Text.Length > 0) onText(reply.Message.Text);
        return Task.FromResult(reply);
    }
}

public class AiConversationTests
{
    private static readonly AiScreen Screen = new("Failures", "Flow X\tFailed");

    private static AiTool Echo(List<string>? calls = null) => new()
    {
        Name = "read_screen",
        Description = "Reads a screen.",
        Run = (args, _) =>
        {
            calls?.Add(AiTool.Text(args, "title") ?? "");
            return Task.FromResult("Screen text");
        }
    };

    [Fact]
    public async Task The_screen_goes_in_the_instructions_and_the_question_in_the_conversation()
    {
        var client = new ScriptedClient().Then("Flow X failed.");
        var conversation = new AiConversation();

        var answer = await conversation.AskAsync(client, "What failed?", Screen, [], _ => { }, null, CancellationToken.None);

        Assert.Equal("Flow X failed.", answer);
        var request = Assert.Single(client.Requests);
        Assert.Contains("<screen title=\"Failures\">", request.System);
        Assert.Contains("Flow X\tFailed", request.System);
        Assert.Equal("What failed?", Assert.Single(client.Sent[0]).Text);
        Assert.Equal(2, conversation.Messages.Count);
    }

    [Fact]
    public async Task Tools_the_model_asks_for_run_and_their_results_go_back()
    {
        var calls = new List<string>();
        var client = new ScriptedClient()
            .ThenTool("read_screen", new { title = "Run history" })
            .Then("It timed out.");
        var conversation = new AiConversation();
        var activity = new List<string>();

        var answer = await conversation.AskAsync(client, "Why?", Screen, [Echo(calls)], _ => { }, c => activity.Add(AiTools.Describe(c)), CancellationToken.None);

        Assert.Equal("It timed out.", answer);
        Assert.Equal(["Run history"], calls);
        Assert.Equal(["Reading Run history…"], activity);

        var second = client.Sent[1];
        Assert.Equal(3, second.Count);
        var result = Assert.Single(second[2].ToolResults);
        Assert.Equal(("call_1", "Screen text", false), (result.CallId, result.Content, result.IsError));
    }

    [Fact]
    public async Task A_tool_the_model_made_up_answers_with_an_error_rather_than_failing()
    {
        var client = new ScriptedClient().ThenTool("delete_everything", new { }).Then("Sorry.");
        var conversation = new AiConversation();

        await conversation.AskAsync(client, "Go", Screen, [Echo()], _ => { }, null, CancellationToken.None);

        var result = Assert.Single(client.Sent[1][2].ToolResults);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_provider_without_tools_is_offered_none()
    {
        var client = new ScriptedClient { SupportsTools = false }.Then("From the screen alone.");

        await new AiConversation().AskAsync(client, "Q", Screen, [Echo()], _ => { }, null, CancellationToken.None);

        Assert.Empty(client.Requests[0].Tools);
        Assert.DoesNotContain("use your tools", client.Requests[0].System);
    }

    [Fact]
    public async Task Endless_tool_use_stops_at_the_limit_with_every_call_answered()
    {
        var client = new ScriptedClient();
        for (var i = 0; i <= AiConversation.MaxToolRounds; i++) client.ThenTool("read_screen", new { title = "A" }, $"call_{i}");
        var conversation = new AiConversation();

        var answer = await conversation.AskAsync(client, "Q", Screen, [Echo()], _ => { }, null, CancellationToken.None);

        Assert.Contains("more lookups than allowed", answer);
        Assert.Equal(AiConversation.MaxToolRounds + 1, client.Requests.Count);
        Assert.True(conversation.Messages[^1].ToolResults.Single().IsError);
    }

    [Fact]
    public async Task A_failed_question_leaves_the_conversation_as_it_was()
    {
        var client = new ScriptedClient()
            .Then("First answer.")
            .ThenTool("read_screen", new { title = "A" })
            .Then(_ => throw new AiException("Rate limited."));
        var conversation = new AiConversation();
        await conversation.AskAsync(client, "First", Screen, [Echo()], _ => { }, null, CancellationToken.None);

        await Assert.ThrowsAsync<AiException>(() =>
            conversation.AskAsync(client, "Second", Screen, [Echo()], _ => { }, null, CancellationToken.None));

        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal("First answer.", conversation.Messages[1].Text);
    }
}

public class AiSettingsTests
{
    private const string Contoso = "https://contoso.crm11.dynamics.com";

    [Fact]
    public void A_sandbox_is_allowed_until_it_is_turned_off()
    {
        var settings = new AiSettings();
        Assert.True(settings.IsEnvironmentAllowed(Contoso, EnvironmentSku.Sandbox));

        settings.SetEnvironmentAllowed(Contoso + "/", false, EnvironmentSku.Sandbox);

        Assert.False(settings.IsEnvironmentAllowed("contoso.crm11.dynamics.com", EnvironmentSku.Sandbox));
        Assert.False(settings.IsEnvironmentAllowed("https://CONTOSO.crm11.dynamics.com/main.aspx", EnvironmentSku.Sandbox));
        Assert.True(settings.IsEnvironmentAllowed("https://fabrikam.crm11.dynamics.com", EnvironmentSku.Sandbox));
        Assert.Equal(["contoso.crm11.dynamics.com"], settings.ExcludedEnvironments);

        settings.SetEnvironmentAllowed(Contoso, true, EnvironmentSku.Sandbox);

        Assert.True(settings.IsEnvironmentAllowed(Contoso, EnvironmentSku.Sandbox));
        Assert.Null(settings.ExcludedEnvironments);
        Assert.Null(settings.AllowedEnvironments);
    }

    [Theory]
    [InlineData(EnvironmentSku.Production, false)]
    [InlineData(EnvironmentSku.Unknown, false)]
    [InlineData(EnvironmentSku.Default, true)]
    [InlineData(EnvironmentSku.Sandbox, true)]
    [InlineData(EnvironmentSku.Developer, true)]
    [InlineData(EnvironmentSku.Trial, true)]
    public void Production_and_unknown_types_start_with_AI_off(EnvironmentSku sku, bool allowed)
    {
        Assert.Equal(allowed, new AiSettings().IsEnvironmentAllowed(Contoso, sku));
    }

    [Fact]
    public void Production_is_read_only_once_allowed_by_hand()
    {
        var settings = new AiSettings();
        settings.SetEnvironmentAllowed(Contoso, true, EnvironmentSku.Production);

        Assert.True(settings.IsEnvironmentAllowed(Contoso, EnvironmentSku.Production));
        Assert.Equal(["contoso.crm11.dynamics.com"], settings.AllowedEnvironments);
        Assert.Null(settings.ExcludedEnvironments);

        // Turned off again, it is back to its default: nothing recorded.
        settings.SetEnvironmentAllowed(Contoso, false, EnvironmentSku.Production);
        Assert.False(settings.IsEnvironmentAllowed(Contoso, EnvironmentSku.Production));
        Assert.Null(settings.AllowedEnvironments);
    }

    [Fact]
    public void A_choice_for_an_environment_not_open_holds_whatever_its_type()
    {
        var settings = new AiSettings();
        settings.SetEnvironmentChoice(Contoso, false);
        Assert.False(settings.IsEnvironmentAllowed(Contoso, EnvironmentSku.Sandbox));

        settings.SetEnvironmentChoice(Contoso, true);
        Assert.True(settings.IsEnvironmentAllowed(Contoso, EnvironmentSku.Production));
    }

    [Fact]
    public void Turning_an_environment_off_twice_lists_it_once()
    {
        var settings = new AiSettings();
        settings.SetEnvironmentAllowed("https://contoso.crm.dynamics.com", false, EnvironmentSku.Sandbox);
        settings.SetEnvironmentAllowed("https://contoso.crm.dynamics.com", false, EnvironmentSku.Sandbox);

        Assert.Single(settings.ExcludedEnvironments!);
    }

    [Fact]
    public void The_assistants_settings_survive_a_save_and_load_without_any_key()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new AppSettings
            {
                Ai = new AiSettings
                {
                    Provider = AiProviderKind.Ollama,
                    OllamaModel = "qwen3",
                    OllamaContextLength = 65536,
                    ExcludedEnvironments = ["prod.crm.dynamics.com"]
                }
            };
            saved.Save(path);

            var json = File.ReadAllText(path);
            Assert.Contains("\"Provider\": \"Ollama\"", json);
            Assert.DoesNotContain("Key", json);

            var loaded = AppSettings.Load(path).Ai!;
            Assert.Equal(AiProviderKind.Ollama, loaded.Provider);
            Assert.Equal(65536, loaded.EffectiveOllamaContextLength);
            Assert.False(loaded.IsEnvironmentAllowed("https://prod.crm.dynamics.com", EnvironmentSku.Sandbox));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Defaults_fill_in_what_was_left_blank()
    {
        var settings = new AiSettings { AnthropicModel = " ", OllamaContextLength = 0 };

        Assert.Equal("claude-opus-5-5", settings.EffectiveAnthropicModel);
        Assert.Equal("anthropic/claude-opus-5.5", settings.EffectiveOpenRouterModel);
        Assert.Equal("http://localhost:11434", settings.EffectiveOllamaUrl);
        Assert.Equal(OllamaChatClient.DefaultContextLength, settings.EffectiveOllamaContextLength);
        Assert.Null(settings.EffectiveClaudeCodeModel);
    }

    [Fact]
    public void A_missing_key_or_model_is_said_before_anything_is_sent()
    {
        var keys = new AiKeyStore(Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}.dat"));
        var service = new AiService(new AiSettings(), () => { }, keys);

        Assert.Contains("Claude API key", Assert.Throws<AiException>(() =>
            service.CreateClient(new AiSettings { Provider = AiProviderKind.Anthropic })).Message);
        Assert.Contains("Ollama model", Assert.Throws<AiException>(() =>
            service.CreateClient(new AiSettings { Provider = AiProviderKind.Ollama })).Message);
        Assert.Contains("Choose an AI provider", Assert.Throws<AiException>(() =>
            service.CreateClient(new AiSettings())).Message);

        // A key typed in the dialog but not saved yet is enough to try it.
        Assert.IsType<AnthropicChatClient>(service.CreateClient(new AiSettings { Provider = AiProviderKind.Anthropic }, "sk-typed"));
    }
}

public class AiKeyStoreTests
{
    [Fact]
    public void Keys_are_kept_encrypted_and_can_be_removed()
    {
        // DPAPI is Windows' own: elsewhere there is nothing to test.
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}.dat");
        try
        {
            var store = new AiKeyStore(path);
            store.Set(AiProviderKind.Anthropic, " sk-ant-secret ");

            Assert.Equal("sk-ant-secret", new AiKeyStore(path).Get(AiProviderKind.Anthropic));
            Assert.DoesNotContain("sk-ant-secret", File.ReadAllText(path));
            Assert.False(store.Has(AiProviderKind.OpenRouter));

            store.Set(AiProviderKind.Anthropic, null);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class AiAssistantTests
{
    private static (AiAssistantViewModel Assistant, ScriptedClient Client, AiService Service) Assistant(AiScreen screen, AiSettings? settings = null)
    {
        var client = new ScriptedClient().Then("An answer.");
        var service = new AiService(settings ?? new AiSettings { Provider = AiProviderKind.Anthropic }, () => { },
            new AiKeyStore(Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}.dat")))
        {
            ClientFactory = (_, _) => client
        };
        return (new AiAssistantViewModel(service, () => screen, () => new AiContext(screen.Title, screen.Environments)), client, service);
    }

    [Fact]
    public async Task A_question_and_its_answer_show_in_the_panel()
    {
        var (assistant, client, _) = Assistant(new AiScreen("Failures", "Flow X"));
        assistant.Input = "What failed?";

        await assistant.SendCommand.ExecuteAsync(null);

        Assert.Equal(["What failed?", "An answer."], assistant.Entries.Select(e => e.Text));
        Assert.False(assistant.Entries[1].IsStreaming);
        Assert.Equal(string.Empty, assistant.Input);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task A_screen_from_a_turned_off_environment_is_never_sent()
    {
        var settings = new AiSettings { Provider = AiProviderKind.Anthropic };
        var screen = new AiScreen("Compare", "rows")
        {
            Environments = [new("Dev", "https://dev.crm.dynamics.com", EnvironmentSku.Developer), new("Prod", "https://prod.crm.dynamics.com", EnvironmentSku.Production)]
        };
        var (assistant, client, _) = Assistant(screen, settings);
        assistant.Input = "Any differences?";

        await assistant.SendCommand.ExecuteAsync(null);

        Assert.Empty(client.Requests);
        var answer = assistant.Entries[1];
        Assert.True(answer.IsError);
        Assert.Contains("Ask AI is off for Prod", answer.Text);
    }

    [Fact]
    public async Task Turning_the_environment_back_on_lets_the_question_through()
    {
        var settings = new AiSettings { Provider = AiProviderKind.Anthropic, ExcludedEnvironments = ["test.crm.dynamics.com"] };
        var screen = new AiScreen("Test", "rows") { Environments = [new("Test", "https://test.crm.dynamics.com", EnvironmentSku.Sandbox)] };
        var (assistant, client, service) = Assistant(screen, settings);

        service.SetEnvironmentAllowed("https://test.crm.dynamics.com", true, EnvironmentSku.Sandbox);
        await assistant.AskCommand.ExecuteAsync("Summarise what this screen shows");

        Assert.Single(client.Requests);
    }

    [Fact]
    public void The_panel_says_before_anything_is_typed_that_an_environment_is_off()
    {
        var screen = new AiScreen("Users", "rows") { Environments = [new("Contoso Prod", "https://prod.crm.dynamics.com", EnvironmentSku.Production)] };
        var (assistant, _, _) = Assistant(screen);

        Assert.True(assistant.IsBlocked);
        Assert.False(assistant.CanAsk);
        Assert.Equal("Ask AI is off for Contoso Prod", assistant.BlockedHeading);
        Assert.False(assistant.AskCommand.CanExecute("Summarise this screen"));
    }

    [Fact]
    public void Allowing_from_the_panel_asks_first_then_lets_questions_through()
    {
        var screen = new AiScreen("Users", "rows") { Environments = [new("Contoso Prod", "https://prod.crm.dynamics.com", EnvironmentSku.Production)] };
        var (assistant, _, service) = Assistant(screen);
        var previous = AiAssistantViewModel.Ask;
        var asked = new List<AskRequest>();
        try
        {
            AiAssistantViewModel.Ask = r => { asked.Add(r); return false; };
            assistant.AllowEnvironmentCommand.Execute(null);
            Assert.True(assistant.IsBlocked);

            AiAssistantViewModel.Ask = r => { asked.Add(r); return true; };
            assistant.AllowEnvironmentCommand.Execute(null);

            Assert.False(assistant.IsBlocked);
            Assert.Equal(2, asked.Count);
            Assert.Equal(["prod.crm.dynamics.com"], service.Settings.AllowedEnvironments);
        }
        finally
        {
            AiAssistantViewModel.Ask = previous;
        }
    }

    [Fact]
    public async Task Tools_used_stay_beside_the_answer_and_retry_asks_again()
    {
        var (assistant, client, service) = Assistant(new AiScreen("Failures", "Flow X"));
        client.ThenTool("search_objects", new { query = "invoice" }).Then("Done.");
        service.Workspace = new EmptyWorkspace();
        client.Then("Again.");

        await assistant.AskCommand.ExecuteAsync("Why?");
        // The first scripted reply ("An answer.") is consumed first; ask once more for the tool round.
        await assistant.RetryCommand.ExecuteAsync(assistant.Entries[1]);

        var answer = assistant.Entries[3];
        Assert.Equal("Why?", answer.Question);
        Assert.Equal(["Searched objects for \"invoice\""], answer.Steps);
    }

    private sealed class EmptyWorkspace : IAiWorkspace
    {
        public string ListScreens() => "";
        public AiScreen? ReadScreen(string title) => null;
        public string SearchObjects(string query, string? environment) => "nothing";
    }

    [Fact]
    public void Suggestions_name_the_selected_item()
    {
        var client = new ScriptedClient();
        var service = new AiService(new AiSettings { Provider = AiProviderKind.Anthropic }, () => { },
            new AiKeyStore(Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}.dat")));
        var assistant = new AiAssistantViewModel(service, () => null, () => new AiContext("Failures", [], "Invoice approval"));

        Assert.Equal("Explain Invoice approval", assistant.Suggestions[2]);
    }

    [Fact]
    public void The_panel_says_where_questions_go()
    {
        var (assistant, _, service) = Assistant(new AiScreen("A", "B"), new AiSettings { Provider = AiProviderKind.Ollama, OllamaModel = "qwen3" });

        Assert.Equal("Ollama · qwen3", assistant.ProviderLabel);
        Assert.Contains("localhost:11434", assistant.PrivacyNote);

        service.Settings.Provider = AiProviderKind.OpenRouter;
        service.Save();
        Assert.Contains("OpenRouter", assistant.ProviderLabel);
    }
}
