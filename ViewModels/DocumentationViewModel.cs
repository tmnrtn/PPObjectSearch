using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>A solution in the documentation dialog's list, ticked to be documented.</summary>
public sealed class DocumentedSolution : ObservableObject
{
    public DocumentedSolution(SolutionInfo solution) => Solution = solution;

    public SolutionInfo Solution { get; }

    /// <summary>The unique name and version beside the friendly name - and that it is a patch, if it is.</summary>
    public string Detail =>
        $"{Solution.UniqueName}{(Solution.Version is { } v ? "  " + v : string.Empty)}{(Solution.ParentSolutionId is null ? string.Empty : "  (patch)")}";

    private bool _isChosen;
    public bool IsChosen
    {
        get => _isChosen;
        set => SetProperty(ref _isChosen, value);
    }
}

/// <summary>
/// The solution documentation export: which solutions, which sections, one file or a file per
/// component, and where.
/// </summary>
public sealed class DocumentationViewModel : ObservableObject, IDisposable
{
    private readonly EnvironmentSessionViewModel _session;
    private CancellationTokenSource? _cts;

    /// <summary>Stops documenting still running when the window closes; nobody is left to read the answer.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public DocumentationViewModel(EnvironmentSessionViewModel session)
    {
        _session = session;
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => !IsBusy && Chosen.Count > 0);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ClearCommand = new RelayCommand(_ =>
        {
            foreach (var choice in Choices) choice.IsChosen = false;
        }, _ => !IsBusy && Choices.Any(c => c.IsChosen));

        // The solution on screen, ticked - whether or not the list holds it yet.
        var solutions = session.Solutions.ToList();
        if (session.SelectedSolution is { } selected && !solutions.Contains(selected)) solutions.Insert(0, selected);

        foreach (var solution in solutions)
        {
            var choice = new DocumentedSolution(solution) { IsChosen = ReferenceEquals(solution, session.SelectedSolution) };
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DocumentedSolution.IsChosen)) ChoicesChanged();
            };
            Choices.Add(choice);
        }

        ChoicesView = CollectionViewSource.GetDefaultView(Choices);
        ChoicesView.Filter = o => o is DocumentedSolution c && Matches(c, _solutionFilter);
    }

    /// <summary>Asks for the folder to write a file per component under; null when cancelled. Replaced in tests.</summary>
    internal Func<string?> PickFolder { get; set; } = () =>
    {
        var folder = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for the documentation" };
        return folder.ShowDialog() == true ? folder.FolderName : null;
    };

    /// <summary>Asks where to save the one file, suggesting a name; null when cancelled. Replaced in tests.</summary>
    internal Func<string, string?> PickFile { get; set; } = suggested =>
    {
        var file = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = suggested };
        return file.ShowDialog() == true ? file.FileName : null;
    };

    /// <summary>The tab the dialog was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;

    /// <summary>Every solution in the environment, to tick the ones to document.</summary>
    public ObservableCollection<DocumentedSolution> Choices { get; } = new();

    public ICollectionView ChoicesView { get; }

    private string _solutionFilter = string.Empty;
    /// <summary>Narrows the list by friendly or unique name; ticked solutions stay ticked when hidden.</summary>
    public string SolutionFilter
    {
        get => _solutionFilter;
        set
        {
            if (SetProperty(ref _solutionFilter, value ?? string.Empty)) ChoicesView.Refresh();
        }
    }

    private static bool Matches(DocumentedSolution choice, string filter) =>
        filter.Length == 0 ||
        choice.Solution.FriendlyName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        choice.Solution.UniqueName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private bool _includePatches;
    /// <summary>Documents each ticked solution's patches with it.</summary>
    public bool IncludePatches
    {
        get => _includePatches;
        set
        {
            if (SetProperty(ref _includePatches, value)) ChoicesChanged();
        }
    }

    /// <summary>The solutions to document: those ticked, then - if asked for - their patches, each once.</summary>
    public IReadOnlyList<SolutionInfo> Chosen
    {
        get
        {
            var chosen = Choices.Where(c => c.IsChosen).Select(c => c.Solution).ToList();
            if (!IncludePatches) return chosen;

            var ids = chosen.Select(s => s.SolutionId).ToHashSet();
            var patches = Choices.Select(c => c.Solution)
                .Where(s => s.ParentSolutionId is { } parent && ids.Contains(parent) && !ids.Contains(s.SolutionId));
            return [.. chosen, .. patches];
        }
    }

    private void ChoicesChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SolutionLabel));
        ExportCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    public string Title => Chosen switch
    {
        [] => "Document solution",
        [var one] => $"Document {one.FriendlyName}",
        var many => $"Document {many.Count:N0} solutions"
    };

    /// <summary>What is being documented - the line under the heading.</summary>
    public string SolutionLabel => Chosen switch
    {
        [] => "No solution selected",
        [var one] => one.DisplayLabel,
        var many => $"{many.Count:N0} solutions: {string.Join(", ", many.Select(s => s.FriendlyName))}"
    };

    public DocumentationOptions Options { get; } = new();

    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ExportCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = "Markdown, readable on GitHub, Azure DevOps or any wiki. Flows include Mermaid diagrams.";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private async Task ExportAsync()
    {
        var solutions = Chosen;
        if (_session.Client is not { } client || solutions.Count == 0) return;

        // One solution is named for itself; several for the environment and the day.
        var name = solutions.Count == 1
            ? solutions[0].UniqueName
            : $"{DocPage.Slug(_session.Title)}-solutions-{DateTime.Now:yyyy-MM-dd}";

        string? target;
        if (Options.FilePerComponent)
        {
            target = PickFolder() is { } folder ? Path.Combine(folder, DocPage.Slug(name)) : null;
        }
        else
        {
            target = PickFile(solutions.Count == 1 ? $"{name}-{solutions[0].Version}.md" : $"{name}.md");
        }

        if (target is null) return;

        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        var clock = Stopwatch.StartNew();

        try
        {
            var components = new Dictionary<Guid, IReadOnlyList<SolutionComponentItem>>();
            for (var i = 0; i < solutions.Count; i++)
            {
                if (solutions.Count > 1) Status = $"Reading the components of {solutions[i].FriendlyName} ({i + 1:N0} of {solutions.Count:N0})...";
                components[solutions[i].SolutionId] = await client.GetSolutionComponentsAsync(solutions[i].SolutionId, ct: cts.Token);
            }

            var pages = await new SolutionDocumenter(client).BuildAsync(
                new DocumentationScope(solutions, components), _session.Title, Options, new Progress<string>(m => Status = m), cts.Token);

            var files = SolutionDocumenter.Render(pages, Options.FilePerComponent);

            if (Options.FilePerComponent)
            {
                foreach (var (file, content) in files)
                {
                    var path = Path.Combine(target, file.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, content, cts.Token);
                }

                Status = $"Wrote {files.Count:N0} file(s) to {target} in {clock.Elapsed.TotalSeconds:0.0} s.";
            }
            else
            {
                await File.WriteAllTextAsync(target, files["README.md"], cts.Token);
                Status = $"Wrote {pages.Count - 1:N0} section page(s) to {target} in {clock.Elapsed.TotalSeconds:0.0} s.";
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = "Could not write the documentation - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            IsBusy = false;
        }
    }
}
