using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.ReferenceData;

/// <summary>Small factories that keep the reference data tests readable.</summary>
internal static class RefData
{
    public const string Table = "new_thing";
    public const string EntitySet = "new_things";
    public const string PrimaryId = "new_thingid";
    public const string PrimaryName = "new_name";

    public static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    public static EntitySummary Entity(
        string logicalName = Table,
        string? entitySet = EntitySet,
        string primaryId = PrimaryId,
        string? primaryName = PrimaryName,
        string? displayName = "Thing") =>
        new(logicalName, displayName, entitySet, primaryId, primaryName, false, false);

    public static EntityColumn Col(
        string name,
        string type = "StringType",
        bool primaryId = false,
        bool primaryName = false,
        bool validForCreate = true,
        bool validForUpdate = true,
        string? displayName = null) =>
        new(name, displayName, type, primaryId, primaryName)
        {
            IsValidForCreate = validForCreate,
            IsValidForUpdate = validForUpdate
        };

    public static EntityColumn Lookup(string name) => Col(name, "LookupType");

    public static EntityComparePlan Plan(
        IEnumerable<EntityColumn> keys,
        IEnumerable<EntityColumn> values,
        bool matchLookupsByName = true,
        EntitySummary? entity = null,
        string? keyLabel = null)
    {
        var keyList = keys.ToList();

        return new EntityComparePlan
        {
            Entity = entity ?? Entity(),
            KeyColumns = keyList,
            ValueColumns = values.ToList(),
            KeyLabel = keyLabel ?? string.Join(" + ", keyList.Select(k => k.LogicalName)),
            MatchLookupsByName = matchLookupsByName
        };
    }

    public static RecordBuilder Row(Guid id) => new(id);

    public static RecordComparison Comparison(
        EntityComparePlan plan,
        RecordCompareStatus status,
        DataRecord? source = null,
        DataRecord? target = null,
        string key = "k",
        IReadOnlyList<ColumnComparison>? differences = null) =>
        new()
        {
            Plan = plan,
            Key = key,
            Status = status,
            Source = source,
            Target = target,
            Differences = differences ?? Array.Empty<ColumnComparison>()
        };
}

/// <summary>Builds a <see cref="DataRecord"/> the way the client would have read it.</summary>
internal sealed class RecordBuilder
{
    private readonly Guid _id;
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _formatted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _navigation = new(StringComparer.OrdinalIgnoreCase);
    private string? _name;

    public RecordBuilder(Guid id) => _id = id;

    public RecordBuilder With(string selectName, string? raw, string? formatted = null)
    {
        _values[selectName] = raw;
        if (formatted is not null) _formatted[selectName] = formatted;
        return this;
    }

    /// <summary>A lookup as it arrives: raw id under the shadow column, plus its annotations.</summary>
    public RecordBuilder WithLookup(
        string logicalName,
        Guid? id,
        string? label,
        string? targetTable = "new_parent",
        string? navigation = "new_ParentId")
    {
        var select = $"_{logicalName}_value";
        _values[select] = id?.ToString();
        if (label is not null) _formatted[select] = label;
        if (targetTable is not null) _targets[select] = targetTable;
        if (navigation is not null) _navigation[select] = navigation;
        return this;
    }

    public RecordBuilder Named(string? name)
    {
        _name = name;
        return this;
    }

    public DataRecord Build() => new()
    {
        Id = _id,
        Values = _values,
        Formatted = _formatted,
        LookupTargets = _targets,
        NavigationProperties = _navigation,
        PrimaryName = _name
    };
}
