using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// Compares two component definitions - the msdyn_componentjson blobs Dataverse returns for a
/// solution layer - property by property. Used both for one layer against the layer beneath it
/// and for the same component in two different environments; the operation is identical, only
/// the two blobs differ.
/// </summary>
public static class DefinitionDiff
{
    /// <summary>
    /// Returns the properties that differ, or null when neither side could be read as a
    /// definition at all - callers word that case themselves, since what it means depends on
    /// where the definitions came from.
    /// </summary>
    /// <param name="prioritize">
    /// Property names to sort to the top, where something authoritative already knows which ones
    /// matter - Dataverse's own msdyn_changes list, for instance.
    /// </param>
    public static IReadOnlyList<DefinitionChange>? Compare(
        string? beforeJson,
        string? afterJson,
        IReadOnlyList<string>? prioritize = null)
    {
        var before = ReadProperties(beforeJson);
        var after = ReadProperties(afterJson);

        if (before is null && after is null) return null;

        before ??= new Dictionary<string, string>(StringComparer.Ordinal);
        after ??= new Dictionary<string, string>(StringComparer.Ordinal);

        var changes = new List<DefinitionChange>();

        foreach (var (name, value) in after)
        {
            if (!before.TryGetValue(name, out var old))
            {
                changes.Add(new DefinitionChange
                {
                    PropertyName = name,
                    Kind = DefinitionChangeKind.Added,
                    CurrentValue = value
                });
                continue;
            }

            if (!string.Equals(old, value, StringComparison.Ordinal))
            {
                changes.Add(new DefinitionChange
                {
                    PropertyName = name,
                    Kind = DefinitionChangeKind.Modified,
                    PreviousValue = old,
                    CurrentValue = value
                });
            }
        }

        foreach (var (name, value) in before.Where(p => !after.ContainsKey(p.Key)))
        {
            changes.Add(new DefinitionChange
            {
                PropertyName = name,
                Kind = DefinitionChangeKind.Removed,
                PreviousValue = value
            });
        }

        var flagged = prioritize is null
            ? null
            : new HashSet<string>(prioritize, StringComparer.OrdinalIgnoreCase);

        return changes
            .OrderBy(c => flagged is not null && flagged.Contains(c.PropertyName) ? 0 : 1)
            .ThenBy(c => c.PropertyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Flattens a definition to property path -> rendered value. Nested objects are walked so a
    /// change buried inside one shows up as its own row rather than as a single unreadable blob.
    /// </summary>
    private static Dictionary<string, string>? ReadProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            Flatten(doc.RootElement, string.Empty, result, 0);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into, int depth)
    {
        foreach (var property in element.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;

            // Deep nesting stops being readable as separate rows, so below the cutoff the
            // subtree is kept whole and diffed as one value.
            if (property.Value.ValueKind == JsonValueKind.Object && depth < 3)
            {
                Flatten(property.Value, path, into, depth + 1);
                continue;
            }

            into[path] = Render(property.Value);
        }
    }

    private static string Render(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "(null)",
        JsonValueKind.String => element.GetString() ?? string.Empty,
        _ => element.GetRawText()
    };
}
