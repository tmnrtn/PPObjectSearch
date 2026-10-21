namespace PPObjectSearch.Models;

/// <summary>
/// The settings of one table that decide where its data goes beyond Dataverse: whether changes to
/// it are audited, tracked for change-based sync (which Synapse Link and Dataverse search both
/// need), and indexed for Dataverse search. Null where the metadata did not say.
/// </summary>
public sealed record TableSetting(
    string LogicalName,
    string? DisplayName,
    Guid MetadataId,
    bool IsCustom,
    bool IsManaged,
    bool? IsAuditEnabled,
    bool? ChangeTrackingEnabled,
    bool? SearchEnabled,
    bool? CanEnableSearch)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? LogicalName : DisplayName;
}

/// <summary>
/// One column a person could choose to audit - custom, writable, not computed - and whether it is.
/// </summary>
public sealed record ColumnAudit(string LogicalName, string? DisplayName, bool? IsAuditEnabled, bool IsManaged);

/// <summary>A Synapse Link (or Fabric link) profile: one link to one data lake folder.</summary>
public sealed record SynapseLinkProfile(
    Guid Id,
    string Name,
    int? StateValue,
    string? State,
    string? Type,
    string? Folder,
    string? CreatedBy,
    DateTimeOffset? CreatedOn)
{
    /// <summary>Profile state 1 - the only state in which tables are being exported.</summary>
    public bool IsActive => StateValue == 1;

    /// <summary>The folder the profile writes to, the name the matrix groups links by.</summary>
    public string FolderLabel => string.IsNullOrWhiteSpace(Folder) ? "(no folder)" : Folder;
}

/// <summary>The runtime state of one table in one link profile.</summary>
public sealed record SynapseLinkTable(
    Guid ProfileId,
    string Table,
    int? SyncStateValue,
    string? SyncState,
    string? InitialSyncState,
    string? MetadataState,
    long? LakeRecordCount,
    long? SourceRecordCount,
    DateTimeOffset? LastSynced,
    bool IsActive)
{
    /// <summary>Completed (4) is the only state in which the lake is keeping up.</summary>
    public bool IsHealthy => SyncStateValue == 4;
}

/// <summary>Every link profile in an environment, and the state of each table in each one.</summary>
public sealed record SynapseLinkSetup(IReadOnlyList<SynapseLinkProfile> Profiles, IReadOnlyList<SynapseLinkTable> Tables)
{
    public static SynapseLinkSetup Empty { get; } = new([], []);
}

/// <summary>
/// The environment-wide switches behind auditing and Dataverse search, from the organization row.
/// Table and column audit settings do nothing while <see cref="AuditEnabled"/> is off.
/// </summary>
public sealed record EnvironmentDataSettings(
    bool? AuditEnabled,
    bool? UserAccessAuditEnabled,
    bool? ReadAuditEnabled,
    int? AuditRetentionDays,
    bool? SearchEnabled)
{
    /// <summary>Retention as the admin center words it: -1 (or nothing) keeps logs forever.</summary>
    public string RetentionLabel => AuditRetentionDays switch
    {
        null => "Unknown",
        -1 or 0 => "Forever",
        var days => $"{days:N0} days"
    };
}

/// <summary>Dataverse search's own account of the index: whether it exists, and each table in it.</summary>
public sealed record SearchIndexStatus(
    string Status,
    string? LockboxStatus,
    string? CmkStatus,
    IReadOnlyList<SearchTableStatus> Tables,
    long? DocumentCount,
    long? StorageMegabytes)
{
    public bool IsProvisioned => string.Equals(Status, "provisioned", StringComparison.OrdinalIgnoreCase);

    public string StatusLabel => Status.ToLowerInvariant() switch
    {
        "provisioned" => "Provisioned",
        "provisioninginprogress" => "Provisioning",
        "notprovisioned" => "Not provisioned",
        _ => Status
    };
}

/// <summary>One table in the search index: how far its sync has got, and which columns are indexed.</summary>
public sealed record SearchTableStatus(
    string Table,
    string? Status,
    DateTimeOffset? LastDataSync,
    IReadOnlyList<string> IndexedColumns)
{
    /// <summary>"EntitySyncComplete" is the steady state; anything else is still catching up or stuck.</summary>
    public bool IsComplete => string.Equals(Status, "EntitySyncComplete", StringComparison.OrdinalIgnoreCase);

    public string StatusLabel => Status switch
    {
        null or "" => "Unknown",
        "EntitySyncComplete" => "Complete",
        "EntitySyncInProgress" => "In progress",
        "EntitySyncNotStarted" => "Not started",
        "EntitySyncFailed" => "Failed",
        var other => other.StartsWith("EntitySync", StringComparison.Ordinal) ? other["EntitySync".Length..] : other
    };
}
