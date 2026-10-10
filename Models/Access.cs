namespace PPObjectSearch.Models;

/// <summary>A user in the environment, as the Users tab lists them.</summary>
public sealed record UserInfo(
    Guid SystemUserId,
    string FullName,
    string? DomainName,
    string? Email,
    Guid? BusinessUnitId,
    string? BusinessUnit,
    bool IsDisabled,
    int? AccessMode,
    string? AccessModeLabel,
    bool IsApplicationUser,
    Guid? AadObjectId,
    string? Title,
    string? LicenseType,
    Guid? DefaultMailboxId,
    DateTimeOffset? CreatedOn)
{
    /// <summary>
    /// "Application user" for a service principal; otherwise the access mode - Read-Write,
    /// Administrative, Read, Non-interactive, Support User, Delegated Admin.
    /// </summary>
    public string UserType => IsApplicationUser ? "Application user" : AccessModeLabel ?? AccessMode?.ToString() ?? "Unknown";

    public string StatusLabel => IsDisabled ? "Disabled" : "Enabled";

    public string SearchText => $"{FullName} {DomainName} {Email} {BusinessUnit} {UserType} {Title} {AadObjectId}";
}

/// <summary>A security role a user holds - directly, or through a team they belong to.</summary>
public sealed record RoleAssignment(
    Guid RoleId,
    Guid? RootRoleId,
    string RoleName,
    string? BusinessUnit,
    Guid? TeamId,
    string? TeamName)
{
    public bool IsDirect => TeamId is null;

    /// <summary>"Direct", or "Team: Service Desk".</summary>
    public string Via => IsDirect ? "Direct" : $"Team: {TeamName}";

    /// <summary>The role as the Security roles tab lists it - roles are copied into every business unit.</summary>
    public Guid DefinitionId => RootRoleId ?? RoleId;
}

/// <summary>
/// A field security profile a user holds - directly, or through a team. It grants read, create
/// and update on secured columns, which security roles do not.
/// </summary>
public sealed record FieldProfileAssignment(
    Guid ProfileId,
    string Name,
    string? Description,
    bool IsManaged,
    Guid? TeamId,
    string? TeamName)
{
    public bool IsDirect => TeamId is null;

    /// <summary>"Direct", or "Team: Service Desk".</summary>
    public string Via => IsDirect ? "Direct" : $"Team: {TeamName}";

    public string ManagedLabel => IsManaged ? "Managed" : "Unmanaged";
}

/// <summary>A security role, at the business unit it was defined in.</summary>
public sealed record SecurityRoleInfo(Guid RoleId, string Name, string? BusinessUnit, bool IsManaged, DateTimeOffset? ModifiedOn)
{
    public string ManagedLabel => IsManaged ? "Managed" : "Unmanaged";
    public string SearchText => $"{Name} {BusinessUnit}";
}

/// <summary>How far a privilege reaches - the four circles of the role editor.</summary>
public enum PrivilegeDepth
{
    None,
    User,
    BusinessUnit,
    ParentChild,
    Organization
}

/// <summary>One privilege a role grants, and its depth.</summary>
public sealed record RolePrivilege(Guid PrivilegeId, string Name, PrivilegeDepth Depth);

/// <summary>Who holds a role: a user or a team, in the business unit of the role copy they hold.</summary>
public sealed record RoleHolder(Guid Id, string Name, bool IsTeam, string? BusinessUnit, string? Detail)
{
    public string Kind => IsTeam ? "Team" : "User";
}

/// <summary>A server-side sync mailbox: its approval, its test results, and what it is enabled for.</summary>
public sealed record MailboxInfo(
    Guid MailboxId,
    string Name,
    string? EmailAddress,
    Guid? RegardingId,
    string? RegardingName,
    string? RegardingType,
    int? Approval,
    string? ApprovalLabel,
    int? IncomingStatus,
    string? IncomingStatusLabel,
    int? OutgoingStatus,
    string? OutgoingStatusLabel,
    string? AppointmentsStatusLabel,
    bool? TestScheduled,
    DateTimeOffset? TestCompletedOn,
    bool? EnabledForIncoming,
    bool? EnabledForOutgoing,
    bool? EnabledForAppointments,
    int? IncomingDelivery,
    string? IncomingDeliveryLabel,
    int? OutgoingDelivery,
    string? OutgoingDeliveryLabel,
    string? ServerProfile,
    bool? IsForwardMailbox,
    bool? ApprovedByExchangeAdmin,
    bool IsActive)
{
    /// <summary>emailrouteraccessapproval: 1 Approved.</summary>
    public bool IsApproved => Approval == 1;

    /// <summary>
    /// What a test of the mailbox last said. Only the directions it delivers are counted: a
    /// mailbox with no incoming delivery method never runs an incoming test.
    /// </summary>
    public MailboxTestStatus TestStatus
    {
        get
        {
            if (TestScheduled == true) return MailboxTestStatus.Pending;

            var results = new List<int?>();
            if (IncomingDelivery is not 0) results.Add(IncomingStatus);
            if (OutgoingDelivery is not 0) results.Add(OutgoingStatus);
            if (results.Count == 0) return MailboxTestStatus.NotRun;

            // incoming/outgoingemailstatus: 0 Not Run, 1 Success, 2 Failure.
            if (results.Contains(2)) return MailboxTestStatus.Failed;
            if (results.TrueForAll(r => r == 1)) return MailboxTestStatus.Passed;
            return results.Contains(1) ? MailboxTestStatus.Partial : MailboxTestStatus.NotRun;
        }
    }

    public string TestLabel => TestStatus switch
    {
        MailboxTestStatus.Pending => "Test scheduled",
        MailboxTestStatus.Failed => "Failed",
        MailboxTestStatus.Passed => "Passed",
        MailboxTestStatus.Partial => "Partly passed",
        _ => "Not tested"
    };

    /// <summary>The <see cref="OwnerKind"/> of a user's mailbox.</summary>
    public const string UserOwner = "User";

    /// <summary>The <see cref="OwnerKind"/> of a queue's mailbox.</summary>
    public const string QueueOwner = "Queue";

    /// <summary>"User", "Queue", or the logical name of whatever else owns it.</summary>
    public string OwnerKind => RegardingType switch
    {
        "systemuser" => UserOwner,
        "queue" => QueueOwner,
        null or "" => "None",
        var other => other
    };

    public string SearchText => $"{Name} {EmailAddress} {RegardingName} {ApprovalLabel} {TestLabel} {ServerProfile}";
}

public enum MailboxTestStatus
{
    NotRun,
    Pending,
    Passed,
    Partial,
    Failed
}

/// <summary>A queue with its owner, type and email settings.</summary>
public sealed record QueueDetail(
    Guid QueueId,
    string Name,
    string? EmailAddress,
    int? ViewType,
    string? ViewTypeLabel,
    string? Owner,
    string? OwnerType,
    string? BusinessUnit,
    Guid? MailboxId,
    string? MailboxName,
    string? IncomingFilteringLabel,
    bool? IgnoreUnsolicited,
    string? ApprovalLabel,
    string? IncomingDeliveryLabel,
    string? OutgoingDeliveryLabel,
    bool? ApprovedByExchangeAdmin,
    int? MemberCount,
    int? ItemCount,
    string? Description,
    bool IsActive)
{
    /// <summary>queueviewtype 0 = Public, 1 = Private.</summary>
    public bool IsPrivate => ViewType == 1;

    public string TypeLabel => ViewTypeLabel ?? (IsPrivate ? "Private" : "Public");

    /// <summary>"Service Desk (Team)".</summary>
    public string OwnerLabel => (Owner, OwnerType) switch
    {
        ({ } owner, { Length: > 0 } type) => $"{owner} ({type})",
        ({ } owner, _) => owner,
        _ => "—"
    };

    public string SearchText => $"{Name} {EmailAddress} {Owner} {BusinessUnit} {TypeLabel}";
}
