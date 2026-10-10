using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>Import jobs, readiness reads, privilege depths, queue fallbacks and plug-in failure scoping on the Dataverse client.</summary>
public class DataverseClientReadsTests
{
    private static readonly Guid Job = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid Assembly = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- import jobs

    [Fact]
    public async Task One_import_job_is_read_with_its_progress()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"importjobs({Job})", """
            {"solutionname":"core","progress":42.5,"startedon":"2026-03-01T12:00:00Z","createdon":"2026-03-01T11:59:00Z"}
            """);

        var job = await Fakes.Dataverse(handler).GetImportJobAsync(Job);

        Assert.Equal(new ImportJobInfo(Job, "core", 42.5, T0, null, T0.AddMinutes(-1)), job);
        Assert.False(job!.IsFinished);
        Assert.Null(handler.Requests.Single().Header("Prefer"));
    }

    [Fact]
    public async Task An_import_job_without_a_numeric_progress_has_none()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"importjobs({Job})", """{"progress":"n/a","completedon":"2026-03-01T12:00:00Z"}""");

        var job = await Fakes.Dataverse(handler).GetImportJobAsync(Job);

        Assert.Null(job!.Progress);
        Assert.True(job.IsFinished);
    }

    [Theory]
    [InlineData(100.0, false, true)]
    [InlineData(99.9, false, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void A_job_is_finished_when_it_completed_or_reached_a_hundred(double? progress, bool completed, bool expected)
    {
        var job = new ImportJobInfo(Job, "core", progress, T0, completed ? T0 : null, T0);

        Assert.Equal(expected, job.IsFinished);
    }

    [Fact]
    public void Without_a_start_time_the_newest_job_is_the_match_and_jobs_without_times_never_match_by_time()
    {
        var undated = new ImportJobInfo(Job, "core", 100, null, null, null);
        var entry = new SolutionHistoryEntry { Id = Guid.NewGuid(), SolutionName = "core", Operation = "Import" };

        Assert.Same(undated, DataverseClient.MatchImportJob([undated], entry));
        Assert.Null(DataverseClient.MatchImportJob([], entry));
        Assert.Null(DataverseClient.MatchImportJob([undated],
            new SolutionHistoryEntry { Id = Guid.NewGuid(), SolutionName = "core", Operation = "Import", StartTime = T0 }));
    }

    // ---------------------------------------------------------------- readiness reads

    [Fact]
    public async Task Plug_in_assembly_versions_skip_rows_without_an_id_and_name_unnamed_ones_by_id()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "pluginassemblies?", $$"""
            {"value":[{"pluginassemblyid":"{{Assembly}}","version":"1.0.0.0"},{"pluginassemblyid":"not-a-guid","name":"x"}]}
            """);

        var versions = await Fakes.Dataverse(handler).GetPluginAssemblyVersionsAsync([Assembly, Assembly]);

        var (name, version) = Assert.Single(versions).Value;
        Assert.Equal(Assembly.ToString(), name);
        Assert.Equal("1.0.0.0", version);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_page_without_rows_reads_as_nothing_found()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "pluginassemblies?", "{}")
            .OnJson(HttpMethod.Get, "workflows?", "{}");
        var client = Fakes.Dataverse(handler);

        Assert.Empty(await client.GetPluginAssemblyVersionsAsync([Assembly]));
        Assert.Empty(await client.GetFlowOwnershipAsync([Assembly]));
        Assert.Empty(await client.GetPluginAssemblyVersionsAsync([]));
    }

    [Theory]
    [InlineData("""{"EntityCollection":[{"requiredcomponentobjectid":"e0000000-0000-0000-0000-000000000009","requiredcomponenttype":1}]}""", 1)]
    [InlineData("""{"EntityCollection":{"Entities":[{"requiredcomponentobjectid":"e0000000-0000-0000-0000-000000000009"}]}}""", 1)]
    [InlineData("""{"EntityCollection":{}}""", 0)]
    [InlineData("""{"value":[{"requiredcomponentobjectid":"bad"}]}""", 0)]
    [InlineData("""{}""", 0)]
    public async Task Missing_dependencies_are_read_from_whichever_shape_the_response_has(string json, int expected)
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "RetrieveMissingDependencies", json);

        var missing = await Fakes.Dataverse(handler).GetMissingDependenciesAsync("o'brien");

        Assert.Equal(expected, missing.Count);
        Assert.Contains("@name='o''brien'", handler.Requests.Single().Url);
    }

    [Theory]
    [InlineData("""{"value":[]}""")]
    [InlineData("""{"value":[{"environmentvariabledefinitionid":"not-a-guid"}]}""")]
    public async Task A_variable_not_in_the_environment_is_none(string json)
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "environmentvariabledefinitions?", json);

        Assert.Null(await Fakes.Dataverse(handler).GetEnvironmentVariableBySchemaNameAsync("new_Url"));
    }

    [Fact]
    public async Task Existing_component_ids_follow_the_next_link()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "page=2", $$"""{"value":[{"objectid":"{{second}}"},{"objectid":"bad"}]}""")
            .OnJson(HttpMethod.Get, "solutioncomponents?", $$"""{"value":[{"objectid":"{{first}}"}],"@odata.nextLink":"{{Fakes.ApiRoot}}solutioncomponents?page=2"}""");

        var found = await Fakes.Dataverse(handler).GetExistingComponentIdsAsync([first, second]);

        Assert.Equal(new HashSet<Guid> { first, second }, found);
    }

    // ---------------------------------------------------------------- privileges and queues

    [Theory]
    [InlineData("""{"Depth":2}""", PrivilegeDepth.ParentChild)]
    [InlineData("""{"Depth":"Deep"}""", PrivilegeDepth.ParentChild)]
    [InlineData("""{"Depth":"Local"}""", PrivilegeDepth.BusinessUnit)]
    [InlineData("""{"Depth":3}""", PrivilegeDepth.Organization)]
    [InlineData("""{"Depth":true}""", PrivilegeDepth.None)]
    [InlineData("""{"Depth":"Sideways"}""", PrivilegeDepth.None)]
    [InlineData("""{}""", PrivilegeDepth.None)]
    public void A_privilege_depth_is_read_by_name_or_number(string json, PrivilegeDepth expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, DataverseClient.ParseDepth(doc.RootElement));
    }

    [Fact]
    public async Task Queues_fall_back_to_the_core_columns_when_the_full_list_is_refused()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "queues?$select=queueid,name,emailaddress,queueviewtype,_ownerid_value,_businessunitid_value,_defaultmailbox_value,incomingemailfilteringmethod",
                HttpStatusCode.BadRequest, "Could not find a property named 'numberofitems'")
            .OnJson(HttpMethod.Get, "queues?", """
                {"value":[
                  {"queueid":"e0000000-0000-0000-0000-000000000011","name":"Support","statecode":1,
                   "_ownerid_value@Microsoft.Dynamics.CRM.lookuplogicalname":"team"},
                  {"queueid":"e0000000-0000-0000-0000-000000000012",
                   "_ownerid_value@Microsoft.Dynamics.CRM.lookuplogicalname":"systemuser"},
                  {"queueid":"e0000000-0000-0000-0000-000000000013","name":"Odd",
                   "_ownerid_value@Microsoft.Dynamics.CRM.lookuplogicalname":"account"},
                  {"queueid":"bad"}]}
                """);

        var queues = await Fakes.Dataverse(handler).GetQueueDetailsAsync();

        Assert.Equal(3, queues.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("numberofitems", handler.Requests[1].Url);
        Assert.Equal(["Team", "User", "account"], queues.Select(q => q.OwnerType));
        Assert.Equal("(unnamed)", queues[1].Name);
    }

    // ---------------------------------------------------------------- failures

    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Plug_in_failures_say_when_tracing_is_off_and_keep_only_the_scoped_plug_ins()
    {
        var step = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"plugintracelogsetting":0}]}""")
            .OnJson(HttpMethod.Get, "plugintracelogs?", $$"""
                {"value":[
                  {"plugintracelogid":"t1","typename":"Contoso.InScope","exceptiondetails":"Message: In scope","primaryentity":"none","messagename":"Create"},
                  {"plugintracelogid":"t2","typename":"Contoso.ByStep","pluginstepid":"{{step}}","exceptiondetails":"By step"},
                  {"plugintracelogid":"t3","typename":"Contoso.Other","exceptiondetails":"Out of scope"},
                  {"plugintracelogid":"t4","exceptiondetails":" "}]}
                """);
        var query = new FailureQuery
        {
            CloudFlows = false, ClassicWorkflows = false,
            PluginTypeNames = new HashSet<string> { "Contoso.InScope" }, PluginStepIds = new HashSet<Guid> { step }
        };

        var data = await Fakes.Dataverse(handler).GetFailuresAsync(From, To, query);

        Assert.Equal(["Plug-in tracing is off in this environment, so only failures traced before it was turned off appear."], data.Notes);
        Assert.Equal(4, data.TraceLogsRead);
        Assert.Equal(["Contoso.InScope", "Contoso.ByStep"], data.Events.Select(e => e.ComponentName));
        Assert.Equal("Create", data.Events[0].Context);
        Assert.Equal(From, data.Events[0].When);
    }

    [Fact]
    public async Task Cancelling_a_failures_read_is_not_noted_as_a_source_that_failed()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "flowruns?", _ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Fakes.Dataverse(handler).GetFailuresAsync(From, To, new FailureQuery { ClassicWorkflows = false, Plugins = false }));
    }
}
