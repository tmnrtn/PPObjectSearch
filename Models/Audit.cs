namespace PPObjectSearch.Models;

/// <summary>One column's change in an audit record.</summary>
public sealed record AuditChange(string Column, string? OldValue, string? NewValue)
{
    /// <summary>Longer values - a flow's definition, a plug-in's configuration - are shown by their size.</summary>
    internal const int LongValue = 200;

    /// <summary>The columns the audited tables change most, by the name the maker portal gives them.</summary>
    private static readonly Dictionary<string, string> ColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["statecode"] = "Status",
        ["statuscode"] = "Status reason",
        ["clientdata"] = "Definition",
        ["xaml"] = "Definition",
        ["name"] = "Name",
        ["description"] = "Description",
        ["ownerid"] = "Owner",
        ["modifiedby"] = "Modified by",
        ["value"] = "Value",
        ["defaultvalue"] = "Default value",
        ["mode"] = "Execution mode",
        ["stage"] = "Stage",
        ["rank"] = "Order",
        ["filteringattributes"] = "Filtering columns",
        ["configuration"] = "Configuration",
        ["(change)"] = "Change"
    };

    /// <summary>The column as a maker knows it; the logical name is kept for the tooltip.</summary>
    public string ColumnLabel => ColumnNames.GetValueOrDefault(Column) ?? Column;

    public string OldShown => Shown(OldValue);
    public string NewShown => Shown(NewValue);

    private static string Shown(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        if (value.Length <= LongValue) return value;

        var bytes = System.Text.Encoding.UTF8.GetByteCount(value);
        return bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} bytes";
    }

    public override string ToString() => $"{Column}: {OldValue ?? "(empty)"} → {NewValue ?? "(empty)"}";
}

/// <summary>One audited change to a configuration row: who changed what, and from and to which values.</summary>
public sealed class AuditRecord
{
    public required DateTimeOffset When { get; init; }
    public string? By { get; init; }

    /// <summary>Create, Update, Delete, Activate and so on, as Dataverse labels it.</summary>
    public string? Action { get; init; }

    /// <summary>Which row the change was made to, when a history covers more than one (a variable's values).</summary>
    public string? Row { get; init; }

    public IReadOnlyList<AuditChange> Changes { get; init; } = Array.Empty<AuditChange>();

    public string ChangesLabel => string.Join(Environment.NewLine, Changes);

    /// <summary>The row's creation, as audited - history that reaches back this far is complete.</summary>
    public bool IsCreate => string.Equals(Action, "Create", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Whether auditing records changes: for the environment, and for the row's table.</summary>
public sealed record AuditStatus(bool? EnvironmentEnabled, bool? TableEnabled)
{
    public bool IsOn => EnvironmentEnabled == true && TableEnabled == true;

    public string Describe(string table) => (EnvironmentEnabled, TableEnabled) switch
    {
        (false, _) => "Auditing is off for this environment, so new changes are not recorded. Only history recorded while it was on is shown.",
        (_, false) => $"Auditing is off for the {table} table, so new changes to it are not recorded. Only history recorded while it was on is shown.",
        (true, true) => $"Auditing is on for this environment and the {table} table.",
        _ => "Whether auditing is on could not be read."
    };
}
