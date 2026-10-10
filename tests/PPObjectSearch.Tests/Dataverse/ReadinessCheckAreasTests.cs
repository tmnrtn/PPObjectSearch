using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>The readiness check's areas one at a time: version, dependencies, connections, plug-ins and layers.</summary>
public class ReadinessCheckAreasTests
{
    private const string TargetUrl = "https://target.crm11.dynamics.com";
    private const string NoDependencies = """{"value":[]}""";

    private static readonly SolutionInfo Solution = new()
    {
        SolutionId = Guid.Parse("10000000-0000-0000-0000-000000000001"), UniqueName = "core", FriendlyName = "Core", Version = "1.1.0.0"
    };

    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static FakeHttpHandler Source() =>
        new FakeHttpHandler().OnJson(HttpMethod.Get, "RetrieveMissingDependencies", NoDependencies);

    private static FakeHttpHandler TargetWith(string solutionsJson) =>
        new FakeHttpHandler().OnJson(HttpMethod.Get, "solutions?", solutionsJson);

    private static string Installed(bool managed, string version) =>
        $$"""{"value":[{"solutionid":"70000000-0000-0000-0000-000000000007","uniquename":"CORE","friendlyname":"Core","ismanaged":{{(managed ? "true" : "false")}},"version":"{{version}}"}]}""";

    private static Task<ReadinessReport> Run(FakeHttpHandler source, FakeHttpHandler target, IReadOnlyList<SolutionComponentItem> components,
        PowerAutomateClient? flows = null, string? environmentId = null, SolutionInfo? solution = null) =>
        new ReadinessCheck(Fakes.Dataverse(source), Fakes.Dataverse(target, TargetUrl), flows, environmentId)
            .RunAsync(solution ?? Solution, components, "Dev", "Prod");

    [Fact]
    public async Task A_solution_new_to_the_target_is_noted_as_a_first_install()
    {
        var report = await Run(Source(), TargetWith("""{"value":[]}"""), [],
            solution: new SolutionInfo { SolutionId = Solution.SolutionId, UniqueName = "core", FriendlyName = "Core" });

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ReadinessSeverity.Info, finding.Severity);
        Assert.Contains("first install (version unknown)", finding.Message);
        Assert.Empty(report.NotChecked);
    }

    [Fact]
    public async Task The_same_version_installed_unmanaged_is_warned_about_and_noted()
    {
        var report = await Run(Source(), TargetWith(Installed(managed: false, "1.1.0.0")), []);

        Assert.Collection(report.Findings,
            f =>
            {
                Assert.Equal(ReadinessSeverity.Warning, f.Severity);
                Assert.Contains("unmanaged", f.Message);
            },
            f =>
            {
                Assert.Equal(ReadinessSeverity.Info, f.Severity);
                Assert.Contains("the same version", f.Message);
            });
    }

    [Fact]
    public async Task A_newer_version_is_an_upgrade()
    {
        var report = await Run(Source(), TargetWith(Installed(managed: true, "1.0.5.0")), []);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("Upgrades the target from 1.0.5.0 to 1.1.0.0.", finding.Message);
    }

    [Fact]
    public async Task A_check_that_fails_is_listed_as_not_checked_and_the_rest_still_run()
    {
        var progress = new List<string>();
        var check = new ReadinessCheck(Fakes.Dataverse(new FakeHttpHandler()), Fakes.Dataverse(new FakeHttpHandler(), TargetUrl), null, null);

        var report = await check.RunAsync(Solution, [], "Dev", "Prod", new SyncProgress(progress));

        Assert.Equal(2, report.NotChecked.Count);
        Assert.StartsWith("the solution version:", report.NotChecked[0]);
        Assert.StartsWith("missing dependencies:", report.NotChecked[1]);
        Assert.Equal("0 blocker(s), 0 warning(s), 0 note(s); 2 check(s) could not run.", report.Summary);
        Assert.Contains("Checking unmanaged layers in the target...", progress);
        var markdown = report.ToMarkdown();
        Assert.Contains("## Not checked", markdown);
        Assert.Contains("- the solution version:", markdown);
        Assert.DoesNotContain("## Blockers", markdown);
    }

    [Fact]
    public async Task A_dependency_needed_by_many_components_names_five_and_counts_the_rest()
    {
        var missing = G(999);
        var rows = string.Join(",", Enumerable.Range(1, 7).Select(i =>
            $$"""{"requiredcomponentobjectid":"{{missing}}","requiredcomponenttype":1,"dependentcomponentobjectid":"{{G(i)}}","dependentcomponenttype":26}"""));
        var source = new FakeHttpHandler().OnJson(HttpMethod.Get, "RetrieveMissingDependencies", $$"""{"value":[{{rows}}]}""");
        var target = TargetWith(Installed(managed: true, "1.0.0.0")).OnJson(HttpMethod.Get, "solutioncomponents?", NoDependencies);

        var report = await Run(source, target, []);

        var finding = Assert.Single(report.Findings, f => f.Area == "Dependencies");
        Assert.Equal(ReadinessSeverity.Blocker, finding.Severity);
        Assert.Contains("and 2 more", finding.Message);
        Assert.Contains(G(1).ToString(), finding.Message);
        Assert.Null(finding.Item);
    }

    private static SolutionComponentItem Reference(int n) => new()
    {
        Name = $"ref{n}", ComponentTypeName = "Connection Reference", ComponentType = 10132, ComponentLogicalName = "connectionreference", ObjectId = G(n)
    };

    private static string ReferenceRow(int n, string name, string? connection) =>
        $$"""{"connectionreferenceid":"{{G(n)}}","connectionreferencelogicalname":"{{name}}","connectorid":"/providers/Microsoft.PowerApps/apis/shared_sql","connectionid":{{(connection is null ? "null" : $"\"{connection}\"")}}}""";

    private static (FakeHttpHandler Source, FakeHttpHandler Target) ReferenceHandlers()
    {
        var source = Source().OnJson(HttpMethod.Get, "connectionreferences?", $$"""
            {"value":[{{ReferenceRow(1, "new_a", "x")}},{{ReferenceRow(2, "new_b", "x")}},{{ReferenceRow(3, "new_c", "x")}},{{ReferenceRow(4, "new_d", "x")}}]}
            """);
        var target = TargetWith(Installed(managed: true, "1.0.0.0"))
            .OnJson(HttpMethod.Get, "connectionreferences?", $$"""
                {"value":[{{ReferenceRow(12, "new_b", null)}},{{ReferenceRow(13, "new_c", "conn-broken")}},{{ReferenceRow(14, "NEW_D", "conn-hidden")}}]}
                """)
            .OnJson(HttpMethod.Get, "solutioncomponents?", NoDependencies);
        return (source, target);
    }

    [Fact]
    public async Task Connection_references_are_checked_against_the_targets_connections()
    {
        var (source, target) = ReferenceHandlers();
        var flowsHandler = new FakeHttpHandler().OnJson(HttpMethod.Get, "environments/env-1/connections", """
            {"value":[{"name":"conn-broken","properties":{"statuses":[{"status":"Error","error":{"message":"Token expired"}}]}}]}
            """);

        var report = await Run(source, target, [Reference(1), Reference(2), Reference(3), Reference(4)],
            new PowerAutomateClient(TestAuth.Tokens(), flowsHandler), "env-1");

        var references = report.Findings.Where(f => f.Area == "Connection references").ToList();
        Assert.Equal(4, references.Count);
        Assert.Contains(references, f => f.Component == "new_a" && f.Message.StartsWith("New to the target (shared_sql)", StringComparison.Ordinal) && f.Item!.ObjectId == G(1));
        Assert.Contains(references, f => f.Component == "new_b" && f.Message.StartsWith("No connection bound", StringComparison.Ordinal));
        Assert.Contains(references, f => f is { Component: "new_c", Severity: ReadinessSeverity.Blocker } && f.Message.Contains("Error - Token expired"));
        Assert.Contains(references, f => f is { Component: "new_d", Severity: ReadinessSeverity.Info });
        Assert.Empty(report.NotChecked);
    }

    [Fact]
    public async Task Connection_status_that_cannot_be_read_is_noted_and_the_references_are_still_checked()
    {
        var (source, target) = ReferenceHandlers();

        var report = await Run(source, target, [Reference(1), Reference(2), Reference(3), Reference(4)],
            new PowerAutomateClient(TestAuth.Tokens(), new FakeHttpHandler()), "env-1");

        Assert.StartsWith("connection status in the target:", Assert.Single(report.NotChecked));
        Assert.Equal(2, report.Findings.Count(f => f.Area == "Connection references"));
    }

    [Fact]
    public async Task Without_a_flow_client_the_connection_status_is_not_asked_for()
    {
        var (source, target) = ReferenceHandlers();

        var report = await Run(source, target, [Reference(1), Reference(2), Reference(3), Reference(4)]);

        Assert.Empty(report.NotChecked);
        Assert.Equal(2, report.Findings.Count(f => f.Area == "Connection references"));
    }

    [Fact]
    public async Task A_flow_owned_by_a_disabled_user_with_no_name_is_still_warned_about()
    {
        var flow = new SolutionComponentItem { Name = "Notify", ComponentTypeName = "Process", ComponentType = 29, ProcessCategory = 5, ObjectId = G(5) };
        var source = Source().OnJson(HttpMethod.Get, "workflows?", $$"""{"value":[{"workflowid":"{{G(5)}}","statecode":1}]}""");
        var target = TargetWith(Installed(managed: true, "1.0.0.0"))
            .OnJson(HttpMethod.Get, "workflows?", $$$"""{"value":[{"workflowid":"{{{G(5)}}}","statecode":1,"owninguser":{"isdisabled":true}}]}""")
            .OnJson(HttpMethod.Get, "solutioncomponents?", NoDependencies);

        var report = await Run(source, target, [flow]);

        var finding = Assert.Single(report.Findings, f => f.Area == "Flows");
        Assert.StartsWith("Owned in the target by a user, who is disabled", finding.Message);
        Assert.Same(flow, finding.Item);
    }

    private static SolutionComponentItem Assembly(int n) => new()
    {
        Name = $"asm{n}", ComponentTypeName = "Plugin Assembly", ComponentType = 91, ObjectId = G(n)
    };

    [Fact]
    public async Task Plug_in_assemblies_compare_their_versions_with_the_target()
    {
        string Rows(params (int Id, string Version)[] rows) =>
            "{\"value\":[" + string.Join(",", rows.Select(r => $$"""{"pluginassemblyid":"{{G(r.Id)}}","name":"Contoso.Plugins{{r.Id}}","version":"{{r.Version}}"}""")) + "]}";
        var source = Source().OnJson(HttpMethod.Get, "pluginassemblies?", Rows((21, "1.0.0.2"), (22, "1.0.0.0"), (23, "3.0.0.0"), (24, "1.0.0.0")));
        var target = TargetWith(Installed(managed: true, "1.0.0.0"))
            .OnJson(HttpMethod.Get, "pluginassemblies?", Rows((21, "1.0.0.1"), (22, "2.0.0.0"), (23, "3.0.0.0")))
            .OnJson(HttpMethod.Get, "solutioncomponents?", NoDependencies);

        var report = await Run(source, target, [Assembly(21), Assembly(22), Assembly(23), Assembly(24)]);

        var plugins = report.Findings.Where(f => f.Area == "Plug-ins").ToList();
        Assert.Equal(2, plugins.Count);
        Assert.Contains(plugins, f => f is { Severity: ReadinessSeverity.Info, Component: "Contoso.Plugins21" } && f.Message.Contains("updates it to 1.0.0.2"));
        Assert.Contains(plugins, f => f is { Severity: ReadinessSeverity.Warning, Component: "Contoso.Plugins22" } && f.Message.Contains("would take it back"));
    }

    [Fact]
    public async Task Components_with_unmanaged_changes_in_the_target_are_warned_about()
    {
        var changed = new SolutionComponentItem { Name = "new_page.html", ComponentTypeName = "Web Resource", ComponentType = 61, ObjectId = G(31) };
        var clean = new SolutionComponentItem { Name = "new_clean.js", ComponentTypeName = "Web Resource", ComponentType = 61, ObjectId = G(32) };
        var absent = new SolutionComponentItem { Name = "new_absent.js", ComponentTypeName = "Web Resource", ComponentType = 61, ObjectId = G(33) };
        var target = TargetWith(Installed(managed: true, "1.0.0.0"))
            .OnJson(HttpMethod.Get, "solutioncomponents?", $$"""{"value":[{"objectid":"{{G(31)}}"},{"objectid":"{{G(32)}}"}]}""")
            .On(HttpMethod.Get, "msdyn_componentlayers", r => FakeHttpHandler.Json(r.Url.Contains(G(31).ToString())
                ? """{"value":[{"msdyn_solutionname":"Active","msdyn_order":2},{"msdyn_solutionname":"core","msdyn_order":1}]}"""
                : """{"value":[{"msdyn_solutionname":"core","msdyn_order":1}]}"""));

        var report = await Run(Source(), target, [changed, clean, absent]);

        var finding = Assert.Single(report.Findings, f => f.Area == "Unmanaged layers");
        Assert.Equal("new_page.html", finding.Component);
        Assert.Same(changed, finding.Item);
        Assert.Equal(2, target.Requests.Count(r => r.Url.Contains("msdyn_componentlayers")));
    }

    [Fact]
    public async Task Only_the_first_four_hundred_components_have_their_layers_checked()
    {
        var components = Enumerable.Range(1, ReadinessCheck.MaxLayerChecks + 1)
            .Select(i => new SolutionComponentItem { Name = $"c{i}", ComponentTypeName = "Other", ComponentType = 10999, ObjectId = G(1000 + i) })
            .ToList();
        var all = "{\"value\":[" + string.Join(",", components.Select(c => $$"""{"objectid":"{{c.ObjectId}}"}""")) + "]}";
        // Fifty ids make a URL too long for a GET, so each full chunk is looked up inside a $batch.
        var target = TargetWith(Installed(managed: true, "1.0.0.0"))
            .OnJson(HttpMethod.Get, "solutioncomponents?", all)
            .On(HttpMethod.Post, "$batch", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("--batchresponse\r\nContent-Type: application/http\r\n\r\nHTTP/1.1 200 OK\r\n\r\n" + all + "\r\n--batchresponse--\r\n")
            });

        var report = await Run(Source(), target, components);

        Assert.Equal("unmanaged layers: only the first 400 of 401 components in the target were checked", Assert.Single(report.NotChecked));
        Assert.DoesNotContain(report.Findings, f => f.Area == "Unmanaged layers");
        Assert.Equal(8, target.Requests.Count(r => r.Url.EndsWith("$batch", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(ReadinessSeverity.Blocker, "Blocker")]
    [InlineData(ReadinessSeverity.Warning, "Warning")]
    [InlineData(ReadinessSeverity.Info, "Info")]
    public void A_finding_labels_its_severity(ReadinessSeverity severity, string expected)
    {
        var finding = new ReadinessFinding { Severity = severity, Area = "a", Component = "c", Message = "m" };

        Assert.Equal(expected, finding.SeverityLabel);
    }

    [Fact]
    public void Markdown_cells_escape_pipes_and_line_breaks_and_list_notes_under_their_own_heading()
    {
        var report = new ReadinessReport { Solution = "Core", Source = "Dev", Target = "Prod", CheckedAt = new DateTimeOffset(2024, 5, 6, 7, 8, 0, TimeSpan.Zero) };
        report.Findings.Add(new ReadinessFinding { Severity = ReadinessSeverity.Info, Area = "A|B", Component = "line\r\nbreak", Message = "ok" });

        var markdown = report.ToMarkdown();

        Assert.Contains("checked 2024-05-06", markdown);
        Assert.Contains("## Notes", markdown);
        Assert.Contains(@"| A\|B | line  break | ok |", markdown);
        Assert.DoesNotContain("## Warnings", markdown);
        Assert.DoesNotContain("## Not checked", markdown);
    }

    [Fact]
    public void Versions_that_do_not_parse_compare_as_text()
    {
        Assert.True(ReadinessCheck.CompareVersions("beta", "alpha") > 0);
        Assert.True(ReadinessCheck.CompareVersions(null, "1.0") < 0);
    }

    /// <summary>Reports progress on the calling thread, so the list is complete when the run returns.</summary>
    private sealed class SyncProgress(List<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Add(value);
    }
}
