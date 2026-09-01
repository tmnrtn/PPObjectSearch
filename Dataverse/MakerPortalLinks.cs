using System.Net;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// Builds https://make.powerapps.com deep links for solution objects.
///
/// Maker portal routes are not a documented, versioned contract, so every template here can be
/// overridden per component type from settings.json (see <c>MakerLinkTemplates</c>). When no
/// specific route is known the link falls back to the object's solution page, which always
/// resolves - the app never renders a link it knows to be dead.
/// </summary>
public sealed class MakerPortalLinkBuilder
{
    private const string MakerRoot = "https://make.powerapps.com";

    private readonly string? _environmentId;
    private readonly string _environmentUrl;
    private readonly Dictionary<string, string> _overrides;

    /// <summary>
    /// Routes keyed by component type number. Placeholders are substituted case-sensitively.
    /// </summary>
    private static readonly Dictionary<int, string> DefaultTemplates = new()
    {
        // Tables are addressed by logical name, not by id.
        [1] = MakerRoot + "/environments/{envId}/entities/{name}",
        // Columns hang off their parent table.
        [2] = MakerRoot + "/environments/{envId}/entities/{primaryEntity}",
        [9] = MakerRoot + "/environments/{envId}/customchoices/{objectId}",
        [300] = MakerRoot + "/e/{envId}/canvas?action=edit&app-id=%2Fproviders%2FMicrosoft.PowerApps%2Fapps%2F{objectId}",
        [80] = MakerRoot + "/environments/{envId}/solutions/{solutionId}/objects/appmodule/{objectId}/view",
        [10029] = MakerRoot + "/environments/{envId}/connectionreferences/{objectId}",
        [380] = MakerRoot + "/environments/{envId}/solutions/{solutionId}/objects/environmentvariable/{objectId}/view"
    };

    /// <summary>
    /// Generic solution-explorer route used when no type specific template exists. The trailing
    /// segment comes from the component's own logical name, which the summary table supplies.
    /// </summary>
    private const string GenericTemplate =
        MakerRoot + "/environments/{envId}/solutions/{solutionId}/objects/{logicalName}/{objectId}/view";

    /// <summary>Always-valid last resort.</summary>
    private const string SolutionTemplate =
        MakerRoot + "/environments/{envId}/solutions/{solutionId}";

    public MakerPortalLinkBuilder(string? environmentId, string environmentUrl, IDictionary<string, string>? overrides)
    {
        _environmentId = string.IsNullOrWhiteSpace(environmentId) ? null : environmentId.Trim();
        _environmentUrl = environmentUrl.TrimEnd('/');
        _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (overrides is not null)
        {
            foreach (var kvp in overrides) _overrides[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>Links can only be built once the Power Platform environment id is known.</summary>
    public bool CanBuildLinks => _environmentId is not null;

    public string? Build(SolutionComponentItem item, Guid solutionId)
    {
        if (_environmentId is null) return null;

        var template = ResolveTemplate(item);

        // A template that needs a value we do not have would produce a dead link.
        if (template.Contains("{name}") && string.IsNullOrWhiteSpace(item.Name)) template = SolutionTemplate;
        if (template.Contains("{primaryEntity}") && string.IsNullOrWhiteSpace(item.PrimaryEntityName)) template = SolutionTemplate;
        if (template.Contains("{logicalName}") && string.IsNullOrWhiteSpace(item.ComponentLogicalName)) template = SolutionTemplate;
        if (template.Contains("{objectId}") && item.ObjectId == Guid.Empty) template = SolutionTemplate;

        return template
            .Replace("{envId}", Uri.EscapeDataString(_environmentId))
            .Replace("{envUrl}", _environmentUrl)
            .Replace("{solutionId}", solutionId.ToString())
            .Replace("{objectId}", item.ObjectId.ToString())
            .Replace("{componentType}", item.ComponentType.ToString())
            .Replace("{primaryEntity}", Uri.EscapeDataString(item.PrimaryEntityName ?? string.Empty))
            .Replace("{logicalName}", Uri.EscapeDataString(item.ComponentLogicalName ?? string.Empty))
            .Replace("{name}", Uri.EscapeDataString(item.Name ?? string.Empty));
    }

    private string ResolveTemplate(SolutionComponentItem item)
    {
        // settings.json wins: keyed by component type number, or by component logical name.
        if (_overrides.TryGetValue(item.ComponentType.ToString(), out var byNumber)) return byNumber;
        if (!string.IsNullOrWhiteSpace(item.ComponentLogicalName) &&
            _overrides.TryGetValue(item.ComponentLogicalName!, out var byName)) return byName;

        // Processes split by category: only modern cloud flows have a maker portal page.
        if (item.ComponentType == 29)
        {
            return item.ComponentTypeName.Contains("Cloud Flow", StringComparison.OrdinalIgnoreCase)
                ? MakerRoot + "/environments/{envId}/flows/{objectId}/details"
                : SolutionTemplate;
        }

        if (DefaultTemplates.TryGetValue(item.ComponentType, out var template)) return template;

        return string.IsNullOrWhiteSpace(item.ComponentLogicalName) ? SolutionTemplate : GenericTemplate;
    }
}
