using System.Text.Json;

namespace PPObjectSearch.Ai;

/// <summary>A tool the model asked for, with its arguments as the model wrote them.</summary>
public sealed record AiToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>What a tool gave back, for the call with the same id.</summary>
public sealed record AiToolResult(string CallId, string Content, bool IsError = false);

public enum AiRole
{
    User,
    Assistant
}

/// <summary>
/// One turn of a conversation, in no provider's format. An assistant turn may carry tool calls;
/// the user turn after it carries their results. <see cref="ProviderState"/> keeps what a
/// provider must be sent back unchanged - Claude's thinking blocks, for one.
/// </summary>
public sealed class AiMessage
{
    public required AiRole Role { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = [];
    public IReadOnlyList<AiToolResult> ToolResults { get; init; } = [];
    public object? ProviderState { get; init; }

    public static AiMessage User(string text) => new() { Role = AiRole.User, Text = text };
}

/// <summary>A tool the assistant may use: a JSON schema for its arguments, and what it does.</summary>
public sealed class AiTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>The JSON schema's properties, by name.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<string> Required { get; init; } = [];

    public required Func<JsonElement, CancellationToken, Task<string>> Run { get; init; }

    /// <summary>The whole parameter schema, as OpenAI-style function definitions want it.</summary>
    public JsonElement Schema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = Properties,
        required = Required
    });

    /// <summary>A string argument's value, or null when it is missing or not a string.</summary>
    public static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>One request: instructions, the conversation so far, and the tools on offer.</summary>
public sealed class AiRequest
{
    public required string System { get; init; }
    public required IReadOnlyList<AiMessage> Messages { get; init; }
    public IReadOnlyList<AiTool> Tools { get; init; } = [];
}

/// <summary>Why the model stopped.</summary>
public enum AiStop
{
    Finished,
    ToolUse,

    /// <summary>Cut off at the output limit.</summary>
    Truncated,

    /// <summary>Declined, for policy reasons.</summary>
    Refused
}

/// <summary>The model's reply to one request.</summary>
public sealed class AiReply
{
    public required AiMessage Message { get; init; }
    public AiStop Stop { get; init; } = AiStop.Finished;
}

/// <summary>A provider: sends a request and streams the reply's text as it comes.</summary>
public interface IAiChatClient
{
    /// <summary>Shown beside the conversation - the provider and model.</summary>
    string Label { get; }

    /// <summary>Whether the model can be given tools, to look beyond the screen it was asked about.</summary>
    bool SupportsTools { get; }

    /// <param name="onText">Each piece of the reply's text, as it arrives.</param>
    Task<AiReply> SendAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken);
}

/// <summary>A failure to show as it is: the provider's own explanation, already readable.</summary>
public sealed class AiException : Exception
{
    public AiException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
