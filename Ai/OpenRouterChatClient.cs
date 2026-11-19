using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PPObjectSearch.Ai;

/// <summary>
/// OpenRouter's chat completions API, which is OpenAI's format: the reply streams as server-sent
/// events, and tool calls arrive in pieces, keyed by their index, to be put back together.
/// </summary>
public sealed class OpenRouterChatClient : IAiChatClient
{
    internal const string BaseUrl = "https://openrouter.ai/api/v1/";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public OpenRouterChatClient(string apiKey, string model, HttpClient http)
    {
        _apiKey = apiKey;
        _model = model;
        _http = http;
    }

    public string Label => $"OpenRouter · {_model}";

    public bool SupportsTools => true;

    public async Task<AiReply> SendAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = true,
            ["messages"] = Messages(request)
        };
        if (request.Tools.Count > 0) body["tools"] = Tools(request.Tools);

        using var message = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        // OpenRouter's attribution headers: which app the requests come from.
        message.Headers.Add("HTTP-Referer", "https://github.com/tmnrtn/PPObjectSearch");
        message.Headers.Add("X-Title", "Power Platform Object Search");

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AiException(Describe((int)response.StatusCode, ErrorMessage(error)));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadStreamAsync(stream, onText, cancellationToken);
    }

    /// <summary>Reads the event stream: text as it comes, tool calls assembled from their pieces.</summary>
    internal static async Task<AiReply> ReadStreamAsync(Stream stream, Action<string> onText, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        string? finish = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            // Blank lines end events; lines starting ':' are keep-alive comments.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            using var chunk = JsonDocument.Parse(data);
            var root = chunk.RootElement;

            // An error after the stream has started comes as a chunk of its own.
            if (root.TryGetProperty("error", out var error))
            {
                throw new AiException($"OpenRouter returned an error: {ErrorText(error)}");
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                finish = reason.GetString();
            }

            if (!choice.TryGetProperty("delta", out var delta)) continue;

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
                content.GetString() is { Length: > 0 } piece)
            {
                text.Append(piece);
                onText(piece);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var i) ? i.GetInt32() : calls.Count;
                    if (!calls.TryGetValue(index, out var entry)) entry = (string.Empty, string.Empty, new StringBuilder());

                    if (call.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idText) entry.Id = idText;
                    if (call.TryGetProperty("function", out var function))
                    {
                        if (function.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } nameText) entry.Name = nameText;
                        if (function.TryGetProperty("arguments", out var args) && args.GetString() is { } argsText) entry.Arguments.Append(argsText);
                    }

                    calls[index] = entry;
                }
            }
        }

        var toolCallList = calls.Values
            .Where(c => c.Name.Length > 0)
            .Select((c, n) => new AiToolCall(c.Id.Length > 0 ? c.Id : $"call_{n}", c.Name, ParseArguments(c.Arguments.ToString())))
            .ToList();

        return new AiReply
        {
            Stop = finish switch
            {
                "tool_calls" => AiStop.ToolUse,
                "length" => AiStop.Truncated,
                "content_filter" => AiStop.Refused,
                _ when toolCallList.Count > 0 => AiStop.ToolUse,
                _ => AiStop.Finished
            },
            Message = new AiMessage { Role = AiRole.Assistant, Text = text.ToString(), ToolCalls = toolCallList }
        };
    }

    /// <summary>The model ids OpenRouter offers, for the settings' model list.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(BaseUrl + "models", cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString())
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JsonArray Messages(AiRequest request)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };

        foreach (var message in request.Messages)
        {
            if (message.Role == AiRole.Assistant)
            {
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = message.Text };
                if (message.ToolCalls.Count > 0)
                {
                    assistant["tool_calls"] = new JsonArray(message.ToolCalls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.GetRawText() }
                    }).ToArray());
                }
                messages.Add(assistant);
            }
            else if (message.ToolResults.Count > 0)
            {
                foreach (var result in message.ToolResults)
                {
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.CallId, ["content"] = result.Content });
                }
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = message.Text });
            }
        }

        return messages;
    }

    private static JsonArray Tools(IReadOnlyList<AiTool> tools) =>
        new(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(t.Schema.GetRawText())
            }
        }).ToArray());

    /// <summary>A tool call's arguments; a model that wrote no arguments, or bad JSON, gets an empty object.</summary>
    private static JsonElement ParseArguments(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? JsonSerializer.SerializeToElement(new { })
                : JsonDocument.Parse(json).RootElement.Clone();
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
            return document.RootElement.TryGetProperty("error", out var error) ? ErrorText(error) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ErrorText(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
            ? message.GetString() ?? error.GetRawText()
            : error.ToString();

    private static string Describe(int status, string? message) => status switch
    {
        401 => "OpenRouter turned the API key down. Check it in Ask AI settings.",
        402 => "OpenRouter says the account is out of credit.",
        429 => "OpenRouter is rate limiting this key. Wait a moment and ask again.",
        _ => $"OpenRouter returned an error ({status}){(message is null ? "." : $": {message}")}"
    };
}
