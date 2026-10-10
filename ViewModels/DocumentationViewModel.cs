using System.Diagnostics;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>The solution documentation export: which sections, one file or a file per component, and where.</summary>
public sealed class DocumentationViewModel : ObservableObject
{
    private readonly EnvironmentSessionViewModel _session;
    private CancellationTokenSource? _cts;

    public DocumentationViewModel(EnvironmentSessionViewModel session)
    {
        _session = session;
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => !IsBusy && _session.SelectedSolution is not null);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
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

    public string Title => $"Document {_session.SelectedSolution?.FriendlyName ?? "solution"}";

    /// <summary>The tab the dialog was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;

    /// <summary>The solution being documented - the line under the heading.</summary>
    public string SolutionLabel => _session.SelectedSolution?.DisplayLabel ?? "No solution selected";
    public DocumentationOptions Options { get; } = new();

    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ExportCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
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
        if (_session.Client is not { } client || _session.SelectedSolution is not { } solution) return;

        string? target;
        if (Options.FilePerComponent)
        {
            target = PickFolder() is { } folder ? Path.Combine(folder, DocPage.Slug(solution.UniqueName)) : null;
        }
        else
        {
            target = PickFile($"{solution.UniqueName}-{solution.Version}.md");
        }

        if (target is null) return;

        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        var clock = Stopwatch.StartNew();

        try
        {
            var items = await client.GetSolutionComponentsAsync(solution.SolutionId, ct: cts.Token);
            var pages = await new SolutionDocumenter(client).BuildAsync(
                solution, items, _session.Title, Options, new Progress<string>(m => Status = m), cts.Token);

            var files = SolutionDocumenter.Render(pages, Options.FilePerComponent);

            if (Options.FilePerComponent)
            {
                foreach (var (name, content) in files)
                {
                    var path = Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar));
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
