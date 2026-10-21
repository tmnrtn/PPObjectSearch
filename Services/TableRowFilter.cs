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
        Kind.Lookup => [TableFilterOperator.Contains, TableFilterOperator.Equals, TableFilterOperator.BeginsWith,
            TableFilterOperator.NotEquals, TableFilterOperator.IsEmpty, TableFilterOperator.IsNotEmpty],
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
        Kind.Lookup => "the related row's name, or its id",
        _ => "text"
    };

    /// <summary>
    /// Whether this comparison is on the name of the row a lookup points at rather than its id -
    /// contains and begins with always are; equals and does not equal are unless a GUID is typed.
    /// The caller reads the lookup's navigations only when it is.
    /// </summary>
    public static bool NeedsLookupNames(EntityColumn column, TableFilterOperator op, string? value) =>
        KindOf(column) == Kind.Lookup && op switch
        {
            TableFilterOperator.Contains or TableFilterOperator.BeginsWith => true,
            TableFilterOperator.Equals or TableFilterOperator.NotEquals => !Guid.TryParse(value?.Trim(), out _),
            _ => false
        };

    /// <summary>
    /// The filter expression, or null with <paramref name="error"/> saying what is wrong with the
    /// value. A choice's label is turned into its number by the caller, who has the labels; a
    /// lookup compared by name needs the <paramref name="lookupNames"/> the caller read for it.
    /// </summary>
    public static string? Build(
        EntityColumn column, TableFilterOperator op, string? value, out string? error,
        IReadOnlyList<Dataverse.LookupNavigation>? lookupNames = null)
    {
        error = null;
        var name = column.SelectName;
        var kind = KindOf(column);

        if (!OperatorsFor(column).Contains(op)) return Invalid($"{Name(column)} cannot be compared with \"{Label(op)}\".", out error);

        if (op == TableFilterOperator.IsEmpty) return $"{name} eq null";
        if (op == TableFilterOperator.IsNotEmpty) return $"{name} ne null";

        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) return Invalid($"Type {ValueHint(column)} to compare {Name(column)} with.", out error);

        return kind switch
        {
            Kind.Text => TextFilter(name, op, text),
            Kind.Lookup when NeedsLookupNames(column, op, text) => LookupNameFilter(column, op, text, lookupNames, out error),
            Kind.Lookup or Kind.Guid => IdFilter(name, op, text, out error),
            Kind.Number or Kind.Choice => NumberFilter(column, kind, op, text, out error),
            Kind.MultiChoice => MultiChoiceFilter(name, text, out error),
            Kind.Boolean => BooleanFilter(name, op, text, out error),
            Kind.Date => DateFilter(name, op, text, out error),
            _ => Invalid($"{Name(column)} cannot be filtered.", out error)
        };
    }

    /// <summary>No filter, and why: the value's problem, said to the user.</summary>
    private static string? Invalid(string message, out string? error)
    {
        error = message;
        return null;
    }

    private static string TextFilter(string name, TableFilterOperator op, string text)
    {
        var quoted = Quote(text);
        return op switch
        {
            TableFilterOperator.Contains => $"contains({name},{quoted})",
            TableFilterOperator.BeginsWith => $"startswith({name},{quoted})",
            TableFilterOperator.NotEquals => $"{name} ne {quoted}",
            _ => $"{name} eq {quoted}"
        };
    }

    private static string? IdFilter(string name, TableFilterOperator op, string text, out string? error)
    {
        error = null;
        return Guid.TryParse(text, out var id)
            ? $"{name} {Comparison(op)} {id}"
            : Invalid($"\"{text}\" is not a row id (GUID).", out error);
    }

    private static string? NumberFilter(EntityColumn column, Kind kind, TableFilterOperator op, string text, out string? error)
    {
        error = null;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
            !decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out number))
        {
            return Invalid($"\"{text}\" is not {ValueHint(column)}.", out error);
        }
        if (kind == Kind.Choice && number != decimal.Truncate(number))
        {
            return Invalid($"\"{text}\" is not a choice's number.", out error);
        }
        return $"{column.SelectName} {Comparison(op)} {number.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? MultiChoiceFilter(string name, string text, out string? error)
    {
        error = null;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var option)
            ? $"Microsoft.Dynamics.CRM.ContainValues(PropertyName='{name}',PropertyValues=['{option}'])"
            : Invalid($"\"{text}\" is not a choice's number.", out error);
    }

    private static string? BooleanFilter(string name, TableFilterOperator op, string text, out string? error)
    {
        error = null;
        bool? flag = text.ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null
        };
        if (flag is not { } yes) return Invalid($"\"{text}\" is not yes or no.", out error);
        return $"{name} {Comparison(op)} {(yes ? "true" : "false")}";
    }

    private static string? DateFilter(string name, TableFilterOperator op, string text, out string? error)
    {
        error = null;
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
            !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
        {
            return Invalid($"\"{text}\" is not a date.", out error);
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

    /// <summary>
    /// A comparison on the name of the row a lookup points at, through each table it can point at:
    /// <c>contains(new_parentid/new_name,'x')</c>. A customer or owner lookup points at one table per
    /// row, so the tables are joined with "or" - except for does not equal, where a row must differ
    /// through every one (the tables it does not point at compare as null, which differs too).
    /// </summary>
    private static string? LookupNameFilter(
        EntityColumn column, TableFilterOperator op, string text,
        IReadOnlyList<Dataverse.LookupNavigation>? lookupNames, out string? error)
    {
        if (lookupNames is not { Count: > 0 })
        {
            error = $"The name of the row {Name(column)} points at cannot be filtered on here - type the row's id (GUID) instead.";
            return null;
        }

        error = null;
        var quoted = Quote(text);
        var parts = lookupNames.Select(n =>
        {
            var path = $"{n.NavigationProperty}/{n.TargetNameColumn}";
            return op switch
            {
                TableFilterOperator.Contains => $"contains({path},{quoted})",
                TableFilterOperator.BeginsWith => $"startswith({path},{quoted})",
                TableFilterOperator.NotEquals => $"{path} ne {quoted}",
                _ => $"{path} eq {quoted}"
            };
        }).ToList();

        if (parts.Count == 1) return parts[0];

        var join = op == TableFilterOperator.NotEquals ? " and " : " or ";
        return "(" + string.Join(join, parts) + ")";
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

    private enum Kind { Text, Number, Choice, MultiChoice, Boolean, Guid, Lookup, Date }

    /// <summary>Text for anything else - string, memo, and whatever a virtual table calls its columns.</summary>
    private static Kind KindOf(EntityColumn column) => column switch
    {
        { IsDateTime: true } => Kind.Date,
        { IsNumeric: true } => Kind.Number,
        { IsBoolean: true } => Kind.Boolean,
        { IsLookup: true } => Kind.Lookup,
        { IsUniqueIdentifier: true } => Kind.Guid,
        { TypeName: "MultiSelectPicklistType" } => Kind.MultiChoice,
        _ when ChoiceTypes.Contains(column.TypeName) => Kind.Choice,
        _ => Kind.Text
    };
}
