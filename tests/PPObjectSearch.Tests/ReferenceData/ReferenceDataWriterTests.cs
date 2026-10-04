using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

public class ReferenceDataWriterTests
{
    private static readonly EntityColumn Code = Col("new_code");
    private static readonly EntityColumn IdColumn = Col(PrimaryId, "UniqueidentifierType", primaryId: true);
    private static readonly EntityColumn Name = Col("new_name", primaryName: true);
    private static readonly EntityColumn Amount = Col("new_amount", "MoneyType");
    private static readonly EntityColumn Currency = Lookup("transactioncurrencyid");
    private static readonly EntityColumn Count = Col("new_count", "IntegerType");
    private static readonly EntityColumn Flag = Col("new_flag", "BooleanType");
    private static readonly EntityColumn Calculated = Col("new_calc", "DecimalType", validForCreate: false, validForUpdate: false);
    private static readonly EntityColumn Parent = Lookup("new_parentid");

    private static readonly EntitySummary ParentTable =
        new("new_parent", "Parent", "new_parents", "new_parentid", "new_name", false, false);

    private static Dictionary<string, EntitySummary> TargetEntities(EntitySummary? thing = null) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Table] = thing ?? Entity(),
            ["new_parent"] = ParentTable
        };

    private static ReferenceDataWriter Writer(FakeHttpHandler handler, Dictionary<string, EntitySummary>? entities = null) =>
        new(Fakes.Dataverse(handler), entities ?? TargetEntities());

    private static JsonElement BodyOf(RecordedRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    private static string[] PropertyNames(JsonElement body) =>
        body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string ParentsResponse(params Guid[] ids) =>
        JsonSerializer.Serialize(new { value = ids.Select(id => new Dictionary<string, object> { ["new_parentid"] = id.ToString() }) });

    /// <summary>A real comparison of one keyed row, so the plan item carries genuine differences.</summary>
    private static ReconcilePlanItem Compared(
        IEnumerable<EntityColumn> values,
        DataRecord? source,
        DataRecord? target,
        ReconcileOptions? options = null)
    {
        var plan = Plan(new[] { Code }, values);
        var result = ReferenceDataComparer.Compare(
            plan,
            source is null ? Array.Empty<DataRecord>() : new[] { source },
            target is null ? Array.Empty<DataRecord>() : new[] { target });

        return Assert.Single(ReferenceDataWriter.Plan(result.Rows, options ?? new ReconcileOptions(true, true, true)));
    }

    [Fact]
    public async Task Apply_refuses_a_row_the_comparison_cannot_vouch_for()
    {
        var handler = new FakeHttpHandler();
        var plan = Plan(new[] { Code }, new[] { Name });
        var result = ReferenceDataComparer.Compare(
            plan, Array.Empty<DataRecord>(), new[] { Row(G(2)).With("new_code", "A").Build() }, sourceTruncated: true);
        var item = Assert.Single(ReferenceDataWriter.Plan(result.Rows, new ReconcileOptions(true, true, true)));

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.False(outcome.Succeeded);
        Assert.Contains("row cap", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    // ---- ReconcileOptions / Plan --------------------------------------------------------------

    [Theory]
    [InlineData(true, false, false, ReconcileAction.Create, true)]
    [InlineData(false, true, true, ReconcileAction.Create, false)]
    [InlineData(false, true, false, ReconcileAction.Update, true)]
    [InlineData(true, false, true, ReconcileAction.Update, false)]
    [InlineData(false, false, true, ReconcileAction.Delete, true)]
    [InlineData(true, true, false, ReconcileAction.Delete, false)]
    public void Options_allow_only_the_actions_switched_on(bool create, bool update, bool delete, ReconcileAction action, bool expected)
    {
        Assert.Equal(expected, new ReconcileOptions(create, update, delete).Allows(action));
    }

    [Theory]
    [InlineData(RecordCompareStatus.OnlyInSource, ReconcileAction.Create)]
    [InlineData(RecordCompareStatus.Different, ReconcileAction.Update)]
    [InlineData(RecordCompareStatus.OnlyInTarget, ReconcileAction.Delete)]
    public void Plan_maps_each_status_to_its_action(RecordCompareStatus status, ReconcileAction expected)
    {
        var row = Comparison(Plan(new[] { Code }, new[] { Name }), status);

        var item = Assert.Single(ReferenceDataWriter.Plan(new[] { row }, new ReconcileOptions(true, true, true)));

        Assert.Equal(expected, item.Action);
        Assert.Same(row, item.Row);
    }

    [Fact]
    public void Plan_leaves_out_matching_rows()
    {
        var row = Comparison(Plan(new[] { Code }, new[] { Name }), RecordCompareStatus.Same);

        Assert.Empty(ReferenceDataWriter.Plan(new[] { row }, new ReconcileOptions(true, true, true)));
    }

    [Fact]
    public void Plan_leaves_out_actions_that_are_switched_off()
    {
        var plan = Plan(new[] { Code }, new[] { Name });
        var rows = new[]
        {
            Comparison(plan, RecordCompareStatus.OnlyInSource, key: "create"),
            Comparison(plan, RecordCompareStatus.Different, key: "update"),
            Comparison(plan, RecordCompareStatus.OnlyInTarget, key: "delete"),
            Comparison(plan, RecordCompareStatus.Same, key: "same")
        };

        var defaults = ReferenceDataWriter.Plan(rows, new ReconcileOptions(true, true, false));
        var deleteOnly = ReferenceDataWriter.Plan(rows, new ReconcileOptions(false, false, true));
        var nothing = ReferenceDataWriter.Plan(rows, new ReconcileOptions(false, false, false));

        Assert.Equal(new[] { "create", "update" }, defaults.Select(i => i.Key).ToArray());
        Assert.Equal(new[] { "delete" }, deleteOnly.Select(i => i.Key).ToArray());
        Assert.Empty(nothing);
    }

    [Fact]
    public void Plan_items_describe_what_they_will_touch()
    {
        var plan = Plan(new[] { Code }, new[] { Name, Amount, Flag });
        var source = Row(G(1)).Named("Alpha").Build();
        var target = Row(G(2)).Named("Beta").Build();
        var differences = new[]
        {
            new ColumnComparison { Column = Name, IsDifferent = true },
            new ColumnComparison { Column = Flag, IsDifferent = true }
        };

        var create = new ReconcilePlanItem { Row = Comparison(plan, RecordCompareStatus.OnlyInSource, source, key: "A"), Action = ReconcileAction.Create };
        var update = new ReconcilePlanItem { Row = Comparison(plan, RecordCompareStatus.Different, source, target, differences: differences), Action = ReconcileAction.Update };
        var delete = new ReconcilePlanItem { Row = Comparison(plan, RecordCompareStatus.OnlyInTarget, target: target), Action = ReconcileAction.Delete };

        Assert.Equal("Create in target", create.ActionLabel);
        Assert.Equal($"3 column(s), id {G(1)}", create.Detail);
        Assert.Equal("new_thing", create.Table);
        Assert.Equal("A", create.Key);
        Assert.Equal("Alpha", create.Name);

        Assert.Equal("Update in target", update.ActionLabel);
        Assert.Equal("new_name, new_flag", update.Detail);

        Assert.Equal("Delete from target", delete.ActionLabel);
        Assert.Equal($"id {G(2)}", delete.Detail);
        Assert.Equal("Beta", delete.Name);
    }

    // ---- ToWriteValue -------------------------------------------------------------------------

    [Theory]
    [InlineData("StringType")]
    [InlineData("IntegerType")]
    [InlineData("BooleanType")]
    [InlineData("MoneyType")]
    public void ToWriteValue_sends_empty_values_as_null(string type)
    {
        Assert.Null(ReferenceDataWriter.ToWriteValue(null, Col("c", type)));
        Assert.Null(ReferenceDataWriter.ToWriteValue("", Col("c", type)));
        Assert.Null(ReferenceDataWriter.ToWriteValue("  ", Col("c", type)));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("yes", false)]
    public void ToWriteValue_sends_booleans_as_json_booleans(string raw, bool expected)
    {
        Assert.Equal(expected, ReferenceDataWriter.ToWriteValue(raw, Col("c", "BooleanType")));
    }

    [Theory]
    [InlineData("IntegerType", "42", 42L)]
    [InlineData("IntegerType", "-7", -7L)]
    [InlineData("BigIntType", "9007199254740993", 9007199254740993L)]
    [InlineData("PicklistType", "100000001", 100000001L)]
    [InlineData("StateType", "1", 1L)]
    [InlineData("StatusType", " 2 ", 2L)]
    public void ToWriteValue_sends_whole_numbers_and_choices_as_longs(string type, string raw, long expected)
    {
        var value = ReferenceDataWriter.ToWriteValue(raw, Col("c", type));

        Assert.IsType<long>(value);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("DecimalType", "1.50", "1.50")]
    [InlineData("MoneyType", "12.3400", "12.3400")]
    [InlineData("DoubleType", "1E-05", "0.00001")]
    [InlineData("DecimalType", "-3", "-3")]
    public void ToWriteValue_sends_decimals_as_numbers(string type, string raw, string expected)
    {
        var value = ReferenceDataWriter.ToWriteValue(raw, Col("c", type));

        Assert.IsType<decimal>(value);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), (decimal)value!);
    }

    [Fact]
    public void ToWriteValue_sends_an_unparseable_number_as_text()
    {
        Assert.Equal("n/a", ReferenceDataWriter.ToWriteValue("n/a", Col("c", "IntegerType")));
        Assert.Equal("n/a", ReferenceDataWriter.ToWriteValue("n/a", Col("c", "DecimalType")));
    }

    [Theory]
    [InlineData("StringType", "  Alpha ", "Alpha")]
    [InlineData("MemoType", "notes", "notes")]
    [InlineData("DateTimeType", "2024-03-01T10:00:00Z", "2024-03-01T10:00:00Z")]
    [InlineData("UniqueidentifierType", "6f9619ff-8b86-d011-b42d-00c04fc964ff", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("MultiSelectPicklistType", "100000000,100000002", "100000000,100000002")]
    [InlineData("StringType", "42", "42")]
    public void ToWriteValue_sends_other_types_as_trimmed_text(string type, string raw, string expected)
    {
        var value = ReferenceDataWriter.ToWriteValue(raw, Col("c", type));

        Assert.IsType<string>(value);
        Assert.Equal(expected, value);
    }

    // ---- ApplyAsync: delete -------------------------------------------------------------------

    [Fact]
    public async Task Apply_delete_sends_a_delete_for_the_target_row()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Delete, "new_things(", HttpStatusCode.NoContent);
        var item = Compared(new[] { Name }, null, Row(G(2)).With("new_code", "A").Build());

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("Deleted.", outcome.Message);
        Assert.Same(item, outcome.Item);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"{Fakes.ApiRoot}new_things({G(2)})", request.Url);
        Assert.Equal("Bearer token:" + Fakes.EnvironmentUrl, request.Authorization);
    }

    [Fact]
    public async Task Apply_delete_without_a_target_row_fails_without_writing()
    {
        var handler = new FakeHttpHandler();
        var item = new ReconcilePlanItem
        {
            Row = Comparison(Plan(new[] { Code }, new[] { Name }), RecordCompareStatus.OnlyInTarget),
            Action = ReconcileAction.Delete
        };

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.False(outcome.Succeeded);
        Assert.Equal("no target row to delete.", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_reports_a_server_error_as_a_failed_row()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Delete, "new_things(", HttpStatusCode.Forbidden, "Principal lacks prvDeletenew_thing.");
        var item = Compared(new[] { Name }, null, Row(G(2)).With("new_code", "A").Build());

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.False(outcome.Succeeded);
        Assert.Contains("Principal lacks prvDeletenew_thing.", outcome.Message);
        Assert.Contains($"delete new_things({G(2)})", outcome.Message);
    }

    [Fact]
    public async Task Apply_writes_to_the_target_environments_entity_set()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Delete, "new_thingset_t(", HttpStatusCode.NoContent);
        var entities = TargetEntities(Entity(entitySet: "new_thingset_t"));
        var item = Compared(new[] { Name }, null, Row(G(2)).With("new_code", "A").Build());

        var outcome = await Writer(handler, entities).ApplyAsync(item);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal($"{Fakes.ApiRoot}new_thingset_t({G(2)})", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Apply_falls_back_to_the_source_entity_set_when_the_target_table_is_unknown()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Delete, "new_things(", HttpStatusCode.NoContent);
        var entities = new Dictionary<string, EntitySummary>(StringComparer.OrdinalIgnoreCase);
        var item = Compared(new[] { Name }, null, Row(G(2)).With("new_code", "A").Build());

        var outcome = await Writer(handler, entities).ApplyAsync(item);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal($"{Fakes.ApiRoot}new_things({G(2)})", Assert.Single(handler.Requests).Url);
    }

    // ---- ApplyAsync: create -------------------------------------------------------------------

    [Fact]
    public async Task Apply_create_posts_the_compared_columns_with_the_source_id()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);
        var source = Row(G(1))
            .With(PrimaryId, G(1).ToString())
            .With("new_code", "A")
            .With("new_name", " Alpha ")
            .With("new_amount", "12.50")
            .With("new_count", "3")
            .With("new_flag", "1")
            .With("new_secret", "not compared")
            .Build();

        var item = Compared(new[] { IdColumn, Code, Name, Amount, Currency, Count, Flag }, source, null);
        Assert.Equal(ReconcileAction.Create, item.Action);

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal($"Created with id {G(1)}.", outcome.Message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Fakes.ApiRoot + "new_things", request.Url);

        var body = BodyOf(request);
        Assert.Equal(
            new[] { "new_amount", "new_code", "new_count", "new_flag", "new_name", PrimaryId },
            PropertyNames(body));
        Assert.Equal(G(1).ToString(), body.GetProperty(PrimaryId).GetString());
        Assert.Equal("A", body.GetProperty("new_code").GetString());
        Assert.Equal("Alpha", body.GetProperty("new_name").GetString());
        Assert.Equal(JsonValueKind.Number, body.GetProperty("new_amount").ValueKind);
        Assert.Equal(12.5m, body.GetProperty("new_amount").GetDecimal());
        Assert.Equal(3, body.GetProperty("new_count").GetInt64());
        Assert.True(body.GetProperty("new_flag").GetBoolean());
    }

    [Fact]
    public async Task Apply_create_sets_the_source_id_even_when_the_primary_id_was_not_compared()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);
        var item = Compared(new[] { Name }, Row(G(7)).With("new_code", "A").With("new_name", "x").Build(), null);

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.True(outcome.Succeeded, outcome.Message);
        var body = BodyOf(Assert.Single(handler.Requests));
        Assert.Equal(new[] { "new_name", PrimaryId }, PropertyNames(body));
        Assert.Equal(G(7).ToString(), body.GetProperty(PrimaryId).GetString());
    }

    [Fact]
    public async Task Apply_create_sends_empty_values_as_null()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);
        var item = Compared(new[] { Name, Amount, Currency }, Row(G(1)).With("new_code", "A").With("new_name", null).Build(), null);

        await Writer(handler).ApplyAsync(item);

        var body = BodyOf(Assert.Single(handler.Requests));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("new_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("new_amount").ValueKind);
    }

    [Fact]
    public async Task Apply_create_skips_read_only_columns_and_says_so()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);
        var updateOnly = Col("new_updateonly", validForCreate: false, validForUpdate: true);
        var source = Row(G(1)).With("new_code", "A").With("new_name", "x").With("new_calc", "9.5").With("new_updateonly", "u").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Name, Calculated, updateOnly }, source, null));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal($"Created with id {G(1)}. Left out as read-only: new_calc, new_updateonly.", outcome.Message);

        var body = BodyOf(Assert.Single(handler.Requests));
        Assert.Equal(new[] { "new_name", PrimaryId }, PropertyNames(body));
    }

    [Fact]
    public async Task Apply_create_binds_a_lookup_after_resolving_it_by_name()
    {
        var parentId = G(500);
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(parentId))
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);

        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Alpha Parent").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(2, handler.Requests.Count);

        var lookup = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, lookup.Method);
        Assert.Equal(
            Fakes.ApiRoot + "new_parents?$select=new_parentid&$top=3&$filter=new_name eq 'Alpha Parent'",
            lookup.Url);

        var body = BodyOf(handler.Requests[1]);
        Assert.Equal(new[] { "new_ParentId@odata.bind", PrimaryId }, PropertyNames(body));
        Assert.Equal($"/new_parents({parentId})", body.GetProperty("new_ParentId@odata.bind").GetString());
    }

    [Fact]
    public async Task Apply_create_leaves_an_empty_lookup_out()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", null, null).Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent, Name }, source, null));

        Assert.True(outcome.Succeeded, outcome.Message);
        var body = BodyOf(Assert.Single(handler.Requests));
        Assert.Equal(new[] { "new_name", PrimaryId }, PropertyNames(body));
    }

    [Fact]
    public async Task Apply_escapes_quotes_in_a_lookup_name()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(G(500)))
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);

        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "O'Brien & Sons").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.EndsWith("$filter=new_name eq 'O''Brien & Sons'", handler.Requests[0].Url);
        Assert.Contains("%26", handler.Requests[0].Uri.OriginalString);
    }

    [Fact]
    public async Task Apply_cannot_end_a_lookup_literal_with_an_encoded_quote()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(G(500)))
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);

        // Decoded once by the server, "%27" would be a quote that closes the literal and lets the
        // rest of the label become filter syntax.
        var label = "x%27 or startswith(new_name,%27A";
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), label).Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.True(outcome.Succeeded, outcome.Message);
        var sent = handler.Requests[0].Uri.OriginalString;
        Assert.Contains("x%2527", sent);
        Assert.DoesNotContain("x%27", sent);
        Assert.EndsWith($"$filter=new_name eq '{label}'", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Apply_keeps_spaces_and_percent_signs_in_a_lookup_name()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(G(500)))
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);

        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), " 50% off ").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.EndsWith("$filter=new_name eq ' 50% off '", handler.Requests[0].Url);
        Assert.Contains("50%25", handler.Requests[0].Uri.OriginalString);
    }

    [Fact]
    public async Task Apply_abandons_a_row_whose_lookup_matches_nothing()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse())
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);

        var source = Row(G(1)).With("new_code", "A").With("new_name", "x").WithLookup("new_parentid", G(10), "Ghost").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Name, Parent }, source, null));

        Assert.False(outcome.Succeeded);
        Assert.Equal(
            "Lookup 'new_parentid' points at 'Ghost', which does not exist in new_parent in the target environment.",
            outcome.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Apply_abandons_a_row_whose_lookup_is_ambiguous()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(G(501), G(502)))
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);

        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Twin").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.False(outcome.Succeeded);
        Assert.Equal("'Twin' matches 2 rows of new_parent, so the reference is ambiguous.", outcome.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Apply_resolves_each_lookup_name_once_per_writer()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(G(500)))
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);
        var writer = Writer(handler);

        var first = await writer.ApplyAsync(Compared(new[] { Parent },
            Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Shared").Build(), null));
        var second = await writer.ApplyAsync(Compared(new[] { Parent },
            Row(G(2)).With("new_code", "B").WithLookup("new_parentid", G(10), "Shared").Build(), null));

        Assert.True(first.Succeeded, first.Message);
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Get));
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Apply_does_not_remember_an_ambiguous_lookup()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(G(501), G(502)));
        var writer = Writer(handler);

        var first = await writer.ApplyAsync(Compared(new[] { Parent },
            Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Twin").Build(), null));
        var second = await writer.ApplyAsync(Compared(new[] { Parent },
            Row(G(2)).With("new_code", "B").WithLookup("new_parentid", G(10), "Twin").Build(), null));

        Assert.False(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains("ambiguous", second.Message);

        // Each attempt asks by name and then, finding several, for the one active row.
        Assert.Equal(4, handler.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task Apply_prefers_the_one_active_row_when_a_name_is_shared()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "statecode eq 0", ParentsResponse(G(502)))
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(G(501), G(502)))
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent },
            Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Twin").Build(), null));

        Assert.True(outcome.Succeeded, outcome.Message);
        var body = BodyOf(handler.Requests.Last());
        Assert.Equal($"/new_parents({G(502)})", body.GetProperty("new_ParentId@odata.bind").GetString());
    }

    [Fact]
    public async Task Apply_does_not_remember_a_lookup_that_matched_nothing()
    {
        var found = false;
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "new_parents?", _ => FakeHttpHandler.Json(found ? ParentsResponse(G(500)) : ParentsResponse()))
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);
        var writer = Writer(handler);

        var first = await writer.ApplyAsync(Compared(new[] { Parent },
            Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Later").Build(), null));

        found = true;
        var second = await writer.ApplyAsync(Compared(new[] { Parent },
            Row(G(2)).With("new_code", "B").WithLookup("new_parentid", G(10), "Later").Build(), null));

        Assert.False(first.Succeeded);
        Assert.True(second.Succeeded, second.Message);
    }

    [Fact]
    public async Task A_row_created_in_the_run_resolves_lookups_written_after_it()
    {
        // A self-referencing table: the parent is created first, then a child names it.
        var self = Lookup("new_parentthingid");
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);
        var writer = Writer(handler);

        var parent = Row(G(1)).With("new_code", "P").Named("Parent row").Build();
        var child = Row(G(2)).With("new_code", "C").Named("Child row")
            .WithLookup("new_parentthingid", G(1), "Parent row", targetTable: Table, navigation: "new_ParentThingId").Build();

        Assert.True((await writer.ApplyAsync(Compared(new[] { self }, parent, null))).Succeeded);
        var outcome = await writer.ApplyAsync(Compared(new[] { self }, child, null));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Get);
        Assert.Equal($"/new_things({G(1)})", BodyOf(handler.Requests[1]).GetProperty("new_ParentThingId@odata.bind").GetString());
    }

    [Fact]
    public async Task Lookups_compared_by_id_are_bound_by_id_once_the_row_is_found()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"new_parents({G(10)})", "{\"new_parentid\":\"" + G(10) + "\"}")
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent);

        var plan = Plan(new[] { Code }, new[] { Parent }, matchLookupsByName: false);
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Twin").Build();
        var item = Assert.Single(ReferenceDataWriter.Plan(
            ReferenceDataComparer.Compare(plan, new[] { source }, Array.Empty<DataRecord>()).Rows,
            new ReconcileOptions(true, true, true)));

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("$filter"));
        Assert.Equal($"/new_parents({G(10)})", BodyOf(handler.Requests.Last()).GetProperty("new_ParentId@odata.bind").GetString());
    }

    [Fact]
    public async Task A_lookup_compared_by_id_fails_when_that_row_is_not_in_the_target()
    {
        var handler = new FakeHttpHandler();
        var plan = Plan(new[] { Code }, new[] { Parent }, matchLookupsByName: false);
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Twin").Build();
        var item = Assert.Single(ReferenceDataWriter.Plan(
            ReferenceDataComparer.Compare(plan, new[] { source }, Array.Empty<DataRecord>()).Rows,
            new ReconcileOptions(true, true, true)));

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.False(outcome.Succeeded);
        Assert.Contains("does not exist", outcome.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public void Writes_are_ordered_parents_first_and_deletes_last_children_first()
    {
        var parentTable = Plan(new[] { Code }, new[] { Name }, entity: ParentTable);
        var thingPlan = Plan(new[] { Code }, new[] { Parent, Name });

        RecordComparison Only(EntityComparePlan plan, DataRecord? source, DataRecord? target) =>
            Assert.Single(ReferenceDataComparer.Compare(plan,
                source is null ? Array.Empty<DataRecord>() : new[] { source },
                target is null ? Array.Empty<DataRecord>() : new[] { target }).Rows);

        var childCreate = Only(thingPlan, Row(G(1)).With("new_code", "C").WithLookup("new_parentid", G(9), "P").Build(), null);
        var parentCreate = Only(parentTable, Row(G(9)).With("new_code", "P").Named("P").Build(), null);
        var parentDelete = Only(parentTable, null, Row(G(8)).With("new_code", "Q").Build());
        var childDelete = Only(thingPlan, null, Row(G(7)).With("new_code", "D").Build());

        // A child delete that only names its table still follows the table order once a create
        // in the same run has shown which table points at which.
        var items = ReferenceDataWriter.Plan(new[] { parentDelete, childCreate, childDelete, parentCreate },
            new ReconcileOptions(true, true, true));

        var ordered = ReferenceDataWriter.OrderForWriting(items, i => i);

        Assert.Equal(
            new[] { (ReconcileAction.Create, "new_parent"), (ReconcileAction.Create, Table),
                    (ReconcileAction.Delete, Table), (ReconcileAction.Delete, "new_parent") },
            ordered.Select(i => (i.Action, i.Table)));
    }

    [Fact]
    public void Rows_of_a_self_referencing_table_are_written_parent_before_child()
    {
        var self = Lookup("new_parentthingid");
        var plan = Plan(new[] { Code }, new[] { self, Name });

        DataRecord Node(int id, string name, string? parent) =>
            (parent is null
                ? Row(G(id)).With("new_code", name).Named(name)
                : Row(G(id)).With("new_code", name).Named(name)
                    .WithLookup("new_parentthingid", G(99), parent, targetTable: Table, navigation: "new_ParentThingId"))
            .Build();

        var rows = ReferenceDataComparer.Compare(plan,
            new[] { Node(3, "grandchild", "child"), Node(2, "child", "root"), Node(1, "root", null) },
            Array.Empty<DataRecord>()).Rows;

        var ordered = ReferenceDataWriter.OrderForWriting(
            ReferenceDataWriter.Plan(rows, new ReconcileOptions(true, true, true)), i => i);

        Assert.Equal(new[] { "root", "child", "grandchild" }, ordered.Select(i => i.Name));
    }

    [Fact]
    public async Task Apply_abandons_a_lookup_without_a_navigation_property()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Alpha", navigation: null).Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.False(outcome.Succeeded);
        Assert.Contains("navigation property for lookup 'new_parentid'", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_abandons_a_lookup_whose_table_is_not_known()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Alpha", targetTable: null).Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not say which table lookup 'new_parentid' points at", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_abandons_a_lookup_to_a_table_missing_from_the_target()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Alpha", targetTable: "new_elsewhere").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.False(outcome.Succeeded);
        Assert.Equal("Lookup 'new_parentid' points at 'new_elsewhere', which is not in the target environment.", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_abandons_a_lookup_with_no_name_to_resolve()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), null).Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, null));

        Assert.False(outcome.Succeeded);
        Assert.Contains("has no name in the source", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_create_reports_an_id_already_taken()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Post, "new_things", HttpStatusCode.PreconditionFailed, "A record with matching key values already exists.");
        var item = Compared(new[] { Name }, Row(G(1)).With("new_code", "A").With("new_name", "x").Build(), null);

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.False(outcome.Succeeded);
        Assert.Contains("A record with matching key values already exists.", outcome.Message);
    }

    // ---- ApplyAsync: update -------------------------------------------------------------------

    [Fact]
    public async Task Apply_update_patches_only_the_differing_columns_with_if_match()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent);

        var source = Row(G(1)).With("new_code", "A").With("new_name", "Alpha").With("new_amount", "10.00").With("new_count", "5").With("new_flag", "true").Build();
        var target = Row(G(2)).With("new_code", "A").With("new_name", "Alpha").With("new_amount", "10").With("new_count", "4").With("new_flag", "false").Build();

        var item = Compared(new[] { Name, Amount, Currency, Count, Flag }, source, target);
        Assert.Equal(ReconcileAction.Update, item.Action);

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("Updated 2 column(s).", outcome.Message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"{Fakes.ApiRoot}new_things({G(2)})", request.Url);
        Assert.Equal("*", request.Header("If-Match"));

        var body = BodyOf(request);
        Assert.Equal(new[] { "new_count", "new_flag" }, PropertyNames(body));
        Assert.Equal(5, body.GetProperty("new_count").GetInt64());
        Assert.True(body.GetProperty("new_flag").GetBoolean());
    }

    [Fact]
    public async Task Apply_update_never_writes_the_primary_id()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent);
        var source = Row(G(1)).With("new_code", "A").With(PrimaryId, G(1).ToString()).With("new_name", "x").Build();
        var target = Row(G(2)).With("new_code", "A").With(PrimaryId, G(2).ToString()).With("new_name", "y").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { IdColumn, Name }, source, target));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(new[] { "new_name" }, PropertyNames(BodyOf(Assert.Single(handler.Requests))));
        Assert.Equal(
            $"Updated 1 column(s). Not changed: the primary id ({PrimaryId}) differs, and an update cannot change a row's id.",
            outcome.Message);
    }

    [Fact]
    public async Task Apply_update_when_only_the_primary_id_differs_says_so_and_writes_nothing()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").With(PrimaryId, G(1).ToString()).Build();
        var target = Row(G(2)).With("new_code", "A").With(PrimaryId, G(2).ToString()).Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { IdColumn }, source, target));

        Assert.False(outcome.Succeeded);
        Assert.Equal(
            $"nothing could be written - the primary id ({PrimaryId}) differs, and an update cannot change a row's id.",
            outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_update_names_both_reasons_when_the_id_and_read_only_columns_are_all_that_differ()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").With(PrimaryId, G(1).ToString()).With("new_calc", "1").Build();
        var target = Row(G(2)).With("new_code", "A").With(PrimaryId, G(2).ToString()).With("new_calc", "2").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { IdColumn, Calculated }, source, target));

        Assert.False(outcome.Succeeded);
        Assert.Equal(
            $"nothing could be written - the primary id ({PrimaryId}) differs, and an update cannot change a row's id; " +
            "read-only in Dataverse: new_calc.",
            outcome.Message);
    }

    [Fact]
    public async Task Money_is_never_written_without_its_currency()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").With("new_amount", "100").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Amount }, source, null));

        Assert.False(outcome.Succeeded);
        Assert.Contains("wrong currency", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Update_and_delete_only_apply_to_the_version_that_was_compared()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent)
            .OnStatus(HttpMethod.Delete, "new_things(", HttpStatusCode.NoContent);
        var writer = Writer(handler);

        var source = Row(G(1)).With("new_code", "A").With("new_name", "New").Build();
        var target = Row(G(2)).With("new_code", "A").With("new_name", "Old").Versioned("W/\"1001\"").Build();
        var gone = Row(G(3)).With("new_code", "B").Versioned("W/\"2002\"").Build();

        Assert.True((await writer.ApplyAsync(Compared(new[] { Name }, source, target))).Succeeded);
        Assert.True((await writer.ApplyAsync(Compared(new[] { Name }, null, gone))).Succeeded);

        Assert.Equal("W/\"1001\"", handler.Requests[0].Header("If-Match"));
        Assert.Equal("W/\"2002\"", handler.Requests[1].Header("If-Match"));
    }

    [Fact]
    public async Task A_row_changed_since_the_comparison_is_reported_rather_than_overwritten()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Patch, "new_things(", HttpStatusCode.PreconditionFailed, "etag mismatch");
        var source = Row(G(1)).With("new_code", "A").With("new_name", "New").Build();
        var target = Row(G(2)).With("new_code", "A").With("new_name", "Old").Versioned("W/\"1001\"").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Name }, source, target));

        Assert.False(outcome.Succeeded);
        Assert.Contains("etag mismatch", outcome.Message);
        Assert.Contains("changed or removed in the target since the comparison ran - compare again", outcome.Message);
    }

    [Fact]
    public async Task Records_carry_their_etag_from_the_read()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "new_things?",
            "{\"value\":[{\"@odata.etag\":\"W/\\\"77\\\"\",\"new_thingid\":\"" + G(1) + "\",\"new_name\":\"A\"}]}");
        using var client = Fakes.Dataverse(handler);

        var rows = await client.GetRecordsAsync(Entity(), new[] { "new_name" }, null, 10);

        var row = Assert.Single(rows);
        Assert.Equal("W/\"77\"", row.ETag);
        Assert.False(row.Values.ContainsKey("@odata.etag"));
        Assert.Contains("$orderby=new_thingid", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Apply_update_clears_a_lookup_emptied_in_the_source()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent);
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", null, null, targetTable: null).Build();
        var target = Row(G(2)).With("new_code", "A").WithLookup("new_parentid", G(20), "Parent").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, target));

        Assert.True(outcome.Succeeded, outcome.Message);
        var body = BodyOf(Assert.Single(handler.Requests));
        Assert.Equal(new[] { "new_ParentId@odata.bind" }, PropertyNames(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("new_ParentId@odata.bind").ValueKind);
    }

    [Fact]
    public async Task Apply_update_clears_a_lookup_using_the_target_rows_navigation_property()
    {
        // Dataverse does not annotate an empty lookup, so the source carries no navigation property.
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent);
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", null, null, targetTable: null, navigation: null).Build();
        var target = Row(G(2)).With("new_code", "A").WithLookup("new_parentid", G(20), "Parent").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, target));

        Assert.True(outcome.Succeeded, outcome.Message);
        var body = BodyOf(Assert.Single(handler.Requests));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("new_ParentId@odata.bind").ValueKind);
    }

    [Fact]
    public async Task Apply_update_cannot_clear_a_lookup_without_its_navigation_property()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", null, null, targetTable: null, navigation: null).Build();
        var target = Row(G(2)).With("new_code", "A").WithLookup("new_parentid", G(20), "Parent", navigation: null).Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, target));

        Assert.False(outcome.Succeeded);
        Assert.Contains("cannot be cleared", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_update_rebinds_a_lookup_that_points_elsewhere()
    {
        var parentId = G(600);
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_parents?", ParentsResponse(parentId))
            .OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent);

        var source = Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Right").Build();
        var target = Row(G(2)).With("new_code", "A").WithLookup("new_parentid", G(20), "Wrong").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Parent }, source, target));

        Assert.True(outcome.Succeeded, outcome.Message);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal($"/new_parents({parentId})", BodyOf(patch).GetProperty("new_ParentId@odata.bind").GetString());
        Assert.Equal("*", patch.Header("If-Match"));
    }

    [Fact]
    public async Task Apply_update_skips_read_only_columns_and_says_so()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent);
        var source = Row(G(1)).With("new_code", "A").With("new_name", "x").With("new_calc", "1").Build();
        var target = Row(G(2)).With("new_code", "A").With("new_name", "y").With("new_calc", "2").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Name, Calculated }, source, target));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("Updated 1 column(s). Left out as read-only: new_calc.", outcome.Message);
        Assert.Equal(new[] { "new_name" }, PropertyNames(BodyOf(Assert.Single(handler.Requests))));
    }

    [Fact]
    public async Task Apply_update_fails_when_every_differing_column_is_read_only()
    {
        var handler = new FakeHttpHandler();
        var source = Row(G(1)).With("new_code", "A").With("new_name", "x").With("new_calc", "1").Build();
        var target = Row(G(2)).With("new_code", "A").With("new_name", "x").With("new_calc", "2").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Name, Calculated }, source, target));

        Assert.False(outcome.Succeeded);
        Assert.Equal("nothing could be written - read-only in Dataverse: new_calc.", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_update_with_no_differences_writes_nothing()
    {
        var handler = new FakeHttpHandler();
        var item = new ReconcilePlanItem
        {
            Row = Comparison(Plan(new[] { Code }, new[] { Name }), RecordCompareStatus.Different,
                Row(G(1)).Build(), Row(G(2)).Build()),
            Action = ReconcileAction.Update
        };

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.True(outcome.Succeeded);
        Assert.Equal("Nothing to change.", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_update_without_a_target_row_fails()
    {
        var handler = new FakeHttpHandler();
        var item = new ReconcilePlanItem
        {
            Row = Comparison(Plan(new[] { Code }, new[] { Name }), RecordCompareStatus.Different, Row(G(1)).Build()),
            Action = ReconcileAction.Update
        };

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.False(outcome.Succeeded);
        Assert.Equal("no target row to update.", outcome.Message);
    }

    [Fact]
    public async Task Apply_create_without_a_source_row_fails()
    {
        var handler = new FakeHttpHandler();
        var item = new ReconcilePlanItem
        {
            Row = Comparison(Plan(new[] { Code }, new[] { Name }), RecordCompareStatus.OnlyInSource),
            Action = ReconcileAction.Create
        };

        var outcome = await Writer(handler).ApplyAsync(item);

        Assert.False(outcome.Succeeded);
        Assert.Equal("no source row to copy.", outcome.Message);
    }

    [Fact]
    public async Task Apply_update_explains_a_row_removed_since_the_comparison()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Patch, "new_things(", HttpStatusCode.PreconditionFailed, "Precondition failed.");
        var source = Row(G(1)).With("new_code", "A").With("new_name", "x").Build();
        var target = Row(G(2)).With("new_code", "A").With("new_name", "y").Build();

        var outcome = await Writer(handler).ApplyAsync(Compared(new[] { Name }, source, target));

        Assert.False(outcome.Succeeded);
        Assert.Contains("changed or removed in the target since the comparison ran", outcome.Message);
    }

    [Fact]
    public async Task Apply_lets_cancellation_escape()
    {
        using var cts = new CancellationTokenSource();

        // The fake does not observe the token itself, so the request is where the run is stopped.
        var handler = new FakeHttpHandler().On(HttpMethod.Delete, "new_things(", _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var item = Compared(new[] { Name }, null, Row(G(2)).With("new_code", "A").Build());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Writer(handler).ApplyAsync(item, cts.Token));
    }
}
