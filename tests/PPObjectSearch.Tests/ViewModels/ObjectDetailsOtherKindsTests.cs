using System.Net;
using System.Net.Http;
using System.Text;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ViewModels.DetailsHarness;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// The details window's own tabs for web resources, environment variables, plug-ins, classic
/// workflows, connection references and the kinds with an Overview.
/// </summary>
public class ObjectDetailsOtherKindsTests
{
    private static readonly Guid DefinitionId = Guid.Parse("de000000-0000-0000-0000-000000000001");

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string WebResource(string name, int type, string label, string content) =>
        $$"""{"name":"{{name}}","webresourcetype":{{type}},"webresourcetype@OData.Community.Display.V1.FormattedValue":"{{label}}","content":"{{content}}"}""";

    private static string Variable(int type, params string[] values) =>
        $$"""
        {"schemaname":"new_ApiUrl","type":{{type}},"defaultvalue":"https://default",
         "environmentvariabledefinition_environmentvariablevalue":[{{string.Join(",", values.Select((v, i) =>
             $$$"""{"environmentvariablevalueid":"ee000000-0000-0000-0000-00000000000{{{i}}}","value":"{{{v}}}"}"""))}}]}
        """;

    private static string TraceSetting(int? setting) =>
        setting is null ? Empty : $$"""{"value":[{"plugintracelogsetting":{{setting}}}]}""";

    private static string Traces(IReadOnlyList<string?> exceptions) =>
        $$"""{"value":[{{string.Join(",", exceptions.Select((e, i) =>
            $$$"""{"plugintracelogid":"7e000000-0000-0000-0000-00000000000{{{i}}}","typename":"Contoso.Plugins.OnCreate","exceptiondetails":{{{(e is null ? "null" : $"\"{e}\"")}}}}"""))}}]}""";

    private static FakeHttpHandler PluginStep(int? setting, string traces) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "organizations?$select=plugintracelogsetting", TraceSetting(setting))
        .OnJson(HttpMethod.Get, "plugintracelogs?", traces)
        .OnJson(HttpMethod.Get, "sdkmessageprocessingsteps?", $$"""{"value":[{"sdkmessageprocessingstepid":"{{Id}}","statecode":0}]}""")
        .Quiet();

    // ---------------------------------------------------------------- web resources

    [Fact]
    public async Task A_script_is_shown_as_highlighted_text()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"webresourceset({Id})", WebResource("new_/scripts/account.js", 3, "Script (JScript)", Base64("alert(1);")))
            .Quiet();
        var details = Details(handler, Item(61));

        await details.LoadAsync();

        Assert.Equal("alert(1);", details.SourceText);
        Assert.Equal(CodeLanguage.JavaScript, details.SourceLanguage);
        Assert.Equal("new_/scripts/account.js · Script (JScript) · 9 bytes", details.SourceStatus);
        Assert.Equal("Source", details.SourceTabHeader);
        Assert.Equal(DetailsTab.Source, details.SelectedTab);
    }

    [Fact]
    public async Task An_image_is_not_shown_as_text()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"webresourceset({Id})", WebResource("new_logo", 5, "PNG format", Base64("PNG!")))
            .Quiet();
        var details = Details(handler, Item(61));

        await details.LoadAsync();

        Assert.Null(details.SourceText);
        Assert.Equal(CodeLanguage.None, details.SourceLanguage);
        Assert.Equal("PNG format - binary content (4 bytes), not shown as text.", details.SourceStatus);
        Assert.False(details.SaveSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_minified_script_is_shown_without_colour_and_says_so()
    {
        var minified = new string('x', CodeHighlighting.MaxHighlightedLineLength + 1);
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"webresourceset({Id})", WebResource("new_bundle.js", 3, "Script (JScript)", Base64(minified)))
            .Quiet();
        var details = Details(handler, Item(61));

        await details.LoadAsync();

        Assert.Equal(minified, details.SourceText);
        Assert.EndsWith(" · not highlighted (too large, or minified)", details.SourceStatus);
    }

    [Fact]
    public async Task Content_that_cannot_be_read_says_why()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"webresourceset({Id})", WebResource("new_broken.js", 3, "Script (JScript)", "%%%not base64"))
            .Quiet();
        var details = Details(handler, Item(61));

        await details.LoadAsync();

        Assert.Equal("Could not read the source - The web resource's content is not valid base64.", details.SourceStatus);
        Assert.Contains("source: The web resource's content is not valid base64.", details.Status);
    }

    // ---------------------------------------------------------------- environment variables

    [Fact]
    public async Task A_variable_shows_its_current_value_ready_to_edit()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Id})", Variable(100000000, "https://current"))
            .Quiet();
        var details = Details(handler, Item(380), session: Session());

        await details.LoadAsync();

        Assert.Equal("new_ApiUrl", details.EnvironmentVariable?.SchemaName);
        Assert.Equal("https://current", details.EnvironmentValueInput);
        Assert.Equal("The current value set in this environment is in effect.", details.EnvironmentVariableStatus);
        Assert.True(details.CanEditEnvironmentValue);
        Assert.True(details.SetEnvironmentValueCommand.CanExecute(null));
        Assert.True(details.ClearEnvironmentValueCommand.CanExecute(null));
        Assert.Equal(DetailsTab.Value, details.SelectedTab);
    }

    [Fact]
    public async Task A_value_record_leads_to_its_definition_and_more_than_one_value_is_flagged()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"environmentvariablevalues({Id})", $$"""{"_environmentvariabledefinitionid_value":"{{DefinitionId}}"}""")
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({DefinitionId})", Variable(100000000, "first", "second"))
            .Quiet();
        var details = Details(handler, Item(381));

        await details.LoadAsync();

        Assert.Equal("first", details.EnvironmentValueInput);
        Assert.Equal("This definition has 2 value records - there should be at most one. The first is shown.", details.EnvironmentVariableStatus);
        Assert.False(details.CanEditEnvironmentValue);
    }

    [Fact]
    public async Task A_variable_that_cannot_be_read_says_why()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, $"environmentvariabledefinitions({Id})", HttpStatusCode.Forbidden, "No read")
            .Quiet();
        var details = Details(handler, Item(380), session: Session());

        await details.LoadAsync();

        Assert.Null(details.EnvironmentVariable);
        Assert.StartsWith("Could not read the environment variable - ", details.EnvironmentVariableStatus);
        Assert.Contains("environment variable: ", details.Status);
        Assert.False(details.CanEditEnvironmentValue);
    }

    [Fact]
    public async Task A_secret_is_left_to_the_maker_portal()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Id})", Variable(100000005))
            .Quiet();
        var details = Details(handler, Item(380), session: Session());

        await details.LoadAsync();

        Assert.False(details.CanEditEnvironmentValue);
        Assert.False(details.SetEnvironmentValueCommand.CanExecute(null));
        Assert.Equal(string.Empty, details.EnvironmentValueInput);
        Assert.Equal("No current value is set in this environment, so the default is in effect.", details.EnvironmentVariableStatus);
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_is_refused_before_anything_is_asked()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Id})", Variable(100000001, "4"))
            .Quiet();
        var details = Details(handler, Item(380), session: Session());
        await details.LoadAsync();

        details.EnvironmentValueInput = "four";
        await details.SetEnvironmentValueCommand.ExecuteAsync(null);

        Assert.Equal("'four' is not a number. Use digits, with '.' for decimals.", details.EnvironmentValueError);
        Assert.DoesNotContain(handler.Requests, r => r.Method != HttpMethod.Get);

        details.EnvironmentValueInput = "5";

        Assert.Null(details.EnvironmentValueError);
    }

    [Fact]
    public async Task Without_a_tab_nothing_is_set()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Id})", Variable(100000001, "4"))
            .Quiet();
        var details = Details(handler, Item(380));
        await details.LoadAsync();

        details.EnvironmentValueInput = "four";
        await details.SetEnvironmentValueCommand.ExecuteAsync(null);

        Assert.Null(details.EnvironmentValueError);
        Assert.DoesNotContain(handler.Requests, r => r.Method != HttpMethod.Get);
    }

    // ---------------------------------------------------------------- plug-ins

    [Fact]
    public async Task A_plugin_steps_trace_log_puts_the_exception_first_and_says_what_is_traced()
    {
        var details = Details(PluginStep(1, Traces([null, "System.InvalidOperationException: boom"])), Item(92), session: Session());

        await details.LoadAsync();

        Assert.Equal(2, details.TraceEntries.Count);
        Assert.True(details.SelectedTrace?.HasException);
        Assert.Equal("Latest 2 entries, 1 with an exception. Tracing is set to exceptions only, so successful runs are not logged.", details.TraceStatus);
        Assert.Equal("2", details.TraceTabCount);
        Assert.False(details.IsTraceOff);
        Assert.Equal("Enabled", details.OtherSwitchStateLabel);
        Assert.Equal("Disable", details.SwitchButtonLabel);
    }

    [Fact]
    public async Task Tracing_switched_off_is_called_out()
    {
        var details = Details(PluginStep(0, Traces([null])), Item(92));

        await details.LoadAsync();

        Assert.True(details.IsTraceOff);
        Assert.StartsWith("Latest 1 entry, 0 with an exception. Plug-in tracing is off in this environment", details.TraceStatus);
    }

    [Fact]
    public async Task Tracing_everything_is_said_too()
    {
        var details = Details(PluginStep(2, Empty), Item(92));

        await details.LoadAsync();

        Assert.Equal("No trace log entries for this plug-in. Tracing is on for all executions.", details.TraceStatus);
        Assert.Null(details.SelectedTrace);
    }

    [Fact]
    public async Task Without_a_known_setting_only_the_entries_are_described()
    {
        var details = Details(PluginStep(null, Empty), Item(92));

        await details.LoadAsync();

        Assert.Equal("No trace log entries for this plug-in.", details.TraceStatus);
        Assert.Equal("0", details.TraceTabCount);
    }

    [Fact]
    public async Task A_trace_log_that_cannot_be_read_says_why()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "organizations?$select=plugintracelogsetting", Empty)
            .OnError(HttpMethod.Get, "plugintracelogs?", HttpStatusCode.Forbidden, "No trace access")
            .OnJson(HttpMethod.Get, "sdkmessageprocessingsteps?", Empty)
            .Quiet();
        var details = Details(handler, Item(92));

        await details.LoadAsync();

        Assert.StartsWith("Could not read the plug-in trace log - ", details.TraceStatus);
        Assert.Contains("trace log: ", details.Status);
    }

    [Fact]
    public async Task An_assembly_with_no_plugin_types_has_no_trace_log()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "organizations?$select=plugintracelogsetting", Empty)
            .OnJson(HttpMethod.Get, "plugintypes?", Empty)
            .Quiet();
        var details = Details(handler, Item(91));

        await details.LoadAsync();

        Assert.Empty(details.TraceEntries);
        Assert.Equal("No trace log entries for this plug-in.", details.TraceStatus);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("plugintracelogs?"));
    }

    // ---------------------------------------------------------------- classic workflows

    private static string Jobs(int count, int status) =>
        $$"""{"value":[{{string.Join(",", Enumerable.Range(0, count).Select(i =>
            $$$"""{"name":"job {{{i}}}","statuscode":{{{status}}},"startedon":"2026-10-01T08:00:00Z"}"""))}}]}""";

    private static FakeHttpHandler Workflow(string jobs) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "_parentworkflowid_value", """{"value":[{"workflowid":"ac000000-0000-0000-0000-000000000001"}]}""")
        .OnJson(HttpMethod.Get, "asyncoperations?", jobs)
        .OnJson(HttpMethod.Get, "$select=workflowid,statecode", $$"""{"value":[{"workflowid":"{{Id}}","statecode":0}]}""")
        .Quiet();

    [Fact]
    public async Task A_classic_workflows_runs_are_its_system_jobs()
    {
        var handler = Workflow(Jobs(1, 31));
        var details = Details(handler, Item(29, 0), EnvironmentId);

        await details.LoadAsync();

        Assert.Equal("Latest 1 run(s), 1 failed.", details.RunsStatus);
        Assert.Equal(RunOutcome.Failed, details.SelectedRun?.Outcome);
        Assert.Null(details.SelectedRun?.PortalUrl);
        Assert.Null(details.FlowUrl);
        Assert.Equal("Draft", details.OtherSwitchStateLabel);
        Assert.Equal("Activate", details.SwitchButtonLabel);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/runs?"));
    }

    [Fact]
    public async Task A_workflow_with_no_jobs_says_they_may_have_been_cleaned_up()
    {
        var details = Details(Workflow(Empty), Item(29, 0));

        await details.LoadAsync();

        Assert.Equal("No system jobs found for this workflow. Completed jobs may have been cleaned up.", details.RunsStatus);
        Assert.Equal("0", details.RunsTabCount);
    }

    [Fact]
    public async Task A_full_page_of_runs_is_counted_as_more()
    {
        var details = Details(Workflow(Jobs(100, 30)), Item(29, 0));

        await details.LoadAsync();

        Assert.Equal("100+", details.RunsTabCount);
        Assert.Equal("Latest 100 run(s), 0 failed.", details.RunsStatus);
    }

    [Fact]
    public async Task System_jobs_that_cannot_be_read_say_why()
    {
        var details = Details(new FakeHttpHandler()
            .OnError(HttpMethod.Get, "_parentworkflowid_value", HttpStatusCode.Forbidden, "No workflow access")
            .OnJson(HttpMethod.Get, "$select=workflowid,statecode", Empty)
            .Quiet(), Item(29, 0));

        await details.LoadAsync();

        Assert.StartsWith("Could not read the run history - ", details.RunsStatus);
        Assert.Contains("runs: ", details.Status);
    }

    // ---------------------------------------------------------------- connection references and overviews

    [Fact]
    public async Task A_connection_reference_with_no_connection_is_broken()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "connectionreferences?", $$"""{"value":[{"connectionreferenceid":"{{Id}}","connectionreferencelogicalname":"new_outlook"}]}""")
            .Quiet();
        var details = Details(handler, Item(10132, logicalName: "connectionreference"), EnvironmentId);

        await details.LoadAsync();

        Assert.Equal("No connection", Assert.Single(details.ConnectionRows).HealthLabel);
        Assert.True(details.HasBrokenConnection);
        Assert.Equal("1 of 1 connection reference(s) have no working connection.", details.ConnectionsStatus);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/connections?"));
    }

    private const string CustomApi = """
        {"uniquename":"new_Recalculate","isfunction":false,"bindingtype":1,"bindingtype@OData.Community.Display.V1.FormattedValue":"Entity",
         "boundentitylogicalname":"account","isprivate":false}
        """;

    [Fact]
    public async Task An_overview_lists_its_properties_and_each_part_as_a_table()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"customapis({Id})", CustomApi)
            .OnJson(HttpMethod.Get, "customapirequestparameters?", """
                {"value":[{"uniquename":"Mode","type":10,"type@OData.Community.Display.V1.FormattedValue":"String","isoptional":true}]}
                """)
            .OnJson(HttpMethod.Get, "customapiresponseproperties?", Empty)
            .Quiet();
        var details = Details(handler, Item(10101, logicalName: "customapi"));

        await details.LoadAsync();

        Assert.Contains(details.OverviewProperties, p => p is { Label: "Binding", Value: "Entity" });
        var parameters = details.OverviewSections.Single(s => s.Heading.StartsWith("Request parameters", StringComparison.Ordinal));
        Assert.Equal(["Name", "Type", "Column 3", "Table"], parameters.Rows.Table!.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
        var row = Assert.Single(parameters.Rows.Cast<System.Data.DataRowView>());
        Assert.Equal("Mode", row["Name"]);
        Assert.Equal(DBNull.Value, row["Table"]);
        Assert.Equal(string.Empty, details.OverviewStatus);
        Assert.Equal(DetailsTab.Overview, details.SelectedTab);
    }

    [Fact]
    public async Task An_overview_part_that_cannot_be_read_is_named()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"customapis({Id})", CustomApi)
            .OnJson(HttpMethod.Get, "customapirequestparameters?", Empty)
            .Quiet();
        var details = Details(handler, Item(10101, logicalName: "customapi"));

        await details.LoadAsync();

        Assert.StartsWith("Some parts could not be read - custom API: ", details.OverviewStatus);
        Assert.Contains("overview custom API: ", details.Status);
    }
}
