using System.Text;
using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>One searchable body of a component: a flow's definition, a form's XML, a view's FetchXML...</summary>
public sealed record DefinitionBody(SolutionComponentItem Item, string Part, string Text);

public sealed partial class DataverseClient
{
    /// <summary>One way of reading bodies: the table, its id column, and which columns hold text.</summary>
    private sealed record BodySource(string EntitySet, string IdColumn, int ChunkSize, params string[] Columns);

    private static readonly Dictionary<int, BodySource> BodySources = new()
    {
        [29] = new("workflows", "workflowid", 10, "clientdata", "xaml"),
        [61] = new("webresourceset", "webresourceid", 10, "content"),
        [60] = new("systemforms", "formid", 20, "formxml"),
        [26] = new("savedqueries", "savedqueryid", 25, "fetchxml", "layoutxml"),
        [92] = new("sdkmessageprocessingsteps", "sdkmessageprocessingstepid", 50, "filteringattributes", "configuration"),
        [62] = new("sitemaps", "sitemapid", 10, "sitemapxml")
    };

    /// <summary>Whether a component has a body that content search reads.</summary>
    public static bool HasSearchableBody(SolutionComponentItem item) => BodySources.ContainsKey(item.ComponentType);

    /// <summary>
    /// The text bodies of these components, read a chunk of rows per request for each kind - never
    /// one request per component. Binary web resources (images) have nothing to search and are left out.
    /// </summary>
    public async Task<IReadOnlyList<DefinitionBody>> GetDefinitionBodiesAsync(
        IReadOnlyList<SolutionComponentItem> items, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var bodies = new List<DefinitionBody>();
        var done = 0;

        foreach (var group in items.Where(i => i.ObjectId != Guid.Empty && BodySources.ContainsKey(i.ComponentType))
                                   .GroupBy(i => i.ComponentType))
        {
            var source = BodySources[group.Key];
            var byId = group.GroupBy(i => i.ObjectId).ToDictionary(g => g.Key, g => g.First());
            var select = string.Join(",", source.Columns.Prepend(source.IdColumn)) +
                         (group.Key == 61 ? ",webresourcetype" : string.Empty);

            foreach (var chunk in byId.Keys.Chunk(source.ChunkSize))
            {
                ct.ThrowIfCancellationRequested();

                var url = EnvironmentUrl + ApiPath + $"{source.EntitySet}?$select={select}&$filter=" +
                          InFilter(source.IdColumn, chunk.Select(id => id.ToString()));

                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("value", out var value))
                {
                    foreach (var row in value.EnumerateArray())
                    {
                        if (!Guid.TryParse(JsonHelper.GetString(row, source.IdColumn), out var id) ||
                            !byId.TryGetValue(id, out var item))
                        {
                            continue;
                        }

                        foreach (var column in source.Columns)
                        {
                            if (BodyText(row, column, group.Key) is { Length: > 0 } text)
                            {
                                bodies.Add(new DefinitionBody(item, column, text));
                            }
                        }
                    }
                }

                done += chunk.Length;
                progress?.Report(done);
            }
        }

        return bodies;
    }

    private static string? BodyText(JsonElement row, string column, int componentType)
    {
        var raw = JsonHelper.GetString(row, column);
        if (string.IsNullOrEmpty(raw) || componentType != 61) return raw;

        if (!IsTextWebResourceType(JsonHelper.GetInt(row, "webresourcetype") ?? 0)) return null;

        try
        {
            return new UTF8Encoding(false).GetString(Convert.FromBase64String(raw)).TrimStart('﻿');
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
