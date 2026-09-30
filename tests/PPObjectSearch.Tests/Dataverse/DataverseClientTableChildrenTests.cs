using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class DataverseClientTableChildrenTests
{
    private const string Api = Fakes.ApiRoot;
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private static readonly Guid TableId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly TableIdentity Account = new(TableId, "account", 1, "Account");

    private static string Page(string rows, string? nextLink = null) =>
        "{\"value\":[" + rows + "]" + (nextLink is null ? "" : ",\"@odata.nextLink\":" + JsonSerializer.Serialize(nextLink)) + "}";

    private static string Label(string text) =>
        $"{{\"UserLocalizedLabel\":{{\"Label\":\"{text}\",\"LanguageCode\":1033}},\"LocalizedLabels\":[{{\"Label\":\"{text}\"}}]}}";

    // ---- Table identity ----

    [Fact]
    public async Task Identity_is_read_by_metadata_id()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"EntityDefinitions({TableId})",
            $"{{\"MetadataId\":\"{TableId}\",\"LogicalName\":\"account\",\"ObjectTypeCode\":1,\"DisplayName\":{Label("Account")}}}");
        using var client = Fakes.Dataverse(handler);

        var identity = await client.GetTableIdentityAsync(TableId, "account");

        Assert.Equal(new TableIdentity(TableId, "account", 1, "Account"), identity);
        Assert.Single(handler.Requests);
        Assert.Contains("?$select=MetadataId,LogicalName,SchemaName,ObjectTypeCode,DisplayName", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Identity_falls_back_to_the_lowercased_logical_name()
    {
        var realId = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, $"EntityDefinitions({TableId})", HttpStatusCode.NotFound, "not found")
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_widget')",
                $"{{\"MetadataId\":\"{realId}\",\"LogicalName\":\"new_widget\"}}");
        using var client = Fakes.Dataverse(handler);

        var identity = await client.GetTableIdentityAsync(TableId, "New_Widget");

        Assert.Equal(new TableIdentity(realId, "new_widget", null, null), identity);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Identity_keeps_the_given_id_when_the_row_has_none()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "EntityDefinitions(", "{\"LogicalName\":\"account\"}");
        using var client = Fakes.Dataverse(handler);

        var identity = await client.GetTableIdentityAsync(TableId, null);

        Assert.Equal(TableId, identity!.MetadataId);
    }

    [Fact]
    public async Task Identity_failure_on_the_last_way_of_naming_the_table_propagates()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "EntityDefinitions(", HttpStatusCode.NotFound, "not found");
        using var client = Fakes.Dataverse(handler);

        await Assert.ThrowsAsync<DataverseException>(() => client.GetTableIdentityAsync(TableId, "account"));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Identity_is_null_when_nothing_names_a_table()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "EntityDefinitions(", "{}");
        using var client = Fakes.Dataverse(handler);

        Assert.Null(await client.GetTableIdentityAsync(TableId, "account"));
        Assert.Null(await client.GetTableIdentityAsync(Guid.Empty, null));
    }

    // ---- Columns ----

    [Fact]
    public async Task Columns_carry_type_requirement_and_a_typed_properties_query()
    {
        var name = Guid.NewGuid();
        var status = Guid.NewGuid();
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "skiptoken=2", Page(
                $"{{\"@odata.type\":\"#Microsoft.Dynamics.CRM.UniqueIdentifierAttributeMetadata\",\"MetadataId\":\"{id}\"," +
                "\"LogicalName\":\"accountid\",\"AttributeType\":\"Uniqueidentifier\",\"IsPrimaryId\":true," +
                "\"RequiredLevel\":{\"Value\":\"SystemRequired\"}}"))
            .OnJson(HttpMethod.Get, "/Attributes?", Page(
                $"{{\"@odata.type\":\"#Microsoft.Dynamics.CRM.StringAttributeMetadata\",\"MetadataId\":\"{name}\"," +
                $"\"LogicalName\":\"name\",\"DisplayName\":{Label("Account Name")},\"AttributeType\":\"String\"," +
                "\"RequiredLevel\":{\"Value\":\"ApplicationRequired\",\"CanBeChanged\":true},\"IsPrimaryName\":true,\"IsManaged\":true}," +
                $"{{\"@odata.type\":\"#Microsoft.Dynamics.CRM.StatusAttributeMetadata\",\"MetadataId\":\"{status}\"," +
                "\"LogicalName\":\"statuscode\",\"AttributeType\":\"Status\",\"RequiredLevel\":{\"Value\":\"None\"}}," +
                "{\"LogicalName\":\"\"}",
                Api + $"EntityDefinitions({TableId})/Attributes?$skiptoken=2"));
        using var client = Fakes.Dataverse(handler);

        var columns = await client.GetTableColumnsAsync(Account);

        Assert.Equal(3, columns.Count);

        var nameColumn = columns[0];
        Assert.Equal(TableChildKind.Column, nameColumn.Kind);
        Assert.Equal("name", nameColumn.Name);
        Assert.Equal("Account Name", nameColumn.DisplayName);
        Assert.Equal("String, required, primary name", nameColumn.Detail);
        Assert.True(nameColumn.IsManaged);
        Assert.Equal(name, nameColumn.Id);
        Assert.Equal($"EntityDefinitions({TableId})/Attributes({name})/Microsoft.Dynamics.CRM.StringAttributeMetadata",
            nameColumn.PropertiesQuery);

        Assert.Equal("Status", columns[1].Detail);
        Assert.Equal(
            $"EntityDefinitions({TableId})/Attributes({status})/Microsoft.Dynamics.CRM.StatusAttributeMetadata?$expand=OptionSet",
            columns[1].PropertiesQuery);

        Assert.Equal("Uniqueidentifier, system required, primary id", columns[2].Detail);
        Assert.False(string.IsNullOrEmpty(columns[0].FilterIndex));
    }

    [Theory]
    [InlineData("#Microsoft.Dynamics.CRM.PicklistAttributeMetadata", "/Microsoft.Dynamics.CRM.PicklistAttributeMetadata?$expand=OptionSet")]
    [InlineData("#Microsoft.Dynamics.CRM.MultiSelectPicklistAttributeMetadata", "/Microsoft.Dynamics.CRM.MultiSelectPicklistAttributeMetadata?$expand=OptionSet")]
    [InlineData("#Microsoft.Dynamics.CRM.StateAttributeMetadata", "/Microsoft.Dynamics.CRM.StateAttributeMetadata?$expand=OptionSet")]
    [InlineData("#Microsoft.Dynamics.CRM.BooleanAttributeMetadata", "/Microsoft.Dynamics.CRM.BooleanAttributeMetadata?$expand=OptionSet")]
    [InlineData("#Microsoft.Dynamics.CRM.LookupAttributeMetadata", "/Microsoft.Dynamics.CRM.LookupAttributeMetadata")]
    [InlineData("#Some.Other.Type", "")]
    [InlineData(null, "")]
    public async Task Column_properties_query_casts_to_the_concrete_type(string? odataType, string suffix)
    {
        var id = Guid.NewGuid();
        var typeField = odataType is null ? "" : $"\"@odata.type\":\"{odataType}\",";
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/Attributes?",
            Page($"{{{typeField}\"MetadataId\":\"{id}\",\"LogicalName\":\"c\"}}"));
        using var client = Fakes.Dataverse(handler);

        var column = Assert.Single(await client.GetTableColumnsAsync(Account));

        Assert.Equal($"EntityDefinitions({TableId})/Attributes({id}){suffix}", column.PropertiesQuery);
    }

    // ---- Relationships and keys ----

    [Fact]
    public async Task Relationships_are_described_from_this_tables_point_of_view()
    {
        var oneToMany = Guid.NewGuid();
        var manyToOne = Guid.NewGuid();
        var manyToMany = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/OneToManyRelationships?", Page(
                $"{{\"MetadataId\":\"{oneToMany}\",\"SchemaName\":\"account_contacts\",\"ReferencedEntity\":\"account\",\"ReferencingEntity\":\"contact\",\"IsManaged\":true}}," +
                "{\"SchemaName\":\"\"}"))
            .OnJson(HttpMethod.Get, "/ManyToOneRelationships?", Page(
                $"{{\"MetadataId\":\"{manyToOne}\",\"SchemaName\":\"account_owner\",\"ReferencedEntity\":\"systemuser\",\"ReferencingEntity\":\"account\"}}"))
            .OnJson(HttpMethod.Get, "/ManyToManyRelationships?", Page(
                $"{{\"MetadataId\":\"{manyToMany}\",\"SchemaName\":\"account_lead\",\"Entity1LogicalName\":\"lead\",\"Entity2LogicalName\":\"account\"}}," +
                "{\"SchemaName\":\"account_self\",\"Entity1LogicalName\":\"ACCOUNT\",\"Entity2LogicalName\":\"account\"}"));
        using var client = Fakes.Dataverse(handler);

        var relationships = await client.GetTableRelationshipsAsync(Account);

        Assert.Equal(new[] { "account_contacts", "account_owner", "account_lead", "account_self" }, relationships.Select(r => r.Name));
        Assert.All(relationships, r => Assert.Equal(TableChildKind.Relationship, r.Kind));
        Assert.Equal("1:N  contact", relationships[0].Detail);
        Assert.True(relationships[0].IsManaged);
        Assert.Equal($"RelationshipDefinitions({oneToMany})/Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata", relationships[0].PropertiesQuery);
        Assert.Equal("N:1  systemuser", relationships[1].Detail);
        Assert.Equal($"RelationshipDefinitions({manyToOne})/Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata", relationships[1].PropertiesQuery);
        Assert.Equal("N:N  lead", relationships[2].Detail);
        Assert.Equal($"RelationshipDefinitions({manyToMany})/Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata", relationships[2].PropertiesQuery);
        Assert.Equal("N:N  account", relationships[3].Detail);
    }

    [Fact]
    public async Task Keys_list_their_columns_or_index_status()
    {
        var key = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/Keys?", Page(
            $"{{\"MetadataId\":\"{key}\",\"LogicalName\":\"acc_number\",\"DisplayName\":{Label("Account number")}," +
            "\"KeyAttributes\":[\"accountnumber\",\"name\"],\"EntityKeyIndexStatus\":\"Active\",\"IsManaged\":false}," +
            "{\"SchemaName\":\"pending_key\",\"KeyAttributes\":[],\"EntityKeyIndexStatus\":\"Pending\"}," +
            "{\"KeyAttributes\":[\"x\"]}"));
        using var client = Fakes.Dataverse(handler);

        var keys = await client.GetTableKeysAsync(Account);

        Assert.Equal(2, keys.Count);
        Assert.Equal(TableChildKind.Key, keys[0].Kind);
        Assert.Equal("acc_number", keys[0].Name);
        Assert.Equal("Account number", keys[0].DisplayName);
        Assert.Equal("accountnumber, name", keys[0].Detail);
        Assert.Equal($"EntityDefinitions({TableId})/Keys({key})", keys[0].PropertiesQuery);
        Assert.Equal("pending_key", keys[1].Name);
        Assert.Equal("Pending", keys[1].Detail);
    }

    // ---- Forms, views, charts ----

    [Fact]
    public async Task Forms_and_dashboards_come_from_one_query_by_entity_name()
    {
        var form = Guid.NewGuid();
        var dashboard = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "systemforms?", Page(
            $"{{\"formid\":\"{form}\",\"name\":\"Main\",\"type\":2,\"type{Formatted}\":\"Main\",\"ismanaged\":true}}," +
            $"{{\"formid\":\"{dashboard}\",\"name\":\"Overview\",\"type\":0,\"type{Formatted}\":\"Dashboard\"}}," +
            "{\"formid\":\"bad\"}"));
        using var client = Fakes.Dataverse(handler);

        var forms = await client.GetTableFormsAsync(Account);

        Assert.Equal(2, forms.Count);
        Assert.Equal(TableChildKind.Form, forms[0].Kind);
        Assert.Equal("Main", forms[0].Detail);
        Assert.True(forms[0].IsManaged);
        Assert.Equal($"systemforms({form})", forms[0].PropertiesQuery);
        Assert.Equal(TableChildKind.Dashboard, forms[1].Kind);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("$filter=objecttypecode eq 'account'", request.Url);
        Assert.Contains("FormattedValue", request.Header("Prefer"));
    }

    [Fact]
    public async Task Rejected_string_filter_is_retried_with_the_object_type_code()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "objecttypecode eq 'account'", HttpStatusCode.BadRequest, "type mismatch")
            .OnJson(HttpMethod.Get, "systemforms?", Page($"{{\"formid\":\"{Guid.NewGuid()}\",\"name\":\"Main\",\"type\":2}}"));
        using var client = Fakes.Dataverse(handler);

        var forms = await client.GetTableFormsAsync(Account);

        Assert.Single(forms);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("$filter=objecttypecode eq 1", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Rejected_string_filter_without_an_object_type_code_propagates()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "systemforms?", HttpStatusCode.BadRequest, "type mismatch");
        using var client = Fakes.Dataverse(handler);

        await Assert.ThrowsAsync<DataverseException>(() =>
            client.GetTableFormsAsync(Account with { ObjectTypeCode = null }));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Views_mark_the_default_view()
    {
        var view = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "savedqueries?", Page(
            $"{{\"savedqueryid\":\"{view}\",\"name\":\"Active Accounts\",\"querytype{Formatted}\":\"Public View\",\"isdefault\":true}}," +
            $"{{\"savedqueryid\":\"{Guid.NewGuid()}\",\"querytype{Formatted}\":\"Lookup View\",\"isdefault\":false}}"));
        using var client = Fakes.Dataverse(handler);

        var views = await client.GetTableViewsAsync(Account);

        Assert.Equal(TableChildKind.View, views[0].Kind);
        Assert.Equal("Public View, default", views[0].Detail);
        Assert.Equal($"savedqueries({view})", views[0].PropertiesQuery);
        Assert.Equal("Lookup View", views[1].Detail);
        Assert.Equal(views[1].Id.ToString(), views[1].Name);
        Assert.EndsWith("$filter=returnedtypecode eq 'account'", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Charts_are_listed_with_default_flag()
    {
        var chart = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "savedqueryvisualizations?", Page(
            $"{{\"savedqueryvisualizationid\":\"{chart}\",\"name\":\"By owner\",\"isdefault\":true}}," +
            $"{{\"savedqueryvisualizationid\":\"{Guid.NewGuid()}\",\"name\":\"Other\"}}"));
        using var client = Fakes.Dataverse(handler);

        var charts = await client.GetTableChartsAsync(Account);

        Assert.Equal(TableChildKind.Chart, charts[0].Kind);
        Assert.Equal("default", charts[0].Detail);
        Assert.Null(charts[1].Detail);
        Assert.Equal($"savedqueryvisualizations({chart})", charts[0].PropertiesQuery);
        Assert.EndsWith("$filter=primaryentitytypecode eq 'account'", handler.Requests[0].Url);
    }

    // ---- Child properties ----

    private static TableChild Child(string query) => new() { Kind = TableChildKind.Column, Name = "c", PropertiesQuery = query };

    [Fact]
    public async Task Child_properties_drop_expansion_then_cast_when_refused()
    {
        var column = Guid.NewGuid();
        var query = $"EntityDefinitions({TableId})/Attributes({column})/Microsoft.Dynamics.CRM.PicklistAttributeMetadata?$expand=OptionSet";
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$expand=OptionSet", HttpStatusCode.BadRequest, "no expand")
            .OnError(HttpMethod.Get, "PicklistAttributeMetadata", HttpStatusCode.BadRequest, "no cast")
            .OnJson(HttpMethod.Get, $"Attributes({column})", "{\"LogicalName\":\"c\",\"AttributeType\":\"Picklist\"}");
        using var client = Fakes.Dataverse(handler);

        var properties = await client.GetChildPropertiesAsync(Child(query));

        Assert.Equal(new[] { Api + query, Api + query[..query.IndexOf('?')], Api + $"EntityDefinitions({TableId})/Attributes({column})" },
            handler.Requests.Select(r => r.Url));
        Assert.Contains(properties, p => p.Name == "LogicalName" && p.Value == "c");
        Assert.Contains(properties, p => p.Name == "AttributeType" && p.Value == "Picklist");
    }

    [Fact]
    public async Task Child_properties_failure_on_the_last_query_propagates()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "systemforms(", HttpStatusCode.NotFound, "gone");
        using var client = Fakes.Dataverse(handler);

        await Assert.ThrowsAsync<DataverseException>(() => client.GetChildPropertiesAsync(Child($"systemforms({Guid.NewGuid()})")));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Record_child_properties_merge_formatted_values()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "systemforms(",
            $"{{\"@odata.context\":\"ctx\",\"name\":\"Main\",\"type\":2,\"type{Formatted}\":\"Main\"," +
            $"\"_ownerid_value\":\"o-1\",\"_ownerid_value{Formatted}\":\"Tom\",\"isdefault\":true}}");
        using var client = Fakes.Dataverse(handler);

        var properties = await client.GetChildPropertiesAsync(Child("systemforms(x)"));

        Assert.Equal(new[] { "name=Main", "type=Main  (2)", "ownerid=Tom  (o-1)", "isdefault=True" },
            properties.Select(p => $"{p.Name}={p.Value}"));
    }

    // ---- RecordProperties.Flatten ----

    private static IReadOnlyList<string> Flatten(string json) =>
        RecordProperties.Flatten(JsonDocument.Parse(json).RootElement).Select(p => $"{p.Name}={p.Value}").ToList();

    [Fact]
    public void Flatten_collapses_labels_and_managed_properties()
    {
        var rows = Flatten(
            $"{{\"DisplayName\":{Label("Account")},\"Description\":{{\"UserLocalizedLabel\":null,\"LocalizedLabels\":[]}}," +
            "\"IsAuditEnabled\":{\"Value\":true,\"CanBeChanged\":true,\"ManagedPropertyLogicalName\":\"canmodifyauditsettings\"}}");

        Assert.Equal(new[] { "DisplayName=Account", "IsAuditEnabled=True" }, rows);
    }

    [Fact]
    public void Flatten_reads_options_as_numbered_single_lines()
    {
        var rows = Flatten(
            "{\"OptionSet\":{\"Name\":\"statuscode\",\"Options\":[" +
            $"{{\"Value\":1,\"Label\":{Label("Active")},\"State\":0,\"Color\":\"#0000ff\"}}," +
            $"{{\"Value\":2,\"Label\":{{\"UserLocalizedLabel\":null,\"LocalizedLabels\":[]}},\"DefaultStatus\":2,\"Description\":{Label("Gone")}}}]}}}}");

        Assert.Equal(new[]
        {
            "OptionSet.Name=statuscode",
            "OptionSet.Option 1=Active  (1)  state 0  #0000ff",
            "OptionSet.Option 2=(unlabelled)  (2)  default status 2  - Gone"
        }, rows);
    }

    [Fact]
    public void Flatten_joins_scalar_arrays_and_indexes_non_plural_paths()
    {
        var rows = Flatten("{\"KeyAttributes\":[\"a\",\"b\"],\"Address\":[{\"x\":1},{\"x\":2}],\"Empty\":[],\"Nothing\":null}");

        Assert.Equal(new[] { "KeyAttributes=a, b", "Address[0].x=1", "Address[1].x=2" }, rows);
    }
}
