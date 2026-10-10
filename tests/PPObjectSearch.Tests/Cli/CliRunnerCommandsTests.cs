using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Cli;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Cli;

/// <summary>The command line's diff formats, compare-data and readiness commands, and how failures exit.</summary>
public sealed class CliRunnerCommandsTests : IDisposable
{
    private const string Dev = "https://dev.crm11.dynamics.com";
    private const string Test = "https://test.crm11.dynamics.com";

    private static readonly Guid Shared = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid DevOnly = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid TestOnly = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppos-cli-" + Guid.NewGuid().ToString("N"));

    public CliRunnerCommandsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static string Solutions(string version, bool managed = true) =>
        $$"""{"value":[{"solutionid":"{{Guid.NewGuid()}}","uniquename":"core","friendlyname":"Core","version":"{{version}}","ismanaged":{{(managed ? "true" : "false")}}}]}""";

    private static string Components(params (Guid Id, string Name)[] items) =>
        "{\"value\":[" + string.Join(",", items.Select(i =>
            $$"""{"msdyn_componenttype":61,"msdyn_componenttypename":"Web Resource","msdyn_name":"{{i.Name}}","msdyn_objectid":"{{i.Id}}"}""")) + "]}";

    private static FakeHttpHandler Environment(string solutionsJson, params (Guid Id, string Name)[] items) => new FakeHttpHandler()
        .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "no range filter")
        .OnJson(HttpMethod.Get, "solutions?$select", solutionsJson)
        .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Components(items))
        .OnJson(HttpMethod.Get, "workflows?", """{"value":[]}""");

    private static FakeHttpHandler Environment(params (Guid Id, string Name)[] items) => Environment(Solutions("1.0.0.0"), items);

    private static (CliRunner Runner, StringWriter Out, StringWriter Err) Runner(
        Dictionary<string, FakeHttpHandler> environments, IReadOnlyList<ReferenceDataConfig>? configs = null)
    {
        // Progress is reported from the thread pool, so the runner writes through synchronised wrappers.
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CliRunner(TextWriter.Synchronized(output), TextWriter.Synchronized(error),
            (url, _) => Task.FromResult(Fakes.Dataverse(environments[url], url)),
            () => configs ?? Array.Empty<ReferenceDataConfig>());
        return (runner, output, error);
    }

    [Fact]
    public async Task A_csv_diff_lists_every_component_with_its_status()
    {
        var (runner, output, error) = Runner(new()
        {
            [Dev] = Environment((Shared, "new_shared.js"), (DevOnly, "new_devonly.js")),
            [Test] = Environment((Shared, "new_shared.js"), (TestOnly, "new_testonly.js"))
        });

        var code = await runner.RunAsync(["diff", "--solution", "core", "--left", Dev, "--right", Test, "--format", "CSV"]);

        Assert.Equal(CliRunner.Found, code);
        var lines = output.ToString().Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Status,Name,Type", lines[0]);
        Assert.Contains(lines, l => l.Contains("new_devonly.js"));
        Assert.Contains(lines, l => l.Contains("new_testonly.js"));
        Assert.Contains("1 only in left, 1 only in right, 1 in both.", error.ToString());
    }

    [Fact]
    public async Task A_json_diff_written_to_a_file_names_each_difference()
    {
        var path = Path.Combine(_dir, "diff.json");
        var (runner, output, error) = Runner(new()
        {
            [Dev] = Environment((Shared, "new_shared.js")),
            [Test] = Environment((Shared, "new_shared.js"), (TestOnly, "new_testonly.js"))
        });

        var code = await runner.RunAsync(["diff", "--solution", "core", "--left", Dev, "--right", Test, "--format", "json", "--out", path]);

        Assert.Equal(CliRunner.Found, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains($"Wrote {path}.", error.ToString());
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var row = Assert.Single(json.RootElement.EnumerateArray().ToList());
        Assert.Equal("OnlyInRight", row.GetProperty("status").GetString());
        Assert.Equal(TestOnly, row.GetProperty("objectId").GetGuid());
    }

    [Fact]
    public async Task A_solution_missing_on_the_right_counts_everything_as_only_in_left()
    {
        var (runner, output, error) = Runner(new()
        {
            [Dev] = Environment((Shared, "new_shared.js")),
            [Test] = Environment("""{"value":[]}""")
        });

        var code = await runner.RunAsync(["diff", "--solution", "core", "--left", Dev, "--right", Test]);

        Assert.Equal(CliRunner.Found, code);
        Assert.Contains($"No solution 'core' in {Test}. Treating it as empty.", error.ToString());
        Assert.Contains($"## Only in {Test} (0)", output.ToString());
        Assert.Contains("None.", output.ToString());
    }

    [Fact]
    public async Task A_solution_missing_on_the_left_is_a_usage_error()
    {
        var (runner, _, error) = Runner(new()
        {
            [Dev] = Environment("""{"value":[]}"""),
            [Test] = Environment()
        });

        Assert.Equal(CliRunner.Usage, await runner.RunAsync(["diff", "--solution", "core", "--left", Dev, "--right", Test]));
        Assert.Contains($"No solution 'core' in {Dev}.", error.ToString());
    }

    [Theory]
    [InlineData("diff", "--format", "xml", "--format is md, csv or json.")]
    [InlineData("readiness", "--fail-on", "info", "--fail-on is blocker or warning.")]
    public async Task An_option_with_a_value_it_does_not_take_is_a_usage_error(string verb, string option, string value, string message)
    {
        var (runner, _, error) = Runner(new());

        var code = await runner.RunAsync([verb, "--solution", "core", "--left", Dev, "--right", Test, "--source", Dev, "--target", Test, option, value]);

        Assert.Equal(CliRunner.Usage, code);
        Assert.Contains(message, error.ToString());
    }

    [Fact]
    public async Task Options_that_do_not_parse_are_a_usage_error()
    {
        var (runner, _, error) = Runner(new());

        Assert.Equal(CliRunner.Usage, await runner.RunAsync(["diff", "core"]));
        Assert.Contains("Unexpected 'core'", error.ToString());
    }

    [Fact]
    public async Task A_run_that_fails_or_is_cancelled_exits_three()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var failing = new CliRunner(output, error, (_, _) => throw new InvalidOperationException("no network"), Array.Empty<ReferenceDataConfig>);
        var cancelled = new CliRunner(output, error, (_, _) => throw new OperationCanceledException(), Array.Empty<ReferenceDataConfig>);

        Assert.Equal(CliRunner.Failed, await failing.RunAsync(["diff", "--solution", "core", "--left", Dev, "--right", Test]));
        Assert.Equal(CliRunner.Failed, await cancelled.RunAsync(["readiness", "--solution", "core", "--source", Dev, "--target", Test]));
        Assert.Contains("Failed: no network", error.ToString());
        Assert.Contains("Cancelled.", error.ToString());
    }

    private static string Things(params (Guid Id, string Name, string Code)[] rows) => JsonSerializer.Serialize(new
    {
        value = rows.Select(r => new Dictionary<string, object> { ["new_thingid"] = r.Id, ["new_name"] = r.Name, ["new_code"] = r.Code })
    });

    private static FakeHttpHandler DataEnvironment(string rows) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "EntityDefinitions?$select=LogicalName", JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    LogicalName = "new_thing", DisplayName = new { UserLocalizedLabel = new { Label = "Thing" } }, EntitySetName = "new_things",
                    PrimaryIdAttribute = "new_thingid", PrimaryNameAttribute = "new_name", IsPrivate = false, IsManaged = false, IsActivity = false
                }
            }
        }))
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_thing')/Attributes", JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                new { LogicalName = "new_thingid", AttributeTypeName = new { Value = "UniqueidentifierType" }, IsValidForRead = true, IsValidForCreate = true, IsValidForUpdate = false, IsPrimaryId = true, IsPrimaryName = false },
                new { LogicalName = "new_name", AttributeTypeName = new { Value = "StringType" }, IsValidForRead = true, IsValidForCreate = true, IsValidForUpdate = true, IsPrimaryId = false, IsPrimaryName = true },
                new { LogicalName = "new_code", AttributeTypeName = new { Value = "StringType" }, IsValidForRead = true, IsValidForCreate = true, IsValidForUpdate = true, IsPrimaryId = false, IsPrimaryName = false }
            }
        }))
        .OnJson(HttpMethod.Get, "new_things?", rows);

    [Fact]
    public async Task A_saved_comparison_writes_each_differing_column_and_missing_row_as_csv()
    {
        var path = Path.Combine(_dir, "data.csv");
        var config = new ReferenceDataConfig
        {
            Name = "Things",
            Entities = [new ReferenceEntityConfig { LogicalName = "new_thing" }]
        };
        var (runner, _, error) = Runner(new()
        {
            [Dev] = DataEnvironment(Things((Shared, "Alpha", "x"), (DevOnly, "Beta", "y"))),
            [Test] = DataEnvironment(Things((Shared, "Alpha", "z")))
        }, [config]);

        var code = await runner.RunAsync(["compare-data", "--config", "things", "--source", Dev, "--target", Test, "--out", path]);

        Assert.Equal(CliRunner.Found, code);
        var lines = (await File.ReadAllLinesAsync(path)).Where(l => l.Length > 0).ToList();
        Assert.Equal("Table,Key,Name,Status,Column,Source value,Target value", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("new_thing,", StringComparison.Ordinal) && l.Contains("Alpha") && l.EndsWith("new_code,x,z", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Beta") && l.EndsWith(",,,", StringComparison.Ordinal));
        Assert.Contains("1 table(s), 2 row(s) compared, 2 differ.", error.ToString());
    }

    [Fact]
    public async Task A_saved_comparison_with_nothing_different_exits_zero_and_prints_its_warnings()
    {
        var config = new ReferenceDataConfig
        {
            Name = "Things",
            Entities = [new ReferenceEntityConfig { LogicalName = "new_thing" }, new ReferenceEntityConfig { LogicalName = "new_gone" }]
        };
        var (runner, output, error) = Runner(new()
        {
            [Dev] = DataEnvironment(Things((Shared, "Alpha", "x"))),
            [Test] = DataEnvironment(Things((Shared, "Alpha", "x")))
        }, [config]);

        var code = await runner.RunAsync(["compare-data", "--config", "Things", "--source", Dev, "--target", Test]);

        Assert.Equal(CliRunner.Clean, code);
        Assert.Equal("Table,Key,Name,Status,Column,Source value,Target value", output.ToString().Trim());
        Assert.Contains("Warning: new_gone: skipped", error.ToString());
    }

    private static FakeHttpHandler Target(string solutionsJson) => Environment(solutionsJson)
        .OnJson(HttpMethod.Get, "solutioncomponents?", """{"value":[]}""");

    private static FakeHttpHandler Source() => Environment(Solutions("1.0.0.0"), (Shared, "new_shared.js"))
        .OnJson(HttpMethod.Get, "RetrieveMissingDependencies", """{"value":[]}""");

    [Fact]
    public async Task Readiness_with_a_blocker_writes_the_report_and_exits_one()
    {
        var path = Path.Combine(_dir, "readiness.md");
        var (runner, _, error) = Runner(new()
        {
            [Dev] = Source(),
            [Test] = Target(Solutions("2.0.0.0"))
        });

        var code = await runner.RunAsync(["readiness", "--solution", "Core", "--source", Dev, "--target", Test, "--out", path]);

        Assert.Equal(CliRunner.Found, code);
        var report = await File.ReadAllTextAsync(path);
        Assert.Contains("# Readiness: Core", report);
        Assert.Contains("## Blockers", report);
        Assert.Contains("1 blocker(s), 0 warning(s), 0 note(s).", error.ToString());
    }

    [Fact]
    public async Task Readiness_fails_on_warnings_only_when_asked_to()
    {
        var environments = new Dictionary<string, FakeHttpHandler>
        {
            [Dev] = Source(),
            [Test] = Target(Solutions("0.9.0.0", managed: false))
        };
        var (runner, output, _) = Runner(environments);

        Assert.Equal(CliRunner.Clean, await runner.RunAsync(["readiness", "--solution", "core", "--source", Dev, "--target", Test]));
        Assert.Equal(CliRunner.Found, await runner.RunAsync(["readiness", "--solution", "core", "--source", Dev, "--target", Test, "--fail-on", "Warning"]));
        Assert.Contains("## Warnings", output.ToString());
    }
}
