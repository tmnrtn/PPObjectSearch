namespace PPObjectSearch.Core;

/// <summary>
/// The endpoints of one Microsoft cloud. Sign-in, Graph, Power Automate, the Power Platform API,
/// discovery and the maker portals all live at different hosts in the government and China
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
    IReadOnlyList<string> EnvironmentHostSuffixes);

public static class Clouds
{
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
        [".dynamics.com"]);

    /// <summary>GCC: the commercial directory and Graph, the government Power Platform services.</summary>
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
        [".crm9.dynamics.com"]);

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
        [".microsoftdynamics.us"]);

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
        [".appsplatform.us"]);

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
        [".dynamics.cn"]);

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
