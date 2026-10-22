using System.IO;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.AppInsights;

/// <summary>The App Insights resource chosen per environment, as settings.json keeps it.</summary>
public sealed class TelemetrySettingsTests : IDisposable
{
    private const string Url = "https://contoso.crm11.dynamics.com";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppos-telemetry-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public TelemetrySettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static TelemetryBinding Binding(string name = "contoso-dataverse") =>
        new() { Name = name, AppId = "app-" + name, ResourceId = "/subscriptions/s/resourceGroups/rg/providers/microsoft.insights/components/" + name };

    [Fact]
    public void A_resource_is_kept_per_host_and_kind_and_cleared_with_null()
    {
        var settings = new AppSettings();
        settings.SetTelemetryBinding(Url, TelemetryKind.DataverseExport, Binding());

        Assert.Equal("contoso-dataverse", settings.GetTelemetryBinding("https://CONTOSO.crm11.dynamics.com/main.aspx", TelemetryKind.DataverseExport)?.Name);
        Assert.Null(settings.GetTelemetryBinding(Url, TelemetryKind.PowerAutomateExport));
        Assert.Null(settings.GetTelemetryBinding("https://other.crm11.dynamics.com", TelemetryKind.DataverseExport));

        settings.SetTelemetryBinding(Url, TelemetryKind.DataverseExport, null);

        Assert.Null(settings.GetTelemetryBinding(Url, TelemetryKind.DataverseExport));
        Assert.Null(settings.AppInsights);
    }

    [Fact]
    public void Choosing_again_replaces_only_that_kind()
    {
        var settings = new AppSettings();
        settings.SetTelemetryBinding(Url, TelemetryKind.DataverseExport, Binding("first"));
        settings.SetTelemetryBinding(Url, TelemetryKind.PowerAutomateExport, Binding("flows"));

        settings.SetTelemetryBinding(Url, TelemetryKind.DataverseExport, Binding("second"));

        Assert.Equal("second", settings.GetTelemetryBinding(Url, TelemetryKind.DataverseExport)?.Name);
        Assert.Equal("flows", settings.GetTelemetryBinding(Url, TelemetryKind.PowerAutomateExport)?.Name);
        Assert.Equal(2, settings.AppInsights!["contoso.crm11.dynamics.com"].Count);
    }

    [Fact]
    public void A_url_without_a_host_sets_nothing()
    {
        var settings = new AppSettings();

        settings.SetTelemetryBinding("https://", TelemetryKind.DataverseExport, Binding());

        Assert.Null(settings.AppInsights);
        Assert.Null(settings.GetTelemetryBinding("", TelemetryKind.DataverseExport));
    }

    [Fact]
    public void Bindings_survive_a_save_and_load_keyed_without_case()
    {
        var settings = new AppSettings();
        settings.SetTelemetryBinding(Url, TelemetryKind.DataverseExport, Binding());
        settings.Save(FilePath);

        var loaded = AppSettings.Load(FilePath);

        var binding = loaded.GetTelemetryBinding("https://Contoso.CRM11.dynamics.com", TelemetryKind.DataverseExport);
        Assert.NotNull(binding);
        Assert.Equal("app-contoso-dataverse", binding.AppId);
        Assert.Equal(TelemetryKind.DataverseExport, binding.KnownKind);
    }

    [Fact]
    public void A_kind_from_a_later_version_is_kept_through_a_load_and_save()
    {
        File.WriteAllText(FilePath, """
            { "AppInsights": { "contoso.crm11.dynamics.com": [
                { "Kind": "CopilotStudioExport", "Name": "agents", "AppId": "a1", "ResourceId": "/x" },
                { "Kind": "DataverseExport", "Name": "dv", "AppId": "a2", "ResourceId": "/y" } ] } }
            """);

        var settings = AppSettings.Load(FilePath);
        Assert.Null(settings.LoadProblem);
        settings.SetTelemetryBinding(Url, TelemetryKind.DataverseExport, Binding("dv2"));
        settings.Save(FilePath);

        var bindings = AppSettings.Load(FilePath).AppInsights!["contoso.crm11.dynamics.com"];
        Assert.Contains(bindings, b => b is { Kind: "CopilotStudioExport", KnownKind: null });
        Assert.Contains(bindings, b => b.Name == "dv2");
        Assert.DoesNotContain(bindings, b => b.Name == "dv");
    }
}
