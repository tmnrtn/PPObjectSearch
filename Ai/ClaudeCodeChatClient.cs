using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PPObjectSearch.Auth;

namespace PPObjectSearch.Ai;

/// <summary>
/// Claude Code's command line, signed in with the user's own Claude subscription. Each question
/// runs <c>claude -p</c> once, unmodified, with every built-in tool and MCP server switched off -
/// it answers from what it is sent and touches nothing on the machine. The app never handles the
/// subscription's sign-in: that stays inside Claude Code, done with <c>claude</c> in a terminal.
/// </summary>
public sealed class ClaudeCodeChatClient : IAiChatClient
{
    private readonly string _program;
    private readonly string? _model;

    public ClaudeCodeChatClient(string program, string? model)
    {
        _program = program;
        _model = model;
    }

    public string Label => _model is null ? "Claude Code" : $"Claude Code · {_model}";

    /// <summary>The app's tools are not offered: Claude Code answers from the screen it is sent.</summary>
    public bool SupportsTools => false;

    /// <summary>
    /// The claude program: the one set in settings, else the first on the PATH, else where the
    /// native installer and npm put it. Null if there is none.
    /// </summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;

        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(name => SafeCombine(dir, name)));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var usual = new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd")
        };

        return onPath.Concat(usual).FirstOrDefault(p => p is not null && File.Exists(p));
    }

    private static string? SafeCombine(string dir, string name)
    {
        try
        {
            return Path.Combine(dir.Trim('"'), name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The arguments for one question. The prompts go in by file and standard input, not the command line.</summary>
    internal static List<string> Arguments(string systemPromptFile, string? model)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--tools", "",
            "--strict-mcp-config",
            "--no-session-persistence",
            "--max-turns", "1",
            "--system-prompt-file", systemPromptFile
        };
        if (model is not null) args.AddRange(["--model", model]);
        return args;
    }

    public async Task<AiReply> SendAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken)
    {
        // An empty folder of its own, so no project's CLAUDE.md or settings come along.
        var workDir = Path.Combine(AppPaths.DataDirectory, "claude-code");
        Directory.CreateDirectory(workDir);
        var systemFile = Path.Combine(workDir, $"system-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(systemFile, request.System, cancellationToken);

        try
        {
            using var process = new Process { StartInfo = StartInfo(Arguments(systemFile, _model), workDir) };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new AiException($"Claude Code could not be started ({_program}): {ex.Message}", ex);
            }

            using var stop = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });

            // Claude Code keeps no session between questions, so each one carries the conversation.
            await process.StandardInput.WriteAsync(Transcript(request.Messages));
            process.StandardInput.Close();

            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            var reply = await ReadStreamAsync(process.StandardOutput, onText, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (reply is null)
            {
                var stderr = (await errors).Trim();
                throw new AiException(stderr.Length > 0
                    ? $"Claude Code stopped without answering: {stderr}"
                    : $"Claude Code stopped without answering (exit code {process.ExitCode}).");
            }

            return reply;
        }
        finally
        {
            try { File.Delete(systemFile); } catch (IOException) { }
        }
    }

    private ProcessStartInfo StartInfo(List<string> args, string workDir)
    {
        var isScript = _program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                       _program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var info = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (isScript)
        {
            // An npm install's claude.cmd runs through cmd; none of the arguments needs escaping
            // beyond quotes, as the prompts themselves go by file and standard input.
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = "/d /s /c \"" + string.Join(' ', new[] { _program }.Concat(args).Select(Quote)) + "\"";
        }
        else
        {
            info.FileName = _program;
            foreach (var arg in args) info.ArgumentList.Add(arg);
        }

        // An API key in the environment would be used instead of the subscription chosen here.
        info.Environment.Remove("ANTHROPIC_API_KEY");
        info.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
        return info;
    }

    private static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";

    /// <summary>The conversation as one prompt: earlier turns labelled, and the newest question last.</summary>
    internal static string Transcript(IReadOnlyList<AiMessage> messages)
    {
        var turns = messages.Where(m => m.ToolResults.Count == 0 && m.Text.Length > 0).ToList();
        if (turns.Count == 1) return turns[0].Text;

        var text = new StringBuilder("The conversation so far:\n\n");
        foreach (var turn in turns.Take(turns.Count - 1))
        {
            text.Append(turn.Role == AiRole.User ? "User: " : "Assistant: ").Append(turn.Text).Append("\n\n");
        }

        text.Append("The user now asks:\n\n").Append(turns[^1].Text);
        return text.ToString();
    }

    /// <summary>
    /// Reads Claude Code's stream-json output: text deltas as they come, then the result event.
    /// Null if the stream ended without a result.
    /// </summary>
    internal static async Task<AiReply?> ReadStreamAsync(TextReader output, Action<string> onText, CancellationToken cancellationToken)
    {
        var streamed = new StringBuilder();

        while (await output.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "stream_event" &&
                    root.TryGetProperty("event", out var ev) &&
                    ev.TryGetProperty("type", out var evType) && evType.GetString() == "content_block_delta" &&
                    ev.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } piece)
                {
                    streamed.Append(piece);
                    onText(piece);
                }
                else if (type == "result")
                {
                    var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                    var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;

                    if (isError)
                    {
                        throw new AiException(result.Length > 0
                            ? $"Claude Code: {result}"
                            : "Claude Code reported an error without saying what.");
                    }

                    // An older Claude Code that does not stream partial messages still gives the whole answer here.
                    if (streamed.Length == 0 && result.Length > 0) onText(result);

                    return new AiReply
                    {
                        Message = new AiMessage { Role = AiRole.Assistant, Text = streamed.Length > 0 ? streamed.ToString() : result }
                    };
                }
            }
        }

        return null;
    }
}
