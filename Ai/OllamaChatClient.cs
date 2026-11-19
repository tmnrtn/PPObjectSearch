using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PPObjectSearch.Ai;

/// <summary>
/// A local Ollama server, through its own chat API rather than its OpenAI-compatible one: only
/// the native API takes a context length, and Ollama's default is too short for most screens -
/// it would drop the start of the screen without a word. The reply streams as one JSON object
/// per line. A model that cannot use tools is asked again without them, and not offered them again.
/// </summary>
public sealed class OllamaChatClient : IAiChatClient
{
    public const string DefaultUrl = "http://localhost:11434";
    public const int DefaultContextLength = 32768;

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly string _model;
    private readonly int _contextLength;
    private bool _toolsRefused;

    public OllamaChatClient(string baseUrl, string model, int contextLength, HttpClient http)
    {
        _baseUrl = BaseUri(baseUrl);
        _model = model;
        _contextLength = contextLength;
        _http = http;
    }

    public string Label => $"Ollama · {_model}";

    public bool SupportsTools => !_toolsRefused;

    internal static Uri BaseUri(string? url)
    {
        var value = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim().TrimEnd('/');
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        return Uri.TryCreate(value + "/", UriKind.Absolute, out var uri)
            ? uri
            : throw new AiException($"\"{url}\" isn't a URL for an Ollama server.");
    }

    public async Task<AiReply> SendAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken)
    {
        var tools = _toolsRefused ? [] : request.Tools;

        try
        {
            return await SendAsync(request, tools, onText, cancellationToken);
        }
        catch (ToolsNotSupported) when (tools.Count > 0)
        {
            _toolsRefused = true;
            return await SendAsync(request, [], onText, cancellationToken);
        }
    }

    private async Task<AiReply> SendAsync(AiRequest request, IReadOnlyList<AiTool> tools, Action<string> onText, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = true,
            ["messages"] = Messages(request),
            ["options"] = new JsonObject { ["num_ctx"] = _contextLength }
        };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.Schema.GetRawText())
                }
            }).ToArray());
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, "api/chat"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new AiException($"Ollama isn't answering at {_baseUrl}. Is it running?", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorMessage(await response.Content.ReadAsStringAsync(cancellationToken));
                if (error?.Contains("does not support tools", StringComparison.OrdinalIgnoreCase) == true) throw new ToolsNotSupported();
                throw new AiException((int)response.StatusCode == 404 && error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true
                    ? $"Ollama doesn't have the model {_model}. Pull it with: ollama pull {_model}"
                    : $"Ollama returned an error ({(int)response.StatusCode}){(error is null ? "." : $": {error}")}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ReadStreamAsync(stream, onText, cancellationToken);
        }
    }

    /// <summary>Reads Ollama's stream: a JSON object a line, the last one marked done.</summary>
    internal static async Task<AiReply> ReadStreamAsync(Stream stream, Action<string> onText, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        string? doneReason = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var chunk = JsonDocument.Parse(line);
            var root = chunk.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                throw new AiException($"Ollama returned an error: {error}");
            }

            if (root.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } piece)
                {
                    text.Append(piece);
                    onText(piece);
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var function)) continue;
                        var name = function.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;

                        var arguments = function.TryGetProperty("arguments", out var a)
                            ? a.ValueKind == JsonValueKind.String ? ParseArguments(a.GetString()) : a.Clone()
                            : JsonSerializer.SerializeToElement(new { });
                        var id = call.TryGetProperty("id", out var i) && i.GetString() is { Length: > 0 } idText ? idText : $"call_{calls.Count}";
                        calls.Add(new AiToolCall(id, name, arguments));
                    }
                }
            }

            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                doneReason = root.TryGetProperty("done_reason", out var reason) ? reason.GetString() : null;
                break;
            }
        }

        return new AiReply
        {
            Stop = calls.Count > 0 ? AiStop.ToolUse : doneReason == "length" ? AiStop.Truncated : AiStop.Finished,
            Message = new AiMessage { Role = AiRole.Assistant, Text = text.ToString(), ToolCalls = calls }
        };
    }

    /// <summary>The models the server has, for the settings' model list.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, string? baseUrl, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(BaseUri(baseUrl), "api/tags"), cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString())
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JsonArray Messages(AiRequest request)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };
        var names = new Dictionary<string, string>();

        foreach (var message in request.Messages)
        {
            if (message.Role == AiRole.Assistant)
            {
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = message.Text };
                if (message.ToolCalls.Count > 0)
                {
                    foreach (var call in message.ToolCalls) names[call.Id] = call.Name;
                    assistant["tool_calls"] = new JsonArray(message.ToolCalls.Select(c => (JsonNode)new JsonObject
                    {
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = JsonNode.Parse(c.Arguments.GetRawText()) }
                    }).ToArray());
                }
                messages.Add(assistant);
            }
            else if (message.ToolResults.Count > 0)
            {
                foreach (var result in message.ToolResults)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["content"] = result.Content,
                        ["tool_name"] = names.GetValueOrDefault(result.CallId, string.Empty)
                    });
                }
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = message.Text });
            }
        }

        return messages;
    }

    private static JsonElement ParseArguments(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? JsonSerializer.SerializeToElement(new { }) : JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.ToString() : null;
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        }
    }

    private sealed class ToolsNotSupported : Exception;
}
