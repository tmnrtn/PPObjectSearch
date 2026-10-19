using System.Globalization;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>How a server-side filter compares a column with the value typed.</summary>
public enum TableFilterOperator
{
    Contains,
    Equals,
    NotEquals,
    BeginsWith,
    GreaterThan,
    LessThan,
    On,
    OnOrBefore,
    OnOrAfter,
    IsEmpty,
    IsNotEmpty
}

/// <summary>
/// Builds the OData filter for one column, one operator and one typed value. The literal follows
/// the column's type as its metadata states it, never the look of its values: a virtual table can
/// hold dates in a text column (msdyn_solutioncomponentsummary.msdyn_modifiedon is one), and a
/// date comparison against that column is refused by the server. Such a column filters as text.
/// </summary>
public static class TableRowFilter
{
    private static readonly HashSet<string> ChoiceTypes = new(StringComparer.Ordinal) { "PicklistType", "StateType", "StatusType" };

    /// <summary>The operators a column of this type supports, the first being the usual one.</summary>
    public static IReadOnlyList<TableFilterOperator> OperatorsFor(EntityColumn column) => KindOf(column) switch
    {
        Kind.Text => [TableFilterOperator.Contains, TableFilterOperator.Equals, TableFilterOperator.BeginsWith,
            TableFilterOperator.NotEquals, TableFilterOperator.IsEmpty, TableFilterOperator.IsNotEmpty],
        Kind.Number => [TableFilterOperator.Equals, TableFilterOperator.NotEquals, TableFilterOperator.GreaterThan,
            TableFilterOperator.LessThan, TableFilterOperator.IsEmpty, TableFilterOperator.IsNotEmpty],
        Kind.Date => [TableFilterOperator.On, TableFilterOperator.OnOrBefore, TableFilterOperator.OnOrAfter,
            TableFilterOperator.IsEmpty, TableFilterOperator.IsNotEmpty],
        Kind.MultiChoice => [TableFilterOperator.Contains, TableFilterOperator.IsEmpty, TableFilterOperator.IsNotEmpty],
        Kind.Boolean => [TableFilterOperator.Equals, TableFilterOperator.NotEquals],
        _ => [TableFilterOperator.Equals, TableFilterOperator.NotEquals, TableFilterOperator.IsEmpty, TableFilterOperator.IsNotEmpty]
    };

    public static string Label(TableFilterOperator op) => op switch
    {
        TableFilterOperator.Contains => "contains",
        TableFilterOperator.Equals => "equals",
        TableFilterOperator.NotEquals => "does not equal",
        TableFilterOperator.BeginsWith => "begins with",
        TableFilterOperator.GreaterThan => "greater than",
        TableFilterOperator.LessThan => "less than",
        TableFilterOperator.On => "on",
        TableFilterOperator.OnOrBefore => "on or before",
        TableFilterOperator.OnOrAfter => "on or after",
        TableFilterOperator.IsEmpty => "is empty",
        _ => "is not empty"
    };

    public static bool NeedsValue(TableFilterOperator op) => op is not (TableFilterOperator.IsEmpty or TableFilterOperator.IsNotEmpty);

    /// <summary>What the value box expects, as a hint beside it.</summary>
    public static string ValueHint(EntityColumn column) => KindOf(column) switch
    {
        Kind.Number => "a number",
        Kind.Date => "a date, such as 2026-10-19",
        Kind.Choice => "the choice's label or number",
        Kind.MultiChoice => "the choice's number",
        Kind.Boolean => "yes or no",
        Kind.Guid => "a row id (GUID)",
        _ => "text"
    };

    /// <summary>
    /// The filter expression, or null with <paramref name="error"/> saying what is wrong with the
    /// value. A choice's label is turned into its number by the caller, who has the labels.
    /// </summary>
    public static string? Build(EntityColumn column, TableFilterOperator op, string? value, out string? error)
    {
        error = null;
        var name = column.SelectName;
        var kind = KindOf(column);

        if (!OperatorsFor(column).Contains(op))
        {
            error = $"{Name(column)} cannot be compared with \"{Label(op)}\".";
            return null;
        }

        if (op == TableFilterOperator.IsEmpty) return $"{name} eq null";
        if (op == TableFilterOperator.IsNotEmpty) return $"{name} ne null";

        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = $"Type {ValueHint(column)} to compare {Name(column)} with.";
            return null;
        }

        switch (kind)
        {
            case Kind.Text:
                var quoted = Quote(text);
                return op switch
                {
                    TableFilterOperator.Contains => $"contains({name},{quoted})",
                    TableFilterOperator.BeginsWith => $"startswith({name},{quoted})",
                    TableFilterOperator.NotEquals => $"{name} ne {quoted}",
                    _ => $"{name} eq {quoted}"
                };

            case Kind.Number:
            case Kind.Choice:
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
                    !decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out number))
                {
                    error = $"\"{text}\" is not {ValueHint(column)}.";
                    return null;
                }
                if (kind == Kind.Choice && number != decimal.Truncate(number))
                {
                    error = $"\"{text}\" is not a choice's number.";
                    return null;
                }
                var literal = number.ToString(CultureInfo.InvariantCulture);
                return $"{name} {Comparison(op)} {literal}";

            case Kind.MultiChoice:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var option))
                {
                    error = $"\"{text}\" is not a choice's number.";
                    return null;
                }
                return $"Microsoft.Dynamics.CRM.ContainValues(PropertyName='{name}',PropertyValues=['{option}'])";

            case Kind.Boolean:
                bool? flag = text.ToLowerInvariant() switch
                {
                    "yes" or "true" or "1" => true,
                    "no" or "false" or "0" => false,
                    _ => null
                };
                if (flag is null)
                {
                    error = $"\"{text}\" is not yes or no.";
                    return null;
                }
                return $"{name} {Comparison(op)} {(flag.Value ? "true" : "false")}";

            case Kind.Guid:
                if (!Guid.TryParse(text, out var id))
                {
                    error = $"\"{text}\" is not a row id (GUID).";
                    return null;
                }
                return $"{name} {Comparison(op)} {id}";

            case Kind.Date:
                if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                    !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
                {
                    error = $"\"{text}\" is not a date.";
                    return null;
                }
                // The query functions compare in the signed-in user's time zone, as the app's own views do.
                var function = op switch
                {
                    TableFilterOperator.OnOrBefore => "OnOrBefore",
                    TableFilterOperator.OnOrAfter => "OnOrAfter",
                    _ => "On"
                };
                return $"Microsoft.Dynamics.CRM.{function}(PropertyName='{name}',PropertyValue='{date:yyyy-MM-dd}')";
        }

        error = $"{Name(column)} cannot be filtered.";
        return null;
    }

    /// <summary>Combines filters with "and", leaving out the empty ones; null when none is left.</summary>
    public static string? And(params string?[] filters)
    {
        var parts = filters.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join(" and ", parts.Select(p => $"({p})"))
        };
    }

    public static bool IsChoice(EntityColumn column) => KindOf(column) == Kind.Choice;

    /// <summary>The column as a message names it: its display name, else its logical name.</summary>
    private static string Name(EntityColumn column) =>
        string.IsNullOrWhiteSpace(column.DisplayName) ? column.LogicalName : column.DisplayName;

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Comparison(TableFilterOperator op) => op switch
    {
        TableFilterOperator.NotEquals => "ne",
        TableFilterOperator.GreaterThan => "gt",
        TableFilterOperator.LessThan => "lt",
        _ => "eq"
    };

    private enum Kind { Text, Number, Choice, MultiChoice, Boolean, Guid, Date }

    /// <summary>Text for anything else - string, memo, and whatever a virtual table calls its columns.</summary>
    private static Kind KindOf(EntityColumn column) => column switch
    {
        { IsDateTime: true } => Kind.Date,
        { IsNumeric: true } => Kind.Number,
        { IsBoolean: true } => Kind.Boolean,
        { IsLookup: true } or { IsUniqueIdentifier: true } => Kind.Guid,
        { TypeName: "MultiSelectPicklistType" } => Kind.MultiChoice,
        _ when ChoiceTypes.Contains(column.TypeName) => Kind.Choice,
        _ => Kind.Text
    };
}
