using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.Admin;

/// <summary>
/// The rule between the app and production. Every write path - reference data reconcile, team and
/// queue membership - goes through it, so it is tested on its own.
/// </summary>
public class WriteGuardTests
{
    private const string Url = "https://contoso.crm11.dynamics.com";

    private static EnvironmentTypeInfo Type(EnvironmentSku sku, string? detail = null) => new(sku, null, detail);

    [Theory]
    [InlineData(EnvironmentSku.Sandbox)]
    [InlineData(EnvironmentSku.Developer)]
    [InlineData(EnvironmentSku.Trial)]
    [InlineData(EnvironmentSku.Teams)]
    public void Non_production_environments_are_writable_without_configuration(EnvironmentSku sku)
    {
        var permission = WriteGuard.Evaluate(new AppSettings(), Url, Type(sku));

        Assert.True(permission.Allowed);
        Assert.False(permission.IsAllowlisted);
    }

    [Theory]
    [InlineData(EnvironmentSku.Production)]
    [InlineData(EnvironmentSku.Default)]
    [InlineData(EnvironmentSku.Unknown)]
    public void Protected_environments_are_refused_by_default(EnvironmentSku sku)
    {
        var permission = WriteGuard.Evaluate(new AppSettings(), Url, Type(sku));

        Assert.False(permission.Allowed);
        Assert.Contains("AllowProductionWrites", permission.Reason);
        Assert.Contains(Url, permission.Reason);
    }

    [Fact]
    public void An_unknown_type_explains_why_it_is_unknown()
    {
        var permission = WriteGuard.Evaluate(new AppSettings(), Url, Type(EnvironmentSku.Unknown, "No Power Platform API token."));

        Assert.Contains("No Power Platform API token.", permission.Reason);
    }

    [Theory]
    [InlineData("https://contoso.crm11.dynamics.com")]
    [InlineData("https://contoso.crm11.dynamics.com/")]
    [InlineData("http://CONTOSO.crm11.dynamics.com")]
    [InlineData("contoso.crm11.dynamics.com")]
    [InlineData("  contoso.crm11.dynamics.com  ")]
    public void An_allowlisted_production_environment_is_writable(string entry)
    {
        var settings = new AppSettings { AllowProductionWrites = [entry] };

        var permission = WriteGuard.Evaluate(settings, Url, Type(EnvironmentSku.Production));

        Assert.True(permission.Allowed);
        Assert.True(permission.IsAllowlisted);
    }

    [Theory]
    [InlineData("*.crm11.dynamics.com")]
    [InlineData("crm11.dynamics.com")]
    [InlineData("https://contoso2.crm11.dynamics.com")]
    [InlineData("https://other.crm11.dynamics.com")]
    [InlineData("")]
    [InlineData("   ")]
    public void The_allowlist_matches_whole_hosts_only(string entry)
    {
        var settings = new AppSettings { AllowProductionWrites = [entry] };

        Assert.False(WriteGuard.Evaluate(settings, Url, Type(EnvironmentSku.Production)).Allowed);
    }

    [Fact]
    public void Allowlisting_one_environment_does_not_clear_another()
    {
        var settings = new AppSettings { AllowProductionWrites = ["https://other.crm11.dynamics.com"] };

        Assert.False(WriteGuard.Evaluate(settings, Url, Type(EnvironmentSku.Production)).Allowed);
        Assert.True(WriteGuard.Evaluate(settings, "https://other.crm11.dynamics.com", Type(EnvironmentSku.Production)).Allowed);
    }

    [Fact]
    public void The_allowlist_also_clears_default_and_unknown_environments()
    {
        var settings = new AppSettings { AllowProductionWrites = [Url] };

        Assert.True(WriteGuard.Evaluate(settings, Url, Type(EnvironmentSku.Default)).Allowed);
        Assert.True(WriteGuard.Evaluate(settings, Url, Type(EnvironmentSku.Unknown)).Allowed);
    }

    // ---------------------------------------------------------------- allowing from the UI

    [Fact]
    public void Allow_adds_exactly_this_host_and_lifts_the_guard()
    {
        var settings = new AppSettings();

        Assert.True(WriteGuard.Allow(settings, "https://Contoso.crm11.dynamics.com/main.aspx?x=1"));

        Assert.Equal(["https://contoso.crm11.dynamics.com"], settings.AllowProductionWrites);
        Assert.True(WriteGuard.Evaluate(settings, Url, Type(EnvironmentSku.Production)).Allowed);
    }

    [Fact]
    public void Allow_does_not_duplicate_an_entry_however_it_was_written()
    {
        var settings = new AppSettings { AllowProductionWrites = ["contoso.crm11.dynamics.com/"] };

        Assert.False(WriteGuard.Allow(settings, Url));
        Assert.Single(settings.AllowProductionWrites);
    }

    [Fact]
    public void Allow_leaves_other_entries_and_other_environments_alone()
    {
        var settings = new AppSettings { AllowProductionWrites = ["https://other.crm11.dynamics.com"] };

        WriteGuard.Allow(settings, Url);

        Assert.Equal(2, settings.AllowProductionWrites!.Count);
        Assert.False(WriteGuard.Evaluate(settings, "https://third.crm11.dynamics.com", Type(EnvironmentSku.Production)).Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Allow_refuses_a_url_with_no_host(string url)
    {
        var settings = new AppSettings();

        Assert.False(WriteGuard.Allow(settings, url));
        Assert.Null(settings.AllowProductionWrites);
    }

    [Fact]
    public void Revoke_removes_every_spelling_of_the_host_and_restores_the_guard()
    {
        var settings = new AppSettings
        {
            AllowProductionWrites = ["https://contoso.crm11.dynamics.com", "CONTOSO.crm11.dynamics.com/", "https://other.crm11.dynamics.com"]
        };

        Assert.True(WriteGuard.Revoke(settings, Url));

        Assert.Equal(["https://other.crm11.dynamics.com"], settings.AllowProductionWrites);
        Assert.False(WriteGuard.Evaluate(settings, Url, Type(EnvironmentSku.Production)).Allowed);
    }

    [Fact]
    public void Revoke_of_an_environment_not_listed_changes_nothing()
    {
        var settings = new AppSettings { AllowProductionWrites = ["https://other.crm11.dynamics.com"] };

        Assert.False(WriteGuard.Revoke(settings, Url));
        Assert.False(WriteGuard.Revoke(new AppSettings(), Url));
        Assert.Single(settings.AllowProductionWrites);
    }

    [Fact]
    public void IsAllowlisted_follows_allow_and_revoke()
    {
        var settings = new AppSettings();

        Assert.False(WriteGuard.IsAllowlisted(settings, Url));
        WriteGuard.Allow(settings, Url);
        Assert.True(WriteGuard.IsAllowlisted(settings, Url));
        WriteGuard.Revoke(settings, Url);
        Assert.False(WriteGuard.IsAllowlisted(settings, Url));
    }

    [Fact]
    public void The_permission_carries_the_type_it_was_judged_on()
    {
        var type = Type(EnvironmentSku.Sandbox);

        Assert.Same(type, WriteGuard.Evaluate(new AppSettings(), Url, type).Type);
    }
}
