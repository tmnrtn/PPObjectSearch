using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>A table privilege: which action it is and its id.</summary>
public sealed record TablePrivilege(string Action, Guid PrivilegeId, string Name);

/// <summary>A security role (its root definition) that grants a privilege.</summary>
public sealed record PrivilegeGrant(Guid RootRoleId, string RoleName);

/// <summary>Who a field security profile grants a secured column to, and what they may do with it.</summary>
public sealed record FieldPermissionGrant(
    Guid ProfileId, string ProfileName, bool CanRead, bool CanCreate, bool CanUpdate,
    IReadOnlyList<string> Users, IReadOnlyList<string> Teams)
{
    public string UsersLabel => string.Join(", ", Users);
    public string TeamsLabel => string.Join(", ", Teams);
}

public sealed partial class DataverseClient
{
    /// <summary>PrivilegeType codes as entity metadata reports them.</summary>
    private static readonly Dictionary<int, string> PrivilegeActions = new()
    {
        [1] = "Create", [2] = "Read", [3] = "Write", [4] = "Delete",
        [5] = "Assign", [6] = "Share", [7] = "Append", [8] = "AppendTo"
    };

    /// <summary>A table's own privileges - prvCreate..., prvRead... - from its metadata.</summary>
    public async Task<IReadOnlyList<TablePrivilege>> GetTablePrivilegesAsync(string logicalName, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath +
            $"EntityDefinitions(LogicalName='{Uri.EscapeDataString(logicalName.ToLowerInvariant())}')?$select=Privileges",
            ct).ConfigureAwait(false);

        var results = new List<TablePrivilege>();
        if (!doc.RootElement.TryGetProperty("Privileges", out var privileges) || privileges.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var privilege in privileges.EnumerateArray())
        {
            if (!Guid.TryParse(JsonHelper.GetString(privilege, "PrivilegeId"), out var id)) continue;
            var type = privilege.TryGetProperty("PrivilegeType", out var t) ? PrivilegeTypeOf(t) : 0;

            if (PrivilegeActions.TryGetValue(type, out var action))
            {
                results.Add(new TablePrivilege(action, id, JsonHelper.GetString(privilege, "Name") ?? action));
            }
        }

        return results;
    }

    /// <summary>The privilege type as a number, whether it came as one or by name.</summary>
    private static int PrivilegeTypeOf(JsonElement type) =>
        type.ValueKind == JsonValueKind.Number ? type.GetInt32() : PrivilegeTypeFromName(type.GetString());

    private static int PrivilegeTypeFromName(string? name) =>
        PrivilegeActions.FirstOrDefault(p => string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>
    /// The roles that grant a privilege, once each by their root definition - Dataverse keeps a
    /// copy of every role in each business unit, and all copies grant the same.
    /// </summary>
    public async Task<IReadOnlyList<PrivilegeGrant>> GetPrivilegeGrantsAsync(Guid privilegeId, CancellationToken ct = default)
    {
        var rows = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + $"privileges({privilegeId})/roleprivileges_association?$select=roleid,name,_parentrootroleid_value",
            row => Guid.TryParse(JsonHelper.GetString(row, "roleid"), out var id)
                ? new PrivilegeGrant(ParseGuid(JsonHelper.GetString(row, "_parentrootroleid_value")) ?? id,
                    JsonHelper.GetString(row, "name") ?? "(unnamed role)")
                : null,
            ct).ConfigureAwait(false);

        return rows.GroupBy(r => r.RootRoleId).Select(g => g.First())
            .OrderBy(r => r.RoleName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The field security profiles that grant anything on a secured column, and who holds each.</summary>
    public async Task<IReadOnlyList<FieldPermissionGrant>> GetFieldPermissionGrantsAsync(
        string table, string column, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  "fieldpermissions?$select=canread,cancreate,canupdate,_fieldsecurityprofileid_value" +
                  $"&$filter=entityname eq '{Escape(table.ToLowerInvariant())}' and attributelogicalname eq '{Escape(column.ToLowerInvariant())}'";

        var permissions = await ReadRowsAsync(url, row =>
            Guid.TryParse(JsonHelper.GetString(row, "_fieldsecurityprofileid_value"), out var profile)
                ? Tuple.Create(profile, Label(row, "_fieldsecurityprofileid_value") ?? profile.ToString(),
                    JsonHelper.GetInt(row, "canread") == 4, JsonHelper.GetInt(row, "cancreate") == 4, JsonHelper.GetInt(row, "canupdate") == 4)
                : null, ct).ConfigureAwait(false);

        var grants = new List<FieldPermissionGrant>();

        foreach (var (profile, name, read, create, update) in permissions)
        {
            if (!read && !create && !update) continue;

            var users = await ReadRowsAsync(
                EnvironmentUrl + ApiPath + $"fieldsecurityprofiles({profile})/systemuserprofiles_association?$select=fullname",
                row => JsonHelper.GetString(row, "fullname"), ct).ConfigureAwait(false);
            var teams = await ReadRowsAsync(
                EnvironmentUrl + ApiPath + $"fieldsecurityprofiles({profile})/teamprofiles_association?$select=name",
                row => JsonHelper.GetString(row, "name"), ct).ConfigureAwait(false);

            grants.Add(new FieldPermissionGrant(profile, name, read, create, update, users, teams));
        }

        return grants;
    }
}
