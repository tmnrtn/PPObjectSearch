using System.Globalization;
using System.IO;
using System.Text;
using PPObjectSearch.Auth;

namespace PPObjectSearch.Services;

/// <summary>
/// A plain text log under %LOCALAPPDATA%\PPObjectSearch\logs, one file a day, kept for two weeks.
///
/// Failures this app recovers from - a best-effort read that came back empty, a cache it could
/// not write - are still worth a line somewhere, or a report of "it said Object reference not set"
/// cannot be traced. Logging never throws: a log that cannot be written is simply not written.
/// </summary>
public static class Log
{
    private const int KeepDays = 14;

    private static readonly object Sync = new();
    private static bool _pruned;

    public static string Folder { get; } = Path.Combine(AppPaths.DataDirectory, "logs");

    /// <summary>Today's file, which is where the next line goes.</summary>
    public static string CurrentFile =>
        Path.Combine(Folder, $"ppobjectsearch-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(level).Append(' ')
            .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(message);

        if (ex is not null) line.AppendLine().Append(ex);

        line.AppendLine();

        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(CurrentFile, line.ToString());
                PruneOnce();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Nowhere to report a failure to log; the app carries on.
            }
        }
    }

    private static void PruneOnce()
    {
        if (_pruned) return;
        _pruned = true;

        var cutoff = DateTime.Now.AddDays(-KeepDays);

        foreach (var file in Directory.EnumerateFiles(Folder, "ppobjectsearch-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An old log that cannot be removed now will be tried again next run.
            }
        }
    }
}
