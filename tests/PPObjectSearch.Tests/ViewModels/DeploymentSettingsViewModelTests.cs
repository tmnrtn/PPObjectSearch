using System.IO;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The deployment settings dialog: choosing the values, saving, and what it says is still blank.</summary>
public sealed class DeploymentSettingsViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ppos-deployment-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* a temp folder left behind is harmless */ }
    }

    private static readonly SolutionInfo Solution = new()
    {
        SolutionId = Guid.NewGuid(), UniqueName = "casemgmt", FriendlyName = "Case management", Version = "1.4.2.0"
    };

    private static IReadOnlyList<EnvironmentVariableInfo> Variables() =>
    [
        new() { SchemaName = "contoso_ErpBaseUrl", TypeLabel = "Text" },
        new() { SchemaName = "contoso_Region", TypeLabel = "Text", CurrentValue = "emea", HasCurrentValue = true }
    ];

    private static IReadOnlyList<ConnectionReferenceInfo> References() =>
    [
        new() { Id = Guid.NewGuid(), LogicalName = "contoso_sharedteams", ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_teams" }
    ];

    private DeploymentSettingsViewModel Dialog(string? pick = "settings.json") =>
        new(DetailsHarness.Session(), Solution, 2, 1, _ => Task.FromResult((Variables(), References())))
        {
            PickPath = _ => pick is null ? null : Path.Combine(Directory.CreateDirectory(_folder).FullName, pick)
        };

    [Fact]
    public void It_offers_this_environments_values_first_and_names_what_it_covers()
    {
        var dialog = Dialog();

        Assert.True(dialog.WithValues);
        Assert.Equal("Case management 1.4.2.0 · 2 environment variables · 1 connection reference", dialog.SolutionLine);
        Assert.True(dialog.IsChoosing);
        Assert.False(dialog.OpenFolderCommand.CanExecute(null));

        dialog.IsTemplate = true;
        Assert.False(dialog.WithValues);
    }

    [Fact]
    public async Task Saving_says_where_it_went_and_which_names_are_still_blank()
    {
        var dialog = Dialog();

        await dialog.SaveCommand.ExecuteAsync(null);

        Assert.True(dialog.IsSaved);
        Assert.True(File.Exists(dialog.SavedPath));
        Assert.Equal([(2, "environment variables, 1 blank"), (1, "connection reference, 1 blank")], dialog.Written.Select(w => (w.Count, w.Label)));
        Assert.Equal(["contoso_ErpBaseUrl", "contoso_sharedteams"], dialog.BlankNames);
        Assert.Equal("2 blanks to fill in before importing", dialog.BlanksHeading);
        Assert.False(dialog.SaveCommand.CanExecute(null));
        Assert.True(dialog.CopyPathCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_cancelled_save_dialog_writes_nothing()
    {
        var dialog = Dialog(pick: null);

        await dialog.SaveCommand.ExecuteAsync(null);

        Assert.False(dialog.IsSaved);
        Assert.Empty(dialog.Written);
    }

    [Fact]
    public void A_template_leaves_every_name_blank()
    {
        Assert.Equal(["contoso_ErpBaseUrl", "contoso_Region", "contoso_sharedteams"],
            DeploymentSettings.BlankNames(Variables(), References(), withValues: false));
    }
}
