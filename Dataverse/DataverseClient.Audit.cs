using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>
    /// The configuration rows whose audit history the timeline offers, by component type: a flow's
    /// on/off state, a plug-in step's enabled state, an environment variable's values.
    /// </summary>
    private const string WorkflowTable = "workflow";
    private const string StateColumn = "statecode";

    private static readonly Dictionary<int, string> AuditedTables = new()
    {
        [29] = WorkflowTable,
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

    /// <summary>The most changes of one row whose values are read as the app shows them; older ones keep their stored values.</summary>
    internal const int RecordAuditDetailLimit = 250;

    /// <summary>How many changes' values are read at once.</summary>
    private const int AuditDetailReads = 4;

    /// <summary>
    /// The audit history of any one row, newest first, with each change's values as the app shows
    /// them: a lookup's name, a choice's label, a date in the user's format. Those come from
    /// RetrieveAuditDetails, one call per change, so only the newest
    /// <see cref="RecordAuditDetailLimit"/> are read that way; the rest - and any whose details
    /// cannot be read - keep the stored change data.
    /// </summary>
    /// <param name="onDetailsRead">How many changes have their values read so far, of how many.</param>
    public async Task<IReadOnlyList<AuditRecord>> GetRecordAuditHistoryAsync(
        string table, Guid id, Action<int, int>? onDetailsRead = null, CancellationToken ct = default)
    {
        var rows = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + "audits?$select=auditid,createdon,action,operation,_userid_value,changedata" +
            $"&$filter=_objectid_value eq {id}&$orderby=createdon desc",
            row => ParseGuid(JsonHelper.GetString(row, "auditid")) is { } auditId && ReadAudit(row, table, null) is { } record
                ? Tuple.Create(auditId, record)
                : null,
            ct).ConfigureAwait(false);

        var records = rows.Select(r => r.Item2).ToArray();
        var detailed = Math.Min(records.Length, RecordAuditDetailLimit);
        var done = 0;

        using var gate = new SemaphoreSlim(AuditDetailReads);

        await Task.WhenAll(Enumerable.Range(0, detailed).Select(async i =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await GetAuditDetailChangesAsync(rows[i].Item1, ct).ConfigureAwait(false) is { } changes)
                {
                    records[i] = records[i].WithChanges(changes);
                }
            }
            finally
            {
                gate.Release();
                onDetailsRead?.Invoke(Interlocked.Increment(ref done), detailed);
            }
        })).ConfigureAwait(false);

        return records.OrderByDescending(r => r.When).ToList();
    }

    /// <summary>
    /// One change's columns with their old and new values, formatted. Null where the details cannot
    /// be read, or are not a column change - a share or an association - so the change data stays.
    /// </summary>
    private async Task<IReadOnlyList<AuditChange>?> GetAuditDetailChangesAsync(Guid auditId, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + $"audits({auditId})/Microsoft.Dynamics.CRM.RetrieveAuditDetails",
                ct, Annotations.All, maxPageSize: false).ConfigureAwait(false);

            return doc.RootElement.TryGetProperty("AuditDetail", out var detail) ? ParseAuditDetail(detail) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Services.Log.Warn($"Audit details of {auditId} could not be read", ex);
            return null;
        }
    }

    /// <summary>
    /// The columns an AttributeAuditDetail's old and new values differ in. Each value is the
    /// formatted one where Dataverse gives one; a lookup's <c>_name_value</c> is named as its column.
    /// </summary>
    internal static IReadOnlyList<AuditChange>? ParseAuditDetail(JsonElement detail)
    {
        var hasOld = detail.TryGetProperty("OldValue", out var oldRow) && oldRow.ValueKind == JsonValueKind.Object;
        var hasNew = detail.TryGetProperty("NewValue", out var newRow) && newRow.ValueKind == JsonValueKind.Object;
        if (!hasOld && !hasNew) return null;

        var before = hasOld ? AuditValues(oldRow) : new Dictionary<string, string?>();
        var after = hasNew ? AuditValues(newRow) : new Dictionary<string, string?>();

        return after.Keys.Concat(before.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => new AuditChange(c, before.GetValueOrDefault(c), after.GetValueOrDefault(c)))
            .Where(c => !string.Equals(c.OldValue, c.NewValue, StringComparison.Ordinal))
            .ToList();
    }

    private static Dictionary<string, string?> AuditValues(JsonElement row)
    {
        var raw = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var formatted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in row.EnumerateObject())
        {
            var at = property.Name.IndexOf('@', StringComparison.Ordinal);
            if (at == 0) continue;

            if (at > 0)
            {
                if (property.Name.EndsWith(FormattedValueSuffix, StringComparison.Ordinal))
                {
                    formatted[property.Name[..at]] = property.Value.GetString();
                }
                continue;
            }

            raw[property.Name] = ReadValue(property.Value);
        }

        return raw.ToDictionary(
            p => ColumnOf(p.Key),
            p => formatted.GetValueOrDefault(p.Key) is { Length: > 0 } label ? label : p.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A lookup's <c>_parentid_value</c> is the column <c>parentid</c>.</summary>
    internal static string ColumnOf(string name) =>
        name.Length > 7 && name.StartsWith('_') && name.EndsWith("_value", StringComparison.Ordinal) ? name[1..^6] : name;

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

    private static string? AuditValue(JsonElement attribute, string name)
    {
        if (!attribute.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString() is { Length: > 0 } s ? s : null,
            _ => value.ToString()
        };
    }

    private static string? NameValue(string table, string column, string? value) => (table, column, value) switch
    {
        (_, _, null) => null,
        (WorkflowTable, StateColumn, "0") => "Off (draft)",
        (WorkflowTable, StateColumn, "1") => "On (activated)",
        (WorkflowTable, StateColumn, "2") => "Suspended",
        ("sdkmessageprocessingstep", StateColumn, "0") => "Enabled",
        ("sdkmessageprocessingstep", StateColumn, "1") => "Disabled",
        (_, StateColumn, "0") => "Active",
        (_, StateColumn, "1") => "Inactive",
        _ => value
    };
}
