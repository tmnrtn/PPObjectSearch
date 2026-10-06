using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>
    /// The configuration rows whose audit history the timeline offers, by component type: a flow's
    /// on/off state, a plug-in step's enabled state, an environment variable's values.
    /// </summary>
    private static readonly Dictionary<int, string> AuditedTables = new()
    {
        [29] = "workflow",
        [92] = "sdkmessageprocessingstep",
        [380] = "environmentvariabledefinition",
        [381] = "environmentvariablevalue"
    };

    public static bool HasAuditHistory(int componentType) => AuditedTables.ContainsKey(componentType);

    public static string? AuditTable(int componentType) => AuditedTables.GetValueOrDefault(componentType);

    /// <summary>Whether auditing is on for the environment and for one table. Unreadable parts are null.</summary>
    public async Task<AuditStatus> GetAuditStatusAsync(string table, CancellationToken ct = default)
    {
        bool? environment = null, tableOn = null;

        try
        {
            using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + "organizations?$select=isauditenabled", ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.GetArrayLength() > 0)
            {
                environment = JsonHelper.GetBool(value[0], "isauditenabled");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Services.Log.Warn("Environment audit setting could not be read", ex);
        }

        try
        {
            using var doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + $"EntityDefinitions(LogicalName='{Uri.EscapeDataString(table)}')?$select=IsAuditEnabled",
                ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("IsAuditEnabled", out var audit) && audit.ValueKind == JsonValueKind.Object)
            {
                tableOn = JsonHelper.GetBool(audit, "Value");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Services.Log.Warn($"Audit setting of {table} could not be read", ex);
        }

        return new AuditStatus(environment, tableOn);
    }

    /// <summary>
    /// The audit history of one component's configuration row, newest first. For an environment
    /// variable it covers the definition and each of its values, since the current value is a row
    /// of its own.
    /// </summary>
    public async Task<IReadOnlyList<AuditRecord>> GetAuditHistoryAsync(
        int componentType, Guid objectId, CancellationToken ct = default)
    {
        if (AuditTable(componentType) is not { } table) return Array.Empty<AuditRecord>();

        var rows = new List<(string Table, Guid Id, string? Label)> { (table, objectId, null) };

        if (componentType == 380)
        {
            var values = await ReadRowsAsync(
                EnvironmentUrl + ApiPath + "environmentvariablevalues?$select=environmentvariablevalueid&$filter=" +
                $"_environmentvariabledefinitionid_value eq {objectId}",
                row => ParseGuid(JsonHelper.GetString(row, "environmentvariablevalueid")) is { } id ? Tuple.Create(id) : null,
                ct).ConfigureAwait(false);

            rows[0] = (table, objectId, "Definition");
            rows.AddRange(values.Select(v => ("environmentvariablevalue", v.Item1, (string?)"Current value")));
        }

        var records = new List<AuditRecord>();

        foreach (var (rowTable, id, label) in rows)
        {
            var found = await ReadRowsAsync(
                EnvironmentUrl + ApiPath + "audits?$select=createdon,action,operation,_userid_value,changedata,attributemask" +
                $"&$filter=_objectid_value eq {id}&$orderby=createdon desc",
                row => ReadAudit(row, rowTable, label), ct).ConfigureAwait(false);
            records.AddRange(found);
        }

        return records.OrderByDescending(r => r.When).ToList();
    }

    private static AuditRecord? ReadAudit(JsonElement row, string table, string? label)
    {
        if (JsonHelper.GetDate(row, "createdon") is not { } when) return null;

        return new AuditRecord
        {
            When = when,
            By = Label(row, "_userid_value"),
            Action = Label(row, "action") ?? Label(row, "operation"),
            Row = label,
            Changes = ParseChangeData(JsonHelper.GetString(row, "changedata"), table)
        };
    }

    /// <summary>
    /// The columns an audit record changed, from its change data:
    /// <c>{"changedAttributes":[{"logicalName":..,"oldValue":..,"newValue":..}]}</c>. State and
    /// status codes are named for the tables the timeline audits; anything unreadable is kept as text.
    /// </summary>
    internal static IReadOnlyList<AuditChange> ParseChangeData(string? changeData, string table)
    {
        if (string.IsNullOrWhiteSpace(changeData)) return Array.Empty<AuditChange>();

        try
        {
            using var doc = JsonDocument.Parse(changeData);
            if (!doc.RootElement.TryGetProperty("changedAttributes", out var attributes) ||
                attributes.ValueKind != JsonValueKind.Array)
            {
                return [new AuditChange("(change)", null, changeData.Trim())];
            }

            return attributes.EnumerateArray()
                .Select(a =>
                {
                    var column = JsonHelper.GetString(a, "logicalName") ?? "(column)";
                    return new AuditChange(column,
                        NameValue(table, column, AuditValue(a, "oldValue")),
                        NameValue(table, column, AuditValue(a, "newValue")));
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [new AuditChange("(change)", null, changeData.Trim())];
        }
    }

    private static string? AuditValue(JsonElement attribute, string name) =>
        attribute.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => value.GetString() is { Length: > 0 } s ? s : null,
                _ => value.ToString()
            }
            : null;

    private static string? NameValue(string table, string column, string? value) => (table, column, value) switch
    {
        (_, _, null) => null,
        ("workflow", "statecode", "0") => "Off (draft)",
        ("workflow", "statecode", "1") => "On (activated)",
        ("workflow", "statecode", "2") => "Suspended",
        ("sdkmessageprocessingstep", "statecode", "0") => "Enabled",
        ("sdkmessageprocessingstep", "statecode", "1") => "Disabled",
        (_, "statecode", "0") => "Active",
        (_, "statecode", "1") => "Inactive",
        _ => value
    };
}
