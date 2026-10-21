using System.Collections.ObjectModel;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The deployment settings dialog: which values to write, then - once saved - where the file went,
/// what it holds, and the names still blank that need filling in before importing.
/// </summary>
public sealed class DeploymentSettingsViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<(IReadOnlyList<EnvironmentVariableInfo> Variables, IReadOnlyList<ConnectionReferenceInfo> References)>> _read;

    public DeploymentSettingsViewModel(
        EnvironmentSessionViewModel environment,
        SolutionInfo solution,
        int variableCount,
        int referenceCount,
        Func<CancellationToken, Task<(IReadOnlyList<EnvironmentVariableInfo>, IReadOnlyList<ConnectionReferenceInfo>)>> read)
    {
        Environment = environment;
        Solution = solution;
        VariableCount = variableCount;
        ReferenceCount = referenceCount;
        _read = read;

        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => !IsBusy && !IsSaved);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => IsSaved);
        CopyPathCommand = new RelayCommand(_ => CopyPath(), _ => IsSaved);
    }

    /// <summary>The tab, for the environment line.</summary>
    public EnvironmentSessionViewModel Environment { get; }
    public SolutionInfo Solution { get; }
    public int VariableCount { get; }
    public int ReferenceCount { get; }

    /// <summary>"Case management 1.4.2.0 · 6 environment variables · 4 connection references".</summary>
    public string SolutionLine =>
        $"{Solution.FriendlyName}{(string.IsNullOrWhiteSpace(Solution.Version) ? string.Empty : " " + Solution.Version)}" +
        $" · {Plural(VariableCount, "environment variable")} · {Plural(ReferenceCount, "connection reference")}";

    public string CurrentValuesLabel => $"Current values in {Environment.Title}";

    private bool _withValues = true;
    /// <summary>True: this environment's current values. False: a blank template.</summary>
    public bool WithValues
    {
        get => _withValues;
        set
        {
            if (SetProperty(ref _withValues, value)) OnPropertyChanged(nameof(IsTemplate));
        }
    }

    public bool IsTemplate
    {
        get => !WithValues;
        set => WithValues = !value;
    }

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyPathCommand { get; }

    /// <summary>Where to save, given a suggested file name; null when the user cancels. Replaced in tests.</summary>
    internal Func<string, string?> PickPath { get; set; } = fileName =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON file (*.json)|*.json", FileName = fileName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) SaveCommand.RaiseCanExecuteChanged();
        }
    }

    private string? _savedPath;
    /// <summary>The file written; null until it is.</summary>
    public string? SavedPath
    {
        get => _savedPath;
        private set
        {
            if (!SetProperty(ref _savedPath, value)) return;
            OnPropertyChanged(nameof(IsSaved));
            OnPropertyChanged(nameof(IsChoosing));
            SaveCommand.RaiseCanExecuteChanged();
            OpenFolderCommand.RaiseCanExecuteChanged();
            CopyPathCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSaved => SavedPath is not null;
    public bool IsChoosing => !IsSaved;

    /// <summary>What the file holds and how much of it is blank, a line each.</summary>
    public ObservableCollection<WriteCount> Written { get; } = new();

    /// <summary>The variables and connection references still blank, by name.</summary>
    public ObservableCollection<string> BlankNames { get; } = new();

    public bool HasBlanks => BlankNames.Count > 0;

    public string BlanksHeading => BlankNames.Count == 1
        ? "1 blank to fill in before importing"
        : $"{BlankNames.Count:N0} blanks to fill in before importing";

    private string _status = string.Empty;
    /// <summary>Reading, or why the file could not be written.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private async Task SaveAsync()
    {
        if (PickPath($"{Solution.UniqueName}-deployment-settings.json") is not { } path) return;

        IsBusy = true;
        Status = "Reading environment variables and connection references…";

        try
        {
            var (variables, references) = await _read(CancellationToken.None);
            await File.WriteAllTextAsync(path, DeploymentSettings.Build(variables, references, WithValues), CancellationToken.None);

            var (blankVariables, blankReferences) = DeploymentSettings.Blanks(variables, references, WithValues);
            Written.Clear();
            Written.Add(new WriteCount(variables.Count, $"environment variable{(variables.Count == 1 ? "" : "s")}, {blankVariables:N0} blank"));
            Written.Add(new WriteCount(references.Count, $"connection reference{(references.Count == 1 ? "" : "s")}, {blankReferences:N0} blank"));

            BlankNames.Clear();
            foreach (var name in DeploymentSettings.BlankNames(variables, references, WithValues)) BlankNames.Add(name);
            OnPropertyChanged(nameof(HasBlanks));
            OnPropertyChanged(nameof(BlanksHeading));

            Status = string.Empty;
            SavedPath = path;
        }
        catch (Exception ex)
        {
            Status = "Could not write the deployment settings - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenFolder()
    {
        if (SavedPath is null) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Auth.AppPaths.Explorer, $"/select,\"{SavedPath}\""));
        }
        catch (Exception ex)
        {
            Status = "Could not open the folder - " + ex.Message;
        }
    }

    private void CopyPath()
    {
        if (SavedPath is null) return;

        Status = ClipboardText.TryCopy(SavedPath, out var failure)
            ? "Copied the path."
            : $"Could not copy - the clipboard is held by another application ({failure}).";
    }

    private static string Plural(int n, string word) => $"{n:N0} {word}{(n == 1 ? string.Empty : "s")}";
}
