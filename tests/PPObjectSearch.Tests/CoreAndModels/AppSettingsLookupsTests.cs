using System.IO;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>Per-environment settings looked up by host, and saving and loading them.</summary>
public sealed class AppSettingsLookupsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppos-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public AppSettingsLookupsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void A_browser_profile_is_kept_per_host_and_cleared_with_null()
    {
        var settings = new AppSettings();
        var profile = new BrowserProfileSetting { Browser = BrowserKind.Chrome, ProfileDirectory = "Profile 3" };

        settings.SetBrowserProfile("contoso.crm11.dynamics.com", profile);

        Assert.Same(profile, settings.GetBrowserProfile("https://CONTOSO.crm11.dynamics.com/main.aspx"));
        Assert.Null(settings.GetBrowserProfile("https://other.crm11.dynamics.com"));

        settings.SetBrowserProfile("https://contoso.crm11.dynamics.com", null);

        Assert.Null(settings.GetBrowserProfile("https://contoso.crm11.dynamics.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    public void An_environment_without_a_host_has_no_profile_and_setting_one_does_nothing(string url)
    {
        var settings = new AppSettings();

        settings.SetBrowserProfile(url, new BrowserProfileSetting());
        settings.SetBrowserProfile(url, null);

        Assert.Null(settings.BrowserProfiles);
        Assert.Null(settings.GetBrowserProfile(url));
    }

    [Fact]
    public void Clearing_a_profile_when_none_are_set_is_harmless()
    {
        var settings = new AppSettings();

        settings.SetBrowserProfile("https://contoso.crm11.dynamics.com", null);

        Assert.Null(settings.BrowserProfiles);
    }

    [Fact]
    public void An_environment_id_is_found_by_host_whatever_its_case_once_loaded()
    {
        File.WriteAllText(FilePath, """
            {
              "EnvironmentIds": { "Contoso.crm11.dynamics.com": "env-1" },
              "BrowserProfiles": { "CONTOSO.crm11.dynamics.com": { "Browser": "Edge", "ProfileDirectory": "Profile 1" } }
            }
            """);

        var settings = AppSettings.Load(FilePath);

        Assert.Equal("env-1", settings.GetEnvironmentId("https://contoso.CRM11.dynamics.com/"));
        Assert.Null(settings.GetEnvironmentId("https://other.crm11.dynamics.com"));
        Assert.Null(settings.GetEnvironmentId(""));
        Assert.Equal("Profile 1", settings.GetBrowserProfile("contoso.crm11.dynamics.com")!.ProfileDirectory);
        Assert.Null(new AppSettings().GetEnvironmentId("https://contoso.crm11.dynamics.com"));
    }

    [Fact]
    public void A_file_holding_null_loads_as_defaults()
    {
        File.WriteAllText(FilePath, "null");

        var settings = AppSettings.Load(FilePath);

        Assert.Null(settings.LoadProblem);
        Assert.Null(settings.EnvironmentIds);
    }

    [Fact]
    public void Tabs_survive_a_save_and_load_and_a_second_save_keeps_a_backup()
    {
        var settings = new AppSettings
        {
            Tabs =
            [
                new TabState
                {
                    EnvironmentUrl = "https://contoso.crm11.dynamics.com", TenantId = "t", AccountId = "a",
                    SolutionUniqueName = "core", EnvironmentType = EnvironmentSku.Production
                }
            ]
        };

        settings.Save(FilePath);
        settings.Save(FilePath);
        var tab = Assert.Single(AppSettings.Load(FilePath).Tabs!);

        Assert.Equal(("t", "a", "core", EnvironmentSku.Production), (tab.TenantId, tab.AccountId, tab.SolutionUniqueName, tab.EnvironmentType));
        Assert.Contains("\"Production\"", File.ReadAllText(FilePath));
        Assert.True(File.Exists(FilePath + ".bak"));
    }

    [Fact]
    public void Settings_that_cannot_be_saved_are_not_an_error()
    {
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var path = Path.Combine(blocker, "settings.json");

        new AppSettings { ClientId = "abc" }.Save(path);

        Assert.False(File.Exists(path));
    }
}
