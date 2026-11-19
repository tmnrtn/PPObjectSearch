using System.Net.Http;

namespace PPObjectSearch.Ai;

/// <summary>
/// The app's AI assistant: which provider it uses, with which key, and what it can reach. One for
/// the app, made by the shell from its settings.
/// </summary>
public sealed class AiService
{
    private readonly Action _save;

    /// <summary>Streams can run for minutes; the timeout only stops a request that has hung outright.</summary>
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    public AiService(AiSettings settings, Action save, AiKeyStore keys, HttpClient? http = null)
    {
        Settings = settings;
        _save = save;
        Keys = keys;
        Http = http ?? SharedHttp.Value;
    }

    /// <summary>The app's assistant, once the shell has made it.</summary>
    public static AiService? Current { get; set; }

    public AiSettings Settings { get; }

    public AiKeyStore Keys { get; }

    public HttpClient Http { get; }

    /// <summary>The rest of the app, for the assistant's tools.</summary>
    public IAiWorkspace? Workspace { get; set; }

    /// <summary>Raised when the settings change, so open conversations show the new provider.</summary>
    public event EventHandler? Changed;

    /// <summary>Replaces the providers' clients in tests.</summary>
    internal Func<AiSettings, AiKeyStore, IAiChatClient?>? ClientFactory { get; set; }

    public bool IsConfigured => Settings.Enabled && Settings.Provider != AiProviderKind.None;

    /// <summary>The environments of a screen that the assistant may not read; empty when it may read them all.</summary>
    public IReadOnlyList<AiEnvironment> Excluded(AiScreen screen) =>
        screen.Environments.Where(e => !Settings.IsEnvironmentAllowed(e.Url, e.Sku)).ToList();

    /// <summary>Allows or turns off an environment, and saves.</summary>
    public void SetEnvironmentAllowed(string environmentUrl, bool allowed, Dataverse.EnvironmentSku sku)
    {
        if (Settings.IsEnvironmentAllowed(environmentUrl, sku) == allowed) return;
        Settings.SetEnvironmentAllowed(environmentUrl, allowed, sku);
        Save();
    }

    public void Save()
    {
        _save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The client for the provider chosen in settings. Throws <see cref="AiException"/> saying
    /// what is missing - a key, or Claude Code - when it cannot be made.
    /// </summary>
    public IAiChatClient CreateClient() => CreateClient(Settings);

    /// <param name="key">A key typed but not yet saved, to use instead of the saved one.</param>
    public IAiChatClient CreateClient(AiSettings settings, string? key = null)
    {
        if (ClientFactory?.Invoke(settings, Keys) is { } fake) return fake;

        if (!settings.Enabled) throw new AiException("Ask AI is off. Turn it on in Ask AI settings.");

        key = string.IsNullOrWhiteSpace(key) ? Keys.Get(settings.Provider) : key.Trim();

        switch (settings.Provider)
        {
            case AiProviderKind.Anthropic:
                return new AnthropicChatClient(
                    key ?? throw new AiException("Add a Claude API key in Ask AI settings."),
                    settings.EffectiveAnthropicModel);

            case AiProviderKind.OpenRouter:
                return new OpenRouterChatClient(
                    key ?? throw new AiException("Add an OpenRouter API key in Ask AI settings."),
                    settings.EffectiveOpenRouterModel,
                    Http);

            case AiProviderKind.Ollama:
                return new OllamaChatClient(
                    settings.EffectiveOllamaUrl,
                    string.IsNullOrWhiteSpace(settings.OllamaModel)
                        ? throw new AiException("Choose an Ollama model in Ask AI settings.")
                        : settings.OllamaModel.Trim(),
                    settings.EffectiveOllamaContextLength,
                    Http);

            case AiProviderKind.ClaudeCode:
                var program = ClaudeCodeChatClient.Find(settings.ClaudeCodePath) ?? throw new AiException(
                    string.IsNullOrWhiteSpace(settings.ClaudeCodePath)
                        ? "Claude Code wasn't found. Install it (https://code.claude.com), sign in by running claude in a " +
                          "terminal, or set where claude is in Ask AI settings."
                        : $"Claude Code wasn't found at {settings.ClaudeCodePath}.");
                return new ClaudeCodeChatClient(program, settings.EffectiveClaudeCodeModel);

            default:
                throw new AiException("Choose an AI provider in Ask AI settings.");
        }
    }
}
