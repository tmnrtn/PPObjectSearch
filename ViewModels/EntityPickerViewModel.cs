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

/// <summary>
/// Picks tables to add to a comparison. The whole environment's table list is loaded by the caller
/// and filtered here, so typing stays instant on an environment with a thousand tables.
/// </summary>
public sealed class EntityPickerViewModel : ObservableObject
{
    public EntityPickerViewModel(IEnumerable<EntitySummary> entities, IEnumerable<string> alreadyAdded)
    {
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

        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        return pick.LogicalName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               (pick.Entity.DisplayName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
