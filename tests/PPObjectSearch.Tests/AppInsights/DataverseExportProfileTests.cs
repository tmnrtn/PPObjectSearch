using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using static PPObjectSearch.Tests.AppInsights.TelemetryFixtures;

namespace PPObjectSearch.Tests.AppInsights;

public class DataverseExportProfileTests
{
    private static readonly TelemetryScope Scope = new(Guid.Parse(OrganizationId), "env-1", "contoso.crm11.dynamics.com");
    private static readonly DateTimeOffset From = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(7);

    // ---------------------------------------------------------------- KQL

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("O'Brien", @"'O\'Brien'")]
    [InlineData(@"C:\temp", @"'C:\\temp'")]
    [InlineData("a\nb", @"'a\nb'")]
    public void A_literal_cannot_end_its_string_early(string value, string literal) =>
        Assert.Equal(literal, Kql.Literal(value));

    [Fact]
    public void Requests_are_this_environments_by_the_host_in_their_url()
    {
        Assert.Equal("| where url contains '//contoso.crm11.dynamics.com/'\n",
            DataverseExportProfile.EnvironmentFilter(Scope, "requests"));
    }

    [Theory]
    [InlineData("dependencies")]
    [InlineData("exceptions")]
    public void Plug_ins_and_exceptions_are_this_environments_by_sharing_an_operation_with_its_requests(string table)
    {
        Assert.Equal("| where operation_Id in (requests | where url contains '//contoso.crm11.dynamics.com/' | distinct operation_Id)\n",
            DataverseExportProfile.EnvironmentFilter(Scope, table));
    }

    [Fact]
    public void A_resource_for_this_environment_alone_is_not_filtered()
    {
        Assert.Empty(DataverseExportProfile.EnvironmentFilter(Scope with { OnlyThisEnvironment = false }, "requests"));
        Assert.Empty(DataverseExportProfile.EnvironmentFilter(new TelemetryScope(null, null, null), "dependencies"));
    }

    [Fact]
    public void Every_failures_query_is_narrowed_to_the_environment()
    {
        Assert.Contains("operation_Id in (requests", DataverseExportProfile.FailedPluginsQuery(Scope, null));
        Assert.Contains("operation_Id in (requests", DataverseExportProfile.ExceptionsQuery(Scope));
        Assert.Contains("| where url contains", DataverseExportProfile.FailedRequestsQuery(Scope));
        Assert.Contains("operation_Id in (requests", DataverseExportProfile.PluginSummaryQuery(Scope, ["Contoso.A"]));
    }

    [Fact]
    public void Plug_in_types_are_matched_by_the_start_of_the_name_and_escaped()
    {
        var query = DataverseExportProfile.FailedPluginsQuery(Scope, ["Contoso.Plugins.A", "Bad'Name"]);

        Assert.StartsWith("dependencies\n| where type =~ 'Plugin'", query);
        Assert.Contains("| where success == false", query);
        Assert.Contains("name startswith 'Contoso.Plugins.A' or name startswith 'Bad\\'Name'", query);
        Assert.EndsWith($"| take {DataverseExportProfile.MaxRows}", query);
    }

    [Fact]
    public void A_solution_without_plug_ins_reads_none()
    {
        Assert.Contains("| where false", DataverseExportProfile.FailedPluginsQuery(Scope, []));
        Assert.DoesNotContain("startswith", DataverseExportProfile.FailedPluginsQuery(Scope, null));
    }

    // ---------------------------------------------------------------- rows to failures

    [Fact]
    public void A_failed_plug_in_carries_the_exception_it_threw()
    {
        var events = DataverseExportProfile.ToFailures(Table(FailedPlugins), Table(Exceptions), Table(FailedRequests), includeUnmatched: true);

        var plugin = Assert.Single(events, e => e.IsPlugin);
        Assert.Equal(FailureSource.AppInsights, plugin.Source);
        Assert.Equal("Contoso.Plugins.AccountPostUpdate", plugin.ComponentKey);
        Assert.Equal("Credit limit cannot be negative.", plugin.ErrorMessage);
        Assert.Equal("Microsoft.Xrm.Sdk.InvalidPluginExecutionException", plugin.ErrorCode);
        Assert.Equal("Update of account", plugin.Context);
        Assert.Equal("op-1", plugin.OperationId);
        Assert.Contains("Problem id: InvalidPluginExecutionException at AccountPostUpdate", plugin.Details);
    }

    [Fact]
    public void A_request_failed_by_a_plug_in_is_not_listed_twice()
    {
        var events = DataverseExportProfile.ToFailures(Table(FailedPlugins), Table(Exceptions), Table(FailedRequests), includeUnmatched: true);

        Assert.DoesNotContain(events, e => e.ComponentKey == "request:PATCH accounts");

        var request = Assert.Single(events, e => e.ComponentKey == "request:POST contacts");
        Assert.Equal("412", request.ErrorCode);
        Assert.Equal("A record with matching key values already exists.\nDuplicate key", request.ErrorMessage);
        Assert.Equal("Request 412", request.StepLabel);
    }

    [Fact]
    public void An_exception_nothing_claimed_is_listed_for_the_whole_environment_only()
    {
        var all = DataverseExportProfile.ToFailures(Table(FailedPlugins), Table(Exceptions), Table(FailedRequests), includeUnmatched: true);
        var scoped = DataverseExportProfile.ToFailures(Table(FailedPlugins), Table(Exceptions), TelemetryTable.Empty, includeUnmatched: false);

        var stray = Assert.Single(all, e => e.ComponentKey.StartsWith("exception:", StringComparison.Ordinal));
        Assert.Equal("RetrieveMultiple", stray.ComponentName);
        Assert.Equal("Exception on account", stray.StepLabel);
        Assert.Equal("The operation timed out.", stray.ErrorMessage);
        Assert.Equal(3, all.Count);
        Assert.Single(scoped);
    }

    [Theory]
    [InlineData("Contoso.Plugins.AccountPostUpdate", "Contoso.Plugins.AccountPostUpdate")]
    [InlineData("Contoso.Plugins.AccountPostUpdate, Contoso.Plugins, Version=1.0.0.0", "Contoso.Plugins.AccountPostUpdate")]
    [InlineData("Contoso.Plugins.AccountPostUpdate: Update of account", "Contoso.Plugins.AccountPostUpdate")]
    [InlineData("Contoso.Plugins.AccountPostUpdate (1.0.0.0)", "Contoso.Plugins.AccountPostUpdate")]
    [InlineData("", "(unknown plug-in)")]
    public void The_plug_in_type_is_the_class_name(string name, string type) =>
        Assert.Equal(type, DataverseExportProfile.PluginTypeName(name));

    [Fact]
    public void The_context_is_the_step_names_description_or_else_the_table()
    {
        Assert.Equal("Creation of contact", DataverseExportProfile.PluginContext(
            new Dictionary<string, string> { ["StepName"] = "Contoso.Validate: Creation of contact", ["EntityName"] = "contact" }));
        Assert.Equal("contact", DataverseExportProfile.PluginContext(
            new Dictionary<string, string> { ["StepName"] = "Validate on contact", ["EntityName"] = "contact" }));
        Assert.Null(DataverseExportProfile.PluginContext(new Dictionary<string, string> { ["EntityName"] = "none" }));
        Assert.Null(DataverseExportProfile.PluginContext(new Dictionary<string, string>()));
    }

    [Fact]
    public void The_plug_in_type_dimension_names_the_component_over_the_name_column()
    {
        var plugins = Table(TelemetryFixtures.FailedPlugins.Replace("\"Contoso.Plugins.AccountPostUpdate\",\"Failed\"",
            "\"Contoso.Plugins.AccountPostUpdate v2.1\",\"Failed\"", StringComparison.Ordinal));

        var plugin = Assert.Single(DataverseExportProfile.ToFailures(plugins, TelemetryTable.Empty, TelemetryTable.Empty, includeUnmatched: false));

        Assert.Equal("Contoso.Plugins.AccountPostUpdate", plugin.ComponentKey);
    }

    // ---------------------------------------------------------------- reading

    [Fact]
    public async Task The_whole_environment_reads_plug_ins_exceptions_and_requests()
    {
        var handler = new FakeHttpHandler().OnDataverseExport();
        using var client = AzureMonitorClientTests.Client(handler);
        var data = new FailureData();

        await DataverseExportProfile.Instance.ReadFailuresAsync(client, AzureMonitorClientTests.AppId, Scope, From, To, new FailureQuery(), data, default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, data.Events.Count);
        Assert.Equal(6, data.TelemetryRowsRead);
        Assert.Empty(data.Notes);
    }

    [Fact]
    public async Task A_solution_reads_only_its_plug_ins()
    {
        var handler = new FakeHttpHandler().OnDataverseExport();
        using var client = AzureMonitorClientTests.Client(handler);
        var data = new FailureData();
        var query = new FailureQuery
        {
            WorkflowIds = new HashSet<Guid>(),
            PluginTypeNames = new HashSet<string>(["Contoso.Plugins.AccountPostUpdate"])
        };

        await DataverseExportProfile.Instance.ReadFailuresAsync(client, AzureMonitorClientTests.AppId, Scope, From, To, query, data, default);

        Assert.DoesNotContain(handler.Requests, r => Query(r).StartsWith("requests", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => Query(r).Contains("name startswith 'Contoso.Plugins.AccountPostUpdate'"));
        Assert.True(Assert.Single(data.Events).IsPlugin);
    }

    [Fact]
    public async Task A_result_at_the_cap_or_incomplete_is_noted()
    {
        var full = JsonSerializer.Serialize(new
        {
            tables = new[]
            {
                new
                {
                    name = "PrimaryResult",
                    columns = new[] { new { name = "name", type = "string" } },
                    rows = Enumerable.Range(0, DataverseExportProfile.MaxRows).Select(i => new object[] { $"P{i}" }).ToArray()
                }
            }
        });
        var partial = """{"tables":[{"name":"PrimaryResult","columns":[],"rows":[]}],"error":{"message":"Partial result"}}""";
        using var client = AzureMonitorClientTests.Client(new FakeHttpHandler().OnDataverseExport(plugins: full, requests: partial));
        var data = new FailureData();

        await DataverseExportProfile.Instance.ReadFailuresAsync(client, AzureMonitorClientTests.AppId, Scope, From, To, new FailureQuery(), data, default);

        Assert.Contains(data.Notes, n => n.Contains("failed plug-ins stopped at"));
        Assert.Contains(data.Notes, n => n.Contains("failed requests came back incomplete: Partial result"));
    }

    // ---------------------------------------------------------------- check

    [Fact]
    public async Task The_check_counts_rows_and_this_environments_share()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Post, "/query",
            """{"tables":[{"name":"PrimaryResult","columns":[{"name":"Total","type":"long"},{"name":"Requests","type":"long"},{"name":"Matching","type":"long"}],"rows":[[1200,400,300]]}]}""");
        using var client = AzureMonitorClientTests.Client(handler);
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        var check = await TelemetryProfiles.CheckAsync(client, DataverseExportProfile.Instance, AzureMonitorClientTests.AppId, Scope, now);

        Assert.Equal(new TelemetryCheck(1200, 400, 300), check);
        Assert.StartsWith("union requests", Query(handler.Requests[0]));
        Assert.Contains("Matching = countif(itemType == 'request' and url contains '//contoso.crm11.dynamics.com/')", Query(handler.Requests[0]));
        Assert.Contains("2026-10-21T12:00:00Z/2026-10-22T12:00:00Z", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData(0, 0, 0, true, "Nothing has arrived", false)]
    [InlineData(500, 0, 0, true, "but no requests", false)]
    [InlineData(500, 200, 0, true, "None of the 200 requests", false)]
    [InlineData(500, 200, 120, true, "120 of 200 requests", true)]
    [InlineData(500, 200, 200, true, "all 200 requests in the last day were made to it", true)]
    [InlineData(500, 200, 0, false, "all read as this environment's", true)]
    public void The_check_says_what_it_found(long total, long requests, long matching, bool only, string says, bool found)
    {
        var check = new TelemetryCheck(total, requests, matching);

        Assert.Contains(says, check.Describe(only));
        Assert.Equal(found, check.Found(only));
    }

    private static string Query(RecordedRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("query").GetString()!;
}
