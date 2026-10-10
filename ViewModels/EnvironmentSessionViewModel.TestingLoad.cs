using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

public sealed partial class EnvironmentSessionViewModel
{
    /// <summary>
    /// Stands in for a completed load: this solution selected and these objects listed, without
    /// reading them or writing the component cache in the user's profile. For tests, after
    /// <see cref="UseConnectedClient"/>.
    /// </summary>
    internal void UseLoadedSolution(SolutionInfo solution, IEnumerable<SolutionComponentItem> items)
    {
        Solutions.Add(solution);
        _selectedSolution = solution;
        OnPropertyChanged(nameof(SelectedSolution));

        _allItems.AddRange(items);
        Items.ReplaceAll(_allItems);
    }
}
