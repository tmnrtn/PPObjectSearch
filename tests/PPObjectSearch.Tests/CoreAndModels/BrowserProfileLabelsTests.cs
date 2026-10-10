using System.IO;
using System.Windows;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>Browser profile labels and matching, finding installed browsers, and closing details windows.</summary>
public class BrowserProfileLabelsTests
{
    [Fact]
    public void An_account_that_is_the_profiles_name_is_not_repeated()
    {
        var profile = new BrowserProfile(BrowserKind.Chrome, "Profile 1", "tom@contoso.com", "TOM@contoso.com");

        Assert.Equal("Chrome · tom@contoso.com", profile.Label);
        Assert.Equal("Chrome", profile.BrowserName);
    }

    [Fact]
    public void A_profile_without_any_name_is_named_by_its_folder_and_a_non_text_account_is_ignored()
    {
        var profiles = BrowserProfiles.ParseLocalState(BrowserKind.Edge, """
            { "profile": { "info_cache": { "Profile 7": { "name": "", "shortcut_name": 3, "user_name": 12 } } } }
            """);

        var profile = Assert.Single(profiles);
        Assert.Equal("Profile 7", profile.Name);
        Assert.Null(profile.Account);
        Assert.Equal("Edge · Profile 7", profile.Label);
    }

    [Theory]
    [InlineData(BrowserKind.Edge, "profile 2", true)]
    [InlineData(BrowserKind.Chrome, "Profile 2", false)]
    [InlineData(BrowserKind.Edge, "Default", false)]
    public void A_saved_setting_matches_its_browser_and_folder(BrowserKind browser, string directory, bool expected)
    {
        var setting = new BrowserProfileSetting { Browser = BrowserKind.Edge, ProfileDirectory = "Profile 2" };

        Assert.Equal(expected, setting.Matches(new BrowserProfile(browser, directory, "Work", null)));
    }

    [Fact]
    public void A_new_setting_points_at_the_default_profile()
    {
        Assert.Equal("Default", new BrowserProfileSetting().ProfileDirectory);
    }

    [Theory]
    [InlineData(BrowserKind.Edge)]
    [InlineData(BrowserKind.Chrome)]
    public void An_installed_browser_is_an_existing_program_or_none(BrowserKind browser)
    {
        var path = BrowserProfiles.ExecutablePath(browser);

        Assert.True(path is null || File.Exists(path));
    }

    [Fact]
    public void Closing_every_details_window_when_none_are_open_does_nothing()
    {
        DetailsWindows.CloseAll();

        Assert.Equal(0, DetailsWindows.Count);
    }

    [Fact]
    public void A_previous_window_with_no_position_yet_sends_the_next_to_the_work_areas_corner()
    {
        var (left, top) = DetailsWindows.Cascade(new Rect(double.NaN, double.NaN, 100, 100), new Size(200, 200), new Rect(0, 0, 1000, 800));

        Assert.Equal((DetailsWindows.CascadeStep, DetailsWindows.CascadeStep), (left, top));
    }
}
