using System.IO;
using System.Runtime.CompilerServices;
using PPObjectSearch.Auth;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>
/// Points the app's data directory - settings, logs, the token and component caches - at a
/// folder of the test run's own before any test touches it, so nothing is read from or written to
/// the real profile. The folder goes when the test run ends.
/// </summary>
public static class TestDataDirectory
{
    public static string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PPObjectSearch.Tests-" + Guid.NewGuid().ToString("N"));

#pragma warning disable CA2255 // A test assembly is exactly where a module initializer belongs.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Use()
    {
        Directory.CreateDirectory(Path);
        AppPaths.DataDirectory = Path;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove();
    }

    private static void Remove()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open as the run ends is left to the temp folder's own cleaning.
        }
    }
}
