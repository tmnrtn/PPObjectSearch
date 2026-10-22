using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.AppInsights;

/// <summary>
/// Query responses shaped like the Dataverse data export's rows. The column names are App
/// Insights' own; the customDimensions keys are provisional until real rows are captured
/// (TRACKER.md, step 2.1) and should be replaced with redacted real ones then.
/// </summary>
internal static class TelemetryFixtures
{
    public const string OrganizationId = "6f3b2c1a-0000-4000-8000-00000000c0de";
    public const string Operation = "op-1";

    public const string FailedPlugins = """
        {"tables":[{"name":"PrimaryResult",
          "columns":[{"name":"timestamp","type":"datetime"},{"name":"id","type":"string"},{"name":"operation_Id","type":"string"},
                     {"name":"name","type":"string"},{"name":"resultCode","type":"string"},{"name":"duration","type":"real"},
                     {"name":"customDimensions","type":"dynamic"}],
          "rows":[
            ["2026-10-20T08:15:02.000Z","dep-1","op-1","Contoso.Plugins.AccountPostUpdate","Failed",812.5,
             "{\"MessageName\":\"Update\",\"PrimaryEntityName\":\"account\",\"orgId\":\"6f3b2c1a-0000-4000-8000-00000000c0de\"}"]
          ]}]}
        """;

    public const string Exceptions = """
        {"tables":[{"name":"PrimaryResult",
          "columns":[{"name":"timestamp","type":"datetime"},{"name":"operation_Id","type":"string"},{"name":"operation_ParentId","type":"string"},
                     {"name":"operation_Name","type":"string"},{"name":"type","type":"string"},{"name":"outerMessage","type":"string"},
                     {"name":"innermostMessage","type":"string"},{"name":"problemId","type":"string"},{"name":"customDimensions","type":"dynamic"}],
          "rows":[
            ["2026-10-20T08:15:02.010Z","op-1","dep-1","PATCH accounts","Microsoft.Xrm.Sdk.InvalidPluginExecutionException",
             "Credit limit cannot be negative.","Credit limit cannot be negative.","InvalidPluginExecutionException at AccountPostUpdate","{}"],
            ["2026-10-20T09:00:00.000Z","op-2","req-2","POST contacts","System.ServiceModel.FaultException",
             "A record with matching key values already exists.","Duplicate key","FaultException at Create","{}"],
            ["2026-10-20T10:00:00.000Z","op-3","","GET accounts","System.TimeoutException","The operation timed out.","","TimeoutException","{}"]
          ]}]}
        """;

    public const string FailedRequests = """
        {"tables":[{"name":"PrimaryResult",
          "columns":[{"name":"timestamp","type":"datetime"},{"name":"id","type":"string"},{"name":"operation_Id","type":"string"},
                     {"name":"name","type":"string"},{"name":"resultCode","type":"string"},{"name":"url","type":"string"},
                     {"name":"duration","type":"real"},{"name":"customDimensions","type":"dynamic"}],
          "rows":[
            ["2026-10-20T08:15:01.900Z","req-1","op-1","PATCH accounts","400","https://contoso.crm11.dynamics.com/api/data/v9.2/accounts(1)",900,"{}"],
            ["2026-10-20T09:00:00.000Z","req-2","op-2","POST contacts","412","https://contoso.crm11.dynamics.com/api/data/v9.2/contacts",120,"{}"]
          ]}]}
        """;

    public static TelemetryTable Table(string json) => AzureMonitorClient.ReadTable(JsonDocument.Parse(json));

    /// <summary>Answers each of the profile's queries by the table it starts with.</summary>
    public static FakeHttpHandler OnDataverseExport(this FakeHttpHandler handler,
        string plugins = FailedPlugins, string exceptions = Exceptions, string requests = FailedRequests) =>
        handler.On(HttpMethod.Post, "/query", r =>
        {
            var query = JsonDocument.Parse(r.Body!).RootElement.GetProperty("query").GetString()!;
            return FakeHttpHandler.Json(query switch
            {
                _ when query.StartsWith("dependencies", StringComparison.Ordinal) => plugins,
                _ when query.StartsWith("exceptions", StringComparison.Ordinal) => exceptions,
                _ when query.StartsWith("requests", StringComparison.Ordinal) => requests,
                _ => """{"tables":[]}"""
            });
        });
}
