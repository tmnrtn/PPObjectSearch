using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.ReferenceData;

public class ReferenceDataPlannerTests
{
    private const string EntityListUrl = "EntityDefinitions?$select=LogicalName";
    private const string ThingAttributesUrl = "EntityDefinitions(LogicalName='new_thing')/Attributes";

    private static string EntitiesJson(params string[] logicalNames) => JsonSerializer.Serialize(new
    {
        value = logicalNames.Select(n => new
        {
            LogicalName = n,
            DisplayName = new { UserLocalizedLabel = new { Label = "Thing" } },
            EntitySetName = n + "s",
            PrimaryIdAttribute = n + "id",
            PrimaryNameAttribute = "new_name",
            IsPrivate = false,
            IsManaged = false,
            IsActivity = false
        })
    });

    private static object Attr(string name, string type, bool primaryId = false, bool primaryName = false) => new
    {
        LogicalName = name,
        AttributeTypeName = new { Value = type },
        IsValidForRead = true,
        IsValidForCreate = true,
        IsValidForUpdate = !primaryId,
        IsPrimaryId = primaryId,
        IsPrimaryName = primaryName
    };

    private static string AttributesJson(params object[] attributes) =>
        JsonSerializer.Serialize(new { value = attributes });

    private static readonly object[] StandardSource =
    {
        Attr("new_thingid", "UniqueidentifierType", primaryId: true),
        Attr("new_name", "StringType", primaryName: true),
        Attr("new_code", "StringType"),
        Attr("new_amount", "MoneyType"),
        Attr("createdon", "DateTimeType"),
        Attr("modifiedby", "LookupType")
    };

    private sealed class Env
    {
        public FakeHttpHandler Source { get; } = new();
        public FakeHttpHandler Target { get; } = new();

        public ReferenceDataPlanner Planner() => new(Fakes.Dataverse(Source), Fakes.Dataverse(Target));
    }

    private static Env Standard(object[]? source = null, object[]? target = null, string[]? targetTables = null)
    {
        var env = new Env();

        env.Source.OnJson(HttpMethod.Get, EntityListUrl, EntitiesJson("new_thing", "new_other"));
        env.Target.OnJson(HttpMethod.Get, EntityListUrl, EntitiesJson(targetTables ?? new[] { "new_thing" }));

        env.Source.OnJson(HttpMethod.Get, ThingAttributesUrl, AttributesJson(source ?? StandardSource));
        env.Target.OnJson(HttpMethod.Get, ThingAttributesUrl, AttributesJson(target ?? StandardSource));

        return env;
    }

    private static ReferenceEntityConfig Config(Action<ReferenceEntityConfig>? configure = null)
    {
        var config = new ReferenceEntityConfig { LogicalName = "new_thing" };
        configure?.Invoke(config);
        return config;
    }

    [Fact]
    public async Task A_column_added_after_the_configuration_was_saved_is_not_compared()
    {
        var columns = StandardSource.Append(Attr("new_secreturl", "StringType")).ToArray();
        var env = Standard(columns, columns);

        var result = await env.Planner().BuildAsync(Config(c =>
        {
            c.ExcludedColumns = new List<string> { "new_thingid", "createdon", "modifiedby" };
            c.ComparedColumns = new List<string> { "new_name", "new_code", "new_amount" };
        }), true);

        Assert.DoesNotContain(result.Plan!.ValueColumns, c => c.LogicalName == "new_secreturl");
        Assert.Contains(result.Warnings, w => w.Contains("added since this configuration was saved") && w.Contains("new_secreturl"));
    }

    [Fact]
    public async Task A_configuration_without_a_recorded_choice_compares_everything_not_excluded()
    {
        var columns = StandardSource.Append(Attr("new_secreturl", "StringType")).ToArray();
        var env = Standard(columns, columns);

        var result = await env.Planner().BuildAsync(Config(c =>
            c.ExcludedColumns = new List<string> { "new_thingid", "createdon", "modifiedby" }), true);

        Assert.Contains(result.Plan!.ValueColumns, c => c.LogicalName == "new_secreturl");
    }

    [Fact]
    public async Task Money_brings_its_currency_into_the_comparison_even_when_excluded()
    {
        var columns = StandardSource.Append(Attr("transactioncurrencyid", "LookupType")).ToArray();
        var env = Standard(columns, columns);

        var result = await env.Planner().BuildAsync(
            Config(c => c.ExcludedColumns = new List<string> { "new_thingid", "transactioncurrencyid" }), true);

        Assert.Contains(result.Plan!.ValueColumns, c => c.LogicalName == "transactioncurrencyid");
        Assert.Contains(result.Warnings, w => w.Contains("transactioncurrencyid is compared because money"));
    }

    [Fact]
    public async Task Currency_is_left_alone_where_no_money_is_compared()
    {
        var columns = StandardSource.Append(Attr("transactioncurrencyid", "LookupType")).ToArray();
        var env = Standard(columns, columns);

        var result = await env.Planner().BuildAsync(
            Config(c => c.ExcludedColumns = new List<string> { "new_thingid", "new_amount", "transactioncurrencyid" }), true);

        Assert.DoesNotContain(result.Plan!.ValueColumns, c => c.LogicalName == "transactioncurrencyid");
        Assert.DoesNotContain(result.Warnings, w => w.Contains("transactioncurrencyid"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Build_fails_without_a_table_name(string? logicalName)
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(new ReferenceEntityConfig { LogicalName = logicalName }, true);

        Assert.Null(result.Plan);
        Assert.Equal(new PlanFailure("(unnamed)", "No table name."), result.Failure);
        Assert.Empty(env.Source.Requests);
    }

    [Fact]
    public async Task Build_fails_for_a_table_missing_from_the_source()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(new ReferenceEntityConfig { LogicalName = "new_absent" }, true);

        Assert.Null(result.Plan);
        Assert.Equal(new PlanFailure("new_absent", "Not in the source environment."), result.Failure);
    }

    [Fact]
    public async Task Build_fails_for_a_table_missing_from_the_target()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(new ReferenceEntityConfig { LogicalName = "new_other" }, true);

        Assert.Null(result.Plan);
        Assert.Equal(new PlanFailure("new_other", "Not in the target environment."), result.Failure);
    }

    [Fact]
    public async Task Build_finds_the_table_regardless_of_case()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(new ReferenceEntityConfig { LogicalName = "NEW_Thing" }, true);

        Assert.Null(result.Failure);
        Assert.Equal("new_thing", result.Plan!.Entity.LogicalName);
    }

    [Fact]
    public async Task Build_keys_on_the_primary_id_and_applies_default_exclusions()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(Config(c => c.Filter = "statecode eq 0"), matchLookupsByName: false);

        Assert.Null(result.Failure);
        Assert.Empty(result.Warnings);

        var plan = result.Plan!;
        Assert.Equal("new_thing", plan.Entity.LogicalName);
        Assert.Equal("new_things", plan.Entity.EntitySetName);
        Assert.Equal(new[] { "new_thingid" }, plan.KeyColumns.Select(c => c.LogicalName).ToArray());
        Assert.Equal("new_thingid", plan.KeyLabel);
        Assert.Equal(new[] { "new_amount", "new_code", "new_name" }, plan.ValueColumns.Select(c => c.LogicalName).ToArray());
        Assert.Equal("statecode eq 0", plan.Filter);
        Assert.False(plan.MatchLookupsByName);
    }

    [Fact]
    public async Task Build_with_an_empty_exclusion_list_compares_every_column()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(Config(c => c.ExcludedColumns = new List<string>()), true);

        Assert.Equal(
            new[] { "createdon", "modifiedby", "new_amount", "new_code", "new_name", "new_thingid" },
            result.Plan!.ValueColumns.Select(c => c.LogicalName).ToArray());
        Assert.True(result.Plan.MatchLookupsByName);
    }

    [Fact]
    public async Task Build_applies_configured_exclusions_regardless_of_case()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(Config(c => c.ExcludedColumns = new List<string> { "NEW_AMOUNT", "createdon" }), true);

        Assert.Equal(
            new[] { "modifiedby", "new_code", "new_name", "new_thingid" },
            result.Plan!.ValueColumns.Select(c => c.LogicalName).ToArray());
    }

    [Fact]
    public async Task Build_leaves_out_columns_the_target_lacks_and_warns()
    {
        var target = new[]
        {
            Attr("new_thingid", "UniqueidentifierType", primaryId: true),
            Attr("new_name", "StringType", primaryName: true),
            Attr("new_extra_in_target", "StringType")
        };

        var env = Standard(target: target);

        var result = await env.Planner().BuildAsync(Config(c => c.ExcludedColumns = new List<string>()), true);

        Assert.Null(result.Failure);
        Assert.Equal(new[] { "new_name", "new_thingid" }, result.Plan!.ValueColumns.Select(c => c.LogicalName).ToArray());

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(
            "new_thing: 4 column(s) exist only in the source and were left out of the comparison " +
            "(createdon, modifiedby, new_amount, new_code).",
            warning);
    }

    [Fact]
    public async Task Build_names_at_most_six_missing_columns()
    {
        var source = new List<object> { Attr("new_thingid", "UniqueidentifierType", primaryId: true) };
        source.AddRange(Enumerable.Range(1, 8).Select(i => Attr($"new_col{i}", "StringType")));

        var env = Standard(source: source.ToArray(), target: new[] { Attr("new_thingid", "UniqueidentifierType", primaryId: true) });

        var result = await env.Planner().BuildAsync(Config(), true);

        Assert.Contains(
            "new_thing: 8 column(s) exist only in the source and were left out of the comparison " +
            "(new_col1, new_col2, new_col3, new_col4, new_col5, new_col6, ...).",
            result.Warnings);
    }

    [Fact]
    public async Task Build_warns_when_every_column_is_excluded()
    {
        var env = Standard(
            source: new[] { Attr("new_thingid", "UniqueidentifierType", primaryId: true), Attr("createdon", "DateTimeType") },
            target: new[] { Attr("new_thingid", "UniqueidentifierType", primaryId: true), Attr("createdon", "DateTimeType") });

        var result = await env.Planner().BuildAsync(Config(), true);

        Assert.NotNull(result.Plan);
        Assert.Empty(result.Plan.ValueColumns);
        Assert.Equal(new[] { "new_thing: every column is excluded, so only presence is compared." }, result.Warnings);
    }

    [Fact]
    public async Task Build_fails_when_the_primary_id_is_missing_from_the_target()
    {
        var env = Standard(target: new[] { Attr("new_name", "StringType", primaryName: true) });

        var result = await env.Planner().BuildAsync(Config(), true);

        Assert.Null(result.Plan);
        Assert.Equal(
            new PlanFailure("new_thing", "Primary id 'new_thingid' is not readable in both environments."),
            result.Failure);
    }

    [Fact]
    public async Task Build_keys_on_chosen_columns()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(Config(c =>
        {
            c.KeySource = RecordKeySource.Columns;
            c.KeyColumns = new List<string> { "new_code", "NEW_NAME" };
        }), true);

        Assert.Null(result.Failure);
        Assert.Equal(new[] { "new_code", "new_name" }, result.Plan!.KeyColumns.Select(c => c.LogicalName).ToArray());
        Assert.Equal("new_code + NEW_NAME", result.Plan.KeyLabel);
    }

    [Fact]
    public async Task Build_fails_when_no_key_columns_are_chosen()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(Config(c => c.KeySource = RecordKeySource.Columns), true);

        Assert.Null(result.Plan);
        Assert.Equal(new PlanFailure("new_thing", "No key columns chosen."), result.Failure);
    }

    [Fact]
    public async Task Build_fails_when_a_key_column_is_not_in_both_environments()
    {
        var target = StandardSource.Where(a => !JsonSerializer.Serialize(a).Contains("\"new_code\"")).ToArray();
        var env = Standard(target: target);

        var result = await env.Planner().BuildAsync(Config(c =>
        {
            c.KeySource = RecordKeySource.Columns;
            c.KeyColumns = new List<string> { "new_code" };
        }), true);

        Assert.Null(result.Plan);
        Assert.Equal(
            new PlanFailure("new_thing", "Key column 'new_code' is not readable in both environments."),
            result.Failure);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Build_keys_on_the_recorded_columns_of_an_alternate_key()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(Config(c =>
        {
            c.KeySource = RecordKeySource.AlternateKey;
            c.AlternateKeyName = "new_codekey";
            c.KeyColumns = new List<string> { "new_code" };
        }), true);

        Assert.Null(result.Failure);
        Assert.Equal("new_codekey", result.Plan!.KeyLabel);
        Assert.Equal("new_code", Assert.Single(result.Plan.KeyColumns).LogicalName);
        Assert.DoesNotContain(env.Source.Requests, r => r.Url.Contains("/Keys"));
    }

    [Fact]
    public async Task Build_fails_for_an_alternate_key_with_no_recorded_columns()
    {
        var env = Standard();

        var result = await env.Planner().BuildAsync(Config(c =>
        {
            c.KeySource = RecordKeySource.AlternateKey;
            c.AlternateKeyName = "new_codekey";
        }), true);

        Assert.Null(result.Plan);
        Assert.Equal(
            new PlanFailure("new_thing", "Alternate key 'new_codekey' has no columns recorded."),
            result.Failure);
    }

    [Fact]
    public async Task Build_reads_table_and_column_metadata_once_per_planner()
    {
        var env = Standard();
        var planner = env.Planner();

        await planner.BuildAsync(Config(), true);
        await planner.BuildAsync(Config(c => c.ExcludedColumns = new List<string>()), false);
        await planner.BuildAsync(new ReferenceEntityConfig { LogicalName = "new_other" }, true);

        Assert.Equal(1, env.Source.Requests.Count(r => r.Url.Contains(EntityListUrl)));
        Assert.Equal(1, env.Target.Requests.Count(r => r.Url.Contains(EntityListUrl)));
        Assert.Equal(1, env.Source.Requests.Count(r => r.Url.Contains(ThingAttributesUrl)));
        Assert.Equal(1, env.Target.Requests.Count(r => r.Url.Contains(ThingAttributesUrl)));
    }

    [Fact]
    public async Task Get_alternate_keys_asks_the_source()
    {
        var env = Standard();
        env.Source.OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_thing')/Keys",
            JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new { LogicalName = "new_codekey", KeyAttributes = new[] { "new_code" } }
                }
            }));

        var keys = await env.Planner().GetAlternateKeysAsync("new_thing");

        var key = Assert.Single(keys);
        Assert.Equal("new_codekey", key.LogicalName);
        Assert.Equal(new[] { "new_code" }, key.KeyAttributes);
        Assert.Empty(env.Target.Requests);
    }
}
