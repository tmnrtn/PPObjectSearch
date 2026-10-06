using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The readiness check window: pick a solution in the source and a target, and see what would
/// stop the import or stop it working afterwards. It only reads - nothing is written anywhere.
/// </summary>
public sealed class ReadinessViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    /// <param name="source">The environment to start from - the tab it was opened from - when there is one.</param>
    public ReadinessViewModel(IEnumerable<EnvironmentSessionViewModel> sessions, EnvironmentSessionViewModel? source = null)
    {
        Sessions = new ObservableCollection<EnvironmentSessionViewModel>(sessions.Where(s => s.IsConnected));

        RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => CanRun);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
        ExportCommand = new RelayCommand(_ => Export(), _ => Report is not null);
        OpenFindingCommand = new RelayCommand(
            p => OpenFinding(p as ReadinessFinding ?? SelectedFinding),
            p => (p as ReadinessFinding ?? SelectedFinding)?.Item is not null);

        _source = (source is not null && Sessions.Contains(source) ? source : null)
                  ?? Sessions.FirstOrDefault(s => s.SelectedSolution is { IsDefaultSolution: false })
                  ?? Sessions.FirstOrDefault();
        _target = Sessions.FirstOrDefault(s => s != _source);
        _solution = _source?.SelectedSolution is { IsDefaultSolution: false } selected ? selected : null;
    }

    public ObservableCollection<EnvironmentSessionViewModel> Sessions { get; }
    public ObservableCollection<ReadinessFinding> Findings { get; } = new();

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenFindingCommand { get; }

    private EnvironmentSessionViewModel? _source;
    public EnvironmentSessionViewModel? Source
    {
        get => _source;
        set
        {
            if (!SetProperty(ref _source, value)) return;
            OnPropertyChanged(nameof(Solutions));
            OnPropertyChanged(nameof(EmptyText));
            Solution = value?.SelectedSolution is { IsDefaultSolution: false } selected ? selected : null;
            RunCommand.RaiseCanExecuteChanged();
        }
    }

    private EnvironmentSessionViewModel? _target;
    public EnvironmentSessionViewModel? Target
    {
        get => _target;
        set
        {
            if (!SetProperty(ref _target, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    /// <summary>The source's solutions, less the default one - "everything" is not something to deploy.</summary>
    public IEnumerable<SolutionInfo> Solutions =>
        Source?.Solutions.Where(s => !s.IsDefaultSolution) ?? Enumerable.Empty<SolutionInfo>();

    private SolutionInfo? _solution;
    public SolutionInfo? Solution
    {
        get => _solution;
        set
        {
            if (!SetProperty(ref _solution, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private ReadinessFinding? _selectedFinding;
    public ReadinessFinding? SelectedFinding
    {
        get => _selectedFinding;
        set
        {
            if (SetProperty(ref _selectedFinding, value)) OpenFindingCommand.RaiseCanExecuteChanged();
        }
    }

    private ReadinessReport? _report;
    public ReadinessReport? Report
    {
        get => _report;
        private set
        {
            if (!SetProperty(ref _report, value)) return;
            ExportCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(NotChecked));
            OnPropertyChanged(nameof(HasNotChecked));
            RaiseFindings();
        }
    }

    public string NotChecked => Report is { NotChecked.Count: > 0 } r
        ? "Not checked: " + string.Join(" · ", r.NotChecked)
        : string.Empty;

    public bool HasNotChecked => !string.IsNullOrEmpty(NotChecked);

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            RaiseFindings();
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private string _readSummary = string.Empty;
    /// <summary>"Read 214 components of Contoso Core in 6.1 s - 1 blocker, 3 warnings, 8 notes".</summary>
    public string ReadSummary
    {
        get => _readSummary;
        private set
        {
            if (SetProperty(ref _readSummary, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>The status bar's left side: what is happening, else what the last check covered.</summary>
    public string StatusLine => string.IsNullOrEmpty(Status) ? ReadSummary : Status;

    // ---------------------------------------------------------------- findings

    public bool HasFindings => Findings.Count > 0;

    /// <summary>Nothing to list and nothing running - the empty state shows in the grid's place.</summary>
    public bool IsEmpty => !IsRunning && Findings.Count == 0;

    public string FindingCountLabel => $"{Findings.Count:N0} finding{(Findings.Count == 1 ? string.Empty : "s")}";

    /// <summary>Why the list is empty and what to change - shown in its place.</summary>
    public string EmptyHeading => Report is not null
        ? "Nothing found"
        : string.IsNullOrEmpty(Status) ? "Nothing checked yet" : "The check did not finish";

    public string EmptyText
    {
        get
        {
            if (Report is { } report)
            {
                return $"No blockers, warnings or notes for {report.Solution} in {report.Target}." +
                       (report.NotChecked.Count > 0 ? " Some checks could not run - see above." : string.Empty);
            }

            if (!string.IsNullOrEmpty(Status)) return "The status bar says why. Check again when it is sorted.";
            if (Source is null || Target is null || Source == Target) return "Choose the environment the solution comes from and a different one it is going to.";
            return Solution is null
                ? $"Choose a solution in {Source.Title}, then Check."
                : $"Check {Solution.DisplayLabel} from {Source.Title} to {Target.Title} - nothing is written.";
        }
    }

    private void RaiseFindings()
    {
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(FindingCountLabel));
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyText));
    }

    internal static string Describe(ReadinessReport report, int components, TimeSpan took)
    {
        static string Count(int n, string word) => $"{n:N0} {word}{(n == 1 ? string.Empty : "s")}";

        return $"Read {Count(components, "component")} of {report.Solution} in {took.TotalSeconds:0.0} s - " +
               $"{Count(report.Count(ReadinessSeverity.Blocker), "blocker")}, " +
               $"{Count(report.Count(ReadinessSeverity.Warning), "warning")}, " +
               $"{Count(report.Count(ReadinessSeverity.Info), "note")}";
    }

    private bool CanRun =>
        !IsRunning && Source?.Client is not null && Target?.Client is not null && Source != Target && Solution is not null;

    private async Task RunAsync()
    {
        if (!CanRun) return;

        var source = Source!;
        var target = Target!;
        var solution = Solution!;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Findings.Clear();
        Report = null;

        try
        {
            Status = $"Reading {solution.DisplayLabel} in {source.Title}...";
            var clock = Stopwatch.StartNew();
            var components = await source.Client!.GetSolutionComponentsAsync(solution.SolutionId, ct: _cts.Token);

            var check = new ReadinessCheck(
                source.Client!, target.Client!,
                target.Client!.CreatePowerAutomateClient(), target.EnvironmentId);

            var report = await check.RunAsync(solution, components, source.Title, target.Title,
                new Progress<string>(message => Status = message), _cts.Token);

            foreach (var finding in report.Findings
                         .OrderBy(f => f.Severity)
                         .ThenBy(f => f.Area, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(f => f.Component, StringComparer.CurrentCultureIgnoreCase))
            {
                Findings.Add(finding);
            }

            Report = report;
            ReadSummary = Describe(report, components.Count, clock.Elapsed);
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = "The check could not run - " + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void OpenFinding(ReadinessFinding? finding)
    {
        if (finding?.Item is { } item) Source?.OpenDetails(item);
    }

    private void Export()
    {
        if (Report is not { } report) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = $"readiness-{Solution?.UniqueName ?? "solution"}-{Target?.Title ?? "target"}.md".Replace(' ', '-')
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, report.ToMarkdown());
            Status = $"Exported to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not export - " + ex.Message;
        }
    }
}
