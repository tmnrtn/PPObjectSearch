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
