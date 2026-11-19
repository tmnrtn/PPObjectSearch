using System.Text.Json.Serialization;

namespace PPObjectSearch.Ai;

/// <summary>Where questions asked of the AI assistant go.</summary>
public enum AiProviderKind
{
    /// <summary>No assistant: the panel offers to set one up.</summary>
    None,

    /// <summary>
    /// The Claude Code command line, signed in with the user's own Claude subscription. The app
    /// runs the unmodified <c>claude</c> program and never sees the subscription's credentials.
    /// </summary>
    ClaudeCode,

    /// <summary>The Claude API, with an Anthropic API key.</summary>
    Anthropic,

    /// <summary>OpenRouter, with an OpenRouter API key and any model it offers.</summary>
    OpenRouter,

    /// <summary>An Ollama server - usually this machine's own - and one of its models. Nothing leaves the network.</summary>
    Ollama
}

/// <summary>
/// The AI assistant's settings in settings.json. API keys are not here: they are kept apart,
/// encrypted for the Windows account, by <see cref="AiKeyStore"/>.
/// </summary>
public sealed class AiSettings
{
    public const string DefaultAnthropicModel = "claude-opus-5-5";
    public const string DefaultOpenRouterModel = "anthropic/claude-opus-5.5";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AiProviderKind Provider { get; set; } = AiProviderKind.None;

    /// <summary>Ask AI's master switch. Off keeps the provider chosen, for when it is turned back on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Claude API model; the default when empty.</summary>
    public string? AnthropicModel { get; set; }

    /// <summary>The OpenRouter model id, such as anthropic/claude-opus-5.5; the default when empty.</summary>
    public string? OpenRouterModel { get; set; }

    /// <summary>The model Claude Code is asked for - an alias such as opus or sonnet; Claude Code's own choice when empty.</summary>
    public string? ClaudeCodeModel { get; set; }

    /// <summary>The claude program to run, when it is not on the PATH or in its usual place.</summary>
    public string? ClaudeCodePath { get; set; }

    /// <summary>The Ollama server; http://localhost:11434 when empty.</summary>
    public string? OllamaUrl { get; set; }

    /// <summary>The Ollama model, such as llama3.1 or qwen3; there is no default.</summary>
    public string? OllamaModel { get; set; }

    /// <summary>
    /// The context length Ollama is asked for, in tokens. Ollama's own default is too short for most
    /// screens; a longer one needs more memory.
    /// </summary>
    public int? OllamaContextLength { get; set; }

    /// <summary>Whether the main window's assistant panel is showing.</summary>
    public bool IsPanelOpen { get; set; }

    /// <summary>
    /// Hosts the assistant may not read, turned off by hand: a screen showing any of them is not
    /// sent, and the assistant's tools pass over them.
    /// </summary>
    public List<string>? ExcludedEnvironments { get; set; }

    /// <summary>
    /// Hosts of environments that start with AI off - production, and any whose type could not be
    /// read - that someone has allowed by hand.
    /// </summary>
    public List<string>? AllowedEnvironments { get; set; }

    /// <summary>
    /// Production environments start with AI off, and so does one whose type could not be read:
    /// as the write guard does, an unknown type is not taken to be safe.
    /// </summary>
    public static bool StartsOff(Dataverse.EnvironmentSku sku) =>
        sku is Dataverse.EnvironmentSku.Production or Dataverse.EnvironmentSku.Unknown;

    /// <summary>Whether the assistant may read an environment: a choice made by hand, else its type's default.</summary>
    public bool IsEnvironmentAllowed(string? environmentUrl, Dataverse.EnvironmentSku sku)
    {
        if (Host(environmentUrl) is not { } host) return true;
        if (Contains(ExcludedEnvironments, host)) return false;
        if (Contains(AllowedEnvironments, host)) return true;
        return !StartsOff(sku);
    }

    /// <summary>Allows or turns off an environment, recording only a choice that differs from its default. The caller saves.</summary>
    public void SetEnvironmentAllowed(string environmentUrl, bool allowed, Dataverse.EnvironmentSku sku)
    {
        if (Host(environmentUrl) is not { } host) return;

        ExcludedEnvironments = Without(ExcludedEnvironments, host);
        AllowedEnvironments = Without(AllowedEnvironments, host);

        if (allowed && StartsOff(sku)) (AllowedEnvironments ??= []).Add(host);
        else if (!allowed && !StartsOff(sku)) (ExcludedEnvironments ??= []).Add(host);
    }

    /// <summary>
    /// Records a choice whatever the environment's type - for one not open, whose type is not known
    /// here, so that the choice holds whatever its type turns out to be. The caller saves.
    /// </summary>
    public void SetEnvironmentChoice(string environmentUrl, bool allowed)
    {
        if (Host(environmentUrl) is not { } host) return;

        ExcludedEnvironments = Without(ExcludedEnvironments, host);
        AllowedEnvironments = Without(AllowedEnvironments, host);
        if (allowed) (AllowedEnvironments ??= []).Add(host);
        else (ExcludedEnvironments ??= []).Add(host);
    }

    private static bool Contains(List<string>? hosts, string host) =>
        hosts?.Contains(host, StringComparer.OrdinalIgnoreCase) == true;

    private static List<string>? Without(List<string>? hosts, string host)
    {
        hosts?.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        return hosts is { Count: 0 } ? null : hosts;
    }

    internal static string? Host(string? environmentUrl)
    {
        var value = (environmentUrl ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;
    }

    [JsonIgnore]
    public string EffectiveAnthropicModel => Blank(AnthropicModel) ?? DefaultAnthropicModel;

    [JsonIgnore]
    public string EffectiveOpenRouterModel => Blank(OpenRouterModel) ?? DefaultOpenRouterModel;

    [JsonIgnore]
    public string? EffectiveClaudeCodeModel => Blank(ClaudeCodeModel);

    [JsonIgnore]
    public string EffectiveOllamaUrl => Blank(OllamaUrl) ?? OllamaChatClient.DefaultUrl;

    [JsonIgnore]
    public int EffectiveOllamaContextLength => OllamaContextLength is > 0 ? OllamaContextLength.Value : OllamaChatClient.DefaultContextLength;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
