using System.Text;

namespace PPObjectSearch.Ai;

/// <summary>
/// One screen, as the assistant is shown it: its title, its content as text, and the environments
/// its content comes from.
/// </summary>
public sealed record AiScreen(string Title, string Content)
{
    public IReadOnlyList<AiEnvironment> Environments { get; init; } = [];
}

/// <summary>
/// What a screen is, without reading all of it: its title, the environments it shows, and the item
/// selected on it, if any - for the panel to say what a question would be about.
/// </summary>
public sealed record AiContext(string Title, IReadOnlyList<AiEnvironment> Environments, string? Selected = null);

/// <summary>A view model that knows which item on its screen is selected, by name.</summary>
public interface IAiSelection
{
    string? SelectedNameForAi { get; }
}

/// <summary>An environment a screen shows, by the name the app gives it, its URL and its type.</summary>
public sealed record AiEnvironment(string Title, string Url, Dataverse.EnvironmentSku Sku = Dataverse.EnvironmentSku.Unknown);

/// <summary>
/// A conversation about one screen. Each question is sent with the screen as it is at that moment,
/// in the instructions rather than the conversation, so a follow-up sees what changed and a long
/// conversation does not carry a copy of the screen for every turn. When the model asks for tools,
/// they run and the model is asked again, up to a limit.
/// </summary>
public sealed class AiConversation
{
    internal const int MaxToolRounds = 8;

    private readonly List<AiMessage> _messages = new();

    public IReadOnlyList<AiMessage> Messages => _messages;

    public void Clear() => _messages.Clear();

    /// <param name="onText">The answer's text as it streams, across tool rounds.</param>
    /// <param name="onTool">Each tool call as it runs, to show while the answer waits.</param>
    public async Task<string> AskAsync(
        IAiChatClient client,
        string question,
        AiScreen screen,
        IReadOnlyList<AiTool> tools,
        Action<string> onText,
        Action<AiToolCall>? onTool,
        CancellationToken cancellationToken)
    {
        var offered = client.SupportsTools ? tools : [];
        var request = new AiRequest
        {
            System = AiPrompts.System(screen, offered.Count > 0),
            Messages = _messages,
            Tools = offered
        };

        var start = _messages.Count;
        _messages.Add(AiMessage.User(question));

        var answer = new StringBuilder();
        try
        {
            for (var round = 0; ; round++)
            {
                var reply = await client.SendAsync(request, piece =>
                {
                    answer.Append(piece);
                    onText(piece);
                }, cancellationToken);

                _messages.Add(reply.Message);

                if (reply.Stop == AiStop.Refused)
                {
                    const string refused = "\n\n(The model declined to answer this.)";
                    answer.Append(refused);
                    onText(refused);
                }
                else if (reply.Stop == AiStop.Truncated)
                {
                    const string cut = "\n\n(The answer was cut off at the model's output limit.)";
                    answer.Append(cut);
                    onText(cut);
                }

                if (reply.Message.ToolCalls.Count == 0) break;

                if (round >= MaxToolRounds)
                {
                    // Every tool call must have its result; say why there is none instead.
                    _messages.Add(Results(reply.Message.ToolCalls.Select(c =>
                        new AiToolResult(c.Id, "Not run: the limit on tool use for one question was reached.", true))));
                    const string limit = "\n\n(Stopped: the question needed more lookups than allowed in one go.)";
                    answer.Append(limit);
                    onText(limit);
                    break;
                }

                // Text before a tool call and the answer after it read as separate paragraphs.
                if (answer.Length > 0 && !answer.ToString().EndsWith("\n\n", StringComparison.Ordinal))
                {
                    answer.Append("\n\n");
                    onText("\n\n");
                }

                var results = new List<AiToolResult>();
                foreach (var call in reply.Message.ToolCalls)
                {
                    results.Add(await RunToolAsync(call, offered, onTool, cancellationToken));
                }

                _messages.Add(Results(results));
            }
        }
        catch
        {
            // A question that failed or was stopped leaves no half-turn behind: the conversation
            // must stay a valid one to send again.
            _messages.RemoveRange(start, _messages.Count - start);
            throw;
        }

        return answer.ToString();
    }

    private static AiMessage Results(IEnumerable<AiToolResult> results) =>
        new() { Role = AiRole.User, ToolResults = results.ToList() };

    private static async Task<AiToolResult> RunToolAsync(
        AiToolCall call, IReadOnlyList<AiTool> tools, Action<AiToolCall>? onTool, CancellationToken cancellationToken)
    {
        var tool = tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null) return new AiToolResult(call.Id, $"There is no tool called {call.Name}.", true);

        onTool?.Invoke(call);

        try
        {
            return new AiToolResult(call.Id, await tool.Run(call.Arguments, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"AI tool {call.Name} failed", ex);
            return new AiToolResult(call.Id, $"The tool failed: {ex.Message}", true);
        }
    }
}
