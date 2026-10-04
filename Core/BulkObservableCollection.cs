using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PPObjectSearch.Core;

/// <summary>
/// An observable collection whose whole contents can be swapped with one notification.
///
/// Filling an ObservableCollection item by item raises a change per item, and a filtered view over
/// it runs its filter and tells its grid each time - for a default solution of tens of thousands
/// of objects, that is tens of thousands of round trips through the UI thread. One Reset costs one.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> items) : base(items)
    {
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
