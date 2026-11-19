using System.Collections.ObjectModel;
using System.Net.Http;
using PPObjectSearch.Ai;
using PPObjectSearch.Core;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// An environment in the settings' list, with its switch: the environments open in the app, and any
/// other that has been allowed or turned off by hand.
/// </summary>
public sealed class AiEnvironmentChoice : ObservableObject
{
    public AiEnvironmentChoice(string title, string url, Dataverse.EnvironmentSku sku, bool isOpen, bool isAllowed)
    {
        Title = title;
        Url = url;
        Sku = sku;
        IsOpen = isOpen;
        _isAllowed = isAllowed;
        WasAllowed = isAllowed;
    }

    /// <summary>As it was when the dialog opened: only a switch that moved is saved.</summary>
    public bool WasAllowed { get; }

    public string Title { get; }
    public string Url { get; }
    public Dataverse.EnvironmentSku Sku { get; }
    public bool IsOpen { get; }

    public string Host => AiSettings.Host(Url) ?? Url;

    /// <summary>The host, and whether the environment is open in the app.</summary>
    public string Detail => IsOpen ? Host : $"{Host} · not open";

    private bool _isAllowed;
    public bool IsAllowed
    {
        get => _isAllowed;
        set
        {
            if (SetProperty(ref _isAllowed, value)) OnPropertyChanged(nameof(State));
        }
    }

    public string State => IsAllowed
        ? "Allowed"
        : AiSettings.StartsOff(Sku) && Sku == Dataverse.EnvironmentSku.Production ? "Off: production" : "Off";
}

/// <summary>
/// Ask AI settings: whether it is on, which provider, which model, its key, and the environments it may not
/// read. Nothing changes until Save; Test tries what is on the dialog, saved or not.
/// </summary>
public sealed class AiSettingsViewModel : ObservableObject
{
    private readonly AiService _service;

    /// <param name="open">The environments open in the app, listed first with their switches.</param>
    public AiSettingsViewModel(AiService service, IReadOnlyList<AiEnvironment>? open = null)
    {
        _service = service;
        var s = service.Settings;

        _isEnabled = s.Enabled;
        _provider = s.Provider;
        _anthropicModel = s.EffectiveAnthropicModel;
        _openRouterModel = s.EffectiveOpenRouterModel;
        _claudeCodeModel = s.ClaudeCodeModel ?? string.Empty;
        _claudeCodePath = s.ClaudeCodePath ?? string.Empty;
        _ollamaUrl = s.EffectiveOllamaUrl;
        _ollamaModel = s.OllamaModel ?? string.Empty;
        _ollamaContextLength = s.EffectiveOllamaContextLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _hasAnthropicKey = service.Keys.Has(AiProviderKind.Anthropic);
        _hasOpenRouterKey = service.Keys.Has(AiProviderKind.OpenRouter);

        // Open environments first, then any other that has a choice recorded for it.
        foreach (var environment in open ?? [])
        {
            if (Environments.Any(e => string.Equals(e.Host, AiSettings.Host(environment.Url), StringComparison.OrdinalIgnoreCase))) continue;
            Environments.Add(new AiEnvironmentChoice(environment.Title, environment.Url, environment.Sku, true,
                s.IsEnvironmentAllowed(environment.Url, environment.Sku)));
        }
        foreach (var host in (s.ExcludedEnvironments ?? []).Concat(s.AllowedEnvironments ?? []))
        {
            if (Environments.Any(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase))) continue;
            var url = "https://" + host;
            Environments.Add(new AiEnvironmentChoice(host, url, Dataverse.EnvironmentSku.Unknown, false,
                s.IsEnvironmentAllowed(url, Dataverse.EnvironmentSku.Unknown)));
        }

        TestCommand = new AsyncRelayCommand(_ => TestAsync(), _ => IsEnabled && Provider != AiProviderKind.None && !IsTesting);
        LoadOpenRouterModelsCommand = new AsyncRelayCommand(_ => LoadOpenRouterModelsAsync());
        LoadOllamaModelsCommand = new AsyncRelayCommand(_ => LoadOllamaModelsAsync());
        RemoveKeyCommand = new RelayCommand(p =>
        {
            if (p is not string name || !Enum.TryParse<AiProviderKind>(name, out var provider)) return;
            _removedKeys.Add(provider);
            if (provider == AiProviderKind.Anthropic) { AnthropicKey = string.Empty; HasAnthropicKey = false; }
            if (provider == AiProviderKind.OpenRouter) { OpenRouterKey = string.Empty; HasOpenRouterKey = false; }
        });
        ReplaceKeyCommand = new RelayCommand(p =>
        {
            if (p is not string name || !Enum.TryParse<AiProviderKind>(name, out var provider)) return;
            if (provider == AiProviderKind.Anthropic) HasAnthropicKey = false;
            if (provider == AiProviderKind.OpenRouter) HasOpenRouterKey = false;
        });
    }

    private readonly HashSet<AiProviderKind> _removedKeys = new();

    // ---------------------------------------------------------------- provider

    private AiProviderKind _provider;
    public AiProviderKind Provider
    {
        get => _provider;
        set
        {
            if (!SetProperty(ref _provider, value)) return;
            TestResult = null;
            OnPropertyChanged(nameof(IsClaudeCode));
            OnPropertyChanged(nameof(IsAnthropic));
            OnPropertyChanged(nameof(IsOpenRouter));
            OnPropertyChanged(nameof(IsOllama));
            OnPropertyChanged(nameof(HasProvider));
            TestCommand.RaiseCanExecuteChanged();
        }
    }


    private bool _isEnabled;
    /// <summary>Ask AI's master switch; off keeps the provider chosen.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value)) TestCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsClaudeCode { get => Provider == AiProviderKind.ClaudeCode; set { if (value) Provider = AiProviderKind.ClaudeCode; } }
    public bool IsAnthropic { get => Provider == AiProviderKind.Anthropic; set { if (value) Provider = AiProviderKind.Anthropic; } }
    public bool IsOpenRouter { get => Provider == AiProviderKind.OpenRouter; set { if (value) Provider = AiProviderKind.OpenRouter; } }
    public bool IsOllama { get => Provider == AiProviderKind.Ollama; set { if (value) Provider = AiProviderKind.Ollama; } }

    public bool HasProvider => Provider != AiProviderKind.None;

    // ---------------------------------------------------------------- Claude Code

    public IReadOnlyList<string> ClaudeCodeModels { get; } = ["", "opus", "sonnet", "haiku", "fable"];

    private string _claudeCodeModel;
    /// <summary>A Claude Code model alias or name; blank for Claude Code's own choice.</summary>
    public string ClaudeCodeModel
    {
        get => _claudeCodeModel;
        set => SetProperty(ref _claudeCodeModel, value ?? string.Empty);
    }

    private string _claudeCodePath;
    public string ClaudeCodePath
    {
        get => _claudeCodePath;
        set
        {
            if (SetProperty(ref _claudeCodePath, value ?? string.Empty)) OnPropertyChanged(nameof(ClaudeCodeFound));
        }
    }

    /// <summary>Where claude was found, or why it wasn't.</summary>
    public string ClaudeCodeFound => ClaudeCodeChatClient.Find(Blank(ClaudeCodePath)) is { } found
        ? $"Using {found}"
        : string.IsNullOrWhiteSpace(ClaudeCodePath)
            ? "Claude Code wasn't found. Install it from code.claude.com, or say where claude is."
            : "There's no file there.";

    // ---------------------------------------------------------------- Claude API

    public IReadOnlyList<string> AnthropicModels { get; } =
        ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1"];

    private string _anthropicModel;
    public string AnthropicModel
    {
        get => _anthropicModel;
        set => SetProperty(ref _anthropicModel, value ?? string.Empty);
    }

    private string _anthropicKey = string.Empty;
    /// <summary>A key typed in; blank keeps the saved one.</summary>
    public string AnthropicKey
    {
        get => _anthropicKey;
        set => SetProperty(ref _anthropicKey, value ?? string.Empty);
    }

    private bool _hasAnthropicKey;
    public bool HasAnthropicKey
    {
        get => _hasAnthropicKey;
        private set => SetProperty(ref _hasAnthropicKey, value);
    }

    // ---------------------------------------------------------------- OpenRouter

    public ObservableCollection<string> OpenRouterModels { get; } =
        ["anthropic/claude-opus-5.5", "anthropic/claude-sonnet-5.5", "anthropic/claude-haiku-5.5"];

    private string _openRouterModel;
    public string OpenRouterModel
    {
        get => _openRouterModel;
        set => SetProperty(ref _openRouterModel, value ?? string.Empty);
    }

    private string _openRouterKey = string.Empty;
    public string OpenRouterKey
    {
        get => _openRouterKey;
        set => SetProperty(ref _openRouterKey, value ?? string.Empty);
    }

    private bool _hasOpenRouterKey;
    public bool HasOpenRouterKey
    {
        get => _hasOpenRouterKey;
        private set => SetProperty(ref _hasOpenRouterKey, value);
    }

    public AsyncRelayCommand LoadOpenRouterModelsCommand { get; }

    private string? _modelsStatus;
    public string? ModelsStatus
    {
        get => _modelsStatus;
        private set => SetProperty(ref _modelsStatus, value);
    }

    private async Task LoadOpenRouterModelsAsync()
    {
        ModelsStatus = "Loading…";
        try
        {
            var models = await OpenRouterChatClient.ListModelsAsync(_service.Http, CancellationToken.None);
            OpenRouterModels.Clear();
            foreach (var model in models) OpenRouterModels.Add(model);
            ModelsStatus = $"{models.Count:N0} models";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            ModelsStatus = $"The list couldn't be loaded: {ex.Message}";
        }
    }

    public RelayCommand RemoveKeyCommand { get; }

    /// <summary>Shows the key box in place of "Saved", to type a new key over the old.</summary>
    public RelayCommand ReplaceKeyCommand { get; }

    // ---------------------------------------------------------------- Ollama

    private string _ollamaUrl;
    public string OllamaUrl
    {
        get => _ollamaUrl;
        set => SetProperty(ref _ollamaUrl, value ?? string.Empty);
    }

    private string _ollamaModel;
    public string OllamaModel
    {
        get => _ollamaModel;
        set => SetProperty(ref _ollamaModel, value ?? string.Empty);
    }

    private string _ollamaContextLength;
    /// <summary>In tokens, as typed; anything that isn't a positive number means the default.</summary>
    public string OllamaContextLength
    {
        get => _ollamaContextLength;
        set => SetProperty(ref _ollamaContextLength, value ?? string.Empty);
    }

    public ObservableCollection<string> OllamaModels { get; } = new();

    public AsyncRelayCommand LoadOllamaModelsCommand { get; }

    private string? _ollamaStatus;
    public string? OllamaStatus
    {
        get => _ollamaStatus;
        private set => SetProperty(ref _ollamaStatus, value);
    }

    /// <summary>The models the Ollama server has; the first is chosen when none is yet.</summary>
    internal async Task LoadOllamaModelsAsync()
    {
        OllamaStatus = "Loading…";
        try
        {
            var models = await OllamaChatClient.ListModelsAsync(_service.Http, OllamaUrl, CancellationToken.None);
            OllamaModels.Clear();
            foreach (var model in models) OllamaModels.Add(model);
            if (string.IsNullOrWhiteSpace(OllamaModel) && models.Count > 0) OllamaModel = models[0];
            OllamaStatus = models.Count == 0 ? "The server has no models yet: ollama pull one." : $"{models.Count:N0} models";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AiException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            OllamaStatus = $"Ollama didn't answer at {OllamaUrl}: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- environments

    /// <summary>Every environment the app knows, each with whether Ask AI may read it.</summary>
    public ObservableCollection<AiEnvironmentChoice> Environments { get; } = new();

    public bool HasEnvironments => Environments.Count > 0;

    // ---------------------------------------------------------------- test and save

    public AsyncRelayCommand TestCommand { get; }

    private bool _isTesting;
    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value)) TestCommand.RaiseCanExecuteChanged();
        }
    }

    private string? _testResult;
    public string? TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }

    private bool _testFailed;
    public bool TestFailed
    {
        get => _testFailed;
        private set => SetProperty(ref _testFailed, value);
    }

    /// <summary>Asks the chosen model one tiny question, with what is on the dialog.</summary>
    internal async Task TestAsync()
    {
        IsTesting = true;
        TestFailed = false;
        TestResult = "Asking…";
        try
        {
            var client = _service.CreateClient(Draft(), Provider switch
            {
                AiProviderKind.Anthropic => AnthropicKey,
                AiProviderKind.OpenRouter => OpenRouterKey,
                _ => null
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var reply = await client.SendAsync(new AiRequest
            {
                System = "You are checking that a connection works.",
                Messages = [AiMessage.User("Reply with the single word: Connected")]
            }, _ => { }, timeout.Token);

            TestResult = $"{client.Label} answered: {reply.Message.Text.Trim()}";
        }
        catch (Exception ex) when (ex is AiException or HttpRequestException or OperationCanceledException)
        {
            TestFailed = true;
            TestResult = ex is OperationCanceledException ? "No answer within two minutes." : ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>The settings as the dialog has them, not yet saved.</summary>
    private AiSettings Draft() => new()
    {
        Enabled = true,
        Provider = Provider,
        AnthropicModel = Blank(AnthropicModel),
        OpenRouterModel = Blank(OpenRouterModel),
        ClaudeCodeModel = Blank(ClaudeCodeModel),
        ClaudeCodePath = Blank(ClaudeCodePath),
        OllamaUrl = Blank(OllamaUrl) is { } url && url != OllamaChatClient.DefaultUrl ? url : null,
        OllamaModel = Blank(OllamaModel),
        OllamaContextLength = int.TryParse(OllamaContextLength, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var length) &&
                              length > 0 && length != OllamaChatClient.DefaultContextLength ? length : null
    };

    public void Save()
    {
        var s = _service.Settings;
        var draft = Draft();
        s.Provider = draft.Provider;
        s.AnthropicModel = draft.AnthropicModel;
        s.OpenRouterModel = draft.OpenRouterModel;
        s.ClaudeCodeModel = draft.ClaudeCodeModel;
        s.ClaudeCodePath = draft.ClaudeCodePath;
        s.OllamaUrl = draft.OllamaUrl;
        s.OllamaModel = draft.OllamaModel;
        s.OllamaContextLength = draft.OllamaContextLength;
        s.Enabled = IsEnabled;
        foreach (var environment in Environments.Where(e => e.IsAllowed != e.WasAllowed))
        {
            if (environment.IsOpen) s.SetEnvironmentAllowed(environment.Url, environment.IsAllowed, environment.Sku);
            else s.SetEnvironmentChoice(environment.Url, environment.IsAllowed);
        }

        foreach (var provider in _removedKeys) _service.Keys.Set(provider, null);
        if (Blank(AnthropicKey) is { } anthropic) _service.Keys.Set(AiProviderKind.Anthropic, anthropic);
        if (Blank(OpenRouterKey) is { } openRouter) _service.Keys.Set(AiProviderKind.OpenRouter, openRouter);

        _service.Save();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
