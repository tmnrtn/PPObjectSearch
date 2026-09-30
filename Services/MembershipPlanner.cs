using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

public enum EntraMatchStatus
{
    Both,
    DataverseOnly,
    EntraOnly
}

/// <summary>One person in an Entra group team comparison: in the team, in the group, or both.</summary>
public sealed record EntraMatchRow(EntraMatchStatus Status, string? MatchedOn, MemberUser? Dataverse, EntraUser? Entra)
{
    public string Name => Dataverse?.FullName ?? Entra?.DisplayName ?? "(no name)";
    public string? Upn => Entra?.Upn ?? Dataverse?.DomainName;
    public string? Email => Entra?.Mail ?? Dataverse?.Email;
    public string? EntraObjectId => Entra?.Id ?? Dataverse?.AadObjectId?.ToString();
}

/// <summary>Why a user is still in a Dataverse group team although Entra does not list them in the group.</summary>
public enum DiagnosisCategory
{
    NoObjectId,
    StaleObjectId,
    EntraNotFound,
    EntraSaysMember,
    CheckFailed,
    EntraDisabled,
    DataverseDisabled,
    LiveNonMember
}

public sealed record Diagnosis(DiagnosisCategory Category, string Detail, string FoundBy, EntraUser? EntraUser, bool? EntraSaysMember)
{
    public string Label => MembershipPlanner.Describe(Category);
}

/// <summary>Why a user in the Entra group is not in the Dataverse group team.</summary>
public enum GroupOnlyCategory
{
    EntraDisabled,
    MembershipTypeExcluded,
    NotProvisioned,
    ObjectIdMismatch,
    DataverseDisabled,
    ShouldBeAdded
}

/// <summary>What Dataverse holds for a 'group only' user, and what that means for the sync.</summary>
public sealed record GroupOnlyDiagnosis(GroupOnlyCategory Category, string Detail, string FoundBy, MemberUser? SystemUser)
{
    public string Label => MembershipPlanner.Describe(Category);
}

public enum QueuePlanStatus
{
    InBoth,
    Add,
    Remove,
    Skip,

    /// <summary>In the queue but not the team, left alone because the sync is additive only.</summary>
    Keep
}

/// <summary>One user in a team-to-queue comparison, and what syncing would do about them.</summary>
public sealed record QueuePlanRow(MemberUser User, QueuePlanStatus Status, string Detail)
{
    /// <summary>Removals that are shown but not ticked - an application user may be there for a reason.</summary>
    public bool IncludedByDefault { get; init; } = true;
}

/// <summary>
/// The decisions behind the admin tools, with no I/O: who matches whom, why a leftover team
/// member was left over, and what a queue needs to look like its team.
/// </summary>
public static class MembershipPlanner
{
    // ---------------------------------------------------------------- Entra group teams

    /// <summary>
    /// Pairs team members with group members on Entra object id, falling back to UPN ==
    /// domainname for users whose object id Dataverse does not hold or holds stale.
    /// </summary>
    public static IReadOnlyList<EntraMatchRow> MatchEntra(IReadOnlyList<MemberUser> team, IReadOnlyList<EntraUser> group)
    {
        var byId = group.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var byUpn = group
            .Where(u => !string.IsNullOrEmpty(u.Upn))
            .GroupBy(u => u.Upn!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<EntraMatchRow>();

        foreach (var dv in team)
        {
            EntraUser? match = null;
            string? on = null;

            if (dv.AadObjectId is { } oid && byId.TryGetValue(oid.ToString(), out var viaId))
            {
                (match, on) = (viaId, "Object id");
            }
            else if (!string.IsNullOrEmpty(dv.DomainName) &&
                     byUpn.TryGetValue(dv.DomainName, out var viaUpn) &&
                     !claimed.Contains(viaUpn.Id))
            {
                (match, on) = (viaUpn, "UPN");
            }

            if (match is not null) claimed.Add(match.Id);

            rows.Add(new EntraMatchRow(match is null ? EntraMatchStatus.DataverseOnly : EntraMatchStatus.Both, on, dv, match));
        }

        rows.AddRange(group
            .Where(u => !claimed.Contains(u.Id))
            .Select(u => new EntraMatchRow(EntraMatchStatus.EntraOnly, null, null, u)));

        return rows
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Upn, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The most likely reason a 'Dataverse only' user survived a sync, from what Entra says about
    /// them: found by object id, found only by UPN, and whether Entra itself calls them a member.
    /// </summary>
    public static Diagnosis Diagnose(MemberUser dv, EntraUser? byId, EntraUser? byUpn, bool? entraSaysMember)
    {
        var entra = byId ?? byUpn;
        var foundBy = byId is not null ? "Object id" : byUpn is not null ? "UPN" : "Not found";

        var (category, detail) = (dv.AadObjectId, byId, byUpn, entraSaysMember) switch
        {
            (null, _, null, _) => (DiagnosisCategory.NoObjectId,
                "The user record has no Entra object id and its UPN is not in Entra, so the sync cannot map it."),
            (null, _, not null, _) => (DiagnosisCategory.NoObjectId,
                $"The user record has no Entra object id (Entra has this UPN as {byUpn!.Id}), so the sync likely cannot map it."),
            (_, null, not null, _) => (DiagnosisCategory.StaleObjectId,
                $"Dataverse holds object id {dv.AadObjectId}, but Entra's user with this UPN is {byUpn!.Id} - recreated? The sync cannot match it."),
            (_, null, null, _) => (DiagnosisCategory.EntraNotFound,
                "The user no longer exists in Entra. The sync does not appear to remove deleted users."),
            (_, _, _, true) => (DiagnosisCategory.EntraSaysMember,
                "Entra says the user IS a transitive member, so the group listing missed them - not a real discrepancy."),
            (_, _, _, null) => (DiagnosisCategory.CheckFailed,
                "Entra could not be asked whether the user is a member."),
            _ when entra!.AccountEnabled == false => (DiagnosisCategory.EntraDisabled,
                "Disabled in Entra and not in the group. The sync appears to skip users who cannot sign in."),
            _ when dv.IsDisabled == true => (DiagnosisCategory.DataverseDisabled,
                "Disabled in Dataverse and not in the group. The sync may skip disabled users."),
            _ => (DiagnosisCategory.LiveNonMember,
                "A live Entra user who is not in the group. The sync should remove them; if they persist, it is not."),
        };

        return new Diagnosis(category, detail, foundBy, entra, entraSaysMember);
    }

    /// <summary>Categories the sync leaves behind that are safe to remove by hand - ticked by default.</summary>
    public static bool IsRemovableByDefault(DiagnosisCategory category) =>
        category is DiagnosisCategory.EntraDisabled or DiagnosisCategory.EntraNotFound or DiagnosisCategory.StaleObjectId;

    /// <summary>
    /// Whether a manual removal may be offered at all. A user Entra calls a member, or one it
    /// could not be asked about, is not evidence of anything wrong.
    /// </summary>
    public static bool CanRemove(DiagnosisCategory category) =>
        category is not (DiagnosisCategory.EntraSaysMember or DiagnosisCategory.CheckFailed);

    public static string Describe(DiagnosisCategory category) => category switch
    {
        DiagnosisCategory.NoObjectId => "No Entra object id",
        DiagnosisCategory.StaleObjectId => "Stale Entra object id",
        DiagnosisCategory.EntraNotFound => "Deleted from Entra",
        DiagnosisCategory.EntraSaysMember => "Entra says member",
        DiagnosisCategory.CheckFailed => "Check failed",
        DiagnosisCategory.EntraDisabled => "Disabled in Entra",
        DiagnosisCategory.DataverseDisabled => "Disabled in Dataverse",
        DiagnosisCategory.LiveNonMember => "Live, not in group",
        _ => category.ToString()
    };

    /// <summary>
    /// Why the sync has not added a 'group only' user, from the user record Dataverse holds for
    /// them - found by Entra object id, else by UPN - and the team's membership type
    /// (0 members and guests, 1 members, 2 owners, 3 guests).
    /// </summary>
    public static GroupOnlyDiagnosis DiagnoseGroupOnly(EntraUser entra, MemberUser? byObjectId, MemberUser? byUpn, int? membershipType)
    {
        var user = byObjectId ?? byUpn;
        var foundBy = byObjectId is not null ? "Object id" : byUpn is not null ? "UPN" : "Not found";
        var isGuest = string.Equals(entra.UserType, "Guest", StringComparison.OrdinalIgnoreCase);

        var (category, detail) =
            entra.AccountEnabled == false
                ? (GroupOnlyCategory.EntraDisabled, "Disabled in Entra - will not be provisioned or synced.")
            : membershipType == 1 && isGuest
                ? (GroupOnlyCategory.MembershipTypeExcluded, "A guest, but the team's membership type is 'Members' - guests are not synced.")
            : membershipType == 3 && !isGuest
                ? (GroupOnlyCategory.MembershipTypeExcluded, "A member, but the team's membership type is 'Guests' - only guests are synced.")
            : membershipType == 2
                ? (GroupOnlyCategory.MembershipTypeExcluded, "The team's membership type is 'Owners' - only group owners are synced. Check whether this user owns the group.")
            : user is null
                ? (GroupOnlyCategory.NotProvisioned, "No user record in Dataverse, and the sync only adds existing users. Pull them in, or have them sign in to the environment.")
            : byObjectId is null
                ? (GroupOnlyCategory.ObjectIdMismatch,
                   $"The user record '{user.DomainName}' is linked to Entra object id {user.AadObjectId?.ToString() ?? "(none)"}, not {entra.Id}. " +
                   "The sync matches on object id, so it cannot add them - recreated in Entra? Re-add them in the admin center.")
            : user.IsDisabled == true
                ? (GroupOnlyCategory.DataverseDisabled, "The user record is disabled - usually no licence or not in the environment's security group. The sync will not add disabled users; pulling them in re-evaluates them.")
            : (GroupOnlyCategory.ShouldBeAdded, "The user record exists, is enabled and is linked by object id - the sync should add them. Re-read in a few minutes.");

        return new GroupOnlyDiagnosis(category, detail, foundBy, user);
    }

    /// <summary>Categories an impersonated WhoAmI (just-in-time user sync) can fix.</summary>
    public static bool CanPullIn(GroupOnlyCategory category) =>
        category is GroupOnlyCategory.NotProvisioned or GroupOnlyCategory.DataverseDisabled;

    /// <summary>Categories fixed by provisioning the user into the environment, by any means.</summary>
    public static bool NeedsUserSync(GroupOnlyCategory category) =>
        category is GroupOnlyCategory.NotProvisioned or GroupOnlyCategory.DataverseDisabled or GroupOnlyCategory.ObjectIdMismatch;

    public static string Describe(GroupOnlyCategory category) => category switch
    {
        GroupOnlyCategory.EntraDisabled => "Disabled in Entra",
        GroupOnlyCategory.MembershipTypeExcluded => "Excluded by membership type",
        GroupOnlyCategory.NotProvisioned => "No Dataverse user",
        GroupOnlyCategory.ObjectIdMismatch => "Linked to another Entra id",
        GroupOnlyCategory.DataverseDisabled => "Dataverse user disabled",
        GroupOnlyCategory.ShouldBeAdded => "Sync should add",
        _ => category.ToString()
    };

    /// <summary>
    /// PowerShell a Power Platform admin can run to provision users into the environment - the
    /// fallback where pulling them in is not possible or not permitted.
    /// </summary>
    public static string BuildUserSyncScript(IEnumerable<(string EntraObjectId, string Name, string Reason)> users, string? environmentId)
    {
        var env = string.IsNullOrWhiteSpace(environmentId) ? "<environment-id>" : environmentId;
        var lines = users.Select(u => $"Add-AdminPowerAppsSyncUser -EnvironmentName {env} -PrincipalObjectId {u.EntraObjectId}   # {u.Name} ({u.Reason})");

        return "# Install-Module Microsoft.PowerApps.Administration.PowerShell   (once)" + Environment.NewLine +
               "Add-PowerAppsAccount" + Environment.NewLine +
               string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    // ---------------------------------------------------------------- queues

    /// <summary>
    /// What it takes for a queue's members to be exactly the team's members. Team members who
    /// are disabled or are application users are not added; queue members who are not in the
    /// team are removed, except application users, which are listed but left unticked. With
    /// <paramref name="additiveOnly"/>, nobody is removed: those queue members are kept instead.
    /// </summary>
    public static IReadOnlyList<QueuePlanRow> PlanQueueSync(
        IReadOnlyList<MemberUser> team, IReadOnlyList<MemberUser> queue, bool additiveOnly = false)
    {
        var inQueue = queue.Select(u => u.SystemUserId).ToHashSet();
        var inTeam = team.Select(u => u.SystemUserId).ToHashSet();
        var rows = new List<QueuePlanRow>();

        foreach (var user in team.DistinctBy(u => u.SystemUserId))
        {
            if (inQueue.Contains(user.SystemUserId))
            {
                rows.Add(new QueuePlanRow(user, QueuePlanStatus.InBoth, "Already in the queue."));
            }
            else if (user.IsApplicationUser)
            {
                rows.Add(new QueuePlanRow(user, QueuePlanStatus.Skip, "Application user - not added."));
            }
            else if (user.IsDisabled == true)
            {
                rows.Add(new QueuePlanRow(user, QueuePlanStatus.Skip, "Disabled user - not added."));
            }
            else
            {
                rows.Add(new QueuePlanRow(user, QueuePlanStatus.Add, "In the team, not in the queue."));
            }
        }

        foreach (var user in queue.DistinctBy(u => u.SystemUserId).Where(u => !inTeam.Contains(u.SystemUserId)))
        {
            if (additiveOnly)
            {
                rows.Add(new QueuePlanRow(user, QueuePlanStatus.Keep, "In the queue, not in the team - kept, as this sync only adds."));
                continue;
            }

            rows.Add(user.IsApplicationUser
                ? new QueuePlanRow(user, QueuePlanStatus.Remove,
                    "Application user in the queue but not the team - unticked, as it may be there for automation.")
                  { IncludedByDefault = false }
                : new QueuePlanRow(user, QueuePlanStatus.Remove,
                    user.IsDisabled == true ? "Disabled user, in the queue but not the team." : "In the queue, not in the team."));
        }

        return rows
            .OrderBy(r => r.Status switch
            {
                QueuePlanStatus.Add => 0,
                QueuePlanStatus.Remove => 1,
                QueuePlanStatus.Keep => 2,
                QueuePlanStatus.Skip => 3,
                _ => 4
            })
            .ThenBy(r => r.User.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
