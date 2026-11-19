using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Services;

namespace PPObjectSearch.Ai;

/// <summary>
/// The AI providers' API keys, in %LOCALAPPDATA%\PPObjectSearch\ai-keys.dat, encrypted with DPAPI
/// for the current Windows account - like the token cache, and never in settings.json, which is
/// documented as a file to read and share.
/// </summary>
public sealed class AiKeyStore
{
    private readonly string _path;

    public AiKeyStore() : this(Path.Combine(AppPaths.DataDirectory, "ai-keys.dat"))
    {
    }

    internal AiKeyStore(string path) => _path = path;

    public string? Get(AiProviderKind provider)
    {
        var key = Read().GetValueOrDefault(provider.ToString());
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public bool Has(AiProviderKind provider) => Get(provider) is not null;

    /// <summary>Sets - or, with null or blank, removes - a provider's key.</summary>
    public void Set(AiProviderKind provider, string? key)
    {
        var keys = Read();
        if (string.IsNullOrWhiteSpace(key)) keys.Remove(provider.ToString());
        else keys[provider.ToString()] = key.Trim();
        Write(keys);
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new();

            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            // Another account's file, or a damaged one: the keys are asked for again.
            Log.Warn("The AI keys file could not be read", ex);
            return new();
        }
    }

    private void Write(Dictionary<string, string> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        if (keys.Count == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }

        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(keys), null, DataProtectionScope.CurrentUser);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, _path, overwrite: true);
    }
}
