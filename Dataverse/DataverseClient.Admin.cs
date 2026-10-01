using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// Teams, queues and who is in them - the reads and writes behind the admin tools. The writes
/// here change membership only; every one of them is reached through a preview and an explicit
/// confirmation that has already passed the write guard.
/// </summary>
public sealed partial class DataverseClient
{
    private const string MemberSelect =
        "systemuserid,fullname,domainname,internalemailaddress,azureactivedirectoryobjectid,isdisabled,accessmode,applicationid";

    /// <summary>Every team, or only those linked to an Entra group.</summary>
    public async Task<IReadOnlyList<TeamInfo>> GetTeamsAsync(bool entraGroupTeamsOnly, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  "teams?$select=teamid,name,teamtype,azureactivedirectoryobjectid,membershiptype,isdefault,_businessunitid_value" +
                  (entraGroupTeamsOnly ? "&$filter=azureactivedirectoryobjectid ne null" : string.Empty) +
                  "&$orderby=name";

        return await ReadRowsAsync(url, row =>
        {
            if (!Guid.TryParse(JsonHelper.GetString(row, "teamid"), out var id)) return null;

            return new TeamInfo(
                id,
                JsonHelper.GetString(row, "name") ?? "(unnamed)",
                JsonHelper.GetInt(row, "teamtype"),
                Label(row, "teamtype"),
                Label(row, "_businessunitid_value"),
                Guid.TryParse(JsonHelper.GetString(row, "azureactivedirectoryobjectid"), out var oid) ? oid : null,
                JsonHelper.GetInt(row, "membershiptype"),
                Label(row, "membershiptype"),
                JsonHelper.GetBool(row, "isdefault") ?? false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Active queues, by name.</summary>
    public async Task<IReadOnlyList<QueueInfo>> GetQueuesAsync(CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  "queues?$select=queueid,name,emailaddress,queueviewtype,_ownerid_value&$filter=statecode eq 0&$orderby=name";

        return await ReadRowsAsync(url, row =>
            Guid.TryParse(JsonHelper.GetString(row, "queueid"), out var id)
                ? new QueueInfo(
                    id,
                    JsonHelper.GetString(row, "name") ?? "(unnamed)",
                    Label(row, "queueviewtype"),
                    Label(row, "_ownerid_value"),
                    JsonHelper.GetString(row, "emailaddress"))
                : null, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MemberUser>> GetTeamMembersAsync(Guid teamId, CancellationToken ct = default) =>
        ReadRowsAsync(EnvironmentUrl + ApiPath + $"teams({teamId})/teammembership_association?$select={MemberSelect}",
            ReadMember, ct);

    public Task<IReadOnlyList<MemberUser>> GetQueueMembersAsync(Guid queueId, CancellationToken ct = default) =>
        ReadRowsAsync(EnvironmentUrl + ApiPath + $"queues({queueId})/queuemembership_association?$select={MemberSelect}",
            ReadMember, ct);

    /// <summary>
    /// The systemusers carrying these Entra object ids - how a sync preview tells an Entra user
    /// Dataverse can add to the team from one it cannot, because they have no user record yet.
    /// </summary>
    public async Task<IReadOnlyList<MemberUser>> GetUsersByAadObjectIdsAsync(
        IEnumerable<string> objectIds, CancellationToken ct = default)
    {
        var ids = objectIds.Where(id => Guid.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var users = new List<MemberUser>();

        foreach (var chunk in ids.Chunk(100))
        {
            var values = string.Join(",", chunk.Select(id => $"'{id}'"));
            var url = EnvironmentUrl + ApiPath +
                      $"systemusers?$select={MemberSelect}" +
                      $"&$filter=Microsoft.Dynamics.CRM.In(PropertyName='azureactivedirectoryobjectid',PropertyValues=[{values}])";

            users.AddRange(await ReadRowsAsync(url, ReadMember, ct).ConfigureAwait(false));
        }

        return users;
    }

    /// <summary>
    /// User records, enabled or not, carrying any of these Entra users' object ids or whose
    /// domainname is one of their UPNs - so a record linked to the wrong Entra id is found too.
    /// </summary>
    public async Task<IReadOnlyList<MemberUser>> FindUsersForEntraUsersAsync(
        IEnumerable<EntraUser> entraUsers, CancellationToken ct = default)
    {
        var found = new Dictionary<Guid, MemberUser>();

        foreach (var chunk in entraUsers.Chunk(50))
        {
            var ids = chunk.Select(u => u.Id).Where(id => Guid.TryParse(id, out _)).ToList();
            var upns = chunk.Select(u => u.Upn).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!).ToList();

            var conditions = new List<string>();
            if (ids.Count > 0) conditions.Add(InFilter("azureactivedirectoryobjectid", ids));
            if (upns.Count > 0) conditions.Add(InFilter("domainname", upns));
            if (conditions.Count == 0) continue;

            var url = EnvironmentUrl + ApiPath + $"systemusers?$select={MemberSelect}&$filter={string.Join(" or ", conditions)}";

            foreach (var user in await ReadRowsAsync(url, ReadMember, ct).ConfigureAwait(false))
            {
                found.TryAdd(user.SystemUserId, user);
            }
        }

        return found.Values.ToList();
    }

    /// <summary>The names of these workflows (flows, child flows), by workflowid. Missing ids are left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetWorkflowNamesAsync(IEnumerable<Guid> workflowIds, CancellationToken ct = default)
    {
        var names = new Dictionary<Guid, string>();

        foreach (var chunk in workflowIds.Distinct().Chunk(50))
        {
            var url = EnvironmentUrl + ApiPath +
                      $"workflows?$select=workflowid,name&$filter={InFilter("workflowid", chunk.Select(id => id.ToString()))}";

            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("value", out var value)) continue;

            foreach (var row in value.EnumerateArray())
            {
                if (Guid.TryParse(JsonHelper.GetString(row, "workflowid"), out var id) &&
                    JsonHelper.GetString(row, "name") is { Length: > 0 } name)
                {
                    names[id] = name;
                }
            }
        }

        return names;
    }

    /// <summary>An In() condition over literal values - quotes doubled, URL-significant characters encoded.</summary>
    private static string InFilter(string column, IEnumerable<string> values) =>
        $"Microsoft.Dynamics.CRM.In(PropertyName='{column}',PropertyValues=[" +
        string.Join(",", values.Select(v => $"'{EscapeFilter(v.Replace("'", "''"))}'")) + "])";

    // ---------------------------------------------------------------- writes

    /// <summary>
    /// Asks Dataverse to bring an Entra group team into line with its group. Dataverse decides
    /// what changes: it only adds group members who already exist as users, and may do the work
    /// after this call returns.
    /// </summary>
    public async Task SyncGroupMembersToTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, EnvironmentUrl + ApiPath + $"teams({teamId})/Microsoft.Dynamics.CRM.SyncGroupMembersToTeam")
        {
            Content = JsonContent(new Dictionary<string, object?>())
        };

        await SendAsync(request, $"sync team {teamId} from its Entra group", ct).ConfigureAwait(false);
    }

    public async Task RemoveTeamMemberAsync(Guid teamId, Guid systemUserId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["Members"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["@odata.type"] = "Microsoft.Dynamics.CRM.systemuser",
                    ["systemuserid"] = systemUserId
                }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, EnvironmentUrl + ApiPath + $"teams({teamId})/Microsoft.Dynamics.CRM.RemoveMembersTeam")
        {
            Content = JsonContent(body)
        };

        await SendAsync(request, $"remove user {systemUserId} from team {teamId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A direct associate on the queue membership relationship rather than AddPrincipalToQueue,
    /// which adds a team's members as a side effect and has been unreliable in practice.
    /// </summary>
    public async Task AddQueueMemberAsync(Guid queueId, Guid systemUserId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["@odata.id"] = EnvironmentUrl + ApiPath + $"systemusers({systemUserId})"
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, EnvironmentUrl + ApiPath + $"queues({queueId})/queuemembership_association/$ref")
        {
            Content = JsonContent(body)
        };

        await SendAsync(request, $"add user {systemUserId} to queue {queueId}", ct).ConfigureAwait(false);
    }

    public async Task RemoveQueueMemberAsync(Guid queueId, Guid systemUserId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, EnvironmentUrl + ApiPath + $"queues({queueId})/queuemembership_association({systemUserId})/$ref");

        await SendAsync(request, $"remove user {systemUserId} from queue {queueId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// WhoAmI, impersonating an Entra user through the CallerObjectId header. Dataverse resolves
    /// that user's record to answer - and, where they have access to the environment but no record
    /// yet, creates it, as it would on their first sign-in. Returns their systemuserid.
    ///
    /// Needs the Act on Behalf of Another User privilege (the Delegate role), assigned directly.
    /// </summary>
    public async Task<Guid> WhoAmIAsAsync(Guid aadObjectId, CancellationToken ct = default)
    {
        var token = await _auth.GetTokenAsync(EnvironmentUrl, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, EnvironmentUrl + ApiPath + "WhoAmI");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("CallerObjectId", aadObjectId.ToString());

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DataverseException(
                $"WhoAmI as {aadObjectId} failed: {ExtractError(body)}", response.StatusCode);
        }

        using var doc = JsonDocument.Parse(body);
        return Guid.TryParse(JsonHelper.GetString(doc.RootElement, "UserId"), out var userId)
            ? userId
            : throw new DataverseException($"WhoAmI as {aadObjectId} returned no user id.");
    }

    // ---------------------------------------------------------------- reading

    private async Task<IReadOnlyList<T>> ReadRowsAsync<T>(string url, Func<JsonElement, T?> read, CancellationToken ct)
        where T : class
    {
        var rows = new List<T>();

        while (url.Length > 0 && rows.Count < MaxRows)
        {
            ct.ThrowIfCancellationRequested();

            using var doc = await GetJsonAsync(url, ct, Annotations.Formatted).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    if (read(row) is { } item) rows.Add(item);
                }
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        return rows;
    }

    private static MemberUser? ReadMember(JsonElement row)
    {
        if (!Guid.TryParse(JsonHelper.GetString(row, "systemuserid"), out var id)) return null;

        return new MemberUser(
            id,
            JsonHelper.GetString(row, "fullname") ?? "(no name)",
            JsonHelper.GetString(row, "domainname"),
            JsonHelper.GetString(row, "internalemailaddress"),
            Guid.TryParse(JsonHelper.GetString(row, "azureactivedirectoryobjectid"), out var oid) ? oid : null,
            JsonHelper.GetBool(row, "isdisabled"),
            Label(row, "accessmode") ?? JsonHelper.GetInt(row, "accessmode")?.ToString(),
            Guid.TryParse(JsonHelper.GetString(row, "applicationid"), out var appId) ? appId : null);
    }

    private static string? Label(JsonElement row, string column) =>
        JsonHelper.GetString(row, column + "@" + Annotations.Formatted);
}
