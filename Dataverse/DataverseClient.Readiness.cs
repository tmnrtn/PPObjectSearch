using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>A component a solution needs but does not contain, and what in the solution needs it.</summary>
public sealed record MissingDependency(Guid RequiredId, int RequiredType, Guid DependentId, int DependentType);

/// <summary>Whether a flow is on, and whether its owner can still run it.</summary>
public sealed record FlowOwnership(bool IsOn, string? OwnerName, bool OwnerDisabled);

public sealed partial class DataverseClient
{
    /// <summary>
    /// The components the solution depends on without containing them (RetrieveMissingDependencies).
    /// Asked of the environment the solution comes from; whether the target has each one is a
    /// separate question.
    /// </summary>
    public async Task<IReadOnlyList<MissingDependency>> GetMissingDependenciesAsync(
        string solutionUniqueName, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  $"RetrieveMissingDependencies(SolutionUniqueName=@name)?@name='{Escape(solutionUniqueName)}'";

        using var doc = await GetJsonAsync(url, ct, maxPageSize: false).ConfigureAwait(false);
        var results = new List<MissingDependency>();

        // The response wraps the dependency rows; accept either shape it has been seen in.
        var rows = doc.RootElement.TryGetProperty("EntityCollection", out var collection)
            ? collection.ValueKind == JsonValueKind.Array ? collection
              : collection.TryGetProperty("Entities", out var entities) ? entities : default
            : doc.RootElement.TryGetProperty("value", out var value) ? value : default;

        if (rows.ValueKind != JsonValueKind.Array) return results;

        foreach (var row in rows.EnumerateArray())
        {
            if (!Guid.TryParse(JsonHelper.GetString(row, "requiredcomponentobjectid"), out var required)) continue;
            Guid.TryParse(JsonHelper.GetString(row, "dependentcomponentobjectid"), out var dependent);

            results.Add(new MissingDependency(
                required,
                JsonHelper.GetInt(row, "requiredcomponenttype") ?? 0,
                dependent,
                JsonHelper.GetInt(row, "dependentcomponenttype") ?? 0));
        }

        return results;
    }

    /// <summary>
    /// Which of these component ids exist in this environment. Every component is in at least the
    /// default solution, so its solution component rows say whether it is here at all.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> GetExistingComponentIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var found = new HashSet<Guid>();

        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            var url = EnvironmentUrl + ApiPath + "solutioncomponents?$select=objectid&$filter=" +
                      InFilter("objectid", chunk.Select(id => id.ToString()));

            while (url.Length > 0)
            {
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("value", out var value))
                {
                    foreach (var row in value.EnumerateArray())
                    {
                        if (Guid.TryParse(JsonHelper.GetString(row, "objectid"), out var id)) found.Add(id);
                    }
                }

                url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
            }
        }

        return found;
    }

    /// <summary>An environment variable found by schema name - how the same variable is matched across environments.</summary>
    public async Task<EnvironmentVariableInfo?> GetEnvironmentVariableBySchemaNameAsync(
        string schemaName, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath +
            $"environmentvariabledefinitions?$select=environmentvariabledefinitionid&$filter=schemaname eq '{Escape(schemaName)}'",
            ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0) return null;
        if (!Guid.TryParse(JsonHelper.GetString(value[0], "environmentvariabledefinitionid"), out var id)) return null;

        return await GetEnvironmentVariableAsync(id, isValueRecord: false, ct).ConfigureAwait(false);
    }

    /// <summary>Each flow's state and its owner - a disabled owner cannot turn a flow on or run it.</summary>
    public async Task<IReadOnlyDictionary<Guid, FlowOwnership>> GetFlowOwnershipAsync(
        IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var results = new Dictionary<Guid, FlowOwnership>();

        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            var url = EnvironmentUrl + ApiPath +
                      "workflows?$select=workflowid,statecode&$expand=owninguser($select=fullname,isdisabled)&$filter=" +
                      InFilter("workflowid", chunk.Select(id => id.ToString()));

            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("value", out var value)) continue;

            foreach (var row in value.EnumerateArray())
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, "workflowid"), out var id)) continue;

                var owner = row.TryGetProperty("owninguser", out var user) && user.ValueKind == JsonValueKind.Object ? user : default;
                results[id] = new FlowOwnership(
                    JsonHelper.GetInt(row, "statecode") == 1,
                    JsonHelper.GetString(owner, "fullname"),
                    JsonHelper.GetBool(owner, "isdisabled") ?? false);
            }
        }

        return results;
    }

    /// <summary>Plug-in assembly names and versions by id.</summary>
    public async Task<IReadOnlyDictionary<Guid, (string Name, string? Version)>> GetPluginAssemblyVersionsAsync(
        IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var results = new Dictionary<Guid, (string, string?)>();

        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            var url = EnvironmentUrl + ApiPath + "pluginassemblies?$select=pluginassemblyid,name,version&$filter=" +
                      InFilter("pluginassemblyid", chunk.Select(id => id.ToString()));

            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("value", out var value)) continue;

            foreach (var row in value.EnumerateArray())
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, "pluginassemblyid"), out var id)) continue;
                results[id] = (JsonHelper.GetString(row, "name") ?? id.ToString(), JsonHelper.GetString(row, "version"));
            }
        }

        return results;
    }
}
