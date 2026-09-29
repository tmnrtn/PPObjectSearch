using System.Collections.ObjectModel;
using PPObjectSearch.Core;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The shared half of both definition comparisons - a list of changed properties and the
/// before/after panes for whichever one is selected. What the two sides actually are is left to
/// the subclass: two solution layers, or the same component in two environments.
/// </summary>
public abstract class DefinitionDiffViewModel : ObservableObject
{
    protected DefinitionDiffViewModel(string beforeHeading, string afterHeading)
    {
        _beforeHeading = beforeHeading;
        _afterHeading = afterHeading;

        CopyBeforeCommand = new RelayCommand(
            _ => Copy(SelectedChange?.PreviousText),
            _ => !string.IsNullOrEmpty(SelectedChange?.PreviousText));

        CopyAfterCommand = new RelayCommand(
            _ => Copy(SelectedChange?.CurrentText),
            _ => !string.IsNullOrEmpty(SelectedChange?.CurrentText));
    }

    public ObservableCollection<DefinitionChange> Changes { get; } = new();

    /// <summary>
    /// What colours each pane's heading bar: an <see cref="Dataverse.EnvironmentSku"/> for an
    /// environment, or "Beneath" / "Layer" for the two sides of a layer diff.
    /// </summary>
    public virtual object? BeforeMarker => null;
    public virtual object? AfterMarker => null;

    /// <summary>"3 changes", for the tab header.</summary>
    public string ChangeCountLabel => Changes.Count == 1 ? "1 change" : $"{Changes.Count:N0} changes";

    public RelayCommand CopyBeforeCommand { get; }
    public RelayCommand CopyAfterCommand { get; }

    private string _beforeHeading;
    /// <summary>What the left pane is showing - a layer name, or an environment name.</summary>
    public string BeforeHeading
    {
        get => _beforeHeading;
        protected set => SetProperty(ref _beforeHeading, value);
    }

    private string _afterHeading;
    public string AfterHeading
    {
        get => _afterHeading;
        protected set => SetProperty(ref _afterHeading, value);
    }

    private DefinitionChange? _selectedChange;
    /// <summary>The property whose before and after the diff panes are showing.</summary>
    public DefinitionChange? SelectedChange
    {
        get => _selectedChange;
        set
        {
            if (!SetProperty(ref _selectedChange, value)) return;

            CopyBeforeCommand.RaiseCanExecuteChanged();
            CopyAfterCommand.RaiseCanExecuteChanged();
        }
    }

    protected void ReplaceChanges(IEnumerable<DefinitionChange>? changes)
    {
        Changes.Clear();
        foreach (var change in changes ?? Enumerable.Empty<DefinitionChange>()) Changes.Add(change);

        SelectedChange = Changes.FirstOrDefault();
        OnPropertyChanged(nameof(ChangeCountLabel));
    }

    /// <summary>Copying a value whole is what most people want from this window; selecting it in
    /// the pane is for when only part of it is wanted.</summary>
    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process can hold the clipboard open; losing a copy is not worth a dialog.
        }
    }
}
