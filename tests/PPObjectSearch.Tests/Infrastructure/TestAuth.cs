using PPObjectSearch.Auth;

namespace PPObjectSearch.Tests.Infrastructure;

public static class TestAuth
{
    /// <summary>An auth context that hands out "token:{resource}" for every resource.</summary>
    public static EnvironmentAuthContext Tokens() =>
        new((resource, _) => Task.FromResult<string?>("token:" + resource));

    /// <summary>An auth context for which only the given resources have tokens.</summary>
    public static EnvironmentAuthContext TokensFor(params string[] resources) =>
        new((resource, _) => Task.FromResult(resources.Contains(resource, StringComparer.OrdinalIgnoreCase) ? "token:" + resource : null));

    /// <summary>An auth context that has no tokens at all.</summary>
    public static EnvironmentAuthContext NoTokens() => new((_, _) => Task.FromResult<string?>(null));
}
