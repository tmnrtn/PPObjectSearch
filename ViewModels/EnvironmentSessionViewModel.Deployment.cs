using System.Windows;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>The deployment settings file for the solution on screen.</summary>
public sealed partial class EnvironmentSessionViewModel
{
    private const int EnvironmentVariableDefinitionType = 380;

    /// <summary>The loaded list's row for a component, where it is in the list.</summary>
    public SolutionComponentItem? FindLoaded(Guid objectId) => _allItems.Find(i => i.ObjectId == objectId);

    /// <summary>Opens a component's details from another window, such as the readiness check.</summary>
    public void OpenDetails(SolutionComponentItem item, DetailsTab tab = DetailsTab.Default) =>
        ShowDetails(item, tab == DetailsTab.Default ? null : new DetailsShortcut(tab.ToString(), tab));

    // ---------------------------------------------------------------- documentation

    private RelayCommand? _documentSolutionCommand;
    public RelayCommand DocumentSolutionCommand => _documentSolutionCommand ??= new RelayCommand(_ =>
    {
        if (_client is null || !IsConnected || SelectedSolution is null)
        {
            Status = "Choose a solution first - the documentation is of one solution.";
            return;
        }

        new Views.DocumentationWindow { DataContext = new DocumentationViewModel(this), Owner = Application.Current.MainWindow }.ShowDialog();
    });

    // ---------------------------------------------------------------- recent changes

    private RelayCommand? _recentChangesCommand;
    public RelayCommand RecentChangesCommand => _recentChangesCommand ??= new RelayCommand(_ =>
    {
        if (_client is null || !IsConnected) return;
        new Views.ChangesWindow { DataContext = new ChangesViewModel(this), Owner = Application.Current.MainWindow }.Show();
    });

    // ---------------------------------------------------------------- failures

    private RelayCommand? _failuresCommand;
    public RelayCommand FailuresCommand => _failuresCommand ??= new RelayCommand(_ =>
    {
        if (_client is null || !IsConnected) return;
        new Views.FailuresWindow { DataContext = new FailuresViewModel(this), Owner = Application.Current.MainWindow }.Show();
    });

    // ---------------------------------------------------------------- table data

    private RelayCommand? _tableDataCommand;
    public RelayCommand TableDataCommand => _tableDataCommand ??= new RelayCommand(_ => OpenTableData());

    /// <summary>The rows of a table, read-only - optionally one table, from its details window.</summary>
    public void OpenTableData(string? tableLogicalName = null)
    {
        if (_client is null || !IsConnected) return;
        new Views.TableDataWindow
        {
            DataContext = new TableDataViewModel(this, tableLogicalName),
            Owner = Application.Current.MainWindow
        }.Show();
    }

    // ---------------------------------------------------------------- security lookup

    private RelayCommand? _securityLookupCommand;
    public RelayCommand SecurityLookupCommand => _securityLookupCommand ??= new RelayCommand(_ => OpenSecurityLookup());

    private void OpenSecurityLookup()
    {
        if (_client is null || !IsConnected) return;

        // The other connected tabs, for comparing a role across environments.
        var sessions = (Application.Current.MainWindow?.DataContext as ShellViewModel)?.Sessions.AsEnumerable() ?? [this];

        new Views.SecurityLookupWindow
        {
            DataContext = new SecurityLookupViewModel(this, sessions),
            Owner = Application.Current.MainWindow
        }.Show();
    }

    // ---------------------------------------------------------------- content search

    /// <summary>Definitions read for content search, kept for as long as the tab.</summary>
    private readonly DefinitionBodyCache _definitionBodies = new();

    private RelayCommand? _contentSearchCommand;
    public RelayCommand ContentSearchCommand => _contentSearchCommand ??= new RelayCommand(_ => OpenContentSearch());

    /// <summary>"Where is this used?" over the loaded list, optionally with the term filled in.</summary>
    public void OpenContentSearch(string? term = null)
    {
        if (_client is null || !IsConnected)
        {
            Status = "Connect first.";
            return;
        }

        var viewModel = new ContentSearchViewModel(this, _client, _definitionBodies, _allItems.ToList(),
            SelectedSolution?.DisplayLabel, term);

        var window = new Views.ContentSearchWindow { DataContext = viewModel, Owner = Application.Current.MainWindow };
        window.Show();

        if (!string.IsNullOrWhiteSpace(term)) _ = viewModel.SearchCommand.ExecuteAsync(null);
    }

    private AsyncRelayCommand? _exportDeploymentSettingsCommand;
    public AsyncRelayCommand ExportDeploymentSettingsCommand => _exportDeploymentSettingsCommand ??= new AsyncRelayCommand(
        _ => ExportDeploymentSettingsAsync());

    private async Task ExportDeploymentSettingsAsync()
    {
        if (_client is null || !IsConnected || SelectedSolution is null)
        {
            Status = "Choose a solution first - the settings file is for one solution.";
            return;
        }

        var variables = _allItems.Where(i => i.ComponentType == EnvironmentVariableDefinitionType && i.ObjectId != Guid.Empty).ToList();
        var references = _allItems.Where(DataverseClient.IsConnectionReference).ToList();

        if (variables.Count == 0 && references.Count == 0)
        {
            Status = $"{SelectedSolution.DisplayLabel} has no environment variables or connection references, so it needs no settings file.";
            return;
        }

        var client = _client;
        var viewModel = new DeploymentSettingsViewModel(this, SelectedSolution, variables.Count, references.Count, async ct =>
        {
            var variableInfo = new List<EnvironmentVariableInfo>();
            foreach (var variable in variables)
            {
                variableInfo.Add(await client.GetEnvironmentVariableAsync(variable.ObjectId, isValueRecord: false, ct));
            }

            var referenceInfo = await client.GetConnectionReferencesAsync(references.Select(r => r.ObjectId), ct);
            return (variableInfo, referenceInfo);
        });

        await ShowDeploymentSettings(viewModel);

        if (viewModel.SavedPath is { } path)
        {
            Status = $"Wrote the deployment settings to {path}" +
                     (viewModel.HasBlanks ? $" - {viewModel.BlankNames.Count:N0} blank(s) need filling in." : ".");
        }
    }

    /// <summary>Shows the deployment settings dialog until it is closed; replaced in tests.</summary>
    internal Func<DeploymentSettingsViewModel, Task> ShowDeploymentSettings { get; set; } = viewModel =>
    {
        new Views.DeploymentSettingsWindow { DataContext = viewModel, Owner = Application.Current?.MainWindow }.ShowDialog();
        return Task.CompletedTask;
    };
}
