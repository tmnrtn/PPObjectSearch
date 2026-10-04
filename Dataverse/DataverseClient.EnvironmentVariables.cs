using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    private const int SecretVariableType = 100000005;

    /// <summary>
    /// An environment variable as this environment resolves it: the definition's default, and the
    /// value record set here, if there is one. Accepts either the definition's id or a value
    /// record's id, since both appear in solutions.
    /// </summary>
    public async Task<EnvironmentVariableInfo> GetEnvironmentVariableAsync(
        Guid objectId, bool isValueRecord, CancellationToken ct = default)
    {
        var definitionId = objectId;

        if (isValueRecord)
        {
            using var valueRecord = await GetJsonAsync(
                EnvironmentUrl + ApiPath +
                $"environmentvariablevalues({objectId})?$select=_environmentvariabledefinitionid_value", ct).ConfigureAwait(false);

            if (!Guid.TryParse(JsonHelper.GetString(valueRecord.RootElement, "_environmentvariabledefinitionid_value"), out definitionId))
            {
                throw new DataverseException("This value record is not attached to an environment variable definition.");
            }
        }

        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath +
            $"environmentvariabledefinitions({definitionId})?$select=schemaname,displayname,type,defaultvalue,description" +
            "&$expand=environmentvariabledefinition_environmentvariablevalue($select=environmentvariablevalueid,value)",
            ct, Annotations.Formatted).ConfigureAwait(false);

        var root = doc.RootElement;
        var type = JsonHelper.GetInt(root, "type");

        string? current = null;
        Guid? valueId = null;
        var valueCount = 0;
        if (root.TryGetProperty("environmentvariabledefinition_environmentvariablevalue", out var values) &&
            values.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                valueCount++;
                if (valueCount == 1)
                {
                    current = JsonHelper.GetString(value, "value");
                    if (Guid.TryParse(JsonHelper.GetString(value, "environmentvariablevalueid"), out var id)) valueId = id;
                }
            }
        }

        return new EnvironmentVariableInfo
        {
            SchemaName = JsonHelper.GetString(root, "schemaname") ?? string.Empty,
            DefinitionId = definitionId,
            ValueId = valueId,
            Type = type,
            DisplayName = JsonHelper.GetString(root, "displayname"),
            Description = JsonHelper.GetString(root, "description"),
            TypeLabel = JsonHelper.GetString(root, "type@" + Annotations.Formatted) ?? type?.ToString() ?? "Unknown",
            IsSecret = type == SecretVariableType,
            DefaultValue = JsonHelper.GetString(root, "defaultvalue"),
            CurrentValue = current,
            HasCurrentValue = valueCount > 0,
            ValueRecordCount = valueCount
        };
    }
}
