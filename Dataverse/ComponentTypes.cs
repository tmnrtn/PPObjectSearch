namespace PPObjectSearch.Dataverse;

/// <summary>
/// Fallback display names for solutioncomponent type codes. The solution component summary
/// virtual table usually supplies a localised name; this fills the gaps.
/// </summary>
public static class ComponentTypes
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Table",
        [2] = "Column",
        [3] = "Relationship",
        [4] = "Attribute Picklist Value",
        [5] = "Attribute Lookup Value",
        [6] = "View Attribute",
        [7] = "Localized Label",
        [8] = "Relationship Extra Condition",
        [9] = "Choice",
        [10] = "Relationship",
        [11] = "Relationship Role",
        [12] = "Relationship Role Map",
        [13] = "Managed Property",
        [14] = "Entity Key",
        [16] = "Privilege",
        [17] = "Privilege Object Type Code",
        [20] = "Security Role",
        [21] = "Role Privilege",
        [22] = "Display String",
        [23] = "Display String Map",
        [24] = "Form",
        [25] = "Organization",
        [26] = "View",
        [29] = "Process",
        [31] = "Report",
        [32] = "Report Entity",
        [33] = "Report Category",
        [34] = "Report Visibility",
        [35] = "Attachment",
        [36] = "Email Template",
        [37] = "Contract Template",
        [38] = "KB Article Template",
        [39] = "Mail Merge Template",
        [44] = "Duplicate Rule",
        [45] = "Duplicate Rule Condition",
        [46] = "Entity Map",
        [47] = "Attribute Map",
        [48] = "Ribbon Command",
        [49] = "Ribbon Context Group",
        [50] = "Ribbon Customization",
        [52] = "Ribbon Rule",
        [53] = "Ribbon Tab To Command Map",
        [55] = "Ribbon Diff",
        [59] = "Chart",
        [60] = "Form",
        [61] = "Web Resource",
        [62] = "Site Map",
        [63] = "Connection Role",
        [64] = "Complex Control",
        [65] = "Hierarchy Rule",
        [66] = "Custom Control",
        [68] = "Custom Control Resource",
        [70] = "Field Security Profile",
        [71] = "Field Permission",
        [90] = "Plug-in Type",
        [91] = "Plug-in Assembly",
        [92] = "SDK Message Processing Step",
        [93] = "SDK Message Processing Step Image",
        [95] = "Service Endpoint",
        [150] = "Custom Control (PCF)",
        [151] = "Custom Control Default Config",
        [152] = "Custom Control",
        [154] = "AI Project",
        [155] = "AI Configuration",
        [161] = "Mobile Offline Profile",
        [162] = "Mobile Offline Profile Item",
        [165] = "Similarity Rule",
        [166] = "Data Source Mapping",
        [201] = "SDK Message",
        [300] = "Canvas App",
        [371] = "Connector",
        [372] = "Connector",
        [380] = "Environment Variable Definition",
        [381] = "Environment Variable Value",
        [400] = "AI Model",
        [401] = "AI Template",
        [402] = "AI Plugin",
        [430] = "Entity Analytics Configuration",
        [431] = "Attribute Image Configuration",
        [432] = "Entity Image Configuration",
        [10018] = "Custom API",
        [10019] = "Custom API Request Parameter",
        [10020] = "Custom API Response Property",
        [10029] = "Connection Reference",
        [10039] = "Table Column Permission",
        [10044] = "Desktop Flow Module",
        [10088] = "Workflow Binary"
    };

    /// <summary>
    /// Sub-classifies process (workflow) rows, which otherwise all read as "Process".
    /// Matches the workflow.category option set.
    /// </summary>
    private static readonly Dictionary<int, string> ProcessCategories = new()
    {
        [0] = "Workflow (classic)",
        [1] = "Dialog",
        [2] = "Business Rule",
        [3] = "Action",
        [4] = "Business Process Flow",
        [5] = "Cloud Flow",
        [6] = "Desktop Flow",
        [7] = "AI Flow"
    };

    public static string GetName(int componentType) =>
        Names.TryGetValue(componentType, out var name) ? name : $"Component type {componentType}";

    public static string? GetProcessCategoryName(int category) =>
        ProcessCategories.TryGetValue(category, out var name) ? name : null;

    /// <summary>
    /// The componenttype option set's own PascalCase member name (e.g. "Entity", "WebResource"),
    /// as opposed to <see cref="Names"/>'s human-friendly label. This is what the undocumented
    /// msdyn_componentlayers virtual table expects in its msdyn_solutioncomponentname filter -
    /// there is no public Web API for component layers, so this list only covers the types worth
    /// the risk of getting right; everything else is left unmapped and the layers check is simply
    /// skipped for it.
    /// </summary>
    private static readonly Dictionary<int, string> SdkNames = new()
    {
        [1] = "Entity",
        [2] = "Attribute",
        [9] = "OptionSet",
        [20] = "Role",
        [26] = "SavedQuery",
        [29] = "Workflow",
        [31] = "Report",
        [36] = "EmailTemplate",
        [60] = "SystemForm",
        [61] = "WebResource",
        [62] = "SiteMap",
        [63] = "ConnectionRole",
        [66] = "CustomControl",
        [70] = "FieldSecurityProfile",
        [90] = "PluginType",
        [91] = "PluginAssembly",
        [92] = "SDKMessageProcessingStep",
        [95] = "ServiceEndpoint",
        [300] = "CanvasApp",
        [380] = "EnvironmentVariableDefinition",
        [10018] = "CustomAPI",
        [10029] = "ConnectionReference"
    };

    public static string? GetSdkName(int componentType) =>
        SdkNames.TryGetValue(componentType, out var name) ? name : null;
}
