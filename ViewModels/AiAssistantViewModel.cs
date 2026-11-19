using System.Collections.ObjectModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using PPObjectSearch.Ai;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One message in the assistant panel: a question, or an answer as it streams in.</summary>
public sealed class AiChatEntry : ObservableObject
{
    public AiChatEntry(bool isUser, string text = "", string? question = null)
    {
        IsUser = isUser;
        _text = text;
        Question = question;
        CopyCommand = new RelayCommand(_ => ClipboardText.TryCopy(Text, out string? _));
    }

    public bool IsUser { get; }

    /// <summary>For an answer, the question it answers - what Retry asks again.</summary>
    public string? Question { get; }

    private string _text;
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    private bool _isStreaming;
    /// <summary>The answer is still coming.</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (SetProperty(ref _isStreaming, value)) OnPropertyChanged(nameof(IsDone));
        }
    }

    public bool IsDone => !IsUser && !IsStreaming;

    private string? _activity;
    /// <summary>What the assistant is doing while the answer waits - reading another screen, say.</summary>
    public string? Activity
    {
        get => _activity;
        set => SetProperty(ref _activity, value);
    }

    /// <summary>What it looked at on the way - "Read Run history" - kept beside the answer.</summary>
    public ObservableCollection<string> Steps { get; } = new();

    private bool _isError;
    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }

    public RelayCommand CopyCommand { get; }
}

/// <summary>A provider offered when Ask AI is not set up yet: a card that opens settings with it chosen.</summary>
public sealed record AiProviderCard(AiProviderKind Kind, string Name, string Note);

/// <summary>
/// The assistant panel beside one screen: a conversation about that screen, each question sent
/// with the screen as it is when asked. A screen keeps its conversation as it moves between a tab
/// and a window; it ends when the screen closes. The panel says, before anything is typed, which
/// screen and environment a question would be about - and when that environment is one Ask AI may
/// not read, it says so and takes no question.
/// </summary>
public sealed class AiAssistantViewModel : ObservableObject
{
    private static readonly ConditionalWeakTable<object, AiAssistantViewModel> ByScreen = new();

    private readonly AiService? _service;
    private readonly Func<AiScreen?> _screen;
    private readonly Func<AiContext> _describe;
    private readonly AiConversation _conversation = new();
    private CancellationTokenSource? _running;

    /// <param name="screen">The screen as it is now: read on the UI thread each time a question is asked.</param>
    /// <param name="describe">What the screen is, cheaply: for the context strip and the environment check.</param>
    public AiAssistantViewModel(AiService? service, Func<AiScreen?> screen, Func<AiContext>? describe = null)
    {
        _service = service;
        _screen = screen;
        _describe = describe ?? (() => new AiContext(string.Empty, []));

        SendCommand = new AsyncRelayCommand(_ => SendAsync(), _ => CanAsk && !string.IsNullOrWhiteSpace(Input));
        AskCommand = new AsyncRelayCommand(p => SendAsync(p as string), _ => CanAsk);
        RetryCommand = new AsyncRelayCommand(p => SendAsync((p as AiChatEntry)?.Question), _ => CanAsk);
        StopCommand = new RelayCommand(_ => _running?.Cancel(), _ => IsBusy);
        ClearCommand = new RelayCommand(_ => Clear(), _ => !IsBusy && Entries.Count > 0);
        OpenSettingsCommand = new RelayCommand(p => OpenSettings?.Invoke(p is AiProviderKind kind ? kind : null));
        AllowEnvironmentCommand = new RelayCommand(_ => AllowBlocked(), _ => Blocked.Count > 0);

        if (_service is not null)
        {
            // Weakly, so a closed screen's conversation is not kept alive by the service.
            var self = new WeakReference<AiAssistantViewModel>(this);
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                if (self.TryGetTarget(out var target)) target.Refresh();
                else _service.Changed -= handler;
            };
            _service.Changed += handler;
        }

        Refresh();
    }

    /// <summary>
    /// The conversation for a screen - an open screen, or an environment - made the first time it
    /// is asked for and kept for as long as the screen is.
    /// </summary>
    public static AiAssistantViewModel For(object screenKey, Func<AiScreen?> screen, Func<AiContext>? describe = null) =>
        ByScreen.GetValue(screenKey, _ => new AiAssistantViewModel(AiService.Current, screen, describe));

    /// <summary>Opens Ask AI settings, with a provider already chosen when one is given; set by the shell.</summary>
    public static Action<AiProviderKind?>? OpenSettings { get; set; }

    /// <summary>Confirms turning Ask AI on for an environment; replaced in tests.</summary>
    internal static Func<AskRequest, bool> Ask { get; set; } = Asking.Show;

    public ObservableCollection<AiChatEntry> Entries { get; } = new();

    public bool HasEntries => Entries.Count > 0;

    public static IReadOnlyList<AiProviderCard> Providers { get; } =
    [
        new(AiProviderKind.ClaudeCode, "My Claude subscription", "Through Claude Code, signed in on this PC"),
        new(AiProviderKind.Anthropic, "Claude API key", "Billed to your Claude Console account"),
        new(AiProviderKind.OpenRouter, "OpenRouter", "Any model OpenRouter offers"),
        new(AiProviderKind.Ollama, "Ollama", "A local model, so nothing leaves your network")
    ];

    // ---------------------------------------------------------------- the screen

    private AiContext _context = new(string.Empty, []);

    /// <summary>The screen a question would be about.</summary>
    public string ContextTitle => _context.Title;

    /// <summary>The one environment it shows, for the strip's colour bar and name; null for several or none.</summary>
    public AiEnvironment? ContextEnvironment => _context.Environments is [var only] ? only : null;

    public bool HasContextEnvironment => ContextEnvironment is not null;

    /// <summary>"Contoso Dev" - or, across several, all their names.</summary>
    public string ContextEnvironments => string.Join(", ", _context.Environments.Select(e => e.Title));

    /// <summary>What to ask: the item selected on the screen named, when there is one.</summary>
    public IReadOnlyList<string> Suggestions => _context.Selected is { Length: > 0 } selected
        ? ["Summarise this screen", "What looks wrong here?", $"Explain {Trim(selected)}"]
        : ["Summarise this screen", "What looks wrong here?", "Explain the selected item"];

    private IReadOnlyList<AiEnvironment> _blocked = [];
    /// <summary>The environments on this screen that Ask AI may not read.</summary>
    public IReadOnlyList<AiEnvironment> Blocked
    {
        get => _blocked;
        private set
        {
            _blocked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(BlockedHeading));
            OnPropertyChanged(nameof(AllowLabel));
            OnPropertyChanged(nameof(BlockedSku));
            AllowEnvironmentCommand.RaiseCanExecuteChanged();
            RaiseCanAsk();
        }
    }

    public bool IsBlocked => Blocked.Count > 0;

    public string BlockedHeading => $"Ask AI is off for {Names(Blocked)}";

    public string AllowLabel => Blocked.Count == 1 ? $"Allow for {Blocked[0].Title}…" : "Allow for these environments…";

    /// <summary>The colour of the environment that is off, for the notice's bar.</summary>
    public Dataverse.EnvironmentSku BlockedSku => Blocked.Count > 0 ? Blocked[0].Sku : Dataverse.EnvironmentSku.Unknown;

    /// <summary>Reads the screen's title and environments again - when it is shown, and when settings change.</summary>
    public void Refresh()
    {
        _context = _describe();
        OnPropertyChanged(nameof(ContextTitle));
        OnPropertyChanged(nameof(ContextEnvironment));
        OnPropertyChanged(nameof(HasContextEnvironment));
        OnPropertyChanged(nameof(ContextEnvironments));
        OnPropertyChanged(nameof(Suggestions));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(ProviderLabel));
        OnPropertyChanged(nameof(PrivacyNote));
        Blocked = _service is null ? [] : _service.Excluded(new AiScreen(_context.Title, string.Empty) { Environments = _context.Environments });
    }

    private void AllowBlocked()
    {
        if (_service is null || Blocked.Count == 0) return;

        var names = Names(Blocked);
        if (!Ask(new AskRequest($"Let Ask AI read {names}?",
                $"Screens showing {names} will be sent to {ProviderName} when you ask about them. You can turn it off again " +
                "from the environment's menu in the sidebar, or in Ask AI settings.")
            {
                Confirm = "Allow"
            }))
        {
            return;
        }

        foreach (var environment in Blocked) _service.SetEnvironmentAllowed(environment.Url, true, environment.Sku);
        Refresh();
    }

    // ---------------------------------------------------------------- provider

    public bool IsConfigured => _service?.IsConfigured == true;

    /// <summary>The provider and model, as the panel names them.</summary>
    public string ProviderLabel => _service?.Settings is not { } s
        ? string.Empty
        : s.Provider switch
        {
            AiProviderKind.ClaudeCode => s.EffectiveClaudeCodeModel is { } m ? $"Claude Code · {m}" : "Claude Code",
            AiProviderKind.Anthropic => $"Claude API · {s.EffectiveAnthropicModel}",
            AiProviderKind.OpenRouter => $"OpenRouter · {s.EffectiveOpenRouterModel}",
            AiProviderKind.Ollama => $"Ollama · {s.OllamaModel}",
            _ => "Not set up"
        };

    private string ProviderName => _service?.Settings.Provider switch
    {
        AiProviderKind.ClaudeCode => "Claude, through Claude Code",
        AiProviderKind.Anthropic => "the Claude API",
        AiProviderKind.OpenRouter => "OpenRouter",
        AiProviderKind.Ollama => "your Ollama server",
        _ => "the AI provider"
    };

    /// <summary>Said under the question box: where the screen goes when a question is asked.</summary>
    public string PrivacyNote => _service?.Settings.Provider switch
    {
        AiProviderKind.ClaudeCode => "Questions send what's on this screen to Claude, through Claude Code.",
        AiProviderKind.Anthropic => "Questions send what's on this screen to the Claude API.",
        AiProviderKind.OpenRouter => "Questions send what's on this screen to OpenRouter and the model's provider.",
        AiProviderKind.Ollama => $"Questions send what's on this screen to Ollama at {_service.Settings.EffectiveOllamaUrl}.",
        _ => string.Empty
    };

    // ---------------------------------------------------------------- asking

    private string _input = string.Empty;
    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value ?? string.Empty)) SendCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            StopCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
            RaiseCanAsk();
        }
    }

    /// <summary>Nothing running, and nothing on the screen that Ask AI may not read.</summary>
    public bool CanAsk => !IsBusy && !IsBlocked;

    private void RaiseCanAsk()
    {
        OnPropertyChanged(nameof(CanAsk));
        SendCommand.RaiseCanExecuteChanged();
        AskCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand SendCommand { get; }

    /// <summary>Asks one of the suggested questions.</summary>
    public AsyncRelayCommand AskCommand { get; }

    /// <summary>Asks an answer's question again.</summary>
    public AsyncRelayCommand RetryCommand { get; }

    public RelayCommand StopCommand { get; }

    /// <summary>New chat: the conversation starts over.</summary>
    public RelayCommand ClearCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand AllowEnvironmentCommand { get; }

    /// <summary>What a question would send about the screen now, to look at before asking.</summary>
    public AiScreen? PreviewScreen() => _screen();

    private void Clear()
    {
        _conversation.Clear();
        Entries.Clear();
        OnPropertyChanged(nameof(HasEntries));
        ClearCommand.RaiseCanExecuteChanged();
    }

    private async Task SendAsync(string? suggested = null)
    {
        var question = (suggested ?? Input).Trim();
        if (question.Length == 0 || IsBusy) return;
        if (suggested is null) Input = string.Empty;

        Entries.Add(new AiChatEntry(isUser: true, question));
        var answer = new AiChatEntry(isUser: false, question: question) { IsStreaming = true };
        Entries.Add(answer);
        OnPropertyChanged(nameof(HasEntries));
        IsBusy = true;

        using var running = _running = new CancellationTokenSource();
        try
        {
            if (_service is null) throw new AiException("Ask AI isn't available.");

            var screen = _screen() ?? new AiScreen("Nothing open", "(There is nothing on screen.)");

            // Checked again as it is sent, from the whole screen: a turned-off environment's data
            // never leaves the app, whatever the panel showed a moment ago.
            if (_service.Excluded(screen) is { Count: > 0 } excluded)
            {
                throw new AiException(
                    $"Ask AI is off for {Names(excluded)}, so this screen isn't sent. Allow it from the notice above, " +
                    "or from the environment's menu in the sidebar.");
            }

            var client = _service.CreateClient();
            var tools = AiTools.For(_service.Workspace);

            await _conversation.AskAsync(client, question, screen, tools,
                piece =>
                {
                    answer.Activity = null;
                    answer.Text += piece;
                },
                call =>
                {
                    answer.Activity = AiTools.Describe(call);
                    answer.Steps.Add(AiTools.Done(call));
                },
                running.Token);

            if (answer.Text.Trim().Length == 0) answer.Text = "(No answer came back.)";
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            answer.Text = answer.Text.Length == 0 ? "(Stopped.)" : answer.Text + "\n\n(Stopped.)";
        }
        catch (AiException ex)
        {
            Fail(answer, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("AI request failed", ex);
            Fail(answer, $"The AI provider couldn't be reached: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("AI request failed", ex);
            Fail(answer, $"Something went wrong asking the AI: {ex.Message}");
        }
        finally
        {
            answer.IsStreaming = false;
            answer.Activity = null;
            _running = null;
            IsBusy = false;
            Refresh();
        }
    }

    private static void Fail(AiChatEntry answer, string message)
    {
        answer.IsError = true;
        answer.Text = answer.Text.Length == 0 ? message : answer.Text + "\n\n" + message;
    }

    private static string Names(IReadOnlyList<AiEnvironment> environments) =>
        string.Join(" and ", environments.Select(e => e.Title));

    private static string Trim(string name) => name.Length > 40 ? name[..39] + "…" : name;
}
