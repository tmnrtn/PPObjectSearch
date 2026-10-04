using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PPObjectSearch.Auth;

namespace PPObjectSearch.Services;

/// <summary>One write, as the run log keeps it.</summary>
public sealed class WriteLogEntry
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public string Run { get; init; } = string.Empty;
    public string Tool { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string? Account { get; init; }

    public string Table { get; init; } = string.Empty;
    public Guid? Id { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? Key { get; init; }
    public string? Name { get; init; }

    /// <summary>The columns the write was meant to touch.</summary>
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

    /// <summary>The whole row just before an update or delete; null for a create.</summary>
    public JsonObject? Before { get; init; }

    /// <summary>What was sent.</summary>
    public JsonObject? After { get; init; }

    public bool Succeeded { get; init; }
    public string? Message { get; init; }

    /// <summary>The write that reverses this one, where there is one.</summary>
    public UndoStep? Undo { get; init; }
}

/// <summary>
/// The record of what each run wrote, one JSON line per write under
/// %LOCALAPPDATA%\PPObjectSearch\writes, one file per run. It is the audit trail for a change ticket,
/// and what Undo works from. Kept indefinitely: deleting it is the user's call, not the app's.
/// </summary>
public sealed class WriteLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();

    public static string DefaultFolder { get; } = System.IO.Path.Combine(AppPaths.DataDirectory, "writes");

    private WriteLog(string path, string run)
    {
        Path = path;
        Run = run;
    }

    /// <summary>The file this run is written to.</summary>
    public string Path { get; }

    public string Run { get; }

    /// <summary>Set once a line could not be written, so the window can say the log is incomplete.</summary>
    public string? Problem { get; private set; }

    /// <summary>A new run's log. Nothing is written until the first write is recorded.</summary>
    public static WriteLog Start(string tool, string? folder = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var run = $"{at:yyyyMMdd-HHmmss}-{tool}-{Guid.NewGuid().ToString("N")[..6]}";
        return new WriteLog(System.IO.Path.Combine(folder ?? DefaultFolder, run + ".jsonl"), run);
    }

    public void Append(WriteLogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Options) + "\n";

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The write itself has happened; failing to record it must not hide that it did.
                Problem = $"The run log could not be written ({ex.Message}).";
                Log.Warn("Could not append to the write log " + Path, ex);
            }
        }
    }

    /// <summary>Every entry of a run log, in the order written. Lines that do not parse are skipped.</summary>
    public static IReadOnlyList<WriteLogEntry> Read(string path)
    {
        var entries = new List<WriteLogEntry>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                if (JsonSerializer.Deserialize<WriteLogEntry>(line, Options) is { } entry) entries.Add(entry);
            }
            catch (JsonException ex)
            {
                Log.Warn($"Skipped an unreadable line in {path}", ex);
            }
        }

        return entries;
    }
}
