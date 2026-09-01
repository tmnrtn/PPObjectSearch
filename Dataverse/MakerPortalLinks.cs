using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// Builds https://make.powerapps.com deep links for solution objects.
///
/// The solution explorer addresses objects as
/// <c>/solutions/{solutionId}/objects/{entitySetName}/{objectId}/view</c>, where the segment is
/// the component type's OData entity set name (roleeditorlayout -> roleeditorlayouts). That is
/// read from table metadata rather than guessed, so it is right for types this code has never
/// heard of.
///
/// Links degrade in steps rather than all the way to nothing: a component whose object route
/// cannot be built still links to that type's object list inside the solution, and only a
/// component whose type cannot be resolved at all falls back to the solution itself.
///
/// Every route can be overridden per component type from settings.json (MakerLinkTemplates).
/// </summary>
public sealed class MakerPortalLinkBuilder
{
    private const string MakerRoot = "https://make.powerapps.com";

    private readonly string? _environmentId;
    private readonly string _environmentUrl;
    private readonly Dictionary<string, string> _overrides;
    private readonly IReadOnlyDictionary<string, TableMetadata> _tables;

    /// <summary>Routes that differ from the standard solution-object shape.</summary>
    private static readonly Dictionary<int, string> DefaultTemplates = new()
    {
        // Tables have their own designer, addressed by metadata id - which for an entity
        // component is the solution component's own object id.
        [1] = MakerRoot + "/environments/{envId}/entities/{objectId}",
        // Columns hang off their parent table, addressed by that table's metadata id.
        [2] = MakerRoot + "/environments/{envId}/entities/{primaryEntityId}",
        // Model-driven apps open in the app designer, which uses the short /e/{env}/s/{solution}
        // form rather than the solution-object route. Confirmed against a live environment.
        [80] = MakerRoot + "/e/{envId}/s/{solutionId}/app/edit/{objectId}",
        // Canvas apps open in the studio, with the app id as an encoded resource path.
        // Confirmed against a live environment.
        [300] = MakerRoot + "/e/{envId}/canvas?action=edit&app-id=%2Fproviders%2FMicrosoft.PowerApps%2Fapps%2F{objectId}"
    };

    /// <summary>One object inside the solution. Confirmed against a live environment.</summary>
    private const string ObjectTemplate =
        MakerRoot + "/environments/{envId}/solutions/{solutionId}/objects/{entitySet}/{objectId}/view";

    /// <summary>Every object of one type inside the solution - the first fallback.</summary>
    private const string TypeListTemplate =
        MakerRoot + "/environments/{envId}/solutions/{solutionId}/objects/{entitySet}";

    /// <summary>Always-valid last resort.</summary>
    private const string SolutionTemplate =
        MakerRoot + "/environments/{envId}/solutions/{solutionId}";

    public MakerPortalLinkBuilder(
        string? environmentId,
        string environmentUrl,
        IDictionary<string, string>? overrides,
        IReadOnlyDictionary<string, TableMetadata>? tables = null)
    {
        _environmentId = string.IsNullOrWhiteSpace(environmentId) ? null : environmentId.Trim();
        _environmentUrl = environmentUrl.TrimEnd('/');
        _tables = tables ?? new Dictionary<string, TableMetadata>();
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

        var entitySet = ResolveEntitySet(item);

        var primaryEntityId = !string.IsNullOrWhiteSpace(item.PrimaryEntityName) &&
                              _tables.TryGetValue(item.PrimaryEntityName!, out var parent) &&
                              parent.MetadataId != Guid.Empty
            ? parent.MetadataId.ToString()
            : null;

        var template = ResolveTemplate(item, entitySet);

        // Step down through the fallbacks rather than straight to the solution page: a template
        // missing a value it needs would otherwise produce a dead link.
        if (!CanSatisfy(template, item, entitySet, primaryEntityId)) template = TypeListTemplate;
        if (!CanSatisfy(template, item, entitySet, primaryEntityId)) template = SolutionTemplate;

        return template
            .Replace("{envId}", Uri.EscapeDataString(_environmentId))
            .Replace("{envUrl}", _environmentUrl)
            .Replace("{solutionId}", solutionId.ToString())
            .Replace("{objectId}", item.ObjectId.ToString())
            .Replace("{componentType}", item.ComponentType.ToString())
            .Replace("{entitySet}", Uri.EscapeDataString(entitySet ?? string.Empty))
            .Replace("{primaryEntityId}", primaryEntityId ?? string.Empty)
            .Replace("{primaryEntity}", Uri.EscapeDataString(item.PrimaryEntityName ?? string.Empty))
            .Replace("{workflowIdUnique}", item.WorkflowIdUnique?.ToString() ?? string.Empty)
            .Replace("{logicalName}", Uri.EscapeDataString(item.ComponentLogicalName ?? string.Empty))
            .Replace("{name}", Uri.EscapeDataString(item.Name ?? string.Empty));
    }

    private static bool CanSatisfy(string template, SolutionComponentItem item, string? entitySet, string? primaryEntityId)
    {
        if (template.Contains("{entitySet}") && string.IsNullOrWhiteSpace(entitySet)) return false;
        if (template.Contains("{objectId}") && item.ObjectId == Guid.Empty) return false;
        if (template.Contains("{primaryEntityId}") && primaryEntityId is null) return false;
        if (template.Contains("{workflowIdUnique}") && item.WorkflowIdUnique is null) return false;
        if (template.Contains("{primaryEntity}") && string.IsNullOrWhiteSpace(item.PrimaryEntityName)) return false;
        if (template.Contains("{logicalName}") && string.IsNullOrWhiteSpace(item.ComponentLogicalName)) return false;
        if (template.Contains("{name}") && string.IsNullOrWhiteSpace(item.Name)) return false;

        return true;
    }

    /// <summary>
    /// The solution explorer's URL segment for a component type, taken from that type's entity
    /// set name. Cloud flows are the known exception: they are workflow rows, but the portal
    /// files them under their own segment rather than "workflows".
    /// </summary>
    private string? ResolveEntitySet(SolutionComponentItem item)
    {
        if (item.ComponentType == 29) return IsModernFlow(item) ? "cloudflows" : "workflows";

        if (!string.IsNullOrWhiteSpace(item.ComponentLogicalName) &&
            _tables.TryGetValue(item.ComponentLogicalName!, out var table) &&
            !string.IsNullOrWhiteSpace(table.EntitySetName))
        {
            return table.EntitySetName;
        }

        return null;
    }

    /// <summary>
    /// The category lives on SubType - every process reports its type as "Process". Dataverse
    /// labels category 5 "Modern Flow"; the portals call it a cloud flow, so accept either.
    /// </summary>
    private static bool IsModernFlow(SolutionComponentItem item)
    {
        var category = item.SubType ?? string.Empty;
        return category.Contains("Modern Flow", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("Cloud Flow", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveTemplate(SolutionComponentItem item, string? entitySet)
    {
        // settings.json wins: keyed by component type number, or by component logical name.
        if (_overrides.TryGetValue(item.ComponentType.ToString(), out var byNumber)) return byNumber;
        if (!string.IsNullOrWhiteSpace(item.ComponentLogicalName) &&
            _overrides.TryGetValue(item.ComponentLogicalName!, out var byName)) return byName;

        if (DefaultTemplates.TryGetValue(item.ComponentType, out var template)) return template;

        // Classic workflows, business rules and BPFs have no object page of their own; their
        // type list is the most useful place to land.
        if (item.ComponentType == 29 && !IsModernFlow(item)) return TypeListTemplate;

        return entitySet is null ? SolutionTemplate : ObjectTemplate;
    }
}
