namespace PPObjectSearch.Models;

/// <summary>One table as the entity picker needs it - enough to list it and to read rows from it.</summary>
public sealed record EntitySummary(
    string LogicalName,
    string? DisplayName,
    string? EntitySetName,
    string PrimaryIdAttribute,
    string? PrimaryNameAttribute,
    bool IsManaged,
    bool IsActivity)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        ? LogicalName
        : $"{DisplayName} ({LogicalName})";

    /// <summary>Rows cannot be read without the entity set name, whatever else is known.</summary>
    public bool IsReadable => !string.IsNullOrWhiteSpace(EntitySetName);
}

/// <summary>
/// A column as record comparison needs it. The type name is the metadata's own
/// <c>AttributeTypeName</c> ("StringType", "LookupType", ...) rather than the coarser
/// AttributeType, because lookups and multi-select choices both report as Virtual there.
/// </summary>
public sealed record EntityColumn(
    string LogicalName,
    string? DisplayName,
    string TypeName,
    bool IsPrimaryId,
    bool IsPrimaryName)
{
    /// <summary>A lookup is only readable through its <c>_name_value</c> shadow column.</summary>
    public bool IsLookup => TypeName is "LookupType" or "CustomerType" or "OwnerType";

    /// <summary>What <c>$select</c> has to ask for, which is not always the logical name.</summary>
    public string SelectName => IsLookup ? $"_{LogicalName}_value" : LogicalName;

    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        ? LogicalName
        : $"{DisplayName} ({LogicalName})";

    /// <summary>Type label without the "Type" suffix the metadata carries.</summary>
    public string TypeLabel => TypeName.EndsWith("Type", StringComparison.Ordinal)
        ? TypeName[..^4]
        : TypeName;

    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "IntegerType", "BigIntType", "DecimalType", "DoubleType", "MoneyType"
    };

    public bool IsNumeric => NumericTypes.Contains(TypeName);
    public bool IsDateTime => TypeName == "DateTimeType";
    public bool IsBoolean => TypeName == "BooleanType";
    public bool IsUniqueIdentifier => TypeName == "UniqueidentifierType";
}

/// <summary>An alternate key, and the columns that make it up.</summary>
public sealed record AlternateKeyInfo(string LogicalName, string? DisplayName, IReadOnlyList<string> KeyAttributes)
{
    public string Label
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(DisplayName) ? LogicalName : DisplayName!;
            return KeyAttributes.Count == 0 ? name : $"{name} ({string.Join(", ", KeyAttributes)})";
        }
    }
}

/// <summary>
/// One row read from a table. Raw values are keyed by the column's select name, so a lookup sits
/// under <c>_name_value</c>; formatted values are the labels Dataverse renders for choices and
/// lookups, keyed the same way.
/// </summary>
public sealed class DataRecord
{
    public required Guid Id { get; init; }
    public required IReadOnlyDictionary<string, string?> Values { get; init; }
    public required IReadOnlyDictionary<string, string?> Formatted { get; init; }

    /// <summary>The primary name column's value, for labelling the row.</summary>
    public string? PrimaryName { get; init; }

    public string? Raw(string selectName) => Values.TryGetValue(selectName, out var value) ? value : null;

    public string? Label(string selectName) => Formatted.TryGetValue(selectName, out var value) ? value : null;

    /// <summary>What a person should see for this column - the label where there is one.</summary>
    public string? Display(string selectName)
    {
        var label = Label(selectName);
        var raw = Raw(selectName);

        if (string.IsNullOrWhiteSpace(label)) return raw;
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(label, raw, StringComparison.Ordinal)) return label;

        return $"{label}  ({raw})";
    }
}
