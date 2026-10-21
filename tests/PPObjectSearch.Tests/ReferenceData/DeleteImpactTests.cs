using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

public class DeleteImpactTests
{
    private const string Relationships = """
        {"value":[
          {"ReferencingEntity":"new_child","ReferencingAttribute":"new_thingid","CascadeConfiguration":{"Delete":"Cascade"}},
          {"ReferencingEntity":"new_order","ReferencingAttribute":"new_thingid","CascadeConfiguration":{"Delete":"Restrict"}},
          {"ReferencingEntity":"new_note","ReferencingAttribute":"new_aboutid","CascadeConfiguration":{"Delete":"RemoveLink"}},
          {"ReferencingEntity":"new_log","ReferencingAttribute":"new_thingid","CascadeConfiguration":{"Delete":"NoCascade"}},
          {"ReferencingEntity":"new_hidden","ReferencingAttribute":"new_thingid","CascadeConfiguration":{"Delete":"Cascade"}}
        ]}
        """;

    private static readonly Dictionary<string, EntitySummary> Entities = new(StringComparer.OrdinalIgnoreCase)
    {
        [Table] = Entity(),
        ["new_child"] = Entity("new_child", "new_children", "new_childid"),
        ["new_order"] = Entity("new_order", "new_orders", "new_orderid"),
        ["new_note"] = Entity("new_note", "new_notes", "new_noteid"),
        ["new_log"] = Entity("new_log", "new_logs", "new_logid")
    };

    [Fact]
    public async Task Each_reaching_relationship_is_counted_and_described()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/OneToManyRelationships", Relationships)
            .OnJson(HttpMethod.Get, "new_children?", """{"@odata.count":5000,"value":[]}""")
            .OnJson(HttpMethod.Get, "new_orders?", """{"@odata.count":2,"value":[]}""")
            .OnJson(HttpMethod.Get, "new_notes?", """{"@odata.count":0,"value":[]}""");

        var lines = await DeleteImpact.CheckAsync(Fakes.Dataverse(handler), Entities,
            new[] { (Table, G(1)), (Table, G(2)) });

        // No cascade is not asked about at all; nothing found is not reported.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("new_logs"));
        Assert.DoesNotContain(lines, l => l.Table == "new_note");

        Assert.Equal(
            "Dataverse will also delete 5,000+ new_child (via new_thingid). " +
            "2 new_order (via new_thingid) still point at them and will make those deletes fail. " +
            "Not counted (table not readable here): new_hidden.",
            DeleteImpact.Describe(lines));

        var count = Uri.UnescapeDataString(handler.Requests.First(r => r.Url.Contains("new_children?")).Url);
        Assert.Contains($"_new_thingid_value eq {G(1)} or _new_thingid_value eq {G(2)}", count);
        Assert.Contains("$count=true", count);
    }

    [Fact]
    public void Each_effect_gets_a_row_cascades_first_then_blocks()
    {
        var shown = DeleteImpact.Shown(
        [
            new("new_note", "new_thingid", "RemoveLink", 3),
            new("new_order", "new_thingid", "Restrict", 2),
            new("new_hidden", "new_thingid", "Cascade", -1),
            new("new_log", "new_thingid", "Cascade", 0),
            new("new_child", "new_thingid", "Cascade", 7)
        ]);

        Assert.Equal(["new_child", "new_order", "new_note", "new_hidden"], shown.Select(l => l.Table));
        Assert.Equal("new_order · via new_thingid - still point at them, so those deletes fail", shown[1].Description);
        Assert.True(shown[0].IsCascade);
        Assert.True(shown[1].IsBlocking);
        Assert.Equal("?", shown[3].CountLabel);
    }

    [Fact]
    public void Nothing_reached_says_so()
    {
        Assert.StartsWith("No other rows", DeleteImpact.Describe(Array.Empty<DeleteImpactLine>()));
    }

    [Fact]
    public async Task Many_rows_are_counted_in_chunks_and_summed()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "new_children?", """{"@odata.count":3,"value":[]}""");

        var ids = Enumerable.Range(1, 120).Select(G).ToList();
        var total = await Fakes.Dataverse(handler).CountReferencingAsync(Entities["new_child"], "new_thingid", ids);

        Assert.Equal(18, total);
        Assert.Equal(6, handler.Requests.Count);
    }
}
