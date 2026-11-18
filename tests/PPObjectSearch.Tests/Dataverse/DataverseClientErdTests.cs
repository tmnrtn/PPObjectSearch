using System.Net;
using System.Net.Http;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class DataverseClientErdTests
{
    private static readonly Guid TableId = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000001");

    private static string Label(string text) =>
        $"{{\"UserLocalizedLabel\":{{\"Label\":\"{text}\",\"LanguageCode\":1033}},\"LocalizedLabels\":[{{\"Label\":\"{text}\"}}]}}";

    /// <summary>ect_case: its id, name, a required lookup to contact, a lookup's shadow name column, and a many-to-many.</summary>
    internal static FakeHttpHandler Case(FakeHttpHandler handler, string key) => handler
        .OnJson(HttpMethod.Get, key + "/Attributes", $$"""
            {"value":[
              {"LogicalName":"ect_caseid","DisplayName":{{Label("Case")}},"AttributeType":"Uniqueidentifier","AttributeTypeName":{"Value":"UniqueidentifierType"},
               "RequiredLevel":{"Value":"SystemRequired"},"IsPrimaryId":true,"IsPrimaryName":false,"IsCustomAttribute":true,"AttributeOf":null},
              {"LogicalName":"ect_name","DisplayName":{{Label("Name")}},"AttributeType":"String","AttributeTypeName":{"Value":"StringType"},
               "RequiredLevel":{"Value":"ApplicationRequired"},"IsPrimaryId":false,"IsPrimaryName":true,"IsCustomAttribute":true,"AttributeOf":null},
              {"LogicalName":"ect_contactid","AttributeType":"Lookup","AttributeTypeName":{"Value":"LookupType"},
               "RequiredLevel":{"Value":"ApplicationRequired"},"IsCustomAttribute":true,"AttributeOf":null},
              {"LogicalName":"ect_contactidname","AttributeType":"String","AttributeTypeName":{"Value":"StringType"},
               "RequiredLevel":{"Value":"None"},"IsCustomAttribute":true,"AttributeOf":"ect_contactid"},
              {"LogicalName":"ect_tags","AttributeType":"Virtual","AttributeTypeName":{"Value":"MultiSelectPicklistType"},
               "RequiredLevel":{"Value":"None"},"IsCustomAttribute":true},
              {"LogicalName":"versionnumber","AttributeType":"BigInt","RequiredLevel":{"Value":"None"},"IsCustomAttribute":false}
            ]}
            """)
        .OnJson(HttpMethod.Get, key + "/ManyToOneRelationships", """
            {"value":[{"SchemaName":"ect_contact_case","ReferencedEntity":"contact","ReferencingAttribute":"ect_contactid"}]}
            """)
        .OnJson(HttpMethod.Get, key + "/ManyToManyRelationships", """
            {"value":[{"SchemaName":"ect_case_contact","Entity1LogicalName":"ect_case","Entity2LogicalName":"contact","IntersectEntityName":"ect_case_contact"}]}
            """)
        .OnJson(HttpMethod.Get, key, $$"""
            {"MetadataId":"{{TableId}}","LogicalName":"ect_case","DisplayName":{{Label("Case")}},"IsCustomEntity":true,"IsIntersect":false}
            """);

    [Fact]
    public async Task A_table_is_read_with_its_columns_lookups_and_many_to_many_relationships()
    {
        var handler = Case(new FakeHttpHandler(), $"EntityDefinitions({TableId})");
        using var client = Fakes.Dataverse(handler);

        var table = await client.GetErdTableAsync(TableId, "ect_case");

        Assert.NotNull(table);
        Assert.Equal(("ect_case", "Case", true, false), (table.LogicalName, table.DisplayName, table.IsCustom, table.IsIntersect));

        // The lookup's shadow name column is left out.
        Assert.Equal(["ect_caseid", "ect_name", "ect_contactid", "ect_tags", "versionnumber"], table.Columns.Select(c => c.LogicalName));
        var id = table.Columns[0];
        Assert.Equal(("Uniqueidentifier", true, true, true), (id.Type, id.IsPrimaryId, id.IsRequired, id.IsCustom));
        Assert.True(table.Columns[1].IsPrimaryName);
        Assert.True(table.Columns[2].IsRequired);
        Assert.Equal("MultiSelectPicklist", table.Columns[3].Type);
        Assert.Equal(("BigInt", false), (table.Columns[4].Type, table.Columns[4].IsCustom));

        var lookup = Assert.Single(table.Lookups);
        Assert.Equal(("ect_contact_case", "ect_contactid", "contact"), (lookup.SchemaName, lookup.Column, lookup.Target));
        var manyToMany = Assert.Single(table.ManyToMany);
        Assert.Equal(("ect_case", "contact", "ect_case_contact"), (manyToMany.Table1, manyToMany.Table2, manyToMany.IntersectTable));

        Assert.Contains(handler.Requests, r => r.Url.Contains("/Attributes?$select=LogicalName,DisplayName,AttributeType,AttributeTypeName,"));
    }

    [Fact]
    public async Task A_table_not_found_by_metadata_id_is_read_by_its_lowercased_logical_name()
    {
        var handler = Case(
            new FakeHttpHandler().OnError(HttpMethod.Get, $"EntityDefinitions({TableId})", HttpStatusCode.NotFound, "not found"),
            "EntityDefinitions(LogicalName='ect_case')");
        using var client = Fakes.Dataverse(handler);

        var table = await client.GetErdTableAsync(TableId, "ECT_Case");

        Assert.Equal("ect_case", table?.LogicalName);
        Assert.All(handler.Requests.Skip(1), r => Assert.Contains("EntityDefinitions(LogicalName='ect_case')", r.Url));
    }

    [Fact]
    public async Task A_table_found_by_neither_name_is_an_error()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "EntityDefinitions(", HttpStatusCode.NotFound, "not found");
        using var client = Fakes.Dataverse(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetErdTableAsync(TableId, "ect_case"));
    }

    [Fact]
    public async Task Display_names_are_read_once_for_every_table()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "EntityDefinitions?$select=MetadataId,LogicalName,DisplayName", $$"""
            {"value":[{"MetadataId":"{{Guid.NewGuid()}}","LogicalName":"account","DisplayName":{{Label("Account")}}},
                      {"MetadataId":"{{Guid.NewGuid()}}","LogicalName":"ect_bare"}]}
            """);
        using var client = Fakes.Dataverse(handler);

        var names = await client.GetTableDisplayNamesAsync();

        Assert.Equal("Account", names["ACCOUNT"]);
        Assert.Null(names["ect_bare"]);
    }
}
