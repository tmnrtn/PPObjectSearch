using System.Net.Http;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>A component with an on/off state the app can change.</summary>
public enum SwitchableKind
{
    /// <summary>A Power Automate cloud flow: Turn on / Turn off.</summary>
    CloudFlow,

    /// <summary>A classic workflow, business rule, action or business process flow: Activate / Deactivate.</summary>
    Process,

    /// <summary>A plug-in step: Enable / Disable.</summary>
    PluginStep
}

/// <summary>The state and status codes that make a component on or off.</summary>
public static class Switchable
{
    private const int ProcessType = 29;
    private const int PluginStepType = 92;

    public static SwitchableKind? KindOf(SolutionComponentItem item) => item.ComponentType switch
    {
        ProcessType when DetailsTabs.KindOf(item) == ObjectKind.CloudFlow => SwitchableKind.CloudFlow,
        ProcessType => SwitchableKind.Process,
        PluginStepType => SwitchableKind.PluginStep,
        _ => null
    };

    public static (string On, string Off) Verbs(SwitchableKind kind) => kind switch
    {
        SwitchableKind.CloudFlow => ("Turn on", "Turn off"),
        SwitchableKind.Process => ("Activate", "Deactivate"),
        _ => ("Enable", "Disable")
    };

    public static (string On, string Off) States(SwitchableKind kind) => kind switch
    {
        SwitchableKind.CloudFlow => ("On", "Off"),
        SwitchableKind.Process => ("Activated", "Draft"),
        _ => ("Enabled", "Disabled")
    };

    internal static string EntitySet(SwitchableKind kind) =>
        kind == SwitchableKind.PluginStep ? "sdkmessageprocessingsteps" : "workflows";

    internal static string IdColumn(SwitchableKind kind) =>
        kind == SwitchableKind.PluginStep ? "sdkmessageprocessingstepid" : "workflowid";

    /// <summary>
    /// A process is on in state 1 (Activated, status 2) and off in state 0 (Draft, status 1). A
    /// plug-in step is the other way round: on in state 0 (Enabled, status 1), off in state 1
    /// (Disabled, status 2).
    /// </summary>
    internal static (int State, int Status) Codes(SwitchableKind kind, bool on) => (kind, on) switch
    {
        (SwitchableKind.PluginStep, true) => (0, 1),
        (SwitchableKind.PluginStep, false) => (1, 2),
        (_, true) => (1, 2),
        _ => (0, 1)
    };

    internal static bool IsOn(SwitchableKind kind, int state) =>
        kind == SwitchableKind.PluginStep ? state == 0 : state == 1;
}

public sealed partial class DataverseClient
{
    /// <summary>Whether each component is on, by id. An id that is not found is left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, bool>> GetSwitchStatesAsync(
        SwitchableKind kind, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var states = new Dictionary<Guid, bool>();
        var set = Switchable.EntitySet(kind);
        var idColumn = Switchable.IdColumn(kind);

        foreach (var chunk in ids.Distinct().Chunk(25))
        {
            var filter = string.Join(" or ", chunk.Select(id => $"{idColumn} eq {id}"));
            var url = EnvironmentUrl + ApiPath + $"{set}?$select={idColumn},statecode&$filter={Uri.EscapeDataString(filter)}";

            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("value", out var rows)) continue;

            foreach (var row in rows.EnumerateArray())
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, idColumn), out var id)) continue;
                if (JsonHelper.GetInt(row, "statecode") is { } state) states[id] = Switchable.IsOn(kind, state);
            }
        }

        return states;
    }

    /// <summary>Turns a component on or off. Turning a cloud flow on can be refused by Dataverse
    /// when a connection it uses is broken; the message says so.</summary>
    public Task SetSwitchStateAsync(SwitchableKind kind, Guid id, bool on, CancellationToken ct = default)
    {
        var (state, status) = Switchable.Codes(kind, on);

        return UpdateRecordAsync(Switchable.EntitySet(kind), id,
            new Dictionary<string, object?> { ["statecode"] = state, ["statuscode"] = status }, ct: ct);
    }

    /// <summary>
    /// Sets the value an environment variable has in this environment: updates the value row where
    /// there is one, creates it where there is not. Null removes the value row, so the default applies.
    /// Returns the value row's id afterwards - null once removed.
    /// </summary>
    public async Task<Guid?> SetEnvironmentVariableValueAsync(
        EnvironmentVariableInfo variable, string? value, CancellationToken ct = default)
    {
        if (value is null)
        {
            if (variable.ValueId is { } existing) await DeleteRecordAsync("environmentvariablevalues", existing, ct: ct).ConfigureAwait(false);
            return null;
        }

        if (variable.ValueId is { } id)
        {
            await UpdateRecordAsync("environmentvariablevalues", id,
                new Dictionary<string, object?> { ["value"] = value }, ct: ct).ConfigureAwait(false);
            return id;
        }

        var newId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, EnvironmentUrl + ApiPath + "environmentvariablevalues")
        {
            Content = JsonContent(new Dictionary<string, object?>
            {
                ["environmentvariablevalueid"] = newId.ToString(),
                ["schemaname"] = variable.SchemaName,
                ["value"] = value,
                ["EnvironmentVariableDefinitionId@odata.bind"] = $"/environmentvariabledefinitions({variable.DefinitionId})"
            })
        };

        await SendAsync(request, $"set the value of {variable.SchemaName}", ct).ConfigureAwait(false);
        return newId;
    }
}
