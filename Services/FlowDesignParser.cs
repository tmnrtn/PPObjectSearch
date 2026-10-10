using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// Reads a cloud flow's definition (workflow.clientdata) into the shape the designer draws:
/// the trigger, then the actions in run-after order, with parallel branches side by side and
/// conditions, switches, loops and scopes holding their own steps.
///
/// Read-only and forgiving: anything it cannot place by run-after is still shown, at the end of
/// its container, and said so in the warnings - a diagram that silently drops a step is worse
/// than one that admits it could not order it.
/// </summary>
public static partial class FlowDesignParser
{
    // The property a definition, a condition's branches, a switch's cases and a scope keep their steps in.
    private const string ActionsProperty = "actions";

    // Loop types, lower-cased as the type switches below compare them.
    private const string ForEachType = "foreach";
    private const string UntilType = "until";

    /// <summary>The definition under "properties" (a solution export), else under "definition", else the root itself.</summary>
    private static JsonElement DefinitionOf(JsonElement root)
    {
        if (root.TryGetProperty("properties", out var properties) && properties.TryGetProperty("definition", out var wrapped))
        {
            return wrapped;
        }

        return root.TryGetProperty("definition", out var definition) ? definition : root;
    }

    public static FlowDesign Parse(string clientData)
    {
        // A flow nests two to four levels per scope, loop or switch case, under five for the
        // definition itself, plus whatever inline JSON its steps carry. The default limit of 64 turns
        // away legitimately nested flows that the Definition tab shows happily; the parse stays bounded.
        using var doc = JsonDocument.Parse(clientData, new JsonDocumentOptions { MaxDepth = 256 });
        var root = doc.RootElement;

        var definition = DefinitionOf(root);

        if (definition.ValueKind != JsonValueKind.Object ||
            (!definition.TryGetProperty("triggers", out _) && !definition.TryGetProperty(ActionsProperty, out _)))
        {
            throw new FormatException("The flow's definition has no triggers or actions.");
        }

        var connections = ReadConnectionReferences(
            root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object ? properties : root);
        var context = new Context(connections);

        var triggers = new List<FlowNode>();
        if (definition.TryGetProperty("triggers", out var triggerObject) && triggerObject.ValueKind == JsonValueKind.Object)
        {
            foreach (var trigger in triggerObject.EnumerateObject())
            {
                triggers.Add(ReadNode(trigger.Name, trigger.Value, isTrigger: true, context));
            }
        }

        var actions = definition.TryGetProperty(ActionsProperty, out var actionObject)
            ? ReadContainer(actionObject, "the flow", context)
            : FlowSequence.Empty;

        return new FlowDesign(triggers, actions, context.ActionCount, context.Warnings);
    }

    private sealed class Context(IReadOnlyDictionary<string, string> connections)
    {
        public IReadOnlyDictionary<string, string> Connections { get; } = connections;
        public List<string> Warnings { get; } = new();
        public int ActionCount { get; set; }
    }

    // ---------------------------------------------------------------- ordering

    /// <summary>The actions of one container, in the order and shape the designer shows them.</summary>
    private static FlowSequence ReadContainer(JsonElement actions, string where, Context context)
    {
        if (actions.ValueKind != JsonValueKind.Object) return FlowSequence.Empty;

        // Definition order, which is the designer's order wherever run-after does not decide.
        var nodes = new Dictionary<string, FlowNode>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var action in actions.EnumerateObject())
        {
            if (action.Value.ValueKind != JsonValueKind.Object) continue;

            nodes[action.Name] = ReadNode(action.Name, action.Value, isTrigger: false, context);
            order.Add(action.Name);
            context.ActionCount++;
        }

        var predecessors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var successors = order.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var name in order)
        {
            var preds = new List<string>();
            foreach (var predecessor in nodes[name].RunAfter.Select(runAfter => runAfter.Action))
            {
                if (nodes.ContainsKey(predecessor))
                {
                    preds.Add(predecessor);
                    successors[predecessor].Add(name);
                }
                else
                {
                    context.Warnings.Add($"'{nodes[name].DisplayName}' runs after '{predecessor.Replace('_', ' ')}', which is not in {where}.");
                }
            }

            predecessors[name] = preds;
        }

        var placer = new Placer(nodes, order, predecessors, successors);
        var heads = order.Where(n => predecessors[n].Count == 0).ToList();
        var steps = placer.Chain(heads, inBranch: false, new List<string>());

        // Whatever run-after could not reach - a cycle, or a join waiting on something unplaced.
        var leftovers = order.Where(n => !placer.Placed.Contains(n)).ToList();
        if (leftovers.Count > 0)
        {
            context.Warnings.Add($"{leftovers.Count} step(s) in {where} could not be ordered by run-after and are shown at the end: " +
                                 string.Join(", ", leftovers.Select(n => nodes[n].DisplayName)) + ".");
            steps.AddRange(leftovers.Select(n => new FlowActionStep(nodes[n])));
        }

        return new FlowSequence(steps);
    }

    private sealed class Placer(
        IReadOnlyDictionary<string, FlowNode> nodes,
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, List<string>> predecessors,
        IReadOnlyDictionary<string, List<string>> successors)
    {
        public HashSet<string> Placed { get; } = new(StringComparer.Ordinal);

        private bool Ready(string name) => !Placed.Contains(name) && predecessors[name].TrueForAll(Placed.Contains);

        private IEnumerable<string> InOrder(IEnumerable<string> names)
        {
            var set = names.ToHashSet(StringComparer.Ordinal);
            return order.Where(set.Contains);
        }

        /// <summary>
        /// Steps from <paramref name="heads"/> on. In a parallel branch, a step waiting on more
        /// than one predecessor is a join: the branch stops there and hands it to the group, which
        /// places it after the branches - in <paramref name="joins"/>.
        /// </summary>
        public List<IFlowStep> Chain(IReadOnlyList<string> heads, bool inBranch, List<string> joins)
        {
            var steps = new List<IFlowStep>();
            var current = heads.ToList();

            while (current.Count > 0)
            {
                if (current.Count == 1)
                {
                    var name = current[0];
                    Placed.Add(name);
                    steps.Add(new FlowActionStep(nodes[name]));

                    var next = successors[name].Where(s => !Placed.Contains(s)).Distinct().ToList();

                    if (inBranch)
                    {
                        joins.AddRange(next.Where(s => predecessors[s].Count > 1));
                        next = next.Where(s => predecessors[s].Count == 1).ToList();
                    }
                    else
                    {
                        // A step waiting on others is reached again when the last of them is placed.
                        next = next.Where(Ready).ToList();
                    }

                    current = InOrder(next).ToList();
                }
                else
                {
                    var groupJoins = new List<string>();
                    var branches = current
                        .Select(head => new FlowSequence(Chain(new[] { head }, inBranch: true, groupJoins)))
                        .ToList();

                    steps.Add(new FlowParallelStep(branches));

                    var distinct = groupJoins.Distinct().ToList();
                    var ready = distinct.Where(Ready).ToList();

                    // A join also waiting on a step outside this group belongs further out.
                    if (inBranch) joins.AddRange(distinct.Except(ready));

                    current = InOrder(ready).ToList();
                }
            }

            return steps;
        }
    }

    // ---------------------------------------------------------------- one step

    private static FlowNode ReadNode(string name, JsonElement element, bool isTrigger, Context context)
    {
        var type = Str(element, "type") ?? "Unknown";
        var kind = isTrigger ? FlowNodeKind.Trigger : KindOf(type);
        var inputs = element.TryGetProperty("inputs", out var i) ? i : default;

        var (connector, operation) = ReadConnector(inputs, context.Connections);
        var category = isTrigger ? FlowNodeCategory.Trigger : CategoryOf(type, kind, connector);

        return new FlowNode
        {
            Name = name,
            Type = type,
            Kind = kind,
            Category = category,
            TypeLabel = isTrigger ? TriggerLabel(type, element, connector, operation) : TypeLabelOf(type, inputs),
            Connector = connector,
            Operation = operation,
            Detail = DetailOf(type, element, inputs),
            Description = Str(element, "description"),
            RunAfter = ReadRunAfter(element),
            Branches = ReadBranches(kind, element, name, context),
            Json = JsonWithoutChildren(element),
            ChildFlowId = ChildFlowIdOf(type, inputs)
        };
    }

    /// <summary>
    /// A child flow call names its flow by workflowid - checked against real flows, where every
    /// reference matched a workflowid and none the solution-independent id.
    /// </summary>
    private static Guid? ChildFlowIdOf(string type, JsonElement inputs) =>
        type.Equals("Workflow", StringComparison.OrdinalIgnoreCase) &&
        inputs.ValueKind == JsonValueKind.Object &&
        inputs.TryGetProperty("host", out var host) &&
        Guid.TryParse(Str(host, "workflowReferenceName"), out var id)
            ? id
            : null;

    private static FlowNodeKind KindOf(string type) => type.ToLowerInvariant() switch
    {
        "if" => FlowNodeKind.Condition,
        "switch" => FlowNodeKind.Switch,
        ForEachType => FlowNodeKind.ForEach,
        UntilType => FlowNodeKind.Until,
        "scope" => FlowNodeKind.Scope,
        _ => FlowNodeKind.Action
    };

    private static FlowNodeCategory CategoryOf(string type, FlowNodeKind kind, string? connector)
    {
        if (kind != FlowNodeKind.Action) return FlowNodeCategory.Control;
        if (connector is not null) return FlowNodeCategory.Connector;

        return type.ToLowerInvariant() switch
        {
            "compose" or "parsejson" or "query" or "select" or "join" or "table" or "javascriptcode" => FlowNodeCategory.Data,
            "initializevariable" or "setvariable" or "incrementvariable" or "decrementvariable"
                or "appendtoarrayvariable" or "appendtostringvariable" => FlowNodeCategory.Variable,
            "http" or "httpwebhook" or "response" => FlowNodeCategory.Http,
            "workflow" => FlowNodeCategory.ChildFlow,
            "terminate" or "wait" => FlowNodeCategory.Control,
            _ when type.Contains("Connection", StringComparison.OrdinalIgnoreCase) => FlowNodeCategory.Connector,
            _ => FlowNodeCategory.Other
        };
    }

    private static string TypeLabelOf(string type, JsonElement inputs) => type.ToLowerInvariant() switch
    {
        "if" => "Condition",
        "switch" => "Switch",
        ForEachType => "Apply to each",
        UntilType => "Do until",
        "scope" => "Scope",
        "compose" => "Compose",
        "parsejson" => "Parse JSON",
        "query" => "Filter array",
        "select" => "Select",
        "join" => "Join",
        "table" => Str(inputs, "format") is { } format ? $"Create {format.ToUpperInvariant()} table" : "Create table",
        "javascriptcode" => "Execute JavaScript code",
        "initializevariable" => "Initialize variable",
        "setvariable" => "Set variable",
        "incrementvariable" => "Increment variable",
        "decrementvariable" => "Decrement variable",
        "appendtoarrayvariable" => "Append to array variable",
        "appendtostringvariable" => "Append to string variable",
        "http" => "HTTP",
        "httpwebhook" => "HTTP webhook",
        "response" => "Response",
        "workflow" => "Run a child flow",
        "terminate" => "Terminate",
        "wait" => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty("until", out _) ? "Delay until" : "Delay",
        _ => SplitWords(type)
    };

    private static string TriggerLabel(string type, JsonElement element, string? connector, string? operation)
    {
        if (connector is not null) return operation ?? "Connector trigger";

        var kind = Str(element, "kind");

        return type.ToLowerInvariant() switch
        {
            "recurrence" => "Recurrence",
            "request" => kind?.ToLowerInvariant() switch
            {
                "button" => "Manually trigger a flow",
                "powerapp" or "powerappv2" => "When Power Apps calls a flow",
                "http" => "When an HTTP request is received",
                "skills" => "When a flow is run from Copilot",
                _ => "When a request is received"
            },
            _ => SplitWords(type)
        };
    }

    /// <summary>A short particular worth seeing on the card itself.</summary>
    private static string? DetailOf(string type, JsonElement element, JsonElement inputs) => type.ToLowerInvariant() switch
    {
        ForEachType => element.TryGetProperty("foreach", out var each) ? Short(each) : null,
        UntilType => element.TryGetProperty("expression", out var until) ? Short(until) : null,
        "initializevariable" => InitializedVariableOf(inputs),
        "setvariable" or "incrementvariable" or "decrementvariable"
            or "appendtoarrayvariable" or "appendtostringvariable" => Str(inputs, "name"),
        "terminate" => Str(inputs, "runStatus"),
        "recurrence" => RecurrenceOf(element),
        "wait" => DelayOf(inputs),
        "workflow" => ChildFlowReferenceOf(inputs),
        _ => null
    };

    /// <summary>"name (type)" of the first variable an Initialize variable step declares.</summary>
    private static string? InitializedVariableOf(JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object ||
            !inputs.TryGetProperty("variables", out var variables) ||
            variables.ValueKind != JsonValueKind.Array ||
            variables.GetArrayLength() == 0)
        {
            return null;
        }

        var v = variables[0];
        return Str(v, "type") is { } t ? $"{Str(v, "name")} ({t})" : Str(v, "name");
    }

    /// <summary>"Every 15 minutes" - null without a frequency.</summary>
    private static string? RecurrenceOf(JsonElement element)
    {
        if (!element.TryGetProperty("recurrence", out var recurrence)) return null;

        var interval = recurrence.TryGetProperty("interval", out var n) ? n.ToString() : "1";
        if (Str(recurrence, "frequency") is not { } frequency) return null;

        var plural = interval == "1" ? "" : "s";
        return $"Every {interval} {frequency.ToLowerInvariant()}{plural}";
    }

    /// <summary>A Delay step's "5 minute".</summary>
    private static string? DelayOf(JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty("interval", out var wait)) return null;

        return $"{(wait.TryGetProperty("count", out var c) ? c.ToString() : "?")} {Str(wait, "unit")?.ToLowerInvariant()}";
    }

    /// <summary>The workflowReferenceName a child flow call names its flow by.</summary>
    private static string? ChildFlowReferenceOf(JsonElement inputs) =>
        inputs.ValueKind == JsonValueKind.Object &&
        inputs.TryGetProperty("host", out var host) &&
        host.TryGetProperty("workflowReferenceName", out var child)
            ? child.GetString()
            : null;

    private static IReadOnlyList<FlowRunAfter> ReadRunAfter(JsonElement element)
    {
        if (!element.TryGetProperty("runAfter", out var runAfter) || runAfter.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<FlowRunAfter>();
        }

        return runAfter.EnumerateObject()
            .Select(p => new FlowRunAfter(
                p.Name,
                p.Value.ValueKind == JsonValueKind.Array
                    ? p.Value.EnumerateArray().Select(s => s.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
                    : new List<string> { "Succeeded" }))
            .ToList();
    }

    private static IReadOnlyList<FlowBranch> ReadBranches(FlowNodeKind kind, JsonElement element, string name, Context context)
    {
        var where = $"'{name.Replace('_', ' ')}'";

        switch (kind)
        {
            case FlowNodeKind.Condition:
                return new[]
                {
                    new FlowBranch("Yes", ActionsIn(element, where, context)),
                    new FlowBranch("No", element.TryGetProperty("else", out var otherwise) ? ActionsIn(otherwise, where, context) : FlowSequence.Empty)
                };

            case FlowNodeKind.Switch:
                return SwitchBranches(element, where, context);

            case FlowNodeKind.ForEach or FlowNodeKind.Until or FlowNodeKind.Scope:
                return new[] { new FlowBranch(string.Empty, ActionsIn(element, where, context)) };

            default:
                return Array.Empty<FlowBranch>();
        }
    }

    /// <summary>A switch's cases in definition order, then its default.</summary>
    private static List<FlowBranch> SwitchBranches(JsonElement element, string where, Context context)
    {
        var branches = new List<FlowBranch>();

        if (element.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Object)
        {
            foreach (var c in cases.EnumerateObject())
            {
                var value = c.Value.TryGetProperty("case", out var v) ? Short(v) : c.Name;
                branches.Add(new FlowBranch($"Case: {value}", ActionsIn(c.Value, where, context)));
            }
        }

        branches.Add(new FlowBranch("Default",
            element.TryGetProperty("default", out var fallback) ? ActionsIn(fallback, where, context) : FlowSequence.Empty));
        return branches;
    }

    /// <summary>The steps a branch, case or loop holds under "actions".</summary>
    private static FlowSequence ActionsIn(JsonElement container, string where, Context context) =>
        container.ValueKind == JsonValueKind.Object && container.TryGetProperty(ActionsProperty, out var a)
            ? ReadContainer(a, where, context)
            : FlowSequence.Empty;

    // ---------------------------------------------------------------- connectors

    /// <summary>Connection reference name to the connector's API name - "shared_commondataserviceforapps".</summary>
    private static Dictionary<string, string> ReadConnectionReferences(JsonElement scope)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (scope.ValueKind != JsonValueKind.Object ||
            !scope.TryGetProperty("connectionReferences", out var references) ||
            references.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var reference in references.EnumerateObject())
        {
            if (ReferenceApi(reference.Value) is { } api) map[reference.Name] = api;
        }

        return map;
    }

    /// <summary>A connection reference's API name: its "api" object's name, else "apiName", else the end of its id.</summary>
    private static string? ReferenceApi(JsonElement reference)
    {
        if (reference.TryGetProperty("api", out var api) && Str(api, "name") is { } apiName) return apiName;

        return Str(reference, "apiName") ?? LastSegment(Str(reference, "id"));
    }

    /// <summary>"/providers/Microsoft.PowerApps/apis/shared_office365" becomes "shared_office365".</summary>
    private static string? LastSegment(string? path) => path?.Split('/')[^1];

    [GeneratedRegex(@"\['(?<name>[^']+)'\]\['connectionId'\]")]
    private static partial Regex LegacyConnectionRegex();

    private static (string? Connector, string? Operation) ReadConnector(JsonElement inputs, IReadOnlyDictionary<string, string> connections)
    {
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var api = LastSegment(Str(host, "apiId"));

        if (api is null && Str(host, "connectionName") is { } reference)
        {
            api = connections.TryGetValue(reference, out var mapped) ? mapped : reference;
        }

        // The older ApiConnection shape names the connection inside an expression.
        if (api is null &&
            host.TryGetProperty("connection", out var connection) &&
            Str(connection, "name") is { } expression &&
            LegacyConnectionRegex().Match(expression) is { Success: true } match)
        {
            var name = match.Groups["name"].Value;
            api = connections.TryGetValue(name, out var legacy) ? legacy : name;
        }

        if (api is null) return (null, null);

        var operationId = Str(host, "operationId");
        return (ConnectorName(api), operationId is null ? null : OperationName(api, operationId));
    }

    private static readonly Dictionary<string, string> Connectors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shared_commondataserviceforapps"] = "Microsoft Dataverse",
        ["shared_commondataservice"] = "Common Data Service (legacy)",
        ["shared_office365"] = "Office 365 Outlook",
        ["shared_office365users"] = "Office 365 Users",
        ["shared_office365groups"] = "Office 365 Groups",
        ["shared_sharepointonline"] = "SharePoint",
        ["shared_teams"] = "Microsoft Teams",
        ["shared_approvals"] = "Approvals",
        ["shared_flowpush"] = "Notifications",
        ["shared_sendmail"] = "Mail",
        ["shared_excelonlinebusiness"] = "Excel Online (Business)",
        ["shared_onedriveforbusiness"] = "OneDrive for Business",
        ["shared_planner"] = "Planner",
        ["shared_azureblob"] = "Azure Blob Storage",
        ["shared_keyvault"] = "Azure Key Vault",
        ["shared_sql"] = "SQL Server",
        ["shared_webcontents"] = "HTTP with Microsoft Entra ID",
        ["shared_servicebus"] = "Service Bus",
        ["shared_powerplatformforadmins"] = "Power Platform for Admins",
        ["shared_flowmanagement"] = "Power Automate Management",
        ["shared_outlook"] = "Outlook.com",
        ["shared_wordonlinebusiness"] = "Word Online (Business)",
        ["shared_powerbi"] = "Power BI",
        ["shared_formsforbusiness"] = "Microsoft Forms",
        ["shared_microsoftforms"] = "Microsoft Forms",
    };

    private static readonly Dictionary<string, string> DataverseOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ListRecords"] = "List rows",
        ["GetItem"] = "Get a row by ID",
        ["CreateRecord"] = "Add a new row",
        ["UpdateRecord"] = "Update a row",
        ["UpdateOnlyRecord"] = "Update a row",
        ["DeleteRecord"] = "Delete a row",
        ["PerformBoundAction"] = "Perform a bound action",
        ["PerformUnboundAction"] = "Perform an unbound action",
        ["AssociateEntities"] = "Relate rows",
        ["DisassociateEntities"] = "Unrelate rows",
        ["ExecuteChangeset"] = "Perform a changeset request",
        ["SubscribeWebhookTrigger"] = "When a row is added, modified or deleted",
        ["GetEntityFileImageFieldContent"] = "Download a file or an image",
        ["UpdateEntityFileImageFieldContent"] = "Upload a file or an image",
        ["SearchRecords"] = "Search rows",
    };

    internal static string ConnectorName(string api)
    {
        if (Connectors.TryGetValue(api, out var name)) return name;

        return api.StartsWith("shared_", StringComparison.OrdinalIgnoreCase) ? api["shared_".Length..] : api;
    }

    internal static string OperationName(string api, string operationId) =>
        api.Equals("shared_commondataserviceforapps", StringComparison.OrdinalIgnoreCase) &&
        DataverseOperations.TryGetValue(operationId, out var known)
            ? known
            : SplitWords(operationId);

    // ---------------------------------------------------------------- helpers

    /// <summary>Between a lower case letter and a capital, before the last capital of a run, and at spaces.</summary>
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|\s+")]
    private static partial Regex WordBreakRegex();

    /// <summary>"GetItemV2" becomes "Get item V2"; "SendEmail_V2" becomes "Send email V2".</summary>
    internal static string SplitWords(string identifier)
    {
        var words = WordBreakRegex().Split(identifier.Replace('_', ' '))
            .Where(w => w.Length > 0)
            .ToList();

        if (words.Count == 0) return identifier;

        // Keep acronyms and version tags as they are; lower-case ordinary words after the first.
        return string.Join(" ", words.Select((w, i) =>
            i == 0 || w.All(char.IsUpper) || (w.Length <= 3 && w.Any(char.IsDigit)) ? w : w.ToLowerInvariant()));
    }

    private static string Short(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        return text.Length > 120 ? text[..117] + "..." : text;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// The step's JSON, minus the actions inside it: a condition shows its expression, a loop its
    /// input, without repeating every step it contains.
    /// </summary>
    private static string JsonWithoutChildren(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            Write(element, writer, depth: 0);
        }

        return Encoding.UTF8.GetString(stream.ToArray());

        static void Write(JsonElement value, Utf8JsonWriter writer, int depth)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                value.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                // "actions" at the top, and inside else/default/cases, are steps of their own.
                if (property.NameEquals(ActionsProperty) && depth <= 2)
                {
                    writer.WriteString(ActionsProperty, $"({property.Value.EnumerateObject().Count()} step(s), shown in the diagram)");
                    continue;
                }

                writer.WritePropertyName(property.Name);

                if (depth < 2 && (property.NameEquals("else") || property.NameEquals("default") || property.NameEquals("cases") ||
                                  (depth == 1 && property.Value.ValueKind == JsonValueKind.Object)))
                {
                    Write(property.Value, writer, depth + 1);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }
    }
}
