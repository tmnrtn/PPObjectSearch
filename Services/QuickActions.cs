using System.Text.Json.Nodes;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// The one-click changes offered next to what the app shows: turning a flow or process on or off,
/// enabling a plug-in step, setting an environment variable's value. The caller has already
/// passed the write guard and had the change confirmed; this makes it and records it in the run
/// log, with the write that reverses it.
/// </summary>
public sealed class QuickActions
{
    private const string Tool = "quick-action";

    private readonly DataverseClient _client;
    private readonly string? _account;
    private readonly string? _logFolder;
    private WriteLog? _log;

    public QuickActions(DataverseClient client, string? account, string? logFolder = null)
    {
        _client = client;
        _account = account;
        _logFolder = logFolder;
    }

    public string? RunLogPath => _log?.Path;

    /// <summary>Turns one component on or off; throws with Dataverse's reason if it is refused.</summary>
    public async Task SetStateAsync(SolutionComponentItem item, SwitchableKind kind, bool on, CancellationToken ct = default)
    {
        var set = Switchable.EntitySet(kind);
        var verb = on ? Switchable.Verbs(kind).On : Switchable.Verbs(kind).Off;

        bool? wasOn = null;
        try
        {
            var states = await _client.GetSwitchStatesAsync(kind, new[] { item.ObjectId }, ct).ConfigureAwait(false);
            if (states.TryGetValue(item.ObjectId, out var state)) wasOn = state;
        }
        catch (DataverseException ex)
        {
            // Not knowing the old state costs the undo step, not the change.
            Log.Warn($"Could not read the state of {item.Name} before changing it", ex);
        }

        var (stateCode, statusCode) = Switchable.Codes(kind, on);
        var after = new JsonObject { ["statecode"] = stateCode, ["statuscode"] = statusCode };

        UndoStep? undo = null;
        if (wasOn is { } previous)
        {
            var (oldState, oldStatus) = Switchable.Codes(kind, previous);
            undo = new UndoStep(UndoMethod.Update, set, item.ObjectId,
                new JsonObject { ["statecode"] = oldState, ["statuscode"] = oldStatus });
        }

        try
        {
            await _client.SetSwitchStateAsync(kind, item.ObjectId, on, ct).ConfigureAwait(false);
            Record(set, item.ObjectId, verb, item.Name, wasOn is null ? null : new JsonObject { ["on"] = wasOn }, after, true,
                $"{verb} - done.", undo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(set, item.ObjectId, verb, item.Name, null, after, false, ex.Message, null);
            throw;
        }
    }

    /// <summary>Sets (or, with null, removes) an environment variable's value in this environment.</summary>
    public async Task SetEnvironmentValueAsync(EnvironmentVariableInfo variable, string? value, CancellationToken ct = default)
    {
        const string set = "environmentvariablevalues";
        var action = value is null ? "Clear value" : "Set value";
        var before = variable.HasCurrentValue ? new JsonObject { ["value"] = variable.CurrentValue } : null;
        var after = value is null ? null : new JsonObject { ["value"] = value };

        try
        {
            var id = await _client.SetEnvironmentVariableValueAsync(variable, value, ct).ConfigureAwait(false);

            UndoStep? undo = (variable.ValueId, id) switch
            {
                // Created: remove it again. Updated: put the old value back. Removed: re-create it.
                (null, { } created) => new UndoStep(UndoMethod.Delete, set, created),
                ({ } existing, { }) => new UndoStep(UndoMethod.Update, set, existing,
                    new JsonObject { ["value"] = variable.CurrentValue }),
                ({ } removed, null) => new UndoStep(UndoMethod.Create, set, removed, new JsonObject
                {
                    ["environmentvariablevalueid"] = removed.ToString(),
                    ["schemaname"] = variable.SchemaName,
                    ["value"] = variable.CurrentValue,
                    ["EnvironmentVariableDefinitionId@odata.bind"] = $"/environmentvariabledefinitions({variable.DefinitionId})"
                }),
                _ => null
            };

            Record(set, id ?? variable.ValueId, action, variable.SchemaName, before, after, true, $"{action} - done.", undo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(set, variable.ValueId, action, variable.SchemaName, before, after, false, ex.Message, null);
            throw;
        }
    }

    private void Record(
        string table, Guid? id, string action, string? name, JsonObject? before, JsonObject? after,
        bool succeeded, string message, UndoStep? undo)
    {
        _log ??= WriteLog.Start(Tool, _logFolder);
        _log.Append(new WriteLogEntry
        {
            Run = _log.Run,
            Tool = Tool,
            Environment = _client.EnvironmentUrl,
            Account = _account,
            Table = table,
            Id = id,
            Action = action,
            Name = name,
            Before = before,
            After = after,
            Succeeded = succeeded,
            Message = message,
            Undo = undo
        });
    }
}
