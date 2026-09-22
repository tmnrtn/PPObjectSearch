using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>What identifies the same row in two environments.</summary>
public enum RecordKeySource
{
    /// <summary>The table's own primary id. Only meaningful where the rows were deployed rather
    /// than created independently in each environment.</summary>
    PrimaryId,

    /// <summary>One of the table's alternate keys, resolved to its columns at compare time.</summary>
    AlternateKey,

    /// <summary>Columns picked by hand - the escape hatch for a table with no alternate key.</summary>
    Columns
}

/// <summary>One table inside a saved comparison configuration.</summary>
public sealed class ReferenceEntityConfig
{
    public string? LogicalName { get; set; }

    /// <summary>Remembered only so a saved configuration reads sensibly before it is loaded.</summary>
    public string? DisplayName { get; set; }

    public RecordKeySource KeySource { get; set; } = RecordKeySource.PrimaryId;

    /// <summary>Logical name of the alternate key, when <see cref="KeySource"/> is AlternateKey.</summary>
    public string? AlternateKeyName { get; set; }

    /// <summary>Columns making up the key, when <see cref="KeySource"/> is Columns.</summary>
    public List<string>? KeyColumns { get; set; }

    /// <summary>OData <c>$filter</c> applied when reading rows, e.g. <c>statecode eq 0</c>.</summary>
    public string? Filter { get; set; }

    /// <summary>
    /// Columns left out of the value comparison. Null means "never configured", which is not the
    /// same as an empty list: null takes the default exclusions, an empty list compares everything.
    /// </summary>
    public List<string>? ExcludedColumns { get; set; }

    public bool IsEnabled { get; set; } = true;

    public ReferenceEntityConfig Clone() => new()
    {
        LogicalName = LogicalName,
        DisplayName = DisplayName,
        KeySource = KeySource,
        AlternateKeyName = AlternateKeyName,
        KeyColumns = KeyColumns is null ? null : new List<string>(KeyColumns),
        Filter = Filter,
        ExcludedColumns = ExcludedColumns is null ? null : new List<string>(ExcludedColumns),
        IsEnabled = IsEnabled
    };
}

/// <summary>A named set of tables to compare, saved in settings.json and picked from a dropdown.</summary>
public sealed class ReferenceDataConfig
{
    public string? Name { get; set; }

    public List<ReferenceEntityConfig>? Entities { get; set; }

    /// <summary>
    /// Match lookup columns on the label Dataverse renders rather than on the id behind it. Ids
    /// only agree between environments where the target row was deployed, so comparing them
    /// directly reports nearly every lookup as different.
    /// </summary>
    public bool MatchLookupsByName { get; set; } = true;

    public int MaxRowsPerEntity { get; set; } = Dataverse.DataverseClient.DefaultMaxRecordsPerEntity;

    public ReferenceDataConfig Clone() => new()
    {
        Name = Name,
        Entities = Entities?.Select(e => e.Clone()).ToList(),
        MatchLookupsByName = MatchLookupsByName,
        MaxRowsPerEntity = MaxRowsPerEntity
    };
}

/// <summary>
/// The columns that say when and by whom a row was touched rather than what it holds. They differ
/// between two environments for every row that was ever deployed, so comparing them would bury the
/// differences that matter.
/// </summary>
public static class SystemColumns
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "createdby", "createdon", "createdonbehalfby",
        "modifiedby", "modifiedon", "modifiedonbehalfby",
        "overriddencreatedon", "versionnumber", "importsequencenumber",
        "timezoneruleversionnumber", "utcconversiontimezonecode",
        "ownerid", "owninguser", "owningteam", "owningbusinessunit",
        "organizationid", "businessunitid", "solutionid", "supportinguser",
        "componentstate", "overwritetime", "ismanaged", "iscustomizable",
        "introducedversion", "exchangerate", "transactioncurrencyid"
    };

    public static bool IsNoise(string logicalName) => Names.Contains(logicalName);

    /// <summary>
    /// The exclusions a table starts with: the housekeeping columns above, plus the primary id,
    /// which is identity rather than a value - two environments that built the same row separately
    /// hold different ids and are not thereby different.
    /// </summary>
    public static List<string> DefaultExclusions(IEnumerable<EntityColumn> columns) =>
        columns.Where(c => c.IsPrimaryId || IsNoise(c.LogicalName))
               .Select(c => c.LogicalName)
               .ToList();
}
