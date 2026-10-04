using System.Net;
using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class ChangeTimelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static SolutionComponentItem Item(string name, int hoursAgo, bool managed = false, int type = 61, Guid? id = null) => new()
    {
        Name = name, ComponentTypeName = "Web Resource", ComponentType = type, ObjectId = id ?? Guid.NewGuid(),
        ModifiedOn = Now.AddHours(-hoursAgo), IsManaged = managed
    };

    [Fact]
    public void Component_changes_and_solution_operations_share_one_timeline_newest_first()
    {
        var script = Item("new_script.js", 2);
        var old = Item("new_old.js", 200);
        var history = new[]
        {
            new SolutionHistoryEntry
            {
                Id = Guid.NewGuid(), SolutionName = "Core", Operation = "Import", Version = "1.2.0.0", IsManaged = true,
                StartTime = Now.AddHours(-5), StatusCode = 1, Succeeded = false, ExceptionMessage = "Missing dependency"
            }
        };

        var entries = ChangeTimeline.Build([script, old], new Dictionary<Guid, string> { [script.ObjectId] = "Alex" }, history, Now.AddDays(-1));

        Assert.Equal(2, entries.Count);
        Assert.Equal("new_script.js", entries[0].What);
        Assert.Equal("Alex", entries[0].By);
        Assert.Equal("Unmanaged", entries[0].ManagedLabel);
        Assert.True(entries[1].IsSolutionOperation);
        Assert.Equal("Failed · version 1.2.0.0 · managed · Missing dependency", entries[1].Detail);

        var markdown = ChangeTimeline.ToMarkdown(entries, "UAT", Now.AddDays(-1));
        Assert.Contains("# Changes in UAT", markdown);
        Assert.Contains("| Component | new_script.js |", markdown);
        Assert.Contains("| Solution | Core | Import |", markdown);
    }

    [Fact]
    public async Task Recent_changes_are_asked_for_by_date_and_checked_again()
    {
        var defaultSolution = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", """
            {"value":[
              {"msdyn_componenttype":61,"msdyn_name":"new_recent.js","msdyn_objectid":"11111111-1111-1111-1111-111111111111","msdyn_modifiedon":"2026-10-04T10:00:00Z"},
              {"msdyn_componenttype":61,"msdyn_name":"new_old.js","msdyn_objectid":"22222222-2222-2222-2222-222222222222","msdyn_modifiedon":"2026-01-01T10:00:00Z"}
            ]}
            """);

        var changed = await Fakes.Dataverse(handler).GetRecentlyChangedAsync(defaultSolution, Now.AddDays(-7));

        Assert.Equal("new_recent.js", Assert.Single(changed).Name);
        var url = Uri.UnescapeDataString(handler.Requests[0].Url);
        Assert.Contains($"(msdyn_solutionid eq {defaultSolution}) and msdyn_modifiedon ge 2026-09-27T12:00:00Z", url);
        Assert.Contains("$orderby=msdyn_modifiedon desc", url);
        Assert.False(changed.IsTruncated);
    }

    [Fact]
    public async Task Who_changed_each_component_is_read_per_table_and_a_failing_table_is_skipped()
    {
        Guid script = Guid.NewGuid(), form = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "webresourceset?", $$"""
                {"value":[{"webresourceid":"{{script}}","_modifiedby_value":"u1","_modifiedby_value@OData.Community.Display.V1.FormattedValue":"Alex"}]}
                """)
            .OnError(HttpMethod.Get, "systemforms?", HttpStatusCode.Forbidden, "no");

        var by = await Fakes.Dataverse(handler).GetModifiedByAsync([Item("s", 1, id: script), Item("f", 1, type: 60, id: form), Item("t", 1, type: 1)]);

        Assert.Equal("Alex", by[script]);
        Assert.False(by.ContainsKey(form));
        Assert.Equal(2, handler.Requests.Count);
    }
}
