using System.IO;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;

namespace PPObjectSearch.Services;

/// <summary>What an export wrote: the steps and iterations, and how many reads failed along the way.</summary>
public sealed record FlowRunExportSummary(int Steps, int Iterations, int Problems);

/// <summary>
/// Writes a flow run as JSON, to analyse outside the app: the run, then each step it reports in
/// the order the designer draws them - where it sits, how it went, its error - and, for a step
/// inside a loop, every iteration. With content, each step's and iteration's inputs and outputs
/// too, read from Power Automate as the export goes.
///
/// The pre-signed links to inputs and outputs are left out: they expire within hours, and while
/// they last they are a key to the content. A read that fails is noted on its step, not fatal.
/// </summary>
public sealed class FlowRunExporter
{
    /// <summary>Content reads in flight at once - enough to be quick, few enough not to be throttled.</summary>
    private const int ParallelReads = 6;

    /// <summary>Iterations whose content is read before they are written, so memory stays bounded.</summary>
    private const int IterationBatch = 24;

    private readonly FlowRunDetail _run;
    private readonly FlowDesign _design;
    private readonly PowerAutomateClient _client;
    private readonly bool _withContent;
    private readonly IProgress<string>? _progress;
    private readonly CancellationToken _ct;
    private readonly SemaphoreSlim _reads = new(ParallelReads);

    private int _iterations;
    private int _problems;

    private FlowRunExporter(FlowRunDetail run, FlowDesign design, PowerAutomateClient client, bool withContent,
        IProgress<string>? progress, CancellationToken ct)
    {
        _run = run;
        _design = design;
        _client = client;
        _withContent = withContent;
        _progress = progress;
        _ct = ct;
    }

    /// <summary>Writes the run to <paramref name="stream"/> as one indented JSON document.</summary>
    public static async Task<FlowRunExportSummary> WriteAsync(
        Stream stream, FlowRunDetail run, FlowDesign design, PowerAutomateClient client,
        FlowRunExportOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var exporter = new FlowRunExporter(run, design, client, options.WithContent, progress, ct);
        return await exporter.WriteAsync(stream, options).ConfigureAwait(false);
    }

    private async Task<FlowRunExportSummary> WriteAsync(Stream stream, FlowRunExportOptions options)
    {
        var places = Place(_design);
        var steps = places.Keys.Where(_run.Actions.ContainsKey)
            .Concat(_run.Actions.Values
                .Where(a => !places.ContainsKey(a.Name))
                .OrderBy(a => a.StartTime ?? DateTimeOffset.MaxValue)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => a.Name))
            .ToList();

        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        WriteIfAny(writer, "flow", options.FlowName);
        WriteIfAny(writer, "environment", options.EnvironmentName);
        writer.WriteString("exportedAt", options.ExportedAt ?? DateTimeOffset.UtcNow);
        writer.WriteBoolean("includesInputsAndOutputs", _withContent);

        writer.WriteStartObject("run");
        writer.WriteString("name", _run.RunName);
        writer.WriteString("status", _run.Status);
        WriteTimes(writer, _run.StartTime, _run.EndTime, _run.Duration);
        WriteError(writer, _run.ErrorCode, _run.ErrorMessage);
        writer.WriteBoolean("readWithAdminAccess", _run.ViaAdminScope);
        writer.WriteEndObject();

        writer.WriteStartArray("notes");
        writer.WriteStringValue("Steps are listed in the order the designer draws them; those the run reports but the flow no longer has come last.");
        writer.WriteStringValue("A step inside a loop reports its last iteration; its \"iterations\" list every pass.");
        writer.WriteStringValue("Steps the run never reached are not listed.");
        writer.WriteEndArray();

        if (_run.Trigger is { } trigger)
        {
            _progress?.Report("Exporting the run - the trigger...");
            writer.WritePropertyName("trigger");
            await WriteStepAsync(writer, trigger, places.GetValueOrDefault(trigger.Name)).ConfigureAwait(false);
        }

        writer.WriteStartArray("steps");
        for (var i = 0; i < steps.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            _progress?.Report($"Exporting the run - step {i + 1:N0} of {steps.Count:N0}...");

            await WriteStepAsync(writer, _run.Actions[steps[i]], places.GetValueOrDefault(steps[i])).ConfigureAwait(false);
            await writer.FlushAsync(_ct).ConfigureAwait(false);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(_ct).ConfigureAwait(false);

        return new FlowRunExportSummary(steps.Count, _iterations, _problems);
    }

    // ---------------------------------------------------------------- a step

    private async Task WriteStepAsync(Utf8JsonWriter writer, FlowActionResult result, Placement? place)
    {
        // Content is asked for before the iterations, so both reads overlap.
        var inputs = ReadAsync(result.InputsLink);
        var outputs = ReadAsync(result.OutputsLink);
        var iterations = place?.Loop is not null ? RepetitionsAsync(result.Name) : null;

        writer.WriteStartObject();
        writer.WriteString("name", result.Name);

        if (place is { Node: var node })
        {
            writer.WriteString("type", node.Type);
            WriteIfAny(writer, "connector", node.Connector);
            WriteIfAny(writer, "operation", node.Operation);
            WriteIfAny(writer, "container", place.Container);
            WriteIfAny(writer, "branch", place.Branch);
            WriteIfAny(writer, "loop", place.Loop);
        }
        else
        {
            writer.WriteBoolean("inCurrentDesign", false);
        }

        writer.WriteString("status", result.Status);
        WriteTimes(writer, result.StartTime, result.EndTime, result.Duration);
        WriteError(writer, result.ErrorCode, result.ErrorMessage);
        WriteContent(writer, "inputs", await inputs.ConfigureAwait(false));
        WriteContent(writer, "outputs", await outputs.ConfigureAwait(false));

        if (iterations is not null) await WriteIterationsAsync(writer, iterations).ConfigureAwait(false);

        writer.WriteEndObject();
    }

    private async Task WriteIterationsAsync(Utf8JsonWriter writer, Task<(FlowRepetitions? List, string? Error)> read)
    {
        var (repetitions, error) = await read.ConfigureAwait(false);
        if (repetitions is null)
        {
            writer.WriteString("iterationsError", error);
            return;
        }

        writer.WriteNumber("iterationCount", repetitions.Count);
        writer.WriteNumber("failedIterationCount",
            repetitions.Count(r => r.Outcome is FlowStepOutcome.Failed or FlowStepOutcome.TimedOut));
        if (repetitions.IsTruncated) writer.WriteBoolean("iterationsTruncated", true);

        writer.WriteStartArray("iterations");
        foreach (var batch in repetitions.Chunk(IterationBatch))
        {
            var contents = batch.Select(r => (Inputs: ReadAsync(r.InputsLink), Outputs: ReadAsync(r.OutputsLink))).ToList();

            for (var i = 0; i < batch.Length; i++)
            {
                var repetition = batch[i];
                writer.WriteStartObject();
                writer.WriteString("iteration", repetition.Label);
                writer.WriteString("status", repetition.Status);
                WriteTimes(writer, repetition.StartTime, repetition.EndTime, repetition.Duration);
                WriteError(writer, repetition.ErrorCode, repetition.ErrorMessage);
                WriteContent(writer, "inputs", await contents[i].Inputs.ConfigureAwait(false));
                WriteContent(writer, "outputs", await contents[i].Outputs.ConfigureAwait(false));
                writer.WriteEndObject();
            }

            _iterations += batch.Length;
            await writer.FlushAsync(_ct).ConfigureAwait(false);
        }

        writer.WriteEndArray();
    }

    // ---------------------------------------------------------------- reads

    private async Task<(FlowRepetitions? List, string? Error)> RepetitionsAsync(string actionName)
    {
        try
        {
            return (await _client.GetRepetitionsAsync(_run, actionName, _ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _problems);
            return (null, "Could not read the iterations - " + ex.Message);
        }
    }

    /// <summary>A step's inputs or outputs: null when not exported or there are none.</summary>
    private async Task<Content?> ReadAsync(string? link)
    {
        if (!_withContent || link is null) return null;

        await _reads.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            return new Content(await _client.GetContentAsync(link, _ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _problems);
            return new Content(null, ex.Message);
        }
        finally
        {
            _reads.Release();
        }
    }

    private sealed record Content(string? Text, string? Error);

    // ---------------------------------------------------------------- writing

    /// <summary>JSON content goes in as JSON, so it can be queried; anything else as a string.</summary>
    private static void WriteContent(Utf8JsonWriter writer, string name, Content? content)
    {
        switch (content)
        {
            case null:
                return;
            case { Error: { } error }:
                writer.WriteString(name + "Error", error);
                return;
        }

        try
        {
            using var doc = JsonDocument.Parse(content.Text ?? string.Empty);
            writer.WritePropertyName(name);
            doc.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteString(name, content.Text);
        }
    }

    private static void WriteTimes(Utf8JsonWriter writer, DateTimeOffset? start, DateTimeOffset? end, TimeSpan? duration)
    {
        if (start is { } s) writer.WriteString("startTime", s);
        if (end is { } e) writer.WriteString("endTime", e);
        if (duration is { } d) writer.WriteNumber("durationMs", (long)d.TotalMilliseconds);
    }

    private static void WriteError(Utf8JsonWriter writer, string? code, string? message)
    {
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message)) return;

        writer.WriteStartObject("error");
        WriteIfAny(writer, "code", code);
        WriteIfAny(writer, "message", message);
        writer.WriteEndObject();
    }

    private static void WriteIfAny(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) writer.WriteString(name, value);
    }

    // ---------------------------------------------------------------- where each step sits

    /// <summary>A step in the design: what holds it, in which branch, and the nearest loop around it.</summary>
    private sealed record Placement(FlowNode Node, string? Container, string? Branch, string? Loop);

    /// <summary>Every step of the design by name, in the order the designer draws them.</summary>
    private static Dictionary<string, Placement> Place(FlowDesign design)
    {
        // Insertion order is kept by Dictionary while nothing is removed, which nothing here does.
        var places = new Dictionary<string, Placement>(StringComparer.Ordinal);

        foreach (var trigger in design.Triggers) places.TryAdd(trigger.Name, new Placement(trigger, null, null, null));
        Walk(design.Actions, null, null, null);

        return places;

        void Walk(FlowSequence sequence, string? container, string? branch, string? loop)
        {
            foreach (var step in sequence.Steps)
            {
                switch (step)
                {
                    case FlowActionStep { Node: var node }:
                        places.TryAdd(node.Name, new Placement(node, container, branch, loop));

                        var inner = node.Kind is FlowNodeKind.ForEach or FlowNodeKind.Until ? node.Name : loop;
                        foreach (var b in node.Branches) Walk(b.Steps, node.Name, b.Label, inner);
                        break;

                    case FlowParallelStep parallel:
                        foreach (var b in parallel.Branches) Walk(b, container, branch, loop);
                        break;
                }
            }
        }
    }
}

/// <summary>What goes into a run export besides the run.</summary>
public sealed record FlowRunExportOptions(bool WithContent, string? FlowName = null, string? EnvironmentName = null)
{
    /// <summary>For tests: a fixed time in place of now.</summary>
    public DateTimeOffset? ExportedAt { get; init; }
}
