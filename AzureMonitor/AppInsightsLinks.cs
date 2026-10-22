using System.IO;
using System.IO.Compression;
using System.Text;
using PPObjectSearch.Core;

namespace PPObjectSearch.AzureMonitor;

/// <summary>Links into the Azure portal for an App Insights resource - the resource, or a query run in its Logs.</summary>
public static class AppInsightsLinks
{
    /// <summary>The resource's overview page, in the environment's tenant.</summary>
    public static string Resource(AzureEndpoints azure, string? tenantId, string resourceId) =>
        $"{azure.Portal}/{Tenant(tenantId)}resource{resourceId}/overview";

    /// <summary>
    /// The resource's Logs with <paramref name="query"/> filled in and run. The portal takes the
    /// query gzipped and base64-encoded, the form its own "share link" produces.
    /// </summary>
    public static string Logs(AzureEndpoints azure, string? tenantId, string resourceId, string query, TimeSpan? timespan = null)
    {
        var link = $"{azure.Portal}/{Tenant(tenantId)}blade/Microsoft_Azure_Monitoring_Logs/LogsBlade" +
                   $"/resourceId/{Uri.EscapeDataString(resourceId)}" +
                   $"/source/LogsBlade.AnalyticsShareLinkToQuery/q/{Uri.EscapeDataString(Compress(query))}";

        return timespan is { } span ? link + "/timespan/" + System.Xml.XmlConvert.ToString(span) : link;
    }

    /// <summary>Every row of one operation - its request, plug-ins, dependencies, exceptions and traces.</summary>
    public static string OperationQuery(string operationId) =>
        "union requests, dependencies, exceptions, traces\n" +
        $"| where operation_Id == {Kql.Literal(operationId)}\n" +
        "| order by timestamp asc";

    private static string Tenant(string? tenantId) => string.IsNullOrWhiteSpace(tenantId) ? "#" : $"#@{tenantId}/";

    internal static string Compress(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }
}
