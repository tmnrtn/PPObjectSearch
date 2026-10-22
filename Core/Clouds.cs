namespace PPObjectSearch.Core;

/// <summary>
/// The endpoints of one Microsoft cloud. Sign-in, Graph, Power Automate, the Power Platform API,
/// discovery, the maker portals and Azure all live at different hosts in the government and China
/// clouds, so nothing that leaves Dataverse may assume the public one.
/// </summary>
public sealed record Cloud(
    string Name,
    string Authority,
    string Graph,
    string FlowResource,
    string FlowApi,
    string BapApi,
    string GlobalDiscovery,
    string MakerPortal,
    string FlowPortal,
    IReadOnlyList<string> EnvironmentHostSuffixes,
    AzureEndpoints Azure,
    string? PowerAppsResource = null,
    string? PowerAppsApi = null,
    string? PlayPortal = null,
    string? CopilotStudioPortal = null);

/// <summary>
/// The Azure side of a cloud, where an environment's Application Insights resource lives. Each
/// API's base URL is also the resource its tokens are asked for.
/// </summary>
/// <param name="ResourceManager">Azure Resource Manager, which lists subscriptions and resources.</param>
/// <param name="AppInsightsApi">The Application Insights query API, which takes an app id.</param>
/// <param name="Portal">The Azure portal, for links to a resource or a query.</param>
public sealed record AzureEndpoints(string ResourceManager, string AppInsightsApi, string Portal);

public static class Clouds
{
    private static readonly AzureEndpoints PublicAzure = new(
        "https://management.azure.com",
        "https://api.applicationinsights.io",
        "https://portal.azure.com");

    // GCC High and DoD both sit in Azure Government; DoD regions share its endpoints.
    private static readonly AzureEndpoints GovernmentAzure = new(
        "https://management.usgovcloudapi.net",
        "https://api.applicationinsights.us",
        "https://portal.azure.us");

    private static readonly AzureEndpoints ChinaAzure = new(
        "https://management.chinacloudapi.cn",
        "https://api.applicationinsights.azure.cn",
        "https://portal.azure.cn");

    public static readonly Cloud Public = new(
        "Public",
        "https://login.microsoftonline.com",
        "https://graph.microsoft.com",
        "https://service.flow.microsoft.com/",
        "https://api.flow.microsoft.com",
        "https://api.bap.microsoft.com",
        "https://globaldisco.crm.dynamics.com",
        "https://make.powerapps.com",
        "https://make.powerautomate.com",
        [".dynamics.com"],
        PublicAzure,
        PowerAppsResource: "https://service.powerapps.com/",
        PowerAppsApi: "https://api.powerapps.com",
        PlayPortal: "https://apps.powerapps.com",
        CopilotStudioPortal: "https://copilotstudio.microsoft.com");

    /// <summary>
    /// GCC: the commercial directory and Graph, the government Power Platform services. A GCC
    /// tenant signs in to commercial Azure, so its Azure resources are taken to be there.
    /// </summary>
    public static readonly Cloud UsGcc = new(
        "US Government (GCC)",
        "https://login.microsoftonline.com",
        "https://graph.microsoft.com",
        "https://gov.service.flow.microsoft.us/",
        "https://gov.api.flow.microsoft.us",
        "https://gov.api.bap.microsoft.us",
        "https://globaldisco.crm9.dynamics.com",
        "https://make.gov.powerapps.us",
        "https://make.gov.powerautomate.us",
        [".crm9.dynamics.com"],
        PublicAzure,
        PowerAppsResource: "https://gov.service.powerapps.us/",
        PowerAppsApi: "https://gov.api.powerapps.us",
        PlayPortal: "https://apps.gov.powerapps.us");

    public static readonly Cloud UsGccHigh = new(
        "US Government (GCC High)",
        "https://login.microsoftonline.us",
        "https://graph.microsoft.us",
        "https://high.service.flow.microsoft.us/",
        "https://high.api.flow.microsoft.us",
        "https://high.api.bap.microsoft.us",
        "https://globaldisco.crm.microsoftdynamics.us",
        "https://make.high.powerapps.us",
        "https://make.high.powerautomate.us",
        [".microsoftdynamics.us"],
        GovernmentAzure,
        PowerAppsResource: "https://high.service.powerapps.us/",
        PowerAppsApi: "https://high.api.powerapps.us",
        PlayPortal: "https://apps.high.powerapps.us");

    public static readonly Cloud UsDod = new(
        "US Government (DoD)",
        "https://login.microsoftonline.us",
        "https://dod-graph.microsoft.us",
        "https://service.flow.appsplatform.us/",
        "https://api.flow.appsplatform.us",
        "https://api.bap.appsplatform.us",
        "https://globaldisco.crm.appsplatform.us",
        "https://make.apps.appsplatform.us",
        "https://make.powerautomate.appsplatform.us",
        [".appsplatform.us"],
        GovernmentAzure,
        PowerAppsResource: "https://service.apps.appsplatform.us/",
        PowerAppsApi: "https://api.apps.appsplatform.us",
        PlayPortal: "https://play.apps.appsplatform.us");

    public static readonly Cloud China = new(
        "China (21Vianet)",
        "https://login.chinacloudapi.cn",
        "https://microsoftgraph.chinacloudapi.cn",
        "https://service.powerautomate.cn/",
        "https://api.powerautomate.cn",
        "https://api.bap.partner.microsoftonline.cn",
        "https://globaldisco.crm.dynamics.cn",
        "https://make.powerapps.cn",
        "https://make.powerautomate.cn",
        [".dynamics.cn"],
        ChinaAzure,
        PowerAppsResource: "https://service.powerapps.cn/",
        PowerAppsApi: "https://api.powerapps.cn",
        PlayPortal: "https://apps.powerapps.cn");

    public static readonly IReadOnlyList<Cloud> All = [UsGcc, UsGccHigh, UsDod, China, Public];

    /// <summary>The cloud an environment URL's host belongs to; null for a host none of them claims.</summary>
    public static Cloud? ForEnvironment(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl)) return null;

        var value = environmentUrl.Contains("://", StringComparison.Ordinal) ? environmentUrl : "https://" + environmentUrl;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;

        // Most specific first: GCC's crm9.dynamics.com is also a dynamics.com host.
        return All.FirstOrDefault(c => c.EnvironmentHostSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The cloud whose sign-in authority this is - as named in a Dataverse challenge. Only the
    /// known authorities are accepted, so a challenge cannot send credentials anywhere else.
    /// </summary>
    public static Cloud? ForAuthorityHost(string? host) =>
        string.IsNullOrWhiteSpace(host)
            ? null
            // GCC shares the public authority and DoD shares GCC High's; the environment's host
            // tells those apart, so an authority alone means the cloud that is usually behind it.
            : Array.Find(new[] { Public, UsGccHigh, China },
                c => string.Equals(new Uri(c.Authority).Host, host, StringComparison.OrdinalIgnoreCase));
}
