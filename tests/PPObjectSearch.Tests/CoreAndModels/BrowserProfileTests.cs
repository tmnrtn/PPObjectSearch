using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>Opening an environment's links and sign-ins in a chosen browser profile.</summary>
public class BrowserProfileTests
{
    private const string LocalState = """
        { "profile": { "info_cache": {
            "Profile 2": { "name": "Client work", "user_name": "tom@client.example" },
            "Default":   { "name": "Personal", "user_name": "" },
            "Profile 1": { "shortcut_name": "Kerv", "user_name": "tom@kerv.example" }
        } } }
        """;

    // ---------------------------------------------------------------- reading profiles

    [Fact]
    public void Profiles_are_read_from_local_state_default_first()
    {
        var profiles = BrowserProfiles.ParseLocalState(BrowserKind.Edge, LocalState);

        Assert.Equal(["Default", "Profile 1", "Profile 2"], profiles.Select(p => p.Directory));
        Assert.All(profiles, p => Assert.Equal(BrowserKind.Edge, p.Browser));
    }

    [Fact]
    public void A_profile_is_labelled_by_browser_name_and_account()
    {
        var profiles = BrowserProfiles.ParseLocalState(BrowserKind.Edge, LocalState);

        Assert.Equal("Edge · Personal", profiles[0].Label);              // no account
        Assert.Equal("Edge · Kerv (tom@kerv.example)", profiles[1].Label); // name from shortcut_name
        Assert.Equal("Edge · Client work (tom@client.example)", profiles[2].Label);
    }

    [Fact]
    public void Chrome_profiles_are_labelled_chrome()
    {
        var profile = BrowserProfiles.ParseLocalState(BrowserKind.Chrome, LocalState)[0];

        Assert.Equal("Chrome · Personal", profile.Label);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "profile": {} }""")]
    [InlineData("""{ "profile": { "info_cache": [] } }""")]
    public void A_local_state_without_profiles_offers_none(string json) =>
        Assert.Empty(BrowserProfiles.ParseLocalState(BrowserKind.Chrome, json));

    // ---------------------------------------------------------------- launching

    [Fact]
    public void A_link_opens_in_the_profile_with_the_link_as_its_own_argument()
    {
        var info = LinkLauncher.StartInfo("https://make.powerapps.com/environments/x",
            new BrowserProfileSetting { Browser = BrowserKind.Edge, ProfileDirectory = "Profile 2" }, @"C:\Edge\msedge.exe");

        Assert.Equal(@"C:\Edge\msedge.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["--profile-directory=Profile 2", "https://make.powerapps.com/environments/x"], info.ArgumentList);
    }

    [Fact]
    public void Without_a_profile_the_default_browser_opens_the_link()
    {
        var info = LinkLauncher.StartInfo("https://make.powerapps.com/", null, null);

        Assert.True(info.UseShellExecute);
        Assert.Equal("https://make.powerapps.com/", info.FileName);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void A_profile_whose_browser_is_gone_falls_back_to_the_default_browser()
    {
        var info = LinkLauncher.StartInfo("https://make.powerapps.com/",
            new BrowserProfileSetting { Browser = BrowserKind.Chrome }, browserPath: null);

        Assert.True(info.UseShellExecute);
    }

    [Theory]
    [InlineData("--disable-web-security")]          // would otherwise read as a browser switch
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Only_web_links_are_ever_opened(string url)
    {
        Assert.Throws<ArgumentException>(() => LinkLauncher.StartInfo(url,
            new BrowserProfileSetting { Browser = BrowserKind.Edge }, @"C:\Edge\msedge.exe"));
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void A_profile_is_saved_per_environment_host()
    {
        var settings = new AppSettings();

        settings.SetBrowserProfile("https://contoso.crm11.dynamics.com/", new BrowserProfileSetting { Browser = BrowserKind.Chrome, ProfileDirectory = "Profile 1" });

        var read = settings.GetBrowserProfile("CONTOSO.crm11.dynamics.com");
        Assert.NotNull(read);
        Assert.Equal(BrowserKind.Chrome, read.Browser);
        Assert.Equal("Profile 1", read.ProfileDirectory);
        Assert.Null(settings.GetBrowserProfile("https://other.crm11.dynamics.com"));
    }

    [Fact]
    public void Choosing_the_default_browser_clears_the_entry()
    {
        var settings = new AppSettings();
        settings.SetBrowserProfile("https://contoso.crm11.dynamics.com", new BrowserProfileSetting());

        settings.SetBrowserProfile("https://contoso.crm11.dynamics.com", null);

        Assert.Null(settings.GetBrowserProfile("https://contoso.crm11.dynamics.com"));
    }

    [Fact]
    public void An_environment_without_a_url_has_no_profile() =>
        Assert.Null(new AppSettings().GetBrowserProfile(""));

    // ---------------------------------------------------------------- the menu

    [Fact]
    public void The_menu_offers_the_default_browser_then_every_profile_with_the_current_one_ticked()
    {
        var found = BrowserProfiles.ParseLocalState(BrowserKind.Edge, LocalState);
        var current = new BrowserProfileSetting { Browser = BrowserKind.Edge, ProfileDirectory = "Profile 1" };

        var options = BrowserProfileOption.Build(found, current);

        Assert.Equal("Default browser", options[0].Label);
        Assert.Null(options[0].Setting);
        Assert.Equal(4, options.Count);
        Assert.Equal(["Edge · Kerv (tom@kerv.example)"], options.Where(o => o.IsSelected).Select(o => o.Label));
    }

    [Fact]
    public void With_no_choice_the_default_browser_is_ticked()
    {
        var options = BrowserProfileOption.Build(BrowserProfiles.ParseLocalState(BrowserKind.Edge, LocalState), null);

        Assert.True(options[0].IsSelected);
        Assert.Single(options, o => o.IsSelected);
    }

    [Fact]
    public void A_saved_profile_no_longer_on_this_machine_is_still_shown_and_marked()
    {
        var current = new BrowserProfileSetting { Browser = BrowserKind.Chrome, ProfileDirectory = "Profile 9" };

        var options = BrowserProfileOption.Build(BrowserProfiles.ParseLocalState(BrowserKind.Edge, LocalState), current);

        var missing = options.Last();
        Assert.True(missing.IsSelected);
        Assert.Contains("not found", missing.Label);
    }

    [Fact]
    public void Each_option_saves_its_own_profile()
    {
        var options = BrowserProfileOption.Build(BrowserProfiles.ParseLocalState(BrowserKind.Edge, LocalState), null);

        var clientWork = options.Single(o => o.Label.StartsWith("Edge · Client work"));
        Assert.Equal("Profile 2", clientWork.Setting!.ProfileDirectory);
        Assert.Equal(BrowserKind.Edge, clientWork.Setting.Browser);
    }
}
