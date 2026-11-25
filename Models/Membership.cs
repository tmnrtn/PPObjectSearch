namespace PPObjectSearch.Models;

/// <summary>A Dataverse team, as the admin tools need it.</summary>
public sealed record TeamInfo(
    Guid TeamId,
    string Name,
    int? TeamType,
    string? TeamTypeLabel,
    string? BusinessUnit,
    Guid? AadObjectId,
    int? MembershipType,
    string? MembershipTypeLabel,
    bool IsDefault)
{
    /// <summary>2 = AAD Security Group, 3 = AAD Office Group. Their membership belongs to Entra.</summary>
    public bool IsEntraGroupTeam => AadObjectId is not null;

    public string TypeDisplay => TeamTypeLabel ?? TeamType?.ToString() ?? "Unknown";

    /// <summary>Team names are not unique across business units, so the picker shows both.</summary>
    public string SearchText => $"{Name} {BusinessUnit} {TypeDisplay}";
}

/// <summary>A Dataverse queue.</summary>
public sealed record QueueInfo(Guid QueueId, string Name, string? TypeLabel, string? Owner, string? Email)
{
    public string SearchText => $"{Name} {Email} {Owner}";
}

/// <summary>A systemuser who belongs to a team or a queue.</summary>
public sealed record MemberUser(
    Guid SystemUserId,
    string FullName,
    string? DomainName,
    string? Email,
    Guid? AadObjectId,
    bool? IsDisabled,
    string? AccessMode,
    Guid? ApplicationId)
{
    /// <summary>Application users are service principals, not people; they are never moved about.</summary>
    public bool IsApplicationUser => ApplicationId is not null;
}

/// <summary>An Entra group, from Microsoft Graph.</summary>
public sealed record EntraGroup(string Id, string? DisplayName, bool? SecurityEnabled, bool? MailEnabled);

/// <summary>An Entra user, from Microsoft Graph.</summary>
public sealed record EntraUser(string Id, string? DisplayName, string? Upn, string? Mail, bool? AccountEnabled, string? UserType = null);

/// <summary>A licence assigned to an Entra user - directly or through a group - and the service plans it switches on.</summary>
public sealed record EntraLicense(string SkuId, string? SkuPartNumber, IReadOnlyList<string> EnabledPlans)
{
    /// <summary>The SKU's part number - ENTERPRISEPACK, DYN365_ENTERPRISE_PLAN1 - which is what Graph names it by.</summary>
    public string Name => SkuPartNumber ?? SkuId;

    public string PlansLabel => string.Join(", ", EnabledPlans);
}

/// <summary>An Entra group a user is in - directly, or through a group nested in it.</summary>
public sealed record EntraUserGroup(
    string Id,
    string? DisplayName,
    string? Description,
    bool? SecurityEnabled,
    bool? MailEnabled,
    bool IsMicrosoft365,
    bool IsDynamic,
    bool IsDirect)
{
    public string Name => DisplayName ?? Id;

    /// <summary>Microsoft 365, Security, Mail-enabled security or Distribution - as the Entra admin center names them.</summary>
    public string Kind => (IsMicrosoft365, SecurityEnabled, MailEnabled) switch
    {
        (true, _, _) => "Microsoft 365",
        (_, true, true) => "Mail-enabled security",
        (_, true, _) => "Security",
        (_, _, true) => "Distribution",
        _ => "Other"
    };

    /// <summary>"Direct" or "Nested", and whether Entra assigns the membership by rule.</summary>
    public string Membership => (IsDirect ? "Direct" : "Nested") + (IsDynamic ? " · dynamic" : string.Empty);

    /// <summary>The Dataverse teams in this environment linked to the group, if any.</summary>
    public IReadOnlyList<TeamInfo> Teams { get; init; } = [];

    /// <summary>The team a link in the list opens - the first, where a group is linked in several business units.</summary>
    public TeamInfo? Team => Teams.Count > 0 ? Teams[0] : null;

    /// <summary>"Case Work", or "Case Work +1" where there are more.</summary>
    public string TeamLabel => Teams.Count switch
    {
        0 => string.Empty,
        1 => Teams[0].Name,
        var n => $"{Teams[0].Name} +{n - 1}"
    };

    public string TeamsToolTip => string.Join("\n", Teams.Select(t => $"{t.Name} ({t.BusinessUnit})"));
}
