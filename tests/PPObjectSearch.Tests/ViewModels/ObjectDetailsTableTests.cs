using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ViewModels.DetailsHarness;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>A table in the details window: what it owns, each child's properties, and its row count.</summary>
public partial class ObjectDetailsTableTests
{
    private static readonly Guid NameColumn = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid NumberColumn = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid StatusColumn = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    private static readonly string Definition =
        $$"""{"MetadataId":"{{Id}}","LogicalName":"account","EntitySetName":"accounts","PrimaryIdAttribute":"accountid","ObjectTypeCode":1}""";

    private static readonly string Columns = $$$"""
        {"value":[
          {"MetadataId":"{{{NameColumn}}}","LogicalName":"name","DisplayName":{"UserLocalizedLabel":{"Label":"Account Name"}},
           "AttributeType":"String","RequiredLevel":{"Value":"ApplicationRequired"},"IsPrimaryName":true,
           "@odata.type":"#Microsoft.Dynamics.CRM.StringAttributeMetadata"},
          {"MetadataId":"{{{NumberColumn}}}","LogicalName":"accountnumber","DisplayName":{"UserLocalizedLabel":{"Label":"Number"}},"AttributeType":"String"},
          {"MetadataId":"{{{StatusColumn}}}","LogicalName":"statuscode","DisplayName":{"UserLocalizedLabel":{"Label":"Status Reason"}},"AttributeType":"Status"}
        ]}
        """;

    /// <summary>A table with three columns, a relationship, a form, a dashboard and a view - and the stored count of 42 rows.</summary>
    private static FakeHttpHandler Table(Action<FakeHttpHandler>? first = null)
    {
        var handler = new FakeHttpHandler();
        first?.Invoke(handler);

        return handler
            .OnJson(HttpMethod.Get, "/Attributes?", Columns)
            .OnJson(HttpMethod.Get, "/Attributes(", """{"LogicalName":"name","MaxLength":100}""")
            .OnJson(HttpMethod.Get, "/OneToManyRelationships?", """{"value":[{"MetadataId":"e0000000-0000-0000-0000-000000000001","SchemaName":"account_contacts","ReferencingEntity":"contact"}]}""")
            .OnJson(HttpMethod.Get, "/ManyToOneRelationships?", Empty)
            .OnJson(HttpMethod.Get, "/ManyToManyRelationships?", Empty)
            .OnJson(HttpMethod.Get, "/Keys?", Empty)
            .OnJson(HttpMethod.Get, "systemforms?", """
                {"value":[{"formid":"f0000000-0000-0000-0000-000000000001","name":"Main","type":2},
                          {"formid":"f0000000-0000-0000-0000-000000000002","name":"Sales dashboard","type":0}]}
                """)
            .OnJson(HttpMethod.Get, "savedqueries?", """{"value":[{"savedqueryid":"a0000000-0000-0000-0000-000000000001","name":"Active Accounts","isdefault":true}]}""")
            .OnJson(HttpMethod.Get, "savedqueryvisualizations?", Empty)
            .OnJson(HttpMethod.Get, "RetrieveTotalRecordCount(", """{"EntityRecordCountCollection":{"Keys":["account"],"Values":[42]}}""")
            .OnJson(HttpMethod.Get, "recordcountsnapshots?", Empty)
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Id})?", Definition)
            .Quiet();
    }

    private static string Page(int page, int count)
    {
        var json = new StringBuilder("""{"value":[""");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"accountid\":\"k").Append(page).Append('-').Append(i).Append("\"}");
        }

        return json.Append("]}").ToString();
    }

    /// <summary>The page number a FetchXML page probe asks for.</summary>
    [GeneratedRegex(@"page='(\d+)'")]
    private static partial Regex PageNumber();

    /// <summary>Answers page probes as a table of exactly fifty thousand rows would.</summary>
    private static void FiftyThousandRows(FakeHttpHandler handler) =>
        handler.On(HttpMethod.Get, "<fetch page=", request =>
        {
            var page = int.Parse(PageNumber().Match(request.Url).Groups[1].Value);
            return FakeHttpHandler.Json(Page(page, page <= 10 ? 5000 : 0));
        });

    private static ObjectDetailsViewModel Account(FakeHttpHandler handler) => Details(handler, Item(1, name: "account"));

    // ---------------------------------------------------------------- what it owns

    [Fact]
    public async Task A_table_lists_what_it_owns_one_group_per_kind()
    {
        var details = Account(Table());

        await details.LoadAsync();

        Assert.Equal(["Columns", "Relationships", "Keys", "Forms", "Views", "Charts", "Dashboards"], details.ChildGroups.Select(g => g.Label));
        Assert.Equal([3, 1, 0, 1, 1, 0, 1], details.ChildGroups.Select(g => g.Count));
        Assert.Equal(7, details.ComponentCount);
        Assert.Equal(TableChildKind.Column, details.SelectedChildGroup?.Kind);
        Assert.Equal(["Account Name", "Number", "Status Reason"], details.Children.Select(c => c.PrimaryLabel));
        Assert.Equal("0 solution(s), 0 dependent, 0 required, 7 child component(s).", details.Status);
    }

    [Fact]
    public async Task A_part_that_cannot_be_read_is_named_and_the_others_still_list()
    {
        var details = Account(Table(h => h.OnError(HttpMethod.Get, "savedqueryvisualizations?", HttpStatusCode.Forbidden, "Charts blocked")));

        await details.LoadAsync();

        Assert.True(details.ChildGroups.Single(g => g.Kind == TableChildKind.Chart).IsEmpty);
        Assert.Equal(3, details.ChildGroups.Single(g => g.Kind == TableChildKind.Column).Count);
        Assert.StartsWith("Some details could not be read - charts: ", details.Status);
        Assert.Contains("Charts blocked", details.Status);
    }

    [Fact]
    public async Task A_table_that_cannot_be_matched_says_so()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "EntityDefinitions(", "{}").Quiet();
        var details = Account(handler);

        await details.LoadAsync();

        Assert.Empty(details.ChildGroups);
        Assert.Equal("Some details could not be read - table metadata: this component could not be matched to a table.", details.Status);
    }

    [Fact]
    public async Task A_table_whose_metadata_cannot_be_read_says_why()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "EntityDefinitions(", HttpStatusCode.Forbidden, "No metadata access").Quiet();
        var details = Details(handler, Item(1, name: " "));

        await details.LoadAsync();

        Assert.Empty(details.ChildGroups);
        Assert.StartsWith("Some details could not be read - table metadata: ", details.Status);
        Assert.Contains("No metadata access", details.Status);
    }

    [Fact]
    public async Task The_filter_narrows_the_group_by_every_word_and_keeps_a_selection_still_in_it()
    {
        var details = Account(Table());
        await details.LoadAsync();
        details.SelectedChild = details.Children[0];

        details.ChildFilter = "string name";

        Assert.Equal(["Account Name"], details.Children.Select(c => c.PrimaryLabel));
        Assert.Same(details.Children[0], details.SelectedChild);

        details.ChildFilter = "status";

        Assert.Equal("Status Reason", Assert.Single(details.Children).PrimaryLabel);
        Assert.Same(details.Children[0], details.SelectedChild);

        details.ChildFilter = "string";

        Assert.Equal(2, details.Children.Count);
        Assert.Null(details.SelectedChild);
    }

    [Fact]
    public async Task Another_group_shows_its_own_children()
    {
        var details = Account(Table());
        await details.LoadAsync();

        details.SelectedChildGroup = details.ChildGroups.Single(g => g.Kind == TableChildKind.Dashboard);

        Assert.Equal("Sales dashboard", Assert.Single(details.Children).PrimaryLabel);
        Assert.Same(details.Children[0], details.SelectedChild);
    }

    // ---------------------------------------------------------------- a child's properties

    [Fact]
    public async Task Selecting_a_child_reads_its_properties_once()
    {
        var handler = Table();
        var details = Account(handler);
        await details.LoadAsync();
        var name = details.Children[0];

        details.SelectedChild = name;
        await Until(() => !details.IsLoadingProperties && details.ChildProperties.Count > 0);

        Assert.Contains(details.ChildProperties, p => p is { Name: "LogicalName", Value: "name" });
        Assert.Equal(string.Empty, details.ChildStatus);

        details.SelectedChild = null;

        Assert.Empty(details.ChildProperties);
        Assert.Equal("Select a component to see its properties.", details.ChildStatus);

        details.SelectedChild = name;

        Assert.NotEmpty(details.ChildProperties);
        Assert.Single(handler.Requests, r => r.Url.Contains("/Attributes("));
    }

    [Fact]
    public async Task A_child_whose_properties_cannot_be_read_says_why()
    {
        var details = Account(Table(h => h.OnError(HttpMethod.Get, "/Attributes(", HttpStatusCode.InternalServerError, "Metadata unavailable")));
        await details.LoadAsync();

        details.SelectedChild = details.Children[0];
        await Until(() => !details.IsLoadingProperties);

        Assert.StartsWith("Properties could not be read - ", details.ChildStatus);
        Assert.Contains("Metadata unavailable", details.ChildStatus);
        Assert.Empty(details.ChildProperties);
    }

    [Fact]
    public async Task A_child_with_no_properties_says_so()
    {
        var details = Account(Table(h => h.OnJson(HttpMethod.Get, "/Attributes(", "{}")));
        await details.LoadAsync();

        details.SelectedChild = details.Children[0];
        await Until(() => !details.IsLoadingProperties && details.ChildStatus != "Loading properties...");

        Assert.Equal("Dataverse returned no properties for this component.", details.ChildStatus);
    }

    [Fact]
    public async Task Properties_that_arrive_after_another_child_is_picked_are_dropped()
    {
        var slowName = new TaskCompletionSource<HttpResponseMessage>();
        var slowNumber = new TaskCompletionSource<HttpResponseMessage>();
        var details = Account(Table(h => h
            .OnAsync(HttpMethod.Get, $"/Attributes({NameColumn})", _ => slowName.Task)
            .OnAsync(HttpMethod.Get, $"/Attributes({NumberColumn})", _ => slowNumber.Task)
            .OnJson(HttpMethod.Get, $"/Attributes({StatusColumn})", """{"LogicalName":"statuscode"}""")));
        await details.LoadAsync();

        details.SelectedChild = details.Children[0];
        details.SelectedChild = details.Children[1];
        details.SelectedChild = details.Children[2];
        await Until(() => !details.IsLoadingProperties && details.ChildProperties.Count > 0);

        slowName.SetResult(FakeHttpHandler.Json("""{"LogicalName":"name"}"""));
        slowNumber.SetResult(FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("late failure"), HttpStatusCode.BadRequest));
        await Until(() => details.Children[0].Properties is not null);

        Assert.Contains(details.ChildProperties, p => p.Value == "statuscode");
        Assert.DoesNotContain(details.ChildProperties, p => p.Value == "name");
        Assert.Equal(string.Empty, details.ChildStatus);
        Assert.False(details.IsLoadingProperties);
    }

    // ---------------------------------------------------------------- row count

    [Fact]
    public async Task The_stored_row_count_shows_until_an_exact_one_is_asked_for()
    {
        var details = Account(Table());

        await details.LoadAsync();

        Assert.Equal("~42 rows", details.RowCountText);
        Assert.Equal("~42", details.RowCountValue);
        Assert.Equal("rows", details.RowCountUnit);
        Assert.Equal("Dataverse's stored count, refreshed about daily. Count rows gives an exact, current number.", details.RowCountDetail);
        Assert.Equal("Count rows", details.CountButtonLabel);
    }

    [Fact]
    public async Task The_stored_count_says_when_it_was_last_refreshed()
    {
        var details = Account(Table(h => h.OnJson(HttpMethod.Get, "recordcountsnapshots?", """{"value":[{"lastupdated":"2026-09-29T02:00:00Z"}]}""")));

        await details.LoadAsync();

        Assert.StartsWith("Dataverse's stored count, last refreshed 2026-09-", details.RowCountDetail);
        Assert.EndsWith(". Count rows gives an exact, current number.", details.RowCountDetail);
    }

    [Fact]
    public async Task Counting_gives_the_exact_number_beside_the_stored_one()
    {
        var details = Account(Table(h => h.OnJson(HttpMethod.Get, "aggregate='true'", """{"value":[{"rowcount":1}]}""")));
        await details.LoadAsync();

        await StartInOrder(() => details.CountRowsCommand.ExecuteAsync(null));

        Assert.Equal("1 row", details.RowCountText);
        Assert.Equal("1", details.RowCountValue);
        Assert.Equal("row", details.RowCountUnit);
        Assert.StartsWith("Exact count by aggregate count, 1 request(s) in ", details.RowCountDetail);
        Assert.EndsWith(" Dataverse's stored count, refreshed about daily. It said 42.", details.RowCountDetail);
        Assert.Null(details.RowCountWarning);
        Assert.False(details.IsCounting);
    }

    [Fact]
    public async Task Counting_without_a_stored_count_says_only_how_it_counted()
    {
        var details = Account(Table(h => h
            .OnJson(HttpMethod.Get, "RetrieveTotalRecordCount(", """{"EntityRecordCountCollection":{"Keys":[],"Values":[]}}""")
            .OnJson(HttpMethod.Get, "aggregate='true'", """{"value":[{"rowcount":7}]}""")));
        await details.LoadAsync();

        Assert.Equal(string.Empty, details.RowCountText);
        Assert.Equal(string.Empty, details.RowCountUnit);

        await StartInOrder(() => details.CountRowsCommand.ExecuteAsync(null));

        Assert.Equal("7 rows", details.RowCountText);
        Assert.EndsWith(" s.", details.RowCountDetail);
    }

    [Fact]
    public async Task A_count_capped_at_fifty_thousand_beside_a_bigger_stored_count_warns_of_the_cap()
    {
        var details = Account(Table(h =>
        {
            h.OnJson(HttpMethod.Get, "RetrieveTotalRecordCount(", """{"EntityRecordCountCollection":{"Keys":["account"],"Values":[60000]}}""");
            FiftyThousandRows(h);
        }));
        await details.LoadAsync();

        await StartInOrder(() => details.CountRowsCommand.ExecuteAsync(null));

        Assert.Equal($"{50_000:N0} rows", details.RowCountText);
        Assert.StartsWith("Exact count by page search, ", details.RowCountDetail);
        Assert.StartsWith($"Counted exactly {50_000:N0}, but Dataverse's stored count says {60_000:N0}.", details.RowCountWarning);
    }

    [Fact]
    public async Task A_count_that_fails_falls_back_to_the_stored_one_and_says_why()
    {
        var details = Account(Table(h => h.OnError(HttpMethod.Get, "aggregate='true'", HttpStatusCode.ServiceUnavailable, "Try later")));
        await details.LoadAsync();

        await StartInOrder(() => details.CountRowsCommand.ExecuteAsync(null));

        Assert.Equal("~42 rows", details.RowCountText);
        Assert.Equal("Dataverse's stored count, refreshed about daily.", details.RowCountDetail);
        Assert.StartsWith("The exact count failed: ", details.RowCountWarning);
        Assert.Contains("Try later", details.RowCountWarning);
    }

    [Fact]
    public async Task A_count_that_fails_without_a_stored_one_says_so()
    {
        var details = Account(new FakeHttpHandler()
            .OnError(HttpMethod.Get, "aggregate='true'", HttpStatusCode.ServiceUnavailable, "Try later")
            .OnJson(HttpMethod.Get, "EntityDefinitions(", Definition));

        await StartInOrder(() => details.CountRowsCommand.ExecuteAsync(null));

        Assert.Equal("Count failed", details.RowCountText);
        Assert.Null(details.RowCountDetail);
    }

    [Fact]
    public async Task A_count_still_running_when_the_window_closes_is_stopped()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var details = Account(Table(h => h.OnAsync(HttpMethod.Get, "aggregate='true'", _ => slow.Task)));
        await details.LoadAsync();

        var count = StartInOrder(() => details.CountRowsCommand.ExecuteAsync(null));

        Assert.True(details.IsCounting);
        Assert.Equal("Stop counting", details.CountButtonLabel);
        Assert.Equal("Counting account...", details.RowCountText);
        Assert.Equal("Counting account...", details.RowCountValue);
        Assert.Equal(string.Empty, details.RowCountUnit);

        details.Detach();
        slow.SetCanceled();
        await count;

        Assert.False(details.IsCounting);
        Assert.Equal("~42 rows", details.RowCountText);
        Assert.Equal("Dataverse's stored count, refreshed about daily. The exact count was stopped.", details.RowCountDetail);
    }

    [Fact]
    public async Task A_count_still_running_when_the_window_is_disposed_is_stopped()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var details = Account(Table(h => h.OnAsync(HttpMethod.Get, "aggregate='true'", _ => slow.Task)));
        await details.LoadAsync();
        var count = StartInOrder(() => details.CountRowsCommand.ExecuteAsync(null));

        details.Dispose();
        slow.SetResult(FakeHttpHandler.Json("""{"value":[{"rowcount":7}]}"""));
        await count;

        Assert.False(details.IsCounting);
        Assert.Equal("~42 rows", details.RowCountText);
        Assert.Equal("Dataverse's stored count, refreshed about daily. The exact count was stopped.", details.RowCountDetail);
    }

    [Fact]
    public async Task A_count_stopped_without_a_stored_one_says_it_stopped()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var details = Account(new FakeHttpHandler()
            .OnAsync(HttpMethod.Get, "aggregate='true'", _ => slow.Task)
            .OnJson(HttpMethod.Get, "EntityDefinitions(", Definition));

        var count = StartInOrder(() => details.CountRowsCommand.ExecuteAsync(null));
        await Until(() => details.RowCountText.StartsWith("Counting account", StringComparison.Ordinal));
        details.Detach();
        slow.SetCanceled();
        await count;

        Assert.Equal("Count stopped", details.RowCountText);
        Assert.Null(details.RowCountDetail);
    }
}
