using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class ReadinessCheckTests
{
    private const string TargetUrl = "https://target.crm11.dynamics.com";

    private static readonly Guid SolutionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Flow = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Variable = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid MissingTable = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid PresentTable = Guid.Parse("50000000-0000-0000-0000-000000000005");

    private static EnvironmentVariableInfo Var(string? current = null, string? @default = null) => new()
    {
        SchemaName = "new_Url",
        TypeLabel = "Text",
        CurrentValue = current,
        HasCurrentValue = current is not null,
        DefaultValue = @default
    };

    private static ConnectionReferenceInfo Ref(string? connection) => new()
    {
        Id = Guid.NewGuid(), LogicalName = "new_outlook", ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_office365",
        ConnectionId = connection
    };

    [Theory]
    [InlineData("1.0.0.1", "1.0.0.2", -1)]
    [InlineData("1.10.0.0", "1.9.0.0", 1)]
    [InlineData("2.0", "2.0", 0)]
    public void Versions_compare_numerically(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(ReadinessCheck.CompareVersions(a, b)));
    }

    [Fact]
    public void A_variable_with_nothing_to_resolve_to_is_flagged()
    {
        var finding = Assert.Single(ReadinessCheck.EvaluateVariable(Var(), null));
        Assert.Equal(ReadinessSeverity.Warning, finding.Severity);
        Assert.Contains("New to the target", finding.Message);

        // A default travels with the solution, so the variable resolves to it.
        Assert.Empty(ReadinessCheck.EvaluateVariable(Var(@default: "x"), null));
        Assert.Empty(ReadinessCheck.EvaluateVariable(Var(), Var(current: "set in target")));
    }

    [Fact]
    public void A_source_value_leaking_into_the_target_is_flagged()
    {
        var finding = Assert.Single(ReadinessCheck.EvaluateVariable(Var(current: "https://dev"), Var(current: "https://dev")));
        Assert.Contains("same as the source", finding.Message);

        Assert.Empty(ReadinessCheck.EvaluateVariable(Var(current: "https://dev"), Var(current: "https://prod")));
    }

    [Fact]
    public void References_without_a_working_connection_in_the_target_are_flagged()
    {
        Assert.Contains("New to the target", ReadinessCheck.EvaluateReference(Ref("c"), null, null, true)!.Message);
        Assert.Contains("No connection bound", ReadinessCheck.EvaluateReference(Ref("c"), Ref(null), null, true)!.Message);

        var failing = new ConnectionInfo { Name = "c", Status = "Error", StatusMessage = "expired" };
        var blocker = ReadinessCheck.EvaluateReference(Ref("c"), Ref("c"), failing, true)!;
        Assert.Equal(ReadinessSeverity.Blocker, blocker.Severity);
        Assert.Contains("expired", blocker.Message);

        Assert.Null(ReadinessCheck.EvaluateReference(Ref("c"), Ref("c"), new ConnectionInfo { Name = "c", Status = "Connected" }, true));
        Assert.Equal(ReadinessSeverity.Info, ReadinessCheck.EvaluateReference(Ref("c"), Ref("c"), null, true)!.Severity);
    }

    [Fact]
    public async Task A_run_reports_each_area_and_what_it_could_not_check()
    {
        var source = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "RetrieveMissingDependencies", $$"""
                {"EntityCollection":[
                  {"requiredcomponentobjectid":"{{MissingTable}}","requiredcomponenttype":1,"dependentcomponentobjectid":"{{Flow}}","dependentcomponenttype":29},
                  {"requiredcomponentobjectid":"{{PresentTable}}","requiredcomponenttype":1,"dependentcomponentobjectid":"{{Flow}}","dependentcomponenttype":29}
                ]}
                """)
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Variable})", """
                {"schemaname":"new_Url","type":100000000,"defaultvalue":null,
                 "environmentvariabledefinition_environmentvariablevalue":[{"environmentvariablevalueid":"60000000-0000-0000-0000-000000000006","value":"https://dev"}]}
                """)
            .OnJson(HttpMethod.Get, "workflows?", $$$"""{"value":[{"workflowid":"{{{Flow}}}","statecode":0,"owninguser":{"fullname":"Dev","isdisabled":false}}]}""");

        var target = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "solutions?", """{"value":[{"solutionid":"70000000-0000-0000-0000-000000000007","uniquename":"core","friendlyname":"Core","ismanaged":true,"version":"1.2.0.0"}]}""")
            .OnJson(HttpMethod.Get, "solutioncomponents?", $$"""{"value":[{"objectid":"{{PresentTable}}"},{"objectid":"{{Flow}}"}]}""")
            .OnJson(HttpMethod.Get, "environmentvariabledefinitions?", """{"value":[{"environmentvariabledefinitionid":"80000000-0000-0000-0000-000000000008"}]}""")
            .OnJson(HttpMethod.Get, "environmentvariabledefinitions(80000000", """
                {"schemaname":"new_Url","type":100000000,
                 "environmentvariabledefinition_environmentvariablevalue":[{"environmentvariablevalueid":"90000000-0000-0000-0000-000000000009","value":"https://dev"}]}
                """)
            .OnJson(HttpMethod.Get, "workflows?", $$$"""{"value":[{"workflowid":"{{{Flow}}}","statecode":1,"owninguser":{"fullname":"Leaver","isdisabled":true}}]}""")
            .OnError(HttpMethod.Get, "RetrieveSolutionComponentLayers", HttpStatusCode.Forbidden, "no access to layers")
            .OnError(HttpMethod.Get, "msdyn_componentlayers", HttpStatusCode.Forbidden, "no access to layers");

        var components = new[]
        {
            new SolutionComponentItem { Name = "Notify", ComponentTypeName = "Process", ComponentType = 29, ProcessCategory = 5, ObjectId = Flow },
            new SolutionComponentItem { Name = "new_Url", ComponentTypeName = "Environment Variable Definition", ComponentType = 380, ObjectId = Variable }
        };
        var solution = new SolutionInfo { SolutionId = SolutionId, UniqueName = "core", FriendlyName = "Core", Version = "1.1.0.0" };

        var check = new ReadinessCheck(Fakes.Dataverse(source), Fakes.Dataverse(target, TargetUrl), null, null);
        var report = await check.RunAsync(solution, components, "Dev", "Prod");

        Assert.Contains(report.Findings, f => f is { Severity: ReadinessSeverity.Blocker, Area: "Version" } && f.Message.Contains("1.2.0.0"));

        var dependency = Assert.Single(report.Findings, f => f.Area == "Dependencies");
        Assert.Contains(MissingTable.ToString(), dependency.Component);
        Assert.Contains("Notify", dependency.Message);
        Assert.Equal(Flow, dependency.Item!.ObjectId);

        Assert.Contains(report.Findings, f => f.Area == "Environment variables" && f.Message.Contains("same as the source"));
        Assert.Contains(report.Findings, f => f.Area == "Flows" && f.Message.Contains("Off in the source"));
        Assert.Contains(report.Findings, f => f.Area == "Flows" && f.Message.Contains("Leaver"));

        var markdown = report.ToMarkdown();
        Assert.Contains("# Readiness: Core", markdown);
        Assert.Contains("## Blockers", markdown);
        Assert.Contains("| Dependencies |", markdown);
    }
}
