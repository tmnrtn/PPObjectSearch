using System.Net.Http;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta.Messages;

namespace PPObjectSearch.Ai;

/// <summary>
/// The Claude API, through Anthropic's SDK. The beta endpoint is used for server-side fallback:
/// if the model declines a request for policy reasons, the API retries it on its default
/// fallback model within the same call, rather than the question simply going unanswered.
/// </summary>
public sealed class AnthropicChatClient : IAiChatClient
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const long MaxOutputTokens = 64000;

    private readonly AnthropicClient _client;
    private readonly string _model;

    public AnthropicChatClient(string apiKey, string model, HttpClient? http = null)
    {
        _model = model;
        _client = http is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, HttpClient = http };
    }

    public string Label => $"Claude API · {_model}";

    public bool SupportsTools => true;

    public async Task<AiReply> SendAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = MaxOutputTokens,
            System = new List<BetaTextBlockParam> { new() { Text = request.System } },
            Messages = request.Messages.Select(ToParam).ToList(),
            Tools = request.Tools.Count == 0 ? null : request.Tools.Select(ToTool).ToList(),

            // The screen and the conversation so far are re-sent with each question; caching
            // them makes a follow-up cheaper and quicker.
            CacheControl = new BetaCacheControlEphemeral(),
            Fallbacks = new BetaFallbacksParam(new Default()),
            Betas = [FallbackBeta]
        };

        BetaMessage message;
        try
        {
            var aggregator = new BetaMessageContentAggregator();
            await foreach (var streamEvent in _client.Beta.Messages.CreateStreaming(parameters, cancellationToken).CollectAsync(aggregator))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    onText(text.Text);
                }
            }

            message = aggregator.Message();
        }
        catch (AnthropicApiException ex)
        {
            throw new AiException(Describe(ex), ex);
        }

        var calls = new List<AiToolCall>();
        var textParts = new List<string>();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var text)) textParts.Add(text.Text);
            else if (block.TryPickToolUse(out var toolUse))
            {
                calls.Add(new AiToolCall(toolUse.ID, toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input)));
            }
        }

        var stop = message.StopReason?.Raw() switch
        {
            "tool_use" => AiStop.ToolUse,
            "max_tokens" => AiStop.Truncated,
            "refusal" => AiStop.Refused,
            _ => AiStop.Finished
        };

        return new AiReply
        {
            Stop = stop,
            Message = new AiMessage
            {
                Role = AiRole.Assistant,
                Text = string.Concat(textParts),
                ToolCalls = calls,

                // Thinking blocks go back exactly as they came, signatures and all.
                ProviderState = message.Content.Select(ToParam).OfType<BetaContentBlockParam>().ToList()
            }
        };
    }

    private static BetaMessageParam ToParam(AiMessage message)
    {
        if (message.Role == AiRole.Assistant)
        {
            var content = message.ProviderState as List<BetaContentBlockParam>
                ?? [new BetaTextBlockParam { Text = message.Text }];
            return new BetaAssistantMessageParam { Content = content };
        }

        if (message.ToolResults.Count > 0)
        {
            List<BetaContentBlockParam> results = message.ToolResults
                .Select(r => (BetaContentBlockParam)new BetaToolResultBlockParam
                {
                    ToolUseID = r.CallId,
                    Content = r.Content,
                    IsError = r.IsError
                })
                .ToList();
            return new BetaUserMessageParam { Content = results };
        }

        return new BetaUserMessageParam { Content = message.Text };
    }

    /// <summary>A response block as the request block that sends it back; null for kinds never echoed.</summary>
    private static BetaContentBlockParam? ToParam(BetaContentBlock block)
    {
        // The API refuses an empty text block, so one is not sent back.
        if (block.TryPickText(out var text))
        {
            if (text.Text.Length == 0) return null;
            return new BetaTextBlockParam { Text = text.Text };
        }
        if (block.TryPickThinking(out var thinking))
            return new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature };
        if (block.TryPickRedactedThinking(out var redacted)) return new BetaRedactedThinkingBlockParam { Data = redacted.Data };
        if (block.TryPickToolUse(out var toolUse))
            return new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input };
        return null;
    }

    private static BetaToolUnion ToTool(AiTool tool) => new BetaTool
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = new()
        {
            Properties = tool.Properties.ToDictionary(p => p.Key, p => p.Value),
            Required = tool.Required.ToList()
        }
    };

    private static string Describe(AnthropicApiException ex) => ex switch
    {
        AnthropicUnauthorizedException => "The Claude API turned the API key down. Check it in Ask AI settings.",
        AnthropicRateLimitException => "The Claude API is rate limiting this key. Wait a moment and ask again.",
        AnthropicNotFoundException => "The Claude API doesn't know that model. Check the model in Ask AI settings.",
        _ => $"The Claude API returned an error: {ex.Message}"
    };
}
