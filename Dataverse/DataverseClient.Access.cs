using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// Users, security roles, mailboxes and queues - the reads behind the Environment admin window.
/// Read-only: nothing here changes the environment.
/// </summary>
public sealed partial class DataverseClient
{
    private const string LookupType = "Microsoft.Dynamics.CRM.lookuplogicalname";

    // ---------------------------------------------------------------- users

    private const string UserSelect =
        "systemuserid,fullname,domainname,internalemailaddress,_businessunitid_value,isdisabled,accessmode," +
        "applicationid,azureactivedirectoryobjectid,title,caltype,_defaultmailbox_value,createdon";

    private const string UserCoreSelect =
        "systemuserid,fullname,domainname,internalemailaddress,_businessunitid_value,isdisabled,accessmode," +
        "applicationid,azureactivedirectoryobjectid";

    /// <summary>Every user in the environment, by name - people, application users and the system's own.</summary>
    public Task<IReadOnlyList<UserInfo>> GetUsersAsync(CancellationToken ct = default) =>
        ReadWithFallbackAsync("systemusers", UserSelect, UserCoreSelect, "&$orderby=fullname", ReadUser, ct);

    private static UserInfo? ReadUser(JsonElement row)
    {
        if (!Guid.TryParse(JsonHelper.GetString(row, "systemuserid"), out var id)) return null;

        return new UserInfo(
            id,
            JsonHelper.GetString(row, "fullname") ?? "(no name)",
            JsonHelper.GetString(row, "domainname"),
            JsonHelper.GetString(row, "internalemailaddress"),
            ParseGuid(JsonHelper.GetString(row, "_businessunitid_value")),
            Label(row, "_businessunitid_value"),
            JsonHelper.GetBool(row, "isdisabled") ?? false,
            JsonHelper.GetInt(row, "accessmode"),
            Label(row, "accessmode"),
            ParseGuid(JsonHelper.GetString(row, "applicationid")) is not null,
            ParseGuid(JsonHelper.GetString(row, "azureactivedirectoryobjectid")),
            JsonHelper.GetString(row, "title"),
            Label(row, "caltype"),
            ParseGuid(JsonHelper.GetString(row, "_defaultmailbox_value")),
            JsonHelper.GetDate(row, "createdon"));
    }

    private const string TeamSelect = "teamid,name,teamtype,azureactivedirectoryobjectid,membershiptype,isdefault,_businessunitid_value";

    /// <summary>The teams a user belongs to - business unit default teams included.</summary>
    public Task<IReadOnlyList<TeamInfo>> GetUserTeamsAsync(Guid systemUserId, CancellationToken ct = default) =>
        ReadRowsAsync(EnvironmentUrl + ApiPath + $"systemusers({systemUserId})/teammembership_association?$select={TeamSelect}",
            ReadTeam, ct);

    private static TeamInfo? ReadTeam(JsonElement row)
    {
        if (!Guid.TryParse(JsonHelper.GetString(row, "teamid"), out var id)) return null;

        return new TeamInfo(
            id,
            JsonHelper.GetString(row, "name") ?? "(unnamed)",
            JsonHelper.GetInt(row, "teamtype"),
            Label(row, "teamtype"),
            Label(row, "_businessunitid_value"),
            ParseGuid(JsonHelper.GetString(row, "azureactivedirectoryobjectid")),
            JsonHelper.GetInt(row, "membershiptype"),
            Label(row, "membershiptype"),
            JsonHelper.GetBool(row, "isdefault") ?? false);
    }

    private const string AssignedRoleSelect = "roleid,name,_businessunitid_value,_parentrootroleid_value";

    /// <summary>
    /// The roles assigned to the user themselves. A role is copied into every business unit, and
    /// the copy assigned says which business unit the user holds it in.
    /// </summary>
    public Task<IReadOnlyList<RoleAssignment>> GetUserRolesAsync(Guid systemUserId, CancellationToken ct = default) =>
        ReadRowsAsync(EnvironmentUrl + ApiPath + $"systemusers({systemUserId})/systemuserroles_association?$select={AssignedRoleSelect}",
            row => ReadAssignment(row, null, null), ct);

    /// <summary>The roles assigned to a team, which every member holds through it.</summary>
    public Task<IReadOnlyList<RoleAssignment>> GetTeamRolesAsync(Guid teamId, string teamName, CancellationToken ct = default) =>
        ReadRowsAsync(EnvironmentUrl + ApiPath + $"teams({teamId})/teamroles_association?$select={AssignedRoleSelect}",
            row => ReadAssignment(row, teamId, teamName), ct);

    private static RoleAssignment? ReadAssignment(JsonElement row, Guid? teamId, string? teamName)
    {
        if (!Guid.TryParse(JsonHelper.GetString(row, "roleid"), out var id)) return null;

        return new RoleAssignment(
            id,
            ParseGuid(JsonHelper.GetString(row, "_parentrootroleid_value")),
            JsonHelper.GetString(row, "name") ?? "(unnamed role)",
            Label(row, "_businessunitid_value"),
            teamId,
            teamName);
    }

    // ---------------------------------------------------------------- security roles

    /// <summary>
    /// Every security role once - at the business unit it was defined in, not the copy Dataverse
    /// keeps in each of the others.
    /// </summary>
    public Task<IReadOnlyList<SecurityRoleInfo>> GetSecurityRolesAsync(CancellationToken ct = default) =>
        ReadRowsAsync(EnvironmentUrl + ApiPath +
                      "roles?$select=roleid,name,_businessunitid_value,ismanaged,modifiedon&$filter=_parentroleid_value eq null&$orderby=name",
            row => Guid.TryParse(JsonHelper.GetString(row, "roleid"), out var id)
                ? new SecurityRoleInfo(
                    id,
                    JsonHelper.GetString(row, "name") ?? "(unnamed role)",
                    Label(row, "_businessunitid_value"),
                    JsonHelper.GetBool(row, "ismanaged") ?? false,
                    JsonHelper.GetDate(row, "modifiedon"))
                : null, ct);

    /// <summary>The privileges a role grants and how deep each reaches, by RetrieveRolePrivilegesRole.</summary>
    public async Task<IReadOnlyList<RolePrivilege>> GetRolePrivilegesAsync(Guid roleId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + $"RetrieveRolePrivilegesRole(RoleId={roleId})", ct)
            .ConfigureAwait(false);

        var found = new List<(Guid Id, string? Name, PrivilegeDepth Depth)>();
        if (doc.RootElement.TryGetProperty("RolePrivileges", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (!Guid.TryParse(JsonHelper.GetString(item, "PrivilegeId"), out var id)) continue;
                found.Add((id, JsonHelper.GetString(item, "PrivilegeName"), ParseDepth(item)));
            }
        }

        // Older environments leave the name out; the privilege table has it.
        var unnamed = found.Where(p => string.IsNullOrEmpty(p.Name)).Select(p => p.Id).Distinct().ToList();
        var names = unnamed.Count == 0
            ? new Dictionary<Guid, string>()
            : await GetPrivilegeNamesAsync(unnamed, ct).ConfigureAwait(false);

        return found
            .Select(p => new RolePrivilege(p.Id, p.Name is { Length: > 0 } n ? n : names.GetValueOrDefault(p.Id, p.Id.ToString()), p.Depth))
            .ToList();
    }

    /// <summary>The Web API sends PrivilegeDepth by name - Basic, Local, Deep, Global - or by number.</summary>
    internal static PrivilegeDepth ParseDepth(JsonElement item)
    {
        if (!item.TryGetProperty("Depth", out var depth)) return PrivilegeDepth.None;

        var value = depth.ValueKind switch
        {
            JsonValueKind.Number => depth.GetInt32().ToString(),
            JsonValueKind.String => depth.GetString(),
            _ => null
        };

        return value switch
        {
            "Basic" or "0" => PrivilegeDepth.User,
            "Local" or "1" => PrivilegeDepth.BusinessUnit,
            "Deep" or "2" => PrivilegeDepth.ParentChild,
            "Global" or "3" => PrivilegeDepth.Organization,
            _ => PrivilegeDepth.None
        };
    }

    private async Task<Dictionary<Guid, string>> GetPrivilegeNamesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();

        foreach (var chunk in ids.Chunk(100))
        {
            var rows = await ReadRowsAsync(
                EnvironmentUrl + ApiPath + $"privileges?$select=privilegeid,name&$filter={InFilter("privilegeid", chunk.Select(i => i.ToString()))}",
                row => Guid.TryParse(JsonHelper.GetString(row, "privilegeid"), out var id) && JsonHelper.GetString(row, "name") is { } name
                    ? Tuple.Create(id, name)
                    : null, ct).ConfigureAwait(false);

            foreach (var (id, name) in rows) names[id] = name;
        }

        return names;
    }

    /// <summary>
    /// The users and teams holding a role, in any business unit: the role's copies are found by
    /// their root role, with their holders expanded.
    /// </summary>
    public async Task<IReadOnlyList<RoleHolder>> GetRoleHoldersAsync(Guid rootRoleId, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  $"roles?$select=roleid,_businessunitid_value&$filter=_parentrootroleid_value eq {rootRoleId}" +
                  "&$expand=systemuserroles_association($select=systemuserid,fullname,domainname,isdisabled)," +
                  "teamroles_association($select=teamid,name,teamtype)";

        var holders = new List<RoleHolder>();

        await ReadRowsAsync(url, row =>
        {
            var unit = Label(row, "_businessunitid_value");

            if (row.TryGetProperty("systemuserroles_association", out var users) && users.ValueKind == JsonValueKind.Array)
            {
                foreach (var user in users.EnumerateArray())
                {
                    if (!Guid.TryParse(JsonHelper.GetString(user, "systemuserid"), out var id)) continue;
                    var detail = JsonHelper.GetString(user, "domainname");
                    if (JsonHelper.GetBool(user, "isdisabled") == true) detail = (detail is null ? "" : detail + " · ") + "disabled";
                    holders.Add(new RoleHolder(id, JsonHelper.GetString(user, "fullname") ?? "(no name)", false, unit, detail));
                }
            }

            if (row.TryGetProperty("teamroles_association", out var teams) && teams.ValueKind == JsonValueKind.Array)
            {
                foreach (var team in teams.EnumerateArray())
                {
                    if (!Guid.TryParse(JsonHelper.GetString(team, "teamid"), out var id)) continue;
                    holders.Add(new RoleHolder(id, JsonHelper.GetString(team, "name") ?? "(unnamed)", true, unit, Label(team, "teamtype")));
                }
            }

            return (object?)null;
        }, ct).ConfigureAwait(false);

        return holders
            .OrderBy(h => h.IsTeam)
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- mailboxes

    private const string MailboxSelect =
        "mailboxid,name,emailaddress,_regardingobjectid_value,emailrouteraccessapproval,incomingemailstatus," +
        "outgoingemailstatus,actstatus,testemailconfigurationscheduled,testmailboxaccesscompletedon," +
        "enabledforincomingemail,enabledforoutgoingemail,enabledforact,incomingemaildeliverymethod," +
        "outgoingemaildeliverymethod,_emailserverprofile_value,isforwardmailbox,isemailaddressapprovedbyo365admin,statecode";

    private const string MailboxCoreSelect =
        "mailboxid,name,emailaddress,_regardingobjectid_value,emailrouteraccessapproval,incomingemailstatus," +
        "outgoingemailstatus,incomingemaildeliverymethod,outgoingemaildeliverymethod,statecode";

    /// <summary>Every mailbox: users', queues' and any forward mailbox.</summary>
    public Task<IReadOnlyList<MailboxInfo>> GetMailboxesAsync(CancellationToken ct = default) =>
        ReadWithFallbackAsync("mailboxes", MailboxSelect, MailboxCoreSelect, "&$orderby=name", ReadMailbox, ct, Annotations.All);

    /// <summary>One mailbox - a queue's or a user's, for their details.</summary>
    public async Task<MailboxInfo?> GetMailboxAsync(Guid mailboxId, CancellationToken ct = default)
    {
        var rows = await ReadWithFallbackAsync("mailboxes", MailboxSelect, MailboxCoreSelect,
            $"&$filter=mailboxid eq {mailboxId}", ReadMailbox, ct, Annotations.All).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    private static MailboxInfo? ReadMailbox(JsonElement row)
    {
        if (!Guid.TryParse(JsonHelper.GetString(row, "mailboxid"), out var id)) return null;

        return new MailboxInfo(
            id,
            JsonHelper.GetString(row, "name") ?? "(unnamed)",
            JsonHelper.GetString(row, "emailaddress"),
            ParseGuid(JsonHelper.GetString(row, "_regardingobjectid_value")),
            Label(row, "_regardingobjectid_value"),
            JsonHelper.GetString(row, "_regardingobjectid_value@" + LookupType),
            JsonHelper.GetInt(row, "emailrouteraccessapproval"),
            Label(row, "emailrouteraccessapproval"),
            JsonHelper.GetInt(row, "incomingemailstatus"),
            Label(row, "incomingemailstatus"),
            JsonHelper.GetInt(row, "outgoingemailstatus"),
            Label(row, "outgoingemailstatus"),
            Label(row, "actstatus"),
            JsonHelper.GetBool(row, "testemailconfigurationscheduled"),
            JsonHelper.GetDate(row, "testmailboxaccesscompletedon"),
            JsonHelper.GetBool(row, "enabledforincomingemail"),
            JsonHelper.GetBool(row, "enabledforoutgoingemail"),
            JsonHelper.GetBool(row, "enabledforact"),
            JsonHelper.GetInt(row, "incomingemaildeliverymethod"),
            Label(row, "incomingemaildeliverymethod"),
            JsonHelper.GetInt(row, "outgoingemaildeliverymethod"),
            Label(row, "outgoingemaildeliverymethod"),
            Label(row, "_emailserverprofile_value"),
            JsonHelper.GetBool(row, "isforwardmailbox"),
            JsonHelper.GetBool(row, "isemailaddressapprovedbyo365admin"),
            JsonHelper.GetInt(row, "statecode") is null or 0);
    }

    // ---------------------------------------------------------------- queues

    private const string QueueSelect =
        "queueid,name,emailaddress,queueviewtype,_ownerid_value,_businessunitid_value,_defaultmailbox_value," +
        "incomingemailfilteringmethod,ignoreunsolicitedemail,emailrouteraccessapproval,incomingemaildeliverymethod," +
        "outgoingemaildeliverymethod,isemailaddressapprovedbyo365admin,numberofmembers,numberofitems,description,statecode";

    private const string QueueCoreSelect =
        "queueid,name,emailaddress,queueviewtype,_ownerid_value,_businessunitid_value,_defaultmailbox_value,statecode";

    /// <summary>Every queue, active or not, with its owner, type and email settings.</summary>
    public Task<IReadOnlyList<QueueDetail>> GetQueueDetailsAsync(CancellationToken ct = default) =>
        ReadWithFallbackAsync("queues", QueueSelect, QueueCoreSelect, "&$orderby=name", row =>
            Guid.TryParse(JsonHelper.GetString(row, "queueid"), out var id)
                ? new QueueDetail(
                    id,
                    JsonHelper.GetString(row, "name") ?? "(unnamed)",
                    JsonHelper.GetString(row, "emailaddress"),
                    JsonHelper.GetInt(row, "queueviewtype"),
                    Label(row, "queueviewtype"),
                    Label(row, "_ownerid_value"),
                    OwnerTypeLabel(JsonHelper.GetString(row, "_ownerid_value@" + LookupType)),
                    Label(row, "_businessunitid_value"),
                    ParseGuid(JsonHelper.GetString(row, "_defaultmailbox_value")),
                    Label(row, "_defaultmailbox_value"),
                    Label(row, "incomingemailfilteringmethod"),
                    JsonHelper.GetBool(row, "ignoreunsolicitedemail"),
                    Label(row, "emailrouteraccessapproval"),
                    Label(row, "incomingemaildeliverymethod"),
                    Label(row, "outgoingemaildeliverymethod"),
                    JsonHelper.GetBool(row, "isemailaddressapprovedbyo365admin"),
                    JsonHelper.GetInt(row, "numberofmembers"),
                    JsonHelper.GetInt(row, "numberofitems"),
                    JsonHelper.GetString(row, "description"),
                    JsonHelper.GetInt(row, "statecode") is null or 0)
                : null, ct, Annotations.All);

    private static string? OwnerTypeLabel(string? logicalName) => logicalName switch
    {
        "team" => "Team",
        "systemuser" => "User",
        null or "" => null,
        var other => other
    };

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Reads a table with its full column list, and if Dataverse rejects it - a column this
    /// environment's version does not have - again with only the columns every version has.
    /// </summary>
    private async Task<IReadOnlyList<T>> ReadWithFallbackAsync<T>(
        string entitySet, string select, string coreSelect, string tail, Func<JsonElement, T?> read,
        CancellationToken ct, string annotations = Annotations.Formatted)
        where T : class
    {
        try
        {
            return await ReadRowsAsync(EnvironmentUrl + ApiPath + $"{entitySet}?$select={select}{tail}", read, ct, annotations)
                .ConfigureAwait(false);
        }
        catch (DataverseException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return await ReadRowsAsync(EnvironmentUrl + ApiPath + $"{entitySet}?$select={coreSelect}{tail}", read, ct, annotations)
                .ConfigureAwait(false);
        }
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
}
