using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

/// <summary>One table in the picker, with its tick.</summary>
public sealed class EntityPick : ObservableObject
{
    public required EntitySummary Entity { get; init; }

    /// <summary>Already in the configuration - shown, but not addable twice.</summary>
    public required bool IsAlreadyAdded { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) OnPropertyChanged(nameof(IsTicked));
        }
    }

    /// <summary>What the check box shows: a table already in the configuration reads as ticked.</summary>
    public bool IsTicked
    {
        get => IsAlreadyAdded || IsSelected;
        set
        {
            if (!IsAlreadyAdded) IsSelected = value;
        }
    }

    public string Label => Entity.Label;
    public string LogicalName => Entity.LogicalName;
    public string StateLabel => (IsAlreadyAdded, Entity.IsManaged) switch
    {
        (true, _) => "Already added",
        (_, true) => "Managed",
        _ => "Unmanaged"
    };

    /// <summary>The display name alone; the logical name sits beside it in mono.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Entity.DisplayName) ? Entity.LogicalName : Entity.DisplayName;
}

/// <summary>One entry in the solution filter: a solution, or none for every table.</summary>
public sealed record SolutionOption(SolutionInfo? Solution)
{
    public string Label => Solution?.DisplayLabel ?? "Any solution";

    public override string ToString() => Label;
}

/// <summary>The tables of a solution in an environment, by logical name.</summary>
public delegate Task<IReadOnlyCollection<string>> SolutionTablesReader(
    EnvironmentSessionViewModel session, SolutionInfo solution, CancellationToken ct);

/// <summary>
/// Picks tables to add to a comparison. The whole environment's table list is loaded by the caller
/// and filtered here, so typing stays instant on an environment with a thousand tables.
///
/// A solution in either environment can narrow the list to its tables - the ones a release
/// actually carries. The first of the solution environments is the one the list was read from.
/// </summary>
public sealed class EntityPickerViewModel : ObservableObject
{
    private readonly SolutionTablesReader? _readSolutionTables;
    private CancellationTokenSource? _solutionCts;

    /// <summary>The chosen solution's tables; null when no solution narrows the list.</summary>
    private HashSet<string>? _solutionTables;

    public EntityPickerViewModel(
        IEnumerable<EntitySummary> entities,
        IEnumerable<string> alreadyAdded,
        IEnumerable<EnvironmentSessionViewModel>? solutionSessions = null,
        SolutionTablesReader? readSolutionTables = null)
    {
        _readSolutionTables = readSolutionTables;
        SolutionSessions = new ObservableCollection<EnvironmentSessionViewModel>(
            readSolutionTables is null ? [] : (solutionSessions ?? []).Distinct());

        var added = alreadyAdded.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Items = new ObservableCollection<EntityPick>(entities.Select(e => new EntityPick
        {
            Entity = e,
            IsAlreadyAdded = added.Contains(e.LogicalName)
        }));

        // The count in the status bar is the only feedback that a tick scrolled out of sight is
        // still ticked, so it follows every one of them.
        foreach (var item in Items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EntityPick.IsSelected)) NotifySelectionChanged();
            };
        }

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = Filter;

        ClearSelectionCommand = new RelayCommand(_ =>
        {
            foreach (var item in Items) item.IsSelected = false;
            NotifySelectionChanged();
        });

        _solutionSession = SolutionSessions.FirstOrDefault();
        LoadSolutionOptions();
    }

    public ObservableCollection<EntityPick> Items { get; }
    public ICollectionView ItemsView { get; }
    public RelayCommand ClearSelectionCommand { get; }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;

            ItemsView.Refresh();
            NotifySelectionChanged();
        }
    }

    private bool _hideManaged;
    /// <summary>Reference data usually lives in tables this tenant owns, and there are hundreds of
    /// managed ones in the way.</summary>
    public bool HideManaged
    {
        get => _hideManaged;
        set
        {
            if (!SetProperty(ref _hideManaged, value)) return;

            ItemsView.Refresh();
            NotifySelectionChanged();
        }
    }

    // ---------------------------------------------------------------- solution filter

    public ObservableCollection<EnvironmentSessionViewModel> SolutionSessions { get; }

    public ObservableCollection<SolutionOption> SolutionOptions { get; } = new();

    public bool CanFilterBySolution => SolutionSessions.Count > 0;

    private EnvironmentSessionViewModel? _solutionSession;
    /// <summary>The environment whose solutions are offered.</summary>
    public EnvironmentSessionViewModel? SolutionSession
    {
        get => _solutionSession;
        set
        {
            if (!SetProperty(ref _solutionSession, value)) return;
            LoadSolutionOptions();
        }
    }

    private SolutionOption? _selectedSolution;
    public SolutionOption? SelectedSolution
    {
        get => _selectedSolution;
        set
        {
            if (!SetProperty(ref _selectedSolution, value)) return;
            SolutionRead = ApplySolutionAsync(value?.Solution);
        }
    }

    /// <summary>The latest solution's read, for tests to wait on.</summary>
    internal Task SolutionRead { get; private set; } = Task.CompletedTask;

    private bool _isReadingSolution;
    public bool IsReadingSolution
    {
        get => _isReadingSolution;
        private set => SetProperty(ref _isReadingSolution, value);
    }

    private string? _solutionProblem;
    /// <summary>Why the solution could not narrow the list; null when it did, or was not asked to.</summary>
    public string? SolutionProblem
    {
        get => _solutionProblem;
        private set => SetProperty(ref _solutionProblem, value);
    }

    /// <summary>
    /// Opens on a solution picked before - by unique name, since the same solution has the same
    /// name in every environment. Nothing happens when that environment or solution is gone.
    /// </summary>
    public void SelectSolution(EnvironmentSessionViewModel? session, string? uniqueName)
    {
        if (session is null || string.IsNullOrWhiteSpace(uniqueName) || !SolutionSessions.Contains(session)) return;

        SolutionSession = session;
        SelectedSolution = SolutionOptions.FirstOrDefault(o =>
            string.Equals(o.Solution?.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase)) ?? SelectedSolution;
    }

    /// <summary>Every solution but the default one, which holds every table anyway.</summary>
    private void LoadSolutionOptions()
    {
        SolutionOptions.Clear();

        if (SolutionSession is { } session)
        {
            SolutionOptions.Add(new SolutionOption(null));
            foreach (var solution in session.Solutions.Where(s => !s.IsDefaultSolution)
                         .OrderBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
            {
                SolutionOptions.Add(new SolutionOption(solution));
            }
        }

        SelectedSolution = SolutionOptions.FirstOrDefault();
    }

    private async Task ApplySolutionAsync(SolutionInfo? solution)
    {
        // A slow read for the solution picked before must not land over this one.
        _solutionCts?.Cancel();
        _solutionCts?.Dispose();
        _solutionCts = null;

        _solutionTables = null;
        SolutionProblem = null;

        if (solution is not null && SolutionSession is { } session && _readSolutionTables is { } read)
        {
            var cts = _solutionCts = new CancellationTokenSource();
            IsReadingSolution = true;
            RefreshFilter();

            try
            {
                var tables = await read(session, solution, cts.Token);
                if (cts.IsCancellationRequested) return;

                _solutionTables = tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The list stays whole: better every table than none.
                SolutionProblem = $"The tables in {solution.FriendlyName} could not be read - {ex.Message}";
            }
        }

        IsReadingSolution = false;
        RefreshFilter();
    }

    private void RefreshFilter()
    {
        ItemsView.Refresh();
        NotifySelectionChanged();
    }

    /// <summary>"3 selected · 412 managed tables hidden".</summary>
    public string Summary
    {
        get
        {
            var chosen = Items.Count(i => i is { IsSelected: true, IsAlreadyAdded: false });
            var parts = new List<string> { $"{chosen:N0} selected" };

            if (HideManaged)
            {
                var hidden = Items.Count(i => i.Entity.IsManaged && !i.IsSelected);
                parts.Add($"{hidden:N0} managed tables hidden");
            }
            else
            {
                parts.Add($"{ItemsView.Cast<EntityPick>().Count():N0} of {Items.Count:N0} tables shown");
            }

            if (IsReadingSolution)
            {
                parts.Add($"reading {SelectedSolution?.Solution?.FriendlyName}...");
            }
            else if (_solutionTables is not null && SelectedSolution?.Solution is { } solution)
            {
                // A solution read in the target can hold tables the source has never had.
                var listed = Items.Select(i => i.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = _solutionTables.Count(t => !listed.Contains(t));
                if (missing > 0)
                {
                    parts.Add($"{missing:N0} of the tables in {solution.FriendlyName} not in {SolutionSessions[0].Title}");
                }
            }

            return string.Join(" · ", parts);
        }
    }

    public int SelectedCount => Items.Count(i => i is { IsSelected: true, IsAlreadyAdded: false });

    public bool HasSelection => SelectedCount > 0;

    public string AddLabel => SelectedCount switch
    {
        0 => "Add tables",
        1 => "Add 1 table",
        var n => $"Add {n:N0} tables"
    };

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AddLabel));
    }

    public IReadOnlyList<EntitySummary> SelectedEntities() =>
        Items.Where(i => i is { IsSelected: true, IsAlreadyAdded: false }).Select(i => i.Entity).ToList();

    private bool Filter(object obj)
    {
        if (obj is not EntityPick pick) return false;

        if (HideManaged && pick.Entity.IsManaged && !pick.IsSelected) return false;

        // Nothing shows while the solution is read, rather than every table for a moment.
        if (IsReadingSolution && !pick.IsSelected) return false;
        if (_solutionTables is not null && !_solutionTables.Contains(pick.LogicalName) && !pick.IsSelected) return false;

        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        return pick.LogicalName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               (pick.Entity.DisplayName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
