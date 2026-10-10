using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

public enum UndoMethod
{
    /// <summary>Re-create a deleted row, with its original id.</summary>
    Create,

    /// <summary>Write back the values an update replaced.</summary>
    Update,

    /// <summary>Remove a row the run created.</summary>
    Delete
}

/// <summary>
/// The write that reverses one write, worked out from the row as it was just before - so it can be
/// kept in the run log and replayed later, without needing the comparison that produced it.
/// </summary>
public sealed record UndoStep(
    UndoMethod Method,
    string EntitySet,
    Guid Id,
    JsonObject? Body = null,
    // A row comes back in its default state; a row that was inactive needs its state set after.
    JsonObject? Then = null)
{
    [JsonIgnore]
    public string Label => Method switch
    {
        UndoMethod.Create => $"re-create {EntitySet}({Id})",
        UndoMethod.Update => $"restore {Body?.Count ?? 0} column(s) of {EntitySet}({Id})",
        _ => $"delete {EntitySet}({Id})"
    };
}

/// <summary>Builds and applies <see cref="UndoStep"/>s.</summary>
public static class WriteUndo
{
    private const string LookupTable = "@Microsoft.Dynamics.CRM.lookuplogicalname";
    private const string Navigation = "@Microsoft.Dynamics.CRM.associatednavigationproperty";
    private const string Bind = "@odata.bind";

    /// <summary>A row this run created goes again.</summary>
    public static UndoStep ForCreate(string entitySet, Guid id) => new(UndoMethod.Delete, entitySet, id);

    /// <summary>
    /// Every column the update wrote, set back to what the snapshot held. A lookup is bound back to
    /// the row it pointed at, or cleared if it pointed at nothing.
    /// </summary>
    public static UndoStep ForUpdate(
        string entitySet,
        Guid id,
        IReadOnlyDictionary<string, object?> written,
        JsonObject before,
        Func<string, string?> entitySetOf)
    {
        var body = new JsonObject();

        foreach (var key in written.Keys)
        {
            if (key.EndsWith(Bind, StringComparison.Ordinal))
            {
                var navigation = key[..^Bind.Length];
                body[key] = BindOf(before, FindLookup(before, navigation), entitySetOf);
                continue;
            }

            body[key] = before[key]?.DeepClone();
        }

        return new UndoStep(UndoMethod.Update, entitySet, id, body);
    }

    /// <summary>
    /// The row again, with its original id, from every column the target accepts on a create.
    /// Columns Dataverse fills in itself - created on, modified by - come back as of the undo.
    /// </summary>
    public static UndoStep ForDelete(
        string entitySet,
        Guid id,
        string primaryIdAttribute,
        JsonObject before,
        IEnumerable<EntityColumn> columns,
        Func<string, string?> entitySetOf)
    {
        var body = new JsonObject { [primaryIdAttribute] = id.ToString() };

        foreach (var column in columns)
        {
            if (column.IsPrimaryId || !column.IsValidForCreate) continue;

            if (column.IsLookup)
            {
                AddBind(body, before, column.SelectName, entitySetOf);
                continue;
            }

            if (before[column.LogicalName] is not { } value) continue;
            body[column.LogicalName] = value.DeepClone();
        }

        var then = DeferState(body, columns);
        return new UndoStep(UndoMethod.Create, entitySet, id, body, then);
    }

    /// <summary>Binds a lookup in a create body to the row it pointed at; nothing where it was empty.</summary>
    private static void AddBind(JsonObject body, JsonObject before, string property, Func<string, string?> entitySetOf)
    {
        if (before[property] is null) return;

        // The annotation names the navigation property, which for a lookup that can point
        // at several tables is specific to the one this row points at.
        var navigation = before[property + Navigation]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(navigation)) return;

        if (BindOf(before, property, entitySetOf) is { } bind) body[navigation + Bind] = bind;
    }

    /// <summary>
    /// A row is created in its default state, and a status belonging to any other is refused. So a
    /// row that was in another state is created without its state and status, and these are
    /// returned to be written once it exists. Null when the create can carry them.
    /// </summary>
    private static JsonObject? DeferState(JsonObject body, IEnumerable<EntityColumn> columns)
    {
        var state = columns.FirstOrDefault(c => c.TypeName == "StateType")?.LogicalName;
        var status = columns.FirstOrDefault(c => c.TypeName == "StatusType")?.LogicalName;

        if (state is null || body[state] is not JsonValue stateValue ||
            !stateValue.TryGetValue<long>(out var code) || code == 0)
        {
            return null;
        }

        var then = new JsonObject { [state] = code };
        body.Remove(state);

        if (status is not null && body[status] is { } statusValue)
        {
            then[status] = statusValue.DeepClone();
            body.Remove(status);
        }

        return then;
    }

    /// <summary>Makes the reversing write. A create carries the original id, so it cannot land twice.</summary>
    public static async Task ApplyAsync(DataverseClient client, UndoStep step, CancellationToken ct = default)
    {
        switch (step.Method)
        {
            case UndoMethod.Delete:
                await client.DeleteRecordAsync(step.EntitySet, step.Id, ct: ct).ConfigureAwait(false);
                break;

            case UndoMethod.Update:
                await client.UpdateRecordAsync(step.EntitySet, step.Id, Values(step.Body), ct: ct).ConfigureAwait(false);
                break;

            default:
                await client.CreateRecordAsync(step.EntitySet, step.Id, Values(step.Body), ct).ConfigureAwait(false);
                if (step.Then is { Count: > 0 })
                {
                    await client.UpdateRecordAsync(step.EntitySet, step.Id, Values(step.Then), ct: ct).ConfigureAwait(false);
                }
                break;
        }
    }

    /// <summary>A write body as the client takes it; each value is serialised as the JSON it was.</summary>
    private static Dictionary<string, object?> Values(JsonObject? body) =>
        body is null
            ? new Dictionary<string, object?>()
            : body.ToDictionary(p => p.Key, p => (object?)p.Value?.DeepClone());

    /// <summary>The lookup property in a snapshot that binds through <paramref name="navigation"/>.</summary>
    private static string? FindLookup(JsonObject row, string navigation)
    {
        foreach (var (key, value) in row)
        {
            if (!key.EndsWith(Navigation, StringComparison.Ordinal)) continue;
            if (value is JsonValue v && v.TryGetValue<string>(out var nav) &&
                string.Equals(nav, navigation, StringComparison.OrdinalIgnoreCase))
            {
                return key[..^Navigation.Length];
            }
        }

        // Dataverse only annotates a lookup that has a value, so none here means it was empty.
        return null;
    }

    private static JsonValue? BindOf(JsonObject row, string? property, Func<string, string?> entitySetOf)
    {
        if (property is null || row[property] is not JsonValue value || !value.TryGetValue<string>(out var raw) ||
            !Guid.TryParse(raw, out var id))
        {
            return null;
        }

        var table = row[property + LookupTable]?.GetValue<string>();
        var set = table is null ? null : entitySetOf(table);

        if (string.IsNullOrWhiteSpace(set))
        {
            throw new DataverseException(
                $"'{property}' points at a {table ?? "table"} row whose entity set is not known, so it cannot be restored.");
        }

        return JsonValue.Create($"/{set}({id})");
    }

    internal static JsonObject? ToJson(IReadOnlyDictionary<string, object?>? values) =>
        values is null ? null : JsonSerializer.SerializeToNode(values)?.AsObject();
}
