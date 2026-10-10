using System.Text;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// Writes a flow's design as a Mermaid flowchart, for pasting into documentation - a wiki, a
/// README, a pull request - wherever Mermaid renders.
///
/// Edges are the flow's run-after links, so parallel branches and joins come out as they run;
/// error paths (run after a failure, time-out or skip) are dotted and labelled. Conditions,
/// switches, loops and scopes become subgraphs, and a step after a container is joined to the
/// whole box, as it runs after the container rather than its last step.
/// </summary>
public static class FlowMermaidExporter
{
    public static string ToMermaid(FlowDesign design, string? flowName = null, IReadOnlyDictionary<Guid, string>? childFlowNames = null)
    {
        var writer = new Writer(design, childFlowNames);
        return writer.Write(flowName);
    }

    private sealed class Writer
    {
        private readonly FlowDesign _design;
        private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FlowNodeCategory> _classes = new(StringComparer.Ordinal);
        private readonly StringBuilder _text = new();

        private readonly IReadOnlyDictionary<Guid, string>? _childFlowNames;

        public Writer(FlowDesign design, IReadOnlyDictionary<Guid, string>? childFlowNames)
        {
            _design = design;
            _childFlowNames = childFlowNames;
        }

        public string Write(string? flowName)
        {
            if (!string.IsNullOrWhiteSpace(flowName)) _text.AppendLine($"%% {OneLine(flowName)} - drawn by PPObjectSearch");
            _text.AppendLine("flowchart TD");

            foreach (var trigger in _design.Triggers)
            {
                Line(1, $"{Id(trigger)}([\"{Label(trigger)}\"])");
                _classes[Id(trigger)] = FlowNodeCategory.Trigger;
            }

            // The top-level heads start from every trigger; with no trigger they simply start.
            var entries = _design.Triggers.Select(Id).ToList();
            WriteSequence(_design.Actions, entries, edgeLabel: null, depth: 1);

            WriteClasses();
            return _text.ToString();
        }

        /// <summary>
        /// One container's steps: each node (or subgraph), then the run-after edges between them,
        /// and an edge from each entry point into the steps that start the container.
        /// </summary>
        private void WriteSequence(FlowSequence sequence, IReadOnlyList<string> entries, string? edgeLabel, int depth)
        {
            var members = Members(sequence).ToList();
            var names = members.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var node in members) WriteNode(node, depth);

            foreach (var node in members)
            {
                var internalRunAfter = node.RunAfter.Where(r => names.Contains(r.Action)).ToList();

                if (internalRunAfter.Count == 0)
                {
                    foreach (var entry in entries) Edge(depth, entry, Target(node), edgeLabel, dotted: false);
                    continue;
                }

                foreach (var runAfter in internalRunAfter)
                {
                    var predecessor = members.First(m => m.Name == runAfter.Action);
                    Edge(depth, Source(predecessor), Target(node),
                        runAfter.IsDefault ? null : string.Join(" or ", runAfter.Statuses.Select(Outcome)),
                        dotted: !runAfter.IsDefault);
                }
            }
        }

        private void WriteNode(FlowNode node, int depth)
        {
            var id = Id(node);
            _classes[id] = node.Category;

            if (!node.IsContainer)
            {
                Line(depth, $"{id}[\"{Label(node)}\"]");
                return;
            }

            Line(depth, $"subgraph {Box(node)} [\"{Escape(node.DisplayName)}\"]");
            Line(depth + 1, "direction TB");

            var header = node.Kind switch
            {
                FlowNodeKind.Condition => $"{id}{{\"{Label(node)}\"}}",
                FlowNodeKind.Switch => $"{id}{{{{\"{Label(node)}\"}}}}",
                FlowNodeKind.ForEach or FlowNodeKind.Until => $"{id}[[\"{Label(node)}\"]]",
                _ => $"{id}(\"{Label(node)}\")"
            };
            Line(depth + 1, header);

            foreach (var branch in node.Branches)
            {
                WriteSequence(branch.Steps, new[] { id }, branch.Label.Length > 0 ? branch.Label : null, depth + 1);
            }

            Line(depth, "end");
        }

        /// <summary>The steps at one level of a sequence, through parallel groups but not into containers.</summary>
        private static IEnumerable<FlowNode> Members(FlowSequence sequence) =>
            sequence.Steps.SelectMany(step => step switch
            {
                FlowActionStep a => new[] { a.Node },
                FlowParallelStep p => p.Branches.SelectMany(Members),
                _ => Enumerable.Empty<FlowNode>()
            });

        private void Edge(int depth, string from, string to, string? label, bool dotted)
        {
            var arrow = dotted ? "-.->" : "-->";
            Line(depth, label is null ? $"{from} {arrow} {to}" : $"{from} {arrow}|\"{Escape(label)}\"| {to}");
        }

        private void WriteClasses()
        {
            // Light fills with dark text: readable whatever the page's own theme.
            var styles = new (FlowNodeCategory Category, string Name, string Style)[]
            {
                (FlowNodeCategory.Trigger, "trigger", "fill:#ddf4ff,stroke:#0969da,color:#1f2328"),
                (FlowNodeCategory.Control, "control", "fill:#fbefff,stroke:#8250df,color:#1f2328"),
                (FlowNodeCategory.Connector, "connector", "fill:#dafbe1,stroke:#1a7f37,color:#1f2328"),
                (FlowNodeCategory.Data, "data", "fill:#fff8c5,stroke:#9a6700,color:#1f2328"),
                (FlowNodeCategory.Variable, "variable", "fill:#ddf4ff,stroke:#218bff,color:#1f2328"),
                (FlowNodeCategory.Http, "http", "fill:#d8f3f6,stroke:#0e7c86,color:#1f2328"),
                (FlowNodeCategory.ChildFlow, "childflow", "fill:#fff1e5,stroke:#bc4c00,color:#1f2328"),
                (FlowNodeCategory.Other, "other", "fill:#f6f8fa,stroke:#6e7781,color:#1f2328"),
            };

            foreach (var (category, name, style) in styles)
            {
                var ids = _classes.Where(c => c.Value == category).Select(c => c.Key).ToList();
                if (ids.Count == 0) continue;

                Line(1, $"classDef {name} {style}");
                Line(1, $"class {string.Join(",", ids)} {name}");
            }
        }

        /// <summary>Short ids - action names can hold characters Mermaid would read as syntax.</summary>
        private string Id(FlowNode node)
        {
            if (!_ids.TryGetValue(node.Name, out var id))
            {
                id = "n" + (_ids.Count + 1);
                _ids[node.Name] = id;
            }

            return id;
        }

        private string Box(FlowNode node) => Id(node) + "_box";

        /// <summary>A container is entered and left as a whole box; a step, as itself.</summary>
        private string Target(FlowNode node) => node.IsContainer ? Box(node) : Id(node);
        private string Source(FlowNode node) => node.IsContainer ? Box(node) : Id(node);

        private string Label(FlowNode node)
        {
            var title = Escape(node.DisplayName);
            var summary = node.ChildFlowId is { } child && _childFlowNames?.TryGetValue(child, out var childName) == true
                ? $"{node.Summary}: {childName}"
                : node.Summary;

            return string.Equals(summary, node.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? title
                : $"{title}<br/><small>{Escape(summary)}</small>";
        }

        private void Line(int depth, string text) => _text.Append(' ', depth * 4).AppendLine(text);

        /// <summary>A run-after status in words, as on the diagram's badges.</summary>
        private static string Outcome(string status) => status.ToLowerInvariant() switch
        {
            "failed" => "failed",
            "skipped" => "skipped",
            "timedout" => "timed out",
            "succeeded" => "succeeded",
            var other => other
        };
    }

    /// <summary>
    /// Mermaid's entity codes for the characters that would end or break a quoted label - and for
    /// angle brackets, so text from the flow cannot become markup.
    /// </summary>
    internal static string Escape(string text) => OneLine(text)
        .Replace("#", "#35;")
        .Replace("\"", "#quot;")
        .Replace("<", "#lt;")
        .Replace(">", "#gt;")
        .Replace("|", "#124;");

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ").Trim();
}
