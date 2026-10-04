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

    /// <summary>Opens a component's details from another window, such as the readiness check.</summary>
    public void OpenDetails(SolutionComponentItem item) => ShowDetails(item);

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
                variableInfo.Add(await _client.GetEnvironmentVariableAsync(variable.ObjectId, isValueRecord: false));
            }

            var referenceInfo = await _client.GetConnectionReferencesAsync(references.Select(r => r.ObjectId));

            await File.WriteAllTextAsync(dialog.FileName, DeploymentSettings.Build(variableInfo, referenceInfo, withValues));

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
