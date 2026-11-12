using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.FlowDesign;

/// <summary>A flow run written out as JSON, to analyse outside the app.</summary>
public class FlowRunExporterTests
{
    private const string Sample = """
        { "properties": { "definition": {
          "triggers": { "manual": { "type": "Request", "kind": "Button" } },
          "actions": {
            "Get_rows": { "type": "Compose" },
            "Loop": { "type": "Foreach", "foreach": "@x", "runAfter": { "Get_rows": ["Succeeded"] }, "actions": {
              "Inner": { "type": "Compose" } } },
            "Check": { "type": "If", "expression": {}, "runAfter": { "Loop": ["Succeeded"] },
                       "actions": { "Yes_step": { "type": "Compose" } },
                       "else": { "actions": { "No_step": { "type": "Compose" } } } }
          } } } }
        """;

    private static readonly DateTimeOffset Start = new(2026, 7, 1, 8, 15, 0, TimeSpan.Zero);

    private static FlowActionResult Result(string name, FlowStepOutcome outcome, double seconds = 1, string? error = null) =>
        new(name, outcome.ToString(), outcome, Start, Start.AddSeconds(seconds),
            error is null ? null : "BadRequest", error,
            $"https://x/in/{name}", $"https://x/out/{name}");

    /// <summary>Check fails in its Yes branch; Removed_step is in the run but no longer in the flow.</summary>
    private static FlowRunDetail Run() => new(
        "08584501234567890123456789CU01", "Failed", FlowStepOutcome.Failed, Start, Start.AddSeconds(12),
        Result("manual", FlowStepOutcome.Succeeded, 0),
        new Dictionary<string, FlowActionResult>
        {
            // Out of order on purpose: the export follows the design, not the API's order.
            ["Removed_step"] = Result("Removed_step", FlowStepOutcome.Succeeded, 0.1),
            ["Check"] = Result("Check", FlowStepOutcome.Failed, 2, "An action failed."),
            ["Yes_step"] = Result("Yes_step", FlowStepOutcome.Failed, 2, "Row not found"),
            ["Inner"] = Result("Inner", FlowStepOutcome.Succeeded, 1.2),
            ["Loop"] = Result("Loop", FlowStepOutcome.Succeeded, 5),
            ["Get_rows"] = Result("Get_rows", FlowStepOutcome.Succeeded, 0.34),
        },
        "ActionFailed", "An action failed.", "https://api/run", ViaAdminScope: false);

    /// <summary>Inner ran three times, the second failing; content is JSON except Get_rows' outputs.</summary>
    private static FakeHttpHandler Handler()
    {
        var reps = Enumerable.Range(0, 3).Select(i => new
        {
            properties = new
            {
                status = i == 1 ? "Failed" : "Succeeded",
                startTime = Start.AddSeconds(i),
                endTime = Start.AddSeconds(i + 0.5),
                repetitionIndexes = new[] { new { scopeName = "Loop", itemIndex = i } },
                inputsLink = new { uri = $"https://x/in/Inner/{i}" },
                error = i == 1 ? new { code = "BadRequest", message = "Item 2 is bad" } : null
            }
        });

        return new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/actions/Inner/repetitions", JsonSerializer.Serialize(new { value = reps }))
            .On(HttpMethod.Get, "https://x/out/Get_rows", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("plain text, not JSON")
            })
            .OnStatus(HttpMethod.Get, "https://x/out/Yes_step", HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "https://x/", r => FakeHttpHandler.Json(JsonSerializer.Serialize(new { from = r.Url })));
    }

    private static async Task<(JsonElement Root, FlowRunExportSummary Summary, FakeHttpHandler Handler)> ExportAsync(bool withContent)
    {
        var handler = Handler();
        using var client = new PowerAutomateClient(TestAuth.Tokens(), handler);
        using var stream = new MemoryStream();

        var summary = await FlowRunExporter.WriteAsync(stream, Run(), FlowDesignParser.Parse(Sample), client,
            new FlowRunExportOptions(withContent, "Sample flow", "Contoso Dev") { ExportedAt = Start.AddDays(1) });

        using var doc = JsonDocument.Parse(stream.ToArray());
        return (doc.RootElement.Clone(), summary, handler);
    }

    private static JsonElement Step(JsonElement root, string name) =>
        root.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    [Fact]
    public async Task Run_is_described_at_the_top()
    {
        var (root, _, _) = await ExportAsync(withContent: false);

        Assert.Equal("Sample flow", root.GetProperty("flow").GetString());
        Assert.Equal("Contoso Dev", root.GetProperty("environment").GetString());
        Assert.False(root.GetProperty("includesInputsAndOutputs").GetBoolean());

        var run = root.GetProperty("run");
        Assert.Equal("08584501234567890123456789CU01", run.GetProperty("name").GetString());
        Assert.Equal("Failed", run.GetProperty("status").GetString());
        Assert.Equal(12_000, run.GetProperty("durationMs").GetInt64());
        Assert.Equal("ActionFailed", run.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(Start, run.GetProperty("startTime").GetDateTimeOffset());

        Assert.Equal("manual", root.GetProperty("trigger").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Steps_follow_the_design_and_steps_no_longer_in_it_come_last()
    {
        var (root, summary, _) = await ExportAsync(withContent: false);

        var names = root.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();

        Assert.Equal(["Get_rows", "Loop", "Inner", "Check", "Yes_step", "Removed_step"], names);
        Assert.Equal(6, summary.Steps);
        Assert.False(Step(root, "Removed_step").GetProperty("inCurrentDesign").GetBoolean());
        Assert.False(Step(root, "Get_rows").TryGetProperty("inCurrentDesign", out _));
    }

    [Fact]
    public async Task Each_step_says_where_it_sits_and_how_it_went()
    {
        var (root, _, _) = await ExportAsync(withContent: false);

        var yes = Step(root, "Yes_step");
        Assert.Equal("Compose", yes.GetProperty("type").GetString());
        Assert.Equal("Check", yes.GetProperty("container").GetString());
        Assert.Equal("Yes", yes.GetProperty("branch").GetString());
        Assert.Equal("Failed", yes.GetProperty("status").GetString());
        Assert.Equal(2_000, yes.GetProperty("durationMs").GetInt64());
        Assert.Equal("Row not found", yes.GetProperty("error").GetProperty("message").GetString());

        var inner = Step(root, "Inner");
        Assert.Equal("Loop", inner.GetProperty("container").GetString());
        Assert.Equal("Loop", inner.GetProperty("loop").GetString());
        Assert.False(inner.TryGetProperty("branch", out _));

        Assert.False(Step(root, "Get_rows").TryGetProperty("error", out _));
    }

    [Fact]
    public async Task A_step_in_a_loop_lists_every_iteration()
    {
        var (root, summary, _) = await ExportAsync(withContent: false);

        var inner = Step(root, "Inner");
        Assert.Equal(3, inner.GetProperty("iterationCount").GetInt32());
        Assert.Equal(1, inner.GetProperty("failedIterationCount").GetInt32());

        var iterations = inner.GetProperty("iterations").EnumerateArray().ToList();
        Assert.Equal(["#1", "#2", "#3"], iterations.Select(i => i.GetProperty("iteration").GetString()));
        Assert.Equal("Item 2 is bad", iterations[1].GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(500, iterations[0].GetProperty("durationMs").GetInt64());
        Assert.Equal(3, summary.Iterations);

        // Only steps inside a loop are asked for iterations.
        Assert.False(Step(root, "Get_rows").TryGetProperty("iterations", out _));
    }

    [Fact]
    public async Task Without_content_nothing_but_iterations_is_read()
    {
        var (root, _, handler) = await ExportAsync(withContent: false);

        Assert.Single(handler.Requests);
        Assert.Contains("/actions/Inner/repetitions", handler.Requests[0].Url);
        Assert.False(Step(root, "Get_rows").TryGetProperty("inputs", out _));
    }

    [Fact]
    public async Task With_content_inputs_and_outputs_are_embedded_as_json_or_text()
    {
        var (root, summary, _) = await ExportAsync(withContent: true);

        Assert.True(root.GetProperty("includesInputsAndOutputs").GetBoolean());

        var rows = Step(root, "Get_rows");
        Assert.Equal("https://x/in/Get_rows", rows.GetProperty("inputs").GetProperty("from").GetString());
        Assert.Equal("plain text, not JSON", rows.GetProperty("outputs").GetString());

        Assert.Equal("https://x/in/manual", root.GetProperty("trigger").GetProperty("inputs").GetProperty("from").GetString());

        var second = Step(root, "Inner").GetProperty("iterations")[1];
        Assert.Equal("https://x/in/Inner/1", second.GetProperty("inputs").GetProperty("from").GetString());
        Assert.False(second.TryGetProperty("outputs", out _));

        Assert.Equal(1, summary.Problems); // Yes_step's outputs, below
    }

    [Fact]
    public async Task A_read_that_fails_is_noted_on_its_step_not_fatal()
    {
        var (root, summary, _) = await ExportAsync(withContent: true);

        var yes = Step(root, "Yes_step");
        Assert.Contains("expired", yes.GetProperty("outputsError").GetString());
        Assert.False(yes.TryGetProperty("outputs", out _));
        Assert.True(yes.TryGetProperty("inputs", out _));
        Assert.Equal(1, summary.Problems);
    }

    [Fact]
    public async Task Links_to_content_are_never_written()
    {
        var handler = Handler();
        using var client = new PowerAutomateClient(TestAuth.Tokens(), handler);
        using var stream = new MemoryStream();

        await FlowRunExporter.WriteAsync(stream, Run(), FlowDesignParser.Parse(Sample), client, new FlowRunExportOptions(false));

        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("https://x/", text);
        Assert.DoesNotContain("Link", text);
    }

    [Fact]
    public async Task Iterations_that_cannot_be_read_are_noted()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "/repetitions", HttpStatusCode.InternalServerError, "Server busy");
        using var client = new PowerAutomateClient(TestAuth.Tokens(), handler);
        using var stream = new MemoryStream();

        var summary = await FlowRunExporter.WriteAsync(stream, Run(), FlowDesignParser.Parse(Sample), client, new FlowRunExportOptions(false));

        using var doc = JsonDocument.Parse(stream.ToArray());
        Assert.Contains("Server busy", Step(doc.RootElement, "Inner").GetProperty("iterationsError").GetString());
        Assert.Equal(1, summary.Problems);
    }

    [Fact]
    public async Task A_cancelled_export_stops()
    {
        using var client = new PowerAutomateClient(TestAuth.Tokens(), Handler());
        using var stream = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlowRunExporter.WriteAsync(
            stream, Run(), FlowDesignParser.Parse(Sample), client, new FlowRunExportOptions(true), ct: cts.Token));
    }
}
