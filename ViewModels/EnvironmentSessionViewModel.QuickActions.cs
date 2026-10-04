using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>Turning components on and off from the grid - one, a selection, or a solution's flows.</summary>
public sealed partial class EnvironmentSessionViewModel
{
    private bool _isSwitching;

    /// <summary>The selected rows that can be switched on or off.</summary>
    private IReadOnlyList<(SolutionComponentItem Item, SwitchableKind Kind)> SwitchableSelection =>
        (Selection.Count > 0 ? Selection : SelectedItem is { } one ? new[] { one } : Array.Empty<SolutionComponentItem>())
        .Select(i => (Item: i, Kind: Switchable.KindOf(i)))
        .Where(x => x.Kind is not null)
        .Select(x => (x.Item, x.Kind!.Value))
        .ToList();

    public bool HasSwitchableSelection => SwitchableSelection.Count > 0;

    private AsyncRelayCommand? _switchOnCommand;
    /// <summary>Turns on, activates or enables every selected flow, process and plug-in step.</summary>
    public AsyncRelayCommand SwitchOnCommand => _switchOnCommand ??= new AsyncRelayCommand(
        _ => SwitchAsync(SwitchableSelection, true), _ => CanSwitch && HasSwitchableSelection);

    private AsyncRelayCommand? _switchOffCommand;
    public AsyncRelayCommand SwitchOffCommand => _switchOffCommand ??= new AsyncRelayCommand(
        _ => SwitchAsync(SwitchableSelection, false), _ => CanSwitch && HasSwitchableSelection);

    private AsyncRelayCommand? _turnOnSolutionFlowsCommand;
    /// <summary>Every cloud flow in the solution shown that is off - the usual fix after an import.</summary>
    public AsyncRelayCommand TurnOnSolutionFlowsCommand => _turnOnSolutionFlowsCommand ??= new AsyncRelayCommand(
        _ => TurnOnSolutionFlowsAsync(), _ => !_isSwitching);

    public string SwitchOnHeader => SwitchHeader(true);
    public string SwitchOffHeader => SwitchHeader(false);

    // A context menu does not ask again when it opens, so this stays loose and the run itself
    // checks that there is a connection and nothing else under way.
    private bool CanSwitch => !_isSwitching;

    private bool ReadyToSwitch()
    {
        if (_client is not null && IsConnected && !IsBusy) return true;

        Status = IsBusy ? "Wait for the current load to finish first." : "Connect first.";
        return false;
    }

    private static bool IsCloudFlow(SolutionComponentItem item) => Switchable.KindOf(item) == SwitchableKind.CloudFlow;

    private string SwitchHeader(bool on)
    {
        var selection = SwitchableSelection;
        if (selection.Count == 0) return on ? "Turn on" : "Turn off";

        var kinds = selection.Select(s => s.Kind).Distinct().ToList();
        var verb = kinds.Count == 1
            ? on ? Switchable.Verbs(kinds[0]).On : Switchable.Verbs(kinds[0]).Off
            : on ? "Turn on" : "Turn off";

        return selection.Count == 1 ? verb : $"{verb} {selection.Count:N0} selected";
    }

    private void RaiseSwitchCommands()
    {
        OnPropertyChanged(nameof(HasSwitchableSelection));
        OnPropertyChanged(nameof(SwitchOnHeader));
        OnPropertyChanged(nameof(SwitchOffHeader));
        SwitchOnCommand.RaiseCanExecuteChanged();
        SwitchOffCommand.RaiseCanExecuteChanged();
        TurnOnSolutionFlowsCommand.RaiseCanExecuteChanged();
    }

    private async Task TurnOnSolutionFlowsAsync()
    {
        if (!ReadyToSwitch()) return;

        var flows = _allItems.Where(IsCloudFlow).ToList();
        if (flows.Count == 0)
        {
            Status = "There are no cloud flows in the list.";
            return;
        }

        Status = $"Checking which of {flows.Count:N0} flow(s) are off...";

        IReadOnlyDictionary<Guid, bool> states;
        try
        {
            states = await _client!.GetSwitchStatesAsync(SwitchableKind.CloudFlow, flows.Select(f => f.ObjectId).ToList());
        }
        catch (Exception ex)
        {
            Status = "Could not read the flows' states - " + ex.Message;
            return;
        }

        var off = flows.Where(f => states.TryGetValue(f.ObjectId, out var on) && !on).ToList();
        if (off.Count == 0)
        {
            Status = $"All {flows.Count:N0} flow(s) here are already on.";
            return;
        }

        await SwitchAsync(off.Select(f => (f, SwitchableKind.CloudFlow)).ToList(), true);
    }

    private async Task SwitchAsync(IReadOnlyList<(SolutionComponentItem Item, SwitchableKind Kind)> targets, bool on)
    {
        if (targets.Count == 0 || !ReadyToSwitch()) return;

        var names = string.Join("\n", targets.Take(15).Select(t => "  " + (t.Item.DisplayName ?? t.Item.Name))) +
                    (targets.Count > 15 ? $"\n  ... and {targets.Count - 15:N0} more" : string.Empty);
        var verb = on ? "Turn on" : "Turn off";
        var title = targets.Count == 1
            ? on ? Switchable.Verbs(targets[0].Kind).On : Switchable.Verbs(targets[0].Kind).Off
            : verb;

        if (!await WriteConfirmation.AskAsync(this, title, $"{title} {targets.Count:N0} component(s)?\n\n{names}")) return;

        _isSwitching = true;
        RaiseSwitchCommands();

        var actions = new QuickActions(_client!, AccountName);
        var done = 0;
        var failed = new List<string>();

        try
        {
            foreach (var (item, kind) in targets)
            {
                done++;
                Status = $"({done}/{targets.Count}) {title.ToLowerInvariant()} {item.DisplayName ?? item.Name}...";

                try
                {
                    await actions.SetStateAsync(item, kind, on);
                }
                catch (Exception ex)
                {
                    failed.Add($"{item.DisplayName ?? item.Name}: {ex.Message}");
                }
            }

            Status = failed.Count == 0
                ? $"{title} - {targets.Count:N0} done. Recorded in the run log."
                : $"{title} - {targets.Count - failed.Count:N0} done, {failed.Count:N0} failed: " +
                  string.Join("; ", failed.Take(3)) + (failed.Count > 3 ? " ..." : string.Empty);
        }
        finally
        {
            _isSwitching = false;
            RaiseSwitchCommands();
        }
    }
}
