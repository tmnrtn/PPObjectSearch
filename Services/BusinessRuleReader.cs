using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PPObjectSearch.Services;

/// <summary>One line of a business rule read as steps: a condition, or something the rule does.</summary>
/// <param name="Depth">How far the step is nested - 0 for the rule's own conditions, 1 for what they do...</param>
/// <param name="Kind">If, Else if, Else, Action or Other.</param>
/// <param name="Text">The step in words - "If creditlimit contains data", "Make name required".</param>
public sealed record BusinessRuleStep(int Depth, string Kind, string Text)
{
    /// <summary>The step indented by its depth, for a one-column list.</summary>
    public string Indented => new string(' ', Depth * 4) + Text;
}

/// <summary>
/// Reads a business rule's workflow XAML as the steps the rule designer shows: its conditions as
/// If / Else if / Else, and its actions as "Lock field", "Make field required" and the like.
/// Element names are matched on their local names, whatever prefix the XAML maps them to; the
/// plumbing between (variables, property reads, expression evaluation) is left out.
/// </summary>
public static partial class BusinessRuleReader
{
    public const string If = "If";
    public const string ElseIf = "Else if";
    public const string Else = "Else";
    public const string Action = "Action";
    public const string Other = "Other";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Activities that only move values about - never steps of their own.</summary>
    private static readonly HashSet<string> Plumbing = new(StringComparer.Ordinal)
    {
        "Variable", "Assign", "Persist", "GetEntityProperty", "InArgument", "OutArgument", "InOutArgument",
        "Null", "Members", "Property", "VisualBasic.Settings", "TerminateWorkflow", "Stop"
    };

    /// <summary>ActivityReference types that are plumbing.</summary>
    private static readonly HashSet<string> PlumbingReferences = new(StringComparer.Ordinal)
    {
        "EvaluateCondition", "EvaluateLogicalCondition", "EvaluateExpression", "Composite"
    };

    /// <summary>The rule's steps in order; empty where the XAML cannot be read or holds nothing recognised.</summary>
    public static IReadOnlyList<BusinessRuleStep> Read(string? xaml)
    {
        if (string.IsNullOrWhiteSpace(xaml))
            return [];

        try
        {
            var root = XDocument.Parse(xaml).Root;
            if (root is null)
                return [];

            var reader = new Reading(root);
            reader.Walk(root.Elements(), 0);
            return reader.Steps;
        }
        catch (Exception)
        {
            // Malformed or unexpected XAML: the caller says the rule could not be read.
            return [];
        }
    }

    private sealed class Reading
    {
        public List<BusinessRuleStep> Steps { get; } = new();

        /// <summary>Variable name to the field it holds, from GetEntityProperty.</summary>
        private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

        /// <summary>Variable name to the literal value it holds, from EvaluateExpression.</summary>
        private readonly Dictionary<string, string> _literals = new(StringComparer.Ordinal);

        /// <summary>Variable name to the condition it holds in words, from Evaluate(Logical)Condition.</summary>
        private readonly Dictionary<string, string> _conditions = new(StringComparer.Ordinal);

        public Reading(XElement root)
        {
            var all = root.DescendantsAndSelf().ToList();

            foreach (var e in all.Where(e => e.Name.LocalName == "GetEntityProperty"))
                if (Var(Attr(e, "Value")) is { } v && Attr(e, "Attribute") is { Length: > 0 } a)
                    _fields[v] = a;

            foreach (var e in all.Where(e => ReferenceType(e) == "EvaluateExpression"))
            {
                if (Var(Argument(e, "Result")) is not { } v)
                    continue;
                var parameters = Argument(e, "Parameters") ?? string.Empty;
                if (Quoted(parameters).FirstOrDefault() is { } literal)
                    _literals[v] = literal;
                else if (Identifiers(parameters).Select(FieldOrLiteral).FirstOrDefault(x => x is not null) is { } other)
                    _literals[v] = other;
            }

            // Conditions refer to each other (And/Or of earlier results), so read them in document order.
            foreach (var e in all)
            {
                switch (ReferenceType(e))
                {
                    case "EvaluateCondition" when Var(Argument(e, "Result")) is { } result:
                        _conditions[result] = Condition(e);
                        break;
                    case "EvaluateLogicalCondition" when Var(Argument(e, "Result")) is { } result:
                        _conditions[result] = Logical(e);
                        break;
                }
            }
        }

        public void Walk(IEnumerable<XElement> elements, int depth)
        {
            foreach (var e in elements)
                Visit(e, depth);
        }

        private void Visit(XElement e, int depth)
        {
            var name = e.Name.LocalName;
            if (Plumbing.Contains(name))
                return;

            // A property element (Activity.Members, ActivityReference.Arguments...) is not an activity.
            if (name.Contains('.'))
                return;

            if (name == "ActivityReference")
            {
                VisitReference(e, depth);
                return;
            }

            if (Act(e) is { } text)
            {
                Add(depth, Action, text);
                return;
            }

            switch (name)
            {
                case "SetDefaultValue":
                    // The value is set just before by SetEntityProperty; this makes it the default.
                    if (_lastSetField is { } defaulted && _lastSetIndex == Steps.Count - 1 && Steps[^1].Depth == depth)
                        Steps[^1] = Steps[^1] with { Text = "Set default value of " + defaulted + _lastSetValue };
                    else
                        Add(depth, Action, "Set default value of " + (FieldOf(e) ?? "a field"));
                    return;

                case "SetAttributeValue":
                    // Usually follows the SetEntityProperty that holds the field and value - already said.
                    if (FieldOf(e) is { } own && !(_lastSetIndex == Steps.Count - 1 && _lastSetField == own))
                        Add(depth, Action, "Set " + own + " value");
                    else if (FieldOf(e) is null && _lastSetIndex != Steps.Count - 1)
                        Add(depth, Action, "Set a field value");
                    return;

                case "SetEntityProperty":
                {
                    var field = Attr(e, "Attribute") ?? "a field";
                    var value = ValueOf(Attr(e, "Value")) is { } v ? " to " + v : string.Empty;
                    Add(depth, Action, $"Set {field} value{value}");
                    _lastSetField = field;
                    _lastSetValue = value;
                    _lastSetIndex = Steps.Count - 1;
                    return;
                }

                case "Recommendation":
                    Add(depth, Action, "Recommendation on " + (FieldOf(e) ?? "a field")
                        + (Attr(e, "Title") is { Length: > 0 } title ? ": " + title : string.Empty));
                    Walk(Activities(e), depth + 1);
                    return;
            }

            if (DisplayName(e) is { } shown && !IsContainer(name))
            {
                Add(depth, Other, shown);
                return;
            }

            // Workflow, Sequence, Activity, collections: walk what they hold.
            Walk(e.Elements(), depth);
        }

        private string? _lastSetField;
        private string _lastSetValue = string.Empty;
        private int _lastSetIndex = -1;

        private void VisitReference(XElement e, int depth)
        {
            switch (ReferenceType(e))
            {
                case "ConditionSequence":
                    Sequence(e, depth);
                    return;
                case "ConditionBranch":
                    Branch(e, depth, If);
                    return;
                case { } type when PlumbingReferences.Contains(type):
                    if (type == "Composite")
                        Walk(Activities(e), depth);
                    return;
                default:
                    if (DisplayName(e) is { } shown)
                        Add(depth, Other, shown);
                    return;
            }
        }

        /// <summary>A condition step: its branches in turn, the first an If, the rest Else if or Else.</summary>
        private void Sequence(XElement e, int depth)
        {
            var first = true;
            foreach (var child in Activities(e))
            {
                if (ReferenceType(child) == "ConditionBranch")
                {
                    Branch(child, depth, first ? If : ElseIf);
                    first = false;
                }
                else
                {
                    Visit(child, depth);
                }
            }
        }

        private void Branch(XElement e, int depth, string kind)
        {
            var condition = Argument(e, "Condition");
            var always = condition is null || Unbracket(condition).Equals("True", StringComparison.OrdinalIgnoreCase);

            if (kind == ElseIf && always)
                Add(depth, Else, Else);
            else
                Add(depth, kind, kind + " " + ConditionText(condition));

            if (Property(e, "Then") is { } then)
                Walk(BodyOf(then), depth + 1);

            // An Else held by the branch itself rather than as a sibling branch.
            if (Property(e, "Else") is { } otherwise && BodyOf(otherwise).Any())
            {
                var at = Steps.Count;
                Add(depth, Else, Else);
                var before = Steps.Count;
                var body = BodyOf(otherwise).ToList();

                // Else holding just another condition reads as Else if.
                if (body.Count == 1 && ReferenceType(body[0]) is "ConditionSequence" or "ConditionBranch")
                {
                    Steps.RemoveAt(at);
                    if (ReferenceType(body[0]) == "ConditionBranch")
                        Branch(body[0], depth, ElseIf);
                    else
                        ElseIfSequence(body[0], depth);
                }
                else
                {
                    Walk(body, depth + 1);
                    if (Steps.Count == before)
                        Steps.RemoveAt(at);
                }
            }
        }

        private void ElseIfSequence(XElement e, int depth)
        {
            foreach (var child in Activities(e))
            {
                if (ReferenceType(child) == "ConditionBranch")
                    Branch(child, depth, ElseIf);
                else
                    Visit(child, depth);
            }
        }

        private string ConditionText(string? condition)
        {
            if (Var(condition) is { } v && _conditions.TryGetValue(v, out var text))
                return text;
            return "(condition)";
        }

        private string Condition(XElement e)
        {
            var op = Unbracket(Argument(e, "ConditionOperator") ?? string.Empty);
            var dot = op.LastIndexOf('.');
            if (dot >= 0)
                op = op[(dot + 1)..];

            var operand = FieldOrLiteral(Var(Argument(e, "Operand")) ?? string.Empty) ?? "(value)";
            var values = Identifiers(Argument(e, "Parameters") ?? string.Empty)
                .Select(FieldOrLiteral).Where(x => x is not null).Cast<string>()
                .Concat(Quoted(Argument(e, "Parameters") ?? string.Empty))
                .ToList();

            var phrase = OperatorPhrase(op);
            return values.Count == 0
                ? $"{operand} {phrase}"
                : $"{operand} {phrase} {string.Join(" and ", values)}";
        }

        private string Logical(XElement e)
        {
            var op = Unbracket(Argument(e, "LogicalOperator") ?? "And");
            var dot = op.LastIndexOf('.');
            if (dot >= 0)
                op = op[(dot + 1)..];
            var joiner = op.Equals("Or", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";

            string Side(string key) =>
                Var(Argument(e, key)) is { } v && _conditions.TryGetValue(v, out var text) ? text : "(condition)";

            return Side("LeftOperand") + joiner + Side("RightOperand");
        }

        private string? FieldOrLiteral(string name) =>
            _fields.TryGetValue(name, out var field) ? field
            : _literals.TryGetValue(name, out var literal) ? literal
            : null;

        private string? ValueOf(string? reference) =>
            Var(reference) is { } v ? FieldOrLiteral(v) : null;

        /// <summary>The words for an action element, or null where it is not one of the known actions.</summary>
        private string? Act(XElement e)
        {
            switch (e.Name.LocalName)
            {
                case "SetDisplayMode":
                    return (IsTrue(Attr(e, "IsReadOnly")) ? "Lock " : "Unlock ") + (FieldOf(e) ?? "a field");
                case "SetVisibility":
                    return (IsTrue(Attr(e, "IsVisible")) ? "Show " : "Hide ") + (FieldOf(e) ?? "a field");
                case "SetFieldRequiredLevel":
                {
                    var level = (Attr(e, "RequiredLevel") ?? Argument(e, "RequiredLevel") ?? string.Empty).Trim();
                    var words = level.ToLowerInvariant() switch
                    {
                        "required" or "applicationrequired" or "systemrequired" => "required",
                        "recommended" => "recommended",
                        "none" => "not required",
                        _ => level.Length > 0 ? level : "required"
                    };
                    return $"Make {FieldOf(e) ?? "a field"} {words}";
                }
                case "SetMessage":
                {
                    var text = "Show error message on " + (FieldOf(e) ?? "a field");
                    return MessageOf(e) is { } message ? $"{text}: \"{message}\"" : text;
                }
                default:
                    return null;
            }
        }

        /// <summary>The message a SetMessage shows: a value it refers to, or the expression just before it.</summary>
        private string? MessageOf(XElement e)
        {
            foreach (var text in e.Attributes().Where(a => a.Name.LocalName is not ("Entity" or "EntityName" or "ControlId" or "ControlType" or "DisplayName"))
                         .Select(a => a.Value)
                         .Concat(e.Descendants().Where(d => !d.HasElements).Select(d => d.Value)))
            {
                if (ValueOf(text) is { } literal)
                    return literal;
                if (Quoted(text).FirstOrDefault() is { } quoted)
                    return quoted;
            }

            foreach (var before in e.ElementsBeforeSelf().Reverse())
            {
                if (ReferenceType(before) == "EvaluateExpression")
                    return Var(Argument(before, "Result")) is { } v && _literals.TryGetValue(v, out var literal) ? literal : null;
                if (before.Name.LocalName is not ("Assign" or "GetEntityProperty" or "ActivityReference"))
                    break;
            }

            return null;
        }

        private void Add(int depth, string kind, string text) =>
            Steps.Add(new BusinessRuleStep(depth, kind, text));
    }

    // ---------------------------------------------------------------- XAML helpers

    private static bool IsContainer(string name) =>
        name is "Activity" or "Workflow" or "Sequence" or "Collection" or "Flowchart";

    private static string? Attr(XElement e, string local) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == local)?.Value;

    private static string? DisplayName(XElement e) =>
        Attr(e, "DisplayName") is { Length: > 0 } shown ? shown : null;

    private static string? FieldOf(XElement e) =>
        Attr(e, "ControlId") is { Length: > 0 } control ? control
        : Attr(e, "Attribute") is { Length: > 0 } attribute ? attribute
        : null;

    private static bool IsTrue(string? value) =>
        value is not null && Unbracket(value).Equals("True", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Microsoft.Crm.Workflow.Activities.ConditionBranch, Microsoft.Crm.Workflow, ..." as ConditionBranch.</summary>
    private static string? ReferenceType(XElement e)
    {
        if (e.Name.LocalName != "ActivityReference" || Attr(e, "AssemblyQualifiedName") is not { } aqn)
            return null;
        var type = aqn.Split(',')[0].Trim();
        var dot = type.LastIndexOf('.');
        return dot >= 0 ? type[(dot + 1)..] : type;
    }

    /// <summary>An ActivityReference's argument by key, as the text it holds.</summary>
    private static string? Argument(XElement e, string key)
    {
        foreach (var holder in e.Elements().Where(c => c.Name.LocalName.EndsWith(".Arguments", StringComparison.Ordinal)))
        {
            foreach (var argument in holder.Elements())
            {
                if ((string?)argument.Attribute(Xaml + "Key") != key)
                    continue;
                if (argument.Name.LocalName == "Null")
                    return null;
                return argument.Value.Trim();
            }
        }

        return Attr(e, key);
    }

    /// <summary>An ActivityReference's property by key (Then, Else, Activities).</summary>
    private static XElement? Property(XElement e, string key) =>
        e.Elements()
            .Where(c => c.Name.LocalName.EndsWith(".Properties", StringComparison.Ordinal))
            .SelectMany(c => c.Elements())
            .FirstOrDefault(p => (string?)p.Attribute(Xaml + "Key") == key && p.Name.LocalName != "Null");

    /// <summary>The activities an ActivityReference holds.</summary>
    private static IEnumerable<XElement> Activities(XElement e) =>
        Property(e, "Activities")?.Elements() ?? Enumerable.Empty<XElement>();

    /// <summary>What a Then or Else holds: a Composite's activities, or the activity itself.</summary>
    private static IEnumerable<XElement> BodyOf(XElement e) =>
        ReferenceType(e) == "Composite" ? Activities(e) : [e];

    private static string Unbracket(string text)
    {
        text = text.Trim();
        return text.Length >= 2 && text[0] == '[' && text[^1] == ']' ? text[1..^1].Trim() : text;
    }

    /// <summary>The variable a "[name]" expression refers to.</summary>
    private static string? Var(string? expression)
    {
        if (expression is null)
            return null;
        var inner = Unbracket(expression);
        return IdentifierOnly().IsMatch(inner) ? inner : null;
    }

    /// <summary>Quoted strings in a VB expression - "Contoso" in New Object() { ..., "Contoso", "String" }.</summary>
    private static IEnumerable<string> Quoted(string expression) =>
        QuotedString().Matches(expression).Select(m => m.Groups[1].Value.Replace("\"\"", "\"")).Take(1);

    /// <summary>Bare names in a VB expression, for variables it refers to.</summary>
    private static IEnumerable<string> Identifiers(string expression) =>
        Identifier().Matches(QuotedString().Replace(expression, string.Empty)).Select(m => m.Value);

    private static string OperatorPhrase(string op) => op switch
    {
        "Equal" => "equals",
        "NotEqual" => "does not equal",
        "GreaterThan" => "is greater than",
        "GreaterEqual" => "is greater than or equal to",
        "LessThan" => "is less than",
        "LessEqual" => "is less than or equal to",
        "Null" => "does not contain data",
        "NotNull" => "contains data",
        "Like" or "Contains" => "contains",
        "NotLike" or "DoesNotContain" => "does not contain",
        "BeginsWith" => "begins with",
        "DoesNotBeginWith" => "does not begin with",
        "EndsWith" => "ends with",
        "DoesNotEndWith" => "does not end with",
        "In" => "is one of",
        "NotIn" => "is not one of",
        "" => "matches",
        _ => SplitWords(op)
    };

    private static string SplitWords(string text) =>
        CamelBoundary().Replace(text, " $1").Trim().ToLowerInvariant();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierOnly();

    [GeneratedRegex(@"""((?:[^""]|"""")*)""")]
    private static partial Regex QuotedString();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"(?<!^)([A-Z])")]
    private static partial Regex CamelBoundary();
}
