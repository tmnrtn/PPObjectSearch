using PPObjectSearch.Core;

namespace PPObjectSearch.Tests.CoreAndModels;

public class AppVersionTests
{
    [Fact]
    public void The_sdk_form_splits_into_version_and_short_commit()
    {
        var (version, commit) = AppVersion.Parse("1.9.0+e8da9ee5c1f2a3b4c5d6e7f8091a2b3c4d5e6f70");

        Assert.Equal("1.9.0", version);
        Assert.Equal("e8da9ee", commit);
    }

    [Fact]
    public void A_version_without_metadata_has_no_commit()
    {
        Assert.Equal(("1.9.0", (string?)null), AppVersion.Parse("1.9.0"));
    }

    [Theory]
    [InlineData("1.9.0+local")]      // not a commit id
    [InlineData("1.9.0+abc")]        // too short to be one
    [InlineData("1.9.0+")]
    public void Other_build_metadata_is_left_out(string informational)
    {
        Assert.Equal(("1.9.0", (string?)null), AppVersion.Parse(informational));
    }

    [Fact]
    public void A_prerelease_tag_is_kept_in_the_version()
    {
        Assert.Equal(("2.0.0-beta.1", "abcdef1"), AppVersion.Parse("2.0.0-beta.1+abcdef1234"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abcdef1234")]
    public void Nothing_usable_reads_as_unknown(string? informational)
    {
        Assert.Equal("unknown", AppVersion.Parse(informational).Version);
    }

    [Fact]
    public void The_running_build_reports_the_project_version()
    {
        var expected = typeof(AppVersion).Assembly.GetName().Version!.ToString(3);

        Assert.Equal(expected, AppVersion.Version);
        Assert.Equal($"Version {expected}", AppVersion.Label);
        Assert.StartsWith(AppVersion.Label, AppVersion.Description);
    }
}
