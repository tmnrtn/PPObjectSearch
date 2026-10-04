using System.Net.Http;
using System.Text;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class ContentSearchTests
{
    private static SolutionComponentItem Item(int type, Guid? id = null, DateTimeOffset? modified = null) => new()
    {
        Name = $"item{type}", ComponentTypeName = "Type", ComponentType = type, ObjectId = id ?? Guid.NewGuid(), ModifiedOn = modified
    };

    [Fact]
    public void Hits_carry_their_line_and_the_text_around_the_match()
    {
        var body = new DefinitionBody(Item(26), "fetchxml",
            "<fetch>\n  <entity name=\"account\">\n    <attribute name=\"Contoso_Status\" />\n  </entity>\n</fetch>");

        var hit = Assert.Single(ContentSearch.Search([body], "contoso_status"));

        Assert.Equal(3, hit.Line);
        Assert.Equal("Contoso_Status", hit.Match);
        Assert.Equal("<attribute name=\"", hit.Before);
        Assert.Equal("\" />", hit.After);
        Assert.Equal("fetchxml, line 3", hit.Where);
    }

    [Fact]
    public void A_long_line_is_cut_to_the_context_either_side()
    {
        var line = new string('a', 200) + "needle" + new string('b', 200);
        var hit = Assert.Single(ContentSearch.Search([new DefinitionBody(Item(61), "content", line)], "needle"));

        Assert.StartsWith("…", hit.Before);
        Assert.EndsWith("…", hit.After);
        Assert.Equal(ContentSearch.Context + 1, hit.Before.Length);
    }

    [Fact]
    public void Every_occurrence_is_found_up_to_the_cap()
    {
        var text = string.Join("\n", Enumerable.Repeat("x new_field y new_field", 30));
        var hits = ContentSearch.Search([new DefinitionBody(Item(29), "clientdata", text)], "new_field");

        Assert.Equal(ContentSearch.MaxHitsPerBody, hits.Count);
        Assert.Equal([1, 1, 2, 2], hits.Take(4).Select(h => h.Line));
        Assert.Empty(ContentSearch.Search([new DefinitionBody(Item(29), "clientdata", text)], "  "));
    }

    [Fact]
    public void Cached_bodies_are_read_again_only_when_the_component_changed()
    {
        var id = Guid.NewGuid();
        var first = Item(26, id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new DefinitionBodyCache();

        Assert.Single(cache.Stale([first]));
        cache.Store([first], [new DefinitionBody(first, "fetchxml", "<fetch/>")]);
        Assert.Empty(cache.Stale([first]));

        var reloaded = Item(26, id, first.ModifiedOn);
        Assert.Same(reloaded, Assert.Single(cache.Bodies([reloaded])).Item);

        var changed = Item(26, id, first.ModifiedOn!.Value.AddDays(1));
        Assert.Single(cache.Stale([changed]));

        // A component with nothing to search is remembered as such.
        var empty = Item(92);
        cache.Store([empty], []);
        Assert.Empty(cache.Stale([empty]));
    }

    [Fact]
    public async Task Bodies_are_read_in_chunks_and_binary_web_resources_skipped()
    {
        Guid flow = Guid.NewGuid(), script = Guid.NewGuid(), image = Guid.NewGuid();
        var js = Convert.ToBase64String(Encoding.UTF8.GetBytes("﻿Xrm.Page.getAttribute('new_status');"));

        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "workflows?", $$"""{"value":[{"workflowid":"{{flow}}","clientdata":"{\"x\":\"new_status\"}","xaml":null}]}""")
            .OnJson(HttpMethod.Get, "webresourceset?", $$"""
                {"value":[
                  {"webresourceid":"{{script}}","webresourcetype":3,"content":"{{js}}"},
                  {"webresourceid":"{{image}}","webresourcetype":5,"content":"iVBORw0KGgo="}
                ]}
                """);

        var items = new[] { Item(29, flow), Item(61, script), Item(61, image), Item(1) };
        var bodies = await Fakes.Dataverse(handler).GetDefinitionBodiesAsync(items);

        Assert.Equal(2, bodies.Count);
        Assert.Equal("Xrm.Page.getAttribute('new_status');", bodies.Single(b => b.Item.ObjectId == script).Text);
        Assert.Equal("clientdata", bodies.Single(b => b.Item.ObjectId == flow).Part);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("$select=webresourceid,content,webresourcetype", handler.Requests[1].Url);
        Assert.False(DataverseClient.HasSearchableBody(Item(1)));
    }
}
