using System.IO;
using System.Net;
using System.Net.Http;
using PPObjectSearch.Cli;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Cli;

public class CliRunnerTests
{
    private const string Dev = "https://dev.crm11.dynamics.com";
    private const string Test = "https://test.crm11.dynamics.com";

    private static readonly Guid Shared = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid DevOnly = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private static string Solutions(Guid id) =>
        $$"""{"value":[{"solutionid":"{{id}}","uniquename":"core","friendlyname":"Core","version":"1.0.0.0","ismanaged":false}]}""";

    private static string Components(params (Guid Id, string Name)[] items) =>
        "{\"value\":[" + string.Join(",", items.Select(i =>
            $$"""{"msdyn_componenttype":61,"msdyn_componenttypename":"Web Resource","msdyn_name":"{{i.Name}}","msdyn_objectid":"{{i.Id}}"}""")) + "]}";

    private static FakeHttpHandler Environment(params (Guid Id, string Name)[] items) => new FakeHttpHandler()
        .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "no range filter")
        .OnJson(HttpMethod.Get, "solutions?$select", Solutions(Guid.NewGuid()))
        .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Components(items))
        .OnJson(HttpMethod.Get, "workflows?", """{"value":[]}""");

    private static (CliRunner Runner, StringWriter Out, StringWriter Err) Runner(
        Dictionary<string, FakeHttpHandler> environments, IReadOnlyList<ReferenceDataConfig>? configs = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CliRunner(output, error,
            (url, _) => Task.FromResult(Fakes.Dataverse(environments[url], url)),
            () => configs ?? Array.Empty<ReferenceDataConfig>());
        return (runner, output, error);
    }

    [Fact]
    public void Options_are_name_value_pairs()
    {
        Assert.True(CliRunner.TryParse(["--solution", "core", "--LEFT", Dev], out var options, out _));
        Assert.Equal("core", options["solution"]);
        Assert.Equal(Dev, options["left"]);

        Assert.False(CliRunner.TryParse(["core"], out _, out var stray));
        Assert.Contains("Unexpected 'core'", stray);
        Assert.False(CliRunner.TryParse(["--solution"], out _, out var missing));
        Assert.Contains("needs a value", missing);
    }

    [Fact]
    public async Task Help_and_mistakes_have_their_own_exit_codes()
    {
        var (runner, output, error) = Runner(new());

        Assert.Equal(CliRunner.Clean, await runner.RunAsync(["help"]));
        Assert.Contains("ppos diff", output.ToString());
        Assert.Equal(CliRunner.Usage, await runner.RunAsync([]));
        Assert.Equal(CliRunner.Usage, await runner.RunAsync(["reconcile"]));
        Assert.Contains("Unknown command 'reconcile'", error.ToString());
        Assert.Equal(CliRunner.Usage, await runner.RunAsync(["diff", "--solution", "core"]));
        Assert.Contains("--left is required", error.ToString());
    }

    [Fact]
    public async Task A_diff_with_differences_reports_them_and_exits_one()
    {
        var (runner, output, error) = Runner(new()
        {
            [Dev] = Environment((Shared, "new_shared.js"), (DevOnly, "new_devonly.js")),
            [Test] = Environment((Shared, "new_shared.js"))
        });

        var code = await runner.RunAsync(["diff", "--solution", "core", "--left", Dev, "--right", Test]);

        Assert.Equal(CliRunner.Found, code);
        Assert.Contains($"## Only in {Dev} (1)", output.ToString());
        Assert.Contains("| Web Resource | new_devonly.js |", output.ToString());
        Assert.Contains("1 only in left, 0 only in right, 1 in both.", error.ToString());
    }

    [Fact]
    public async Task A_diff_with_nothing_missing_exits_zero_and_json_lists_only_differences()
    {
        var (runner, output, _) = Runner(new()
        {
            [Dev] = Environment((Shared, "new_shared.js")),
            [Test] = Environment((Shared, "new_shared.js"))
        });

        Assert.Equal(CliRunner.Clean, await runner.RunAsync(["diff", "--solution", "Core", "--left", Dev, "--right", Test, "--format", "json"]));
        Assert.Equal("[]", output.ToString().Trim());
    }

    [Fact]
    public async Task A_saved_comparison_must_exist()
    {
        var (runner, _, error) = Runner(new(), [new ReferenceDataConfig { Name = "Core reference data" }]);

        Assert.Equal(CliRunner.Usage, await runner.RunAsync(["compare-data", "--config", "Other", "--source", Dev, "--target", Test]));
        Assert.Contains("Saved ones: Core reference data", error.ToString());
    }

    [Fact]
    public void The_object_diff_matches_by_id_then_by_type_and_name()
    {
        var left = new[]
        {
            new SolutionComponentItem { Name = "a", ComponentTypeName = "T", ComponentType = 61, ObjectId = Shared },
            new SolutionComponentItem { Name = "Same name", ComponentTypeName = "T", ComponentType = 61, ObjectId = Guid.NewGuid() }
        };
        var right = new[]
        {
            new SolutionComponentItem { Name = "renamed", ComponentTypeName = "T", ComponentType = 61, ObjectId = Shared },
            new SolutionComponentItem { Name = "same NAME", ComponentTypeName = "T", ComponentType = 61, ObjectId = Guid.NewGuid() },
            new SolutionComponentItem { Name = "extra", ComponentTypeName = "T", ComponentType = 61, ObjectId = Guid.NewGuid() }
        };

        var rows = ComponentDiff.Diff(left, right);

        Assert.Equal(2, rows.Count(r => r.Status == CompareStatus.Same));
        Assert.Equal("extra", Assert.Single(rows, r => r.Status == CompareStatus.OnlyInRight).Name);
    }
}
