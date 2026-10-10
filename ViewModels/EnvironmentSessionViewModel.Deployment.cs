using System.IO;
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

        var answer = MessageBox.Show(
            $"Fill the settings file with {Title}'s current values?\n\n" +
            "Yes - each variable's current value here and each connection reference's connection here.\n" +
            "No - a template with every value blank.",
            "Deployment settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        var withValues = answer == MessageBoxResult.Yes;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON file (*.json)|*.json",
            FileName = $"{SelectedSolution.UniqueName}-deployment-settings.json"
        };
        if (dialog.ShowDialog() != true) return;

        Status = "Reading environment variables and connection references...";

        try
        {
            var variableInfo = new List<EnvironmentVariableInfo>();
            foreach (var variable in variables)
            {
                variableInfo.Add(await _client.GetEnvironmentVariableAsync(variable.ObjectId, isValueRecord: false, CancellationToken.None));
            }

            var referenceInfo = await _client.GetConnectionReferencesAsync(references.Select(r => r.ObjectId), CancellationToken.None);

            await File.WriteAllTextAsync(dialog.FileName, DeploymentSettings.Build(variableInfo, referenceInfo, withValues), CancellationToken.None);

            var (blankVariables, blankReferences) = DeploymentSettings.Blanks(variableInfo, referenceInfo, withValues);
            Status = $"Wrote {variableInfo.Count:N0} variable(s) and {referenceInfo.Count:N0} connection reference(s) to {dialog.FileName}" +
                     (blankVariables + blankReferences > 0
                         ? $" - {blankVariables:N0} value(s) and {blankReferences:N0} connection(s) are blank and need filling in."
                         : ".");
        }
        catch (Exception ex)
        {
            Status = "Could not write the deployment settings - " + ex.Message;
        }
    }
}
