using System.Text.RegularExpressions;

namespace PPObjectSearch.Models;

/// <summary>
/// One row, as a person has it to hand: a link to it in a model-driven app or the Web API, or its
/// table and id typed or pasted together - "account 0f6a…", "account(0f6a…)".
/// </summary>
/// <param name="Table">A logical name, an entity set name (from a Web API link) or a type code (an old <c>etc=</c> link).</param>
/// <param name="Host">The environment a link points at; null for a table and id typed in.</param>
public sealed partial record RecordReference(string Table, Guid Id, string? Host = null)
{
    /// <summary>An old link names its table by number: "etc=1" is account.</summary>
    public int? TypeCode => int.TryParse(Table, out var code) ? code : null;

    public override string ToString() => $"{Table} {Id}";

    /// <summary>
    /// The row a link or a table-and-id names, or why there is none. A link has to carry its row:
    /// <c>main.aspx?etn=…&amp;id=…</c> (or <c>etc=</c>), or <c>/api/data/v9.2/accounts(…)</c>.
    /// </summary>
    public static bool TryParse(string? text, out RecordReference? reference, out string problem)
    {
        reference = null;
        var value = (text ?? string.Empty).Trim().Trim('"', '\'', '<', '>').Trim();

        if (value.Length == 0)
        {
            problem = "Paste a link to the row, or type its table's logical name and its id.";
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return TryParseLink(uri, out reference, out problem);
        }

        // Typed: one id and one table name, in either order, between any separators.
        var words = Separators().Split(value).Where(w => w.Length > 0).ToList();
        var ids = words.Select(w => Guid.TryParse(w, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        var names = words.Where(w => !Guid.TryParse(w, out _)).ToList();

        if (ids.Count != 1)
        {
            problem = ids.Count == 0 ? "There is no row id (a GUID) in that." : "That holds more than one row id - give one.";
            return false;
        }

        if (names.Count != 1 || !TableName().IsMatch(names[0]))
        {
            problem = names.Count == 0
                ? "Which table is the row in? Type its logical name before the id - account 0f6a…"
                : "Give the table's logical name and the row's id, and nothing else - account 0f6a…";
            return false;
        }

        reference = new RecordReference(names[0].ToLowerInvariant(), ids[0]);
        problem = string.Empty;
        return true;
    }

    private static bool TryParseLink(Uri uri, out RecordReference? reference, out string problem)
    {
        reference = null;

        // A Web API link: the table's entity set, then the row's id in brackets.
        if (WebApiRow().Match(Uri.UnescapeDataString(uri.AbsolutePath)) is { Success: true } api &&
            Guid.TryParse(api.Groups["id"].Value, out var apiId))
        {
            reference = new RecordReference(api.Groups["set"].Value.ToLowerInvariant(), apiId, uri.Host);
            problem = string.Empty;
            return true;
        }

        // An app link: the query, or after the # in a few older ones.
        var query = Query(uri.Query);
        foreach (var (key, val) in Query(uri.Fragment)) query.TryAdd(key, val);

        var table = query.GetValueOrDefault("etn") is { Length: > 0 } etn ? etn : query.GetValueOrDefault("etc");
        if (string.IsNullOrWhiteSpace(table) || !Guid.TryParse(query.GetValueOrDefault("id"), out var id))
        {
            problem = "That link is not to a row - open the row, then copy the address from the browser.";
            return false;
        }

        reference = new RecordReference(table.ToLowerInvariant(), id, uri.Host);
        problem = string.Empty;
        return true;
    }

    private static Dictionary<string, string> Query(string part)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in part.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            if (at <= 0) continue;
            values.TryAdd(Uri.UnescapeDataString(pair[..at]), Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' ')));
        }

        return values;
    }

    [GeneratedRegex(@"[\s,;:/|()\[\]=]+")]
    private static partial Regex Separators();

    /// <summary>A logical name, an entity set name or a type code.</summary>
    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*|[0-9]+)$")]
    private static partial Regex TableName();

    [GeneratedRegex(@"/api/data/v[0-9.]+/(?<set>[A-Za-z_][A-Za-z0-9_]*)\((?<id>\{?[0-9A-Fa-f-]{36}\}?)\)", RegexOptions.IgnoreCase)]
    private static partial Regex WebApiRow();
}
