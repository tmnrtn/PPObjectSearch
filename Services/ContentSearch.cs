using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>One place a term appears inside a component's definition.</summary>
public sealed class ContentHit
{
    public required SolutionComponentItem Item { get; init; }

    /// <summary>Which body: "clientdata", "formxml", "fetchxml"...</summary>
    public required string Part { get; init; }

    public required int Line { get; init; }

    /// <summary>The line around the match, split so the match itself can be shown highlighted.</summary>
    public required string Before { get; init; }
    public required string Match { get; init; }
    public required string After { get; init; }

    public string Component => Item.PrimaryLabel;
    public string ComponentType => Item.ComponentTypeName;
    public string Where => $"{Part}, line {Line:N0}";
    public string Snippet => Before + Match + After;
}

/// <summary>
/// Finds a term inside component definitions - what metadata search cannot see: a column used in
/// a flow expression, a script, a view's FetchXML or a step's filtering attributes. Case-insensitive.
/// </summary>
public static class ContentSearch
{
    /// <summary>Characters of context kept on either side of a match.</summary>
    public const int Context = 60;

    /// <summary>Past this many hits in one body, the rest of it is skipped - a term in every line says enough.</summary>
    public const int MaxHitsPerBody = 20;

    public static IReadOnlyList<ContentHit> Search(IEnumerable<DefinitionBody> bodies, string term)
    {
        var hits = new List<ContentHit>();
        term = term.Trim();
        if (term.Length == 0) return hits;

        foreach (var body in bodies)
        {
            var found = 0;
            var line = 1;
            var lineStart = 0;
            var scanned = 0;
            var text = body.Text;

            for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                 at >= 0 && found < MaxHitsPerBody;
                 at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
            {
                // Count lines only as far as this match, carrying on from the last one.
                for (; scanned < at; scanned++)
                {
                    if (text[scanned] != '\n') continue;
                    line++;
                    lineStart = scanned + 1;
                }

                var lineEnd = text.IndexOf('\n', at);
                if (lineEnd < 0) lineEnd = text.Length;

                var from = Math.Max(lineStart, at - Context);
                var to = Math.Min(lineEnd, at + term.Length + Context);

                hits.Add(new ContentHit
                {
                    Item = body.Item,
                    Part = body.Part,
                    Line = line,
                    Before = (from > lineStart ? "…" : string.Empty) + text[from..at].TrimStart(),
                    Match = text.Substring(at, term.Length),
                    After = text[(at + term.Length)..to].TrimEnd('\r') + (to < lineEnd ? "…" : string.Empty)
                });

                found++;
            }
        }

        return hits;
    }
}

/// <summary>
/// Bodies already read, kept per component with the modified date they were read at - so a second
/// search costs nothing, and a component changed since is read again.
/// </summary>
public sealed class DefinitionBodyCache
{
    private readonly Dictionary<Guid, (DateTimeOffset? ModifiedOn, IReadOnlyList<DefinitionBody> Bodies)> _cache = new();
    private readonly object _sync = new();

    /// <summary>The components whose bodies are not cached, or were cached at an older modified date.</summary>
    public IReadOnlyList<SolutionComponentItem> Stale(IEnumerable<SolutionComponentItem> items)
    {
        lock (_sync)
        {
            return items.Where(i => !_cache.TryGetValue(i.ObjectId, out var cached) || cached.ModifiedOn != i.ModifiedOn).ToList();
        }
    }

    public void Store(IEnumerable<SolutionComponentItem> read, IEnumerable<DefinitionBody> bodies)
    {
        var byItem = bodies.GroupBy(b => b.Item.ObjectId).ToDictionary(g => g.Key, g => (IReadOnlyList<DefinitionBody>)g.ToList());

        lock (_sync)
        {
            // A component with no text body is remembered too, so it is not asked for again.
            foreach (var item in read)
            {
                _cache[item.ObjectId] = (item.ModifiedOn,
                    byItem.TryGetValue(item.ObjectId, out var found) ? found : Array.Empty<DefinitionBody>());
            }
        }
    }

    public IReadOnlyList<DefinitionBody> Bodies(IEnumerable<SolutionComponentItem> items)
    {
        lock (_sync)
        {
            // Rebound to the rows passed in, so a hit opens the component as it is listed now.
            return items.SelectMany(i => _cache.TryGetValue(i.ObjectId, out var cached)
                    ? cached.Bodies.Select(b => b with { Item = i })
                    : Enumerable.Empty<DefinitionBody>())
                .ToList();
        }
    }
}
