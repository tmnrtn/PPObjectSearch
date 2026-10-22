using System.Text.Json.Serialization;

namespace PPObjectSearch.Models;

/// <summary>
/// What an Application Insights resource holds for an environment. Each kind of export writes its
/// own tables and dimensions, so each is read by its own profile.
/// </summary>
public enum TelemetryKind
{
    /// <summary>The Power Platform admin center's Dataverse data export: plug-ins, API calls, forms.</summary>
    DataverseExport,

    /// <summary>The Power Platform admin center's Power Automate data export: flow runs and actions.</summary>
    PowerAutomateExport,

    /// <summary>A canvas app's own connection string: sessions, screens, Trace() calls, errors.</summary>
    CanvasApp,

    /// <summary>Anything else written with the Application Insights SDK - your own plug-ins or functions.</summary>
    Custom
}

/// <summary>
/// An Application Insights resource chosen for an environment, and what it holds. Saved per
/// environment in settings.json, so it is what the Failures window and details read from.
/// </summary>
public sealed class TelemetryBinding
{
    /// <summary>
    /// The <see cref="TelemetryKind"/> by name. Kept as text so a kind this version does not know -
    /// written by a later one - survives a load and save rather than failing the whole file.
    /// </summary>
    public string Kind { get; set; } = nameof(TelemetryKind.DataverseExport);

    /// <summary>The Azure resource id, e.g. /subscriptions/…/providers/microsoft.insights/components/name.</summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>The resource's app id, which the query API is asked by.</summary>
    public string AppId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? SubscriptionName { get; set; }

    /// <summary>
    /// Whether to keep only rows that name this environment. On by default, since one resource can
    /// receive several environments' export; off for a resource that receives this one alone.
    /// </summary>
    public bool OnlyThisEnvironment { get; set; } = true;

    /// <summary>For a canvas app binding, the app it belongs to; null for an environment-wide export.</summary>
    public string? AppName { get; set; }

    [JsonIgnore]
    public TelemetryKind? KnownKind => Enum.TryParse<TelemetryKind>(Kind, ignoreCase: true, out var kind) ? kind : null;

    /// <summary>"contoso-dataverse (Contoso Prod)".</summary>
    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(SubscriptionName) ? Name : $"{Name} ({SubscriptionName})";
}
