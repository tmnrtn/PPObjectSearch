using System.IO;
using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>The undo steps quick actions log for environment variable values, and switches whose old state is unknown.</summary>
public sealed class QuickActionsUndoTests : IDisposable
{
    private static readonly Guid Id = Guid.Parse("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid ValueId = Guid.Parse("f0000000-0000-0000-0000-000000000002");
    private static readonly Guid DefinitionId = Guid.Parse("f0000000-0000-0000-0000-000000000003");

    private readonly string _logs = Path.Combine(Path.GetTempPath(), "ppos-quick-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_logs)) Directory.Delete(_logs, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static EnvironmentVariableInfo Variable(Guid? valueId, string? current) => new()
    {
        SchemaName = "new_Url", TypeLabel = "Text", DefinitionId = DefinitionId, ValueId = valueId,
        CurrentValue = current, HasCurrentValue = current is not null
    };

    [Fact]
    public async Task A_new_value_is_undone_by_deleting_it()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "environmentvariablevalues", HttpStatusCode.NoContent);
        var actions = new QuickActions(Fakes.Dataverse(handler), "ada@contoso.com", _logs);

        await actions.SetEnvironmentValueAsync(Variable(null, null), "https://prod");

        var entry = Assert.Single(WriteLog.Read(actions.RunLogPath!));
        Assert.Equal("Set value", entry.Action);
        Assert.Null(entry.Before);
        Assert.Equal("https://prod", entry.After!["value"]!.GetValue<string>());
        Assert.Equal(UndoMethod.Delete, entry.Undo!.Method);
        Assert.NotEqual(Guid.Empty, entry.Undo.Id);
    }

    [Fact]
    public async Task A_changed_value_is_undone_by_putting_the_old_one_back()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Patch, "environmentvariablevalues(", HttpStatusCode.NoContent);
        var actions = new QuickActions(Fakes.Dataverse(handler), null, _logs);

        await actions.SetEnvironmentValueAsync(Variable(ValueId, "https://dev"), "https://prod");

        var entry = Assert.Single(WriteLog.Read(actions.RunLogPath!));
        Assert.Equal(UndoMethod.Update, entry.Undo!.Method);
        Assert.Equal(ValueId, entry.Undo.Id);
        Assert.Equal("https://dev", entry.Undo.Body!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Clearing_a_value_that_was_never_set_needs_no_undo()
    {
        var handler = new FakeHttpHandler();
        var actions = new QuickActions(Fakes.Dataverse(handler), null, _logs);

        await actions.SetEnvironmentValueAsync(Variable(null, null), null);

        Assert.Empty(handler.Requests);
        var entry = Assert.Single(WriteLog.Read(actions.RunLogPath!));
        Assert.Equal("Clear value", entry.Action);
        Assert.Null(entry.Undo);
    }

    [Fact]
    public async Task A_refused_value_is_logged_as_failed_and_reported()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Patch, "environmentvariablevalues(", HttpStatusCode.Forbidden, "no write privilege");
        var actions = new QuickActions(Fakes.Dataverse(handler), null, _logs);

        await Assert.ThrowsAsync<DataverseException>(() => actions.SetEnvironmentValueAsync(Variable(ValueId, "old"), "new"));

        var entry = Assert.Single(WriteLog.Read(actions.RunLogPath!));
        Assert.False(entry.Succeeded);
        Assert.Contains("no write privilege", entry.Message);
        Assert.Equal(ValueId, entry.Id);
    }

    [Fact]
    public async Task A_switch_whose_old_state_cannot_be_read_still_happens_without_an_undo()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "sdkmessageprocessingsteps?", HttpStatusCode.Forbidden, "no read")
            .OnStatus(HttpMethod.Patch, "sdkmessageprocessingsteps(", HttpStatusCode.NoContent);
        var actions = new QuickActions(Fakes.Dataverse(handler), null, _logs);
        var step = new SolutionComponentItem { Name = "Validate", ComponentTypeName = "Step", ComponentType = 92, ObjectId = Id };

        await actions.SetStateAsync(step, SwitchableKind.PluginStep, on: true);

        var entry = Assert.Single(WriteLog.Read(actions.RunLogPath!));
        Assert.True(entry.Succeeded);
        Assert.Null(entry.Before);
        Assert.Null(entry.Undo);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Patch);
    }
}
