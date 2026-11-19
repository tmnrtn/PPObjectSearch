using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PPObjectSearch.Ai;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Ai;

public class AiClientTests
{
    private static HttpResponseMessage Stream(string body, string type = "text/event-stream") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static AiRequest Request(params AiTool[] tools) => new()
    {
        System = "Instructions",
        Messages = [AiMessage.User("What failed?")],
        Tools = tools
    };

    private static AiTool Tool(string name = "read_screen") => new()
    {
        Name = name,
        Description = "Reads a screen.",
        Properties = new Dictionary<string, JsonElement> { ["title"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
        Required = ["title"],
        Run = (_, _) => Task.FromResult("content")
    };

    // ---------------------------------------------------------------- Claude API

    private const string AnthropicToolStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Let me "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"look."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_screen","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"title\": \"Failures\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":20}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    [Fact]
    public async Task Claude_API_streams_text_and_returns_tool_calls()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "v1/messages", _ => Stream(AnthropicToolStream));
        var client = new AnthropicChatClient("sk-test", "claude-opus-5-5", new HttpClient(handler));
        var pieces = new List<string>();

        var reply = await client.SendAsync(Request(Tool()), pieces.Add, CancellationToken.None);

        Assert.Equal(["Let me ", "look."], pieces);
        Assert.Equal(AiStop.ToolUse, reply.Stop);
        Assert.Equal("Let me look.", reply.Message.Text);
        var call = Assert.Single(reply.Message.ToolCalls);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("read_screen", call.Name);
        Assert.Equal("Failures", AiTool.Text(call.Arguments, "title"));
    }

    [Fact]
    public async Task Claude_API_asks_for_server_side_fallback_caching_and_tools()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "v1/messages", _ => Stream(AnthropicToolStream));
        var client = new AnthropicChatClient("sk-test", "claude-opus-5-5", new HttpClient(handler));

        await client.SendAsync(Request(Tool()), _ => { }, CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Bearer sk-test", sent.Authorization);
        Assert.Contains("server-side-fallback-2026-07-01", sent.Header("anthropic-beta"));

        using var body = JsonDocument.Parse(sent.Body!);
        var root = body.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("ephemeral", root.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("read_screen", root.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("Instructions", root.GetProperty("system")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Claude_API_sends_a_tool_turn_back_as_it_came_with_its_results()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "v1/messages", _ => Stream(AnthropicToolStream));
        var client = new AnthropicChatClient("sk-test", "claude-opus-5-5", new HttpClient(handler));
        var first = await client.SendAsync(Request(Tool()), _ => { }, CancellationToken.None);

        var followUp = new AiRequest
        {
            System = "Instructions",
            Messages =
            [
                AiMessage.User("What failed?"),
                first.Message,
                new AiMessage { Role = AiRole.User, ToolResults = [new AiToolResult("toolu_1", "Flow X failed")] }
            ],
            Tools = [Tool()]
        };
        await client.SendAsync(followUp, _ => { }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        var assistant = messages[1].GetProperty("content");
        Assert.Equal("tool_use", assistant[1].GetProperty("type").GetString());
        Assert.Equal("Failures", assistant[1].GetProperty("input").GetProperty("title").GetString());

        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task Claude_API_names_a_rejected_key()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Post, "v1/messages",
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized);
        var client = new AnthropicChatClient("sk-bad", "claude-opus-5-5", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AiException>(() => client.SendAsync(Request(), _ => { }, CancellationToken.None));
        Assert.Contains("API key", ex.Message);
    }

    // ---------------------------------------------------------------- OpenRouter

    [Fact]
    public async Task OpenRouter_assembles_tool_calls_from_their_pieces()
    {
        var stream = """
            : OPENROUTER PROCESSING

            data: {"choices":[{"delta":{"content":"Checking"}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"search_objects","arguments":"{\"que"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ry\": \"invoice\"}"}}]}}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var pieces = new List<string>();

        var reply = await OpenRouterChatClient.ReadStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(stream)), pieces.Add, CancellationToken.None);

        Assert.Equal(["Checking"], pieces);
        Assert.Equal(AiStop.ToolUse, reply.Stop);
        var call = Assert.Single(reply.Message.ToolCalls);
        Assert.Equal(("call_a", "search_objects"), (call.Id, call.Name));
        Assert.Equal("invoice", AiTool.Text(call.Arguments, "query"));
    }

    [Fact]
    public async Task OpenRouter_says_what_an_error_mid_stream_was()
    {
        var stream = """
            data: {"error":{"message":"Provider returned error","code":502}}

            """;

        var ex = await Assert.ThrowsAsync<AiException>(() =>
            OpenRouterChatClient.ReadStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(stream)), _ => { }, CancellationToken.None));
        Assert.Contains("Provider returned error", ex.Message);
    }

    [Fact]
    public async Task OpenRouter_sends_the_conversation_in_OpenAI_form()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "chat/completions",
            _ => Stream("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n"));
        var client = new OpenRouterChatClient("or-key", "anthropic/claude-opus-5.5", new HttpClient(handler));
        var call = new AiToolCall("call_a", "read_screen", JsonSerializer.SerializeToElement(new { title = "Failures" }));

        var reply = await client.SendAsync(new AiRequest
        {
            System = "Instructions",
            Messages =
            [
                AiMessage.User("What failed?"),
                new AiMessage { Role = AiRole.Assistant, ToolCalls = [call] },
                new AiMessage { Role = AiRole.User, ToolResults = [new AiToolResult("call_a", "Flow X")] }
            ],
            Tools = [Tool()]
        }, _ => { }, CancellationToken.None);

        Assert.Equal("Hi", reply.Message.Text);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Bearer or-key", sent.Authorization);

        using var body = JsonDocument.Parse(sent.Body!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(["system", "user", "assistant", "tool"], messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("call_a", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("""{"title":"Failures"}""", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("read_screen", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task OpenRouter_names_a_rejected_key()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Post, "chat/completions",
            """{"error":{"message":"No auth credentials found","code":401}}""", HttpStatusCode.Unauthorized);
        var client = new OpenRouterChatClient("bad", "m", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AiException>(() => client.SendAsync(Request(), _ => { }, CancellationToken.None));
        Assert.Contains("API key", ex.Message);
    }

    // ---------------------------------------------------------------- Ollama

    [Fact]
    public async Task Ollama_reads_its_line_a_chunk_stream_with_tool_calls()
    {
        var stream = """
            {"message":{"role":"assistant","content":"Look"},"done":false}
            {"message":{"role":"assistant","content":"ing","tool_calls":[{"function":{"name":"read_screen","arguments":{"title":"Failures"}}}]},"done":false}
            {"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}
            """;
        var pieces = new List<string>();

        var reply = await OllamaChatClient.ReadStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(stream)), pieces.Add, CancellationToken.None);

        Assert.Equal(["Look", "ing"], pieces);
        Assert.Equal(AiStop.ToolUse, reply.Stop);
        Assert.Equal("Failures", AiTool.Text(Assert.Single(reply.Message.ToolCalls).Arguments, "title"));
    }

    [Fact]
    public async Task Ollama_is_asked_for_a_context_long_enough_for_a_screen()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "api/chat",
            _ => Stream("""{"message":{"content":"Hi"},"done":true}""" + "\n", "application/x-ndjson"));
        var client = new OllamaChatClient("localhost:11434", "qwen3", 40000, new HttpClient(handler));

        await client.SendAsync(Request(Tool()), _ => { }, CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/api/chat", sent.Url);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal(40000, body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("qwen3", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Ollama_asks_again_without_tools_when_the_model_has_none()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "api/chat", r => r.Body!.Contains("\"tools\"")
            ? FakeHttpHandler.Json("""{"error":"registry.ollama.ai/library/gemma:2b does not support tools"}""", HttpStatusCode.BadRequest)
            : Stream("""{"message":{"content":"Answer"},"done":true}""" + "\n"));
        var client = new OllamaChatClient(OllamaChatClient.DefaultUrl, "gemma:2b", 32768, new HttpClient(handler));

        var reply = await client.SendAsync(Request(Tool()), _ => { }, CancellationToken.None);

        Assert.Equal("Answer", reply.Message.Text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(client.SupportsTools);
    }

    [Fact]
    public async Task Ollama_says_how_to_get_a_missing_model()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Post, "api/chat", """{"error":"model \"llama9\" not found, try pulling it first"}""", HttpStatusCode.NotFound);
        var client = new OllamaChatClient(OllamaChatClient.DefaultUrl, "llama9", 32768, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AiException>(() => client.SendAsync(Request(), _ => { }, CancellationToken.None));
        Assert.Contains("ollama pull llama9", ex.Message);
    }

    [Fact]
    public async Task Ollama_lists_the_servers_models()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "api/tags", """{"models":[{"name":"qwen3:8b"},{"name":"llama3.1:latest"}]}""");

        var models = await OllamaChatClient.ListModelsAsync(new HttpClient(handler), "http://gpu-box:11434", CancellationToken.None);

        Assert.Equal(["llama3.1:latest", "qwen3:8b"], models);
        Assert.Equal("http://gpu-box:11434/api/tags", Assert.Single(handler.Requests).Url);
    }

    // ---------------------------------------------------------------- Claude Code

    [Fact]
    public async Task Claude_Code_streams_text_deltas_and_ends_at_the_result()
    {
        var output = """
            {"type":"system","subtype":"init"}
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Two flows "}}}
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"failed."}}}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Two flows failed."}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"Two flows failed."}
            """;
        var pieces = new List<string>();

        var reply = await ClaudeCodeChatClient.ReadStreamAsync(new StringReader(output), pieces.Add, CancellationToken.None);

        Assert.Equal(["Two flows ", "failed."], pieces);
        Assert.Equal("Two flows failed.", reply!.Message.Text);
    }

    [Fact]
    public async Task Claude_Code_without_partial_messages_still_answers_from_the_result()
    {
        var pieces = new List<string>();

        var reply = await ClaudeCodeChatClient.ReadStreamAsync(
            new StringReader("""{"type":"result","subtype":"success","is_error":false,"result":"All fine."}"""), pieces.Add, CancellationToken.None);

        Assert.Equal(["All fine."], pieces);
        Assert.Equal("All fine.", reply!.Message.Text);
    }

    [Fact]
    public async Task Claude_Code_passes_on_its_own_error()
    {
        var ex = await Assert.ThrowsAsync<AiException>(() => ClaudeCodeChatClient.ReadStreamAsync(
            new StringReader("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}"""),
            _ => { }, CancellationToken.None));

        Assert.Contains("Please run /login", ex.Message);
    }

    [Fact]
    public void Claude_Code_runs_with_no_tools_no_MCP_servers_and_no_saved_session()
    {
        var args = ClaudeCodeChatClient.Arguments("system.md", "sonnet");

        Assert.Equal("-p", args[0]);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("system.md", args[args.IndexOf("--system-prompt-file") + 1]);
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);
        Assert.DoesNotContain("--model", ClaudeCodeChatClient.Arguments("system.md", null));
    }

    [Fact]
    public void Claude_Code_is_sent_the_conversation_with_the_new_question_last()
    {
        Assert.Equal("Only question", ClaudeCodeChatClient.Transcript([AiMessage.User("Only question")]));

        var transcript = ClaudeCodeChatClient.Transcript(
        [
            AiMessage.User("What failed?"),
            new AiMessage { Role = AiRole.Assistant, Text = "Flow X." },
            AiMessage.User("Why?")
        ]);

        Assert.Contains("User: What failed?", transcript);
        Assert.Contains("Assistant: Flow X.", transcript);
        Assert.EndsWith("The user now asks:\n\nWhy?", transcript);
    }
}
