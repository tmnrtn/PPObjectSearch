using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class QuickActionsTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ValueId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DefinitionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static string LogFolder() =>
        Path.Combine(Path.GetTempPath(), "ppobjectsearch-tests", Guid.NewGuid().ToString("N"));

    private static SolutionComponentItem Item(int type, int? category = null) => new()
    {
        Name = "thing",
        ComponentTypeName = "x",
        ComponentType = type,
        ProcessCategory = category,
        ObjectId = Id
    };

    private static JsonElement Body(RecordedRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    [Theory]
    [InlineData(29, 5, SwitchableKind.CloudFlow)]
    [InlineData(29, 0, SwitchableKind.Process)]
    [InlineData(29, 2, SwitchableKind.Process)]
    [InlineData(92, null, SwitchableKind.PluginStep)]
    public void Flows_processes_and_plugin_steps_can_be_switched(int type, int? category, SwitchableKind expected)
    {
        Assert.Equal(expected, Switchable.KindOf(Item(type, category)));
    }

    [Fact]
    public void Other_components_cannot()
    {
        Assert.Null(Switchable.KindOf(Item(1)));
        Assert.Null(Switchable.KindOf(Item(380)));
    }

    [Theory]
    [InlineData(SwitchableKind.CloudFlow, true, "workflows", 1, 2)]
    [InlineData(SwitchableKind.Process, false, "workflows", 0, 1)]
    [InlineData(SwitchableKind.PluginStep, true, "sdkmessageprocessingsteps", 0, 1)]
    [InlineData(SwitchableKind.PluginStep, false, "sdkmessageprocessingsteps", 1, 2)]
    public async Task Switching_sends_the_state_and_status_that_go_together(
        SwitchableKind kind, bool on, string set, int state, int status)
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Patch, set, HttpStatusCode.NoContent);

        await Fakes.Dataverse(handler).SetSwitchStateAsync(kind, Id, on);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith($"{set}({Id})", request.Url);
        Assert.Equal(state, Body(request).GetProperty("statecode").GetInt32());
        Assert.Equal(status, Body(request).GetProperty("statuscode").GetInt32());
    }

    [Fact]
    public async Task States_are_read_as_on_or_off_for_the_kind()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "sdkmessageprocessingsteps?", $$"""
                {"value":[{"sdkmessageprocessingstepid":"{{Id}}","statecode":0},{"sdkmessageprocessingstepid":"{{ValueId}}","statecode":1}]}
                """);

        var states = await Fakes.Dataverse(handler).GetSwitchStatesAsync(SwitchableKind.PluginStep, new[] { Id, ValueId });

        Assert.True(states[Id]);
        Assert.False(states[ValueId]);
    }

    [Fact]
    public async Task A_value_is_created_where_there_is_none_bound_to_its_definition()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "environmentvariablevalues", HttpStatusCode.NoContent);
        var variable = new EnvironmentVariableInfo { SchemaName = "new_Url", TypeLabel = "Text", DefinitionId = DefinitionId };

        var id = await Fakes.Dataverse(handler).SetEnvironmentVariableValueAsync(variable, "https://example.com");

        var body = Body(Assert.Single(handler.Requests));
        Assert.Equal(id.ToString(), body.GetProperty("environmentvariablevalueid").GetString());
        Assert.Equal("https://example.com", body.GetProperty("value").GetString());
        Assert.Equal("new_Url", body.GetProperty("schemaname").GetString());
        Assert.Equal($"/environmentvariabledefinitions({DefinitionId})",
            body.GetProperty("EnvironmentVariableDefinitionId@odata.bind").GetString());
    }

    [Fact]
    public async Task An_existing_value_is_updated_and_a_cleared_one_deleted()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Patch, "environmentvariablevalues(", HttpStatusCode.NoContent)
            .OnStatus(HttpMethod.Delete, "environmentvariablevalues(", HttpStatusCode.NoContent);
        var variable = new EnvironmentVariableInfo
        {
            SchemaName = "new_Url", TypeLabel = "Text", DefinitionId = DefinitionId, ValueId = ValueId,
            CurrentValue = "old", HasCurrentValue = true
        };
        var client = Fakes.Dataverse(handler);

        Assert.Equal(ValueId, await client.SetEnvironmentVariableValueAsync(variable, "new"));
        Assert.Null(await client.SetEnvironmentVariableValueAsync(variable, null));

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal("new", Body(handler.Requests[0]).GetProperty("value").GetString());
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.EndsWith($"environmentvariablevalues({ValueId})", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData(EnvironmentVariableValues.Number, " 12.50 ", "12.50", null)]
    [InlineData(EnvironmentVariableValues.Number, "twelve", null, "not a number")]
    [InlineData(EnvironmentVariableValues.Boolean, "True", "yes", null)]
    [InlineData(EnvironmentVariableValues.Boolean, "no", "no", null)]
    [InlineData(EnvironmentVariableValues.Boolean, "maybe", null, "yes/no")]
    [InlineData(EnvironmentVariableValues.Json, "{\"a\":1}", "{\"a\":1}", null)]
    [InlineData(EnvironmentVariableValues.Json, "{a:1}", null, "Not valid JSON")]
    [InlineData(EnvironmentVariableValues.DataSource, "  ", null, "cannot be empty")]
    [InlineData(EnvironmentVariableValues.Secret, "x", null, "Key Vault")]
    [InlineData(EnvironmentVariableValues.Text, " kept as typed ", " kept as typed ", null)]
    public void Values_are_checked_against_the_type(int type, string input, string? value, string? error)
    {
        var (normalised, problem) = EnvironmentVariableValues.Normalise(type, input);

        Assert.Equal(value, normalised);
        if (error is null) Assert.Null(problem);
        else Assert.Contains(error, problem);
    }

    [Fact]
    public async Task A_switch_is_logged_with_the_write_that_reverses_it()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "workflows?", $$"""{"value":[{"workflowid":"{{Id}}","statecode":1}]}""")
            .OnStatus(HttpMethod.Patch, "workflows(", HttpStatusCode.NoContent);
        var actions = new QuickActions(Fakes.Dataverse(handler), "someone@contoso.com", LogFolder());

        await actions.SetStateAsync(Item(29, 5), SwitchableKind.CloudFlow, on: false);

        var entry = Assert.Single(WriteLog.Read(actions.RunLogPath!));
        Assert.Equal("Turn off", entry.Action);
        Assert.Equal("quick-action", entry.Tool);
        Assert.True(entry.Succeeded);
        Assert.True(entry.Before!["on"]!.GetValue<bool>());
        Assert.Equal(UndoMethod.Update, entry.Undo!.Method);
        Assert.Equal(1, entry.Undo.Body!["statecode"]!.GetValue<int>());
        Assert.Equal(2, entry.Undo.Body["statuscode"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_refused_switch_is_logged_as_failed_and_reported()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "workflows?", """{"value":[]}""")
            .OnError(HttpMethod.Patch, "workflows(", HttpStatusCode.BadRequest, "connection reference is broken");
        var actions = new QuickActions(Fakes.Dataverse(handler), null, LogFolder());

        var ex = await Assert.ThrowsAsync<DataverseException>(() =>
            actions.SetStateAsync(Item(29, 5), SwitchableKind.CloudFlow, on: true));

        Assert.Contains("connection reference is broken", ex.Message);
        var entry = Assert.Single(WriteLog.Read(actions.RunLogPath!));
        Assert.False(entry.Succeeded);
        Assert.Null(entry.Undo);
    }

    [Fact]
    public async Task Clearing_a_value_is_undone_by_recreating_it()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Delete, "environmentvariablevalues(", HttpStatusCode.NoContent);
        var actions = new QuickActions(Fakes.Dataverse(handler), null, LogFolder());
        var variable = new EnvironmentVariableInfo
        {
            SchemaName = "new_Url", TypeLabel = "Text", DefinitionId = DefinitionId, ValueId = ValueId,
            CurrentValue = "old", HasCurrentValue = true
        };

        await actions.SetEnvironmentValueAsync(variable, null);

        var entry = Assert.Single(WriteLog.Read(actions.RunLogPath!));
        Assert.Equal("Clear value", entry.Action);
        Assert.Equal("old", entry.Before!["value"]!.GetValue<string>());
        Assert.Equal(UndoMethod.Create, entry.Undo!.Method);
        Assert.Equal("old", entry.Undo.Body!["value"]!.GetValue<string>());
        Assert.Equal(ValueId, entry.Undo.Id);
    }
}
