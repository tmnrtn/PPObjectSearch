using PPObjectSearch.Dataverse;

namespace PPObjectSearch.Services;

/// <summary>Whether this app may write to an environment, and the reason either way.</summary>
public sealed record WritePermission(bool Allowed, EnvironmentTypeInfo Type, string Reason)
{
    /// <summary>True where the environment is protected but has been named in settings - worth
    /// saying differently from an ordinary sandbox, because it means a guard was deliberately
    /// lifted rather than never applying.</summary>
    public bool IsAllowlisted { get; init; }
}

/// <summary>
/// The rule that stands between this app and a production environment.
///
/// Production is refused unless that exact environment URL is named in
/// <see cref="AppSettings.AllowProductionWrites"/>. An environment whose type could not be read is
/// refused on the same terms: the guard fails closed, because "the Power Platform API did not
/// answer" is not evidence that somewhere is safe to write to. The tenant's default environment is
/// treated as production too - it is not labelled so, but everyone in the tenant is in it.
/// </summary>
public static class WriteGuard
{
    public static WritePermission Evaluate(
        AppSettings settings,
        string environmentUrl,
        EnvironmentTypeInfo type)
    {
        if (!type.IsProtected)
        {
            return new WritePermission(true, type, $"{type.SkuLabel} environment - writes are allowed.");
        }

        var allowlisted = IsAllowlisted(settings, environmentUrl);

        if (allowlisted)
        {
            return new WritePermission(
                true, type,
                $"{type.SkuLabel} environment, cleared for writing by AllowProductionWrites in settings.json.")
            {
                IsAllowlisted = true
            };
        }

        var why = type.Sku == EnvironmentSku.Unknown
            ? type.Detail ?? "The environment type could not be determined."
            : $"This is a {type.SkuLabel.ToLowerInvariant()} environment.";

        return new WritePermission(
            false, type,
            $"Writing here is blocked. {why} Add \"{environmentUrl}\" to AllowProductionWrites in " +
            "settings.json to permit it.");
    }

    /// <summary>
    /// Adds exactly this environment's host to the allowlist, as "https://host". Called only after
    /// the person has confirmed it for this one environment. False if it was already there or the
    /// URL has no host. The caller saves the settings.
    /// </summary>
    public static bool Allow(AppSettings settings, string environmentUrl)
    {
        var host = TryHost(environmentUrl);
        if (host is null || IsAllowlisted(settings, environmentUrl)) return false;

        (settings.AllowProductionWrites ??= new List<string>()).Add("https://" + host);
        return true;
    }

    /// <summary>
    /// Removes every entry naming this environment's host, however it was written. False if there
    /// was none. The caller saves the settings.
    /// </summary>
    public static bool Revoke(AppSettings settings, string environmentUrl)
    {
        var host = TryHost(environmentUrl);
        if (host is null || settings.AllowProductionWrites is not { Count: > 0 } allowed) return false;

        var removed = allowed.RemoveAll(entry =>
            !string.IsNullOrWhiteSpace(entry) &&
            string.Equals(TryHost(entry) ?? entry.Trim(), host, StringComparison.OrdinalIgnoreCase));

        return removed > 0;
    }

    /// <summary>
    /// Compared by host, so a trailing slash or a different scheme cannot accidentally match or
    /// fail to match. Nothing broader than a whole host is ever accepted - no wildcards, no
    /// suffix matching - so one entry can only ever unlock the one environment it names.
    /// </summary>
    public static bool IsAllowlisted(AppSettings settings, string environmentUrl)
    {
        if (settings.AllowProductionWrites is not { Count: > 0 } allowed) return false;

        var host = TryHost(environmentUrl);
        if (host is null) return false;

        foreach (var entry in allowed)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;

            var entryHost = TryHost(entry) ?? entry.Trim();

            if (string.Equals(entryHost, host, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string? TryHost(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;

        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.Host : null;
    }
}
