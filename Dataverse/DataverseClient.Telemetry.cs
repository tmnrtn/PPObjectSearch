namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>An Azure Monitor client signed in as this environment's account, in its cloud.</summary>
    public AzureMonitor.AzureMonitorClient CreateAzureMonitorClient() => new(_auth, Cloud, _handler);

    /// <summary>Most plug-in types of one assembly whose telemetry is read together.</summary>
    private const int MaxTelemetryTypes = 40;

    /// <summary>
    /// The plug-in type names telemetry knows a plug-in object by: a type's own, a step's type's,
    /// or every type in an assembly. Empty for anything else, or a step that runs no plug-in type.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPluginTypeNamesAsync(Guid objectId, int componentType, CancellationToken ct = default)
    {
        switch (componentType)
        {
            case 90: // plug-in type
                return await TypeNameAsync(objectId, ct).ConfigureAwait(false) is { } name ? [name] : [];

            case 92: // step: the type it runs is its event handler
            {
                using var step = await GetJsonAsync(
                    EnvironmentUrl + ApiPath + $"sdkmessageprocessingsteps({objectId})?$select=_eventhandler_value", ct).ConfigureAwait(false);

                return Guid.TryParse(JsonHelper.GetString(step.RootElement, "_eventhandler_value"), out var typeId) &&
                       await TypeNameAsync(typeId, ct).ConfigureAwait(false) is { } stepType
                    ? [stepType]
                    : [];
            }

            case 91: // assembly
            {
                using var types = await GetJsonAsync(
                    EnvironmentUrl + ApiPath +
                    $"plugintypes?$select=typename&$filter=_pluginassemblyid_value eq {objectId}", ct).ConfigureAwait(false);

                return JsonHelper.Rows(types.RootElement)
                    .Select(row => JsonHelper.GetString(row, "typename"))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxTelemetryTypes)
                    .ToList();
            }

            default:
                return [];
        }
    }

    private async Task<string?> TypeNameAsync(Guid typeId, CancellationToken ct)
    {
        try
        {
            using var type = await GetJsonAsync(EnvironmentUrl + ApiPath + $"plugintypes({typeId})?$select=typename", ct).ConfigureAwait(false);
            return JsonHelper.GetString(type.RootElement, "typename") is { Length: > 0 } name ? name : null;
        }
        catch (DataverseException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A step whose handler is a service endpoint, not a plug-in type.
            return null;
        }
    }
}
