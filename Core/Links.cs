namespace PPObjectSearch.Core;

/// <summary>
/// Checks on URLs the server hands back - paging links - before a bearer token goes with them.
/// </summary>
public static class Links
{
    /// <summary>
    /// The next page's URL, if it stays on the host the current page came from over HTTPS. A
    /// nextLink pointing anywhere else would be sent the user's token for the API being paged; it
    /// is refused rather than followed.
    /// </summary>
    /// <param name="fail">Makes the exception the calling client normally throws.</param>
    public static string? SameHostNext(string? next, string current, Func<string, Exception> fail)
    {
        if (string.IsNullOrEmpty(next)) return null;

        Uri.TryCreate(next, UriKind.Absolute, out var nextUri);
        Uri.TryCreate(current, UriKind.Absolute, out var currentUri);

        if (nextUri is null || currentUri is null ||
            nextUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(nextUri.Host, currentUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw fail(
                $"The server's link to the next page ({nextUri?.Host ?? next}) is not on {currentUri?.Host ?? current}, " +
                "so it was not followed with your credentials.");
        }

        return next;
    }
}
