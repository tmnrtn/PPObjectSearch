namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>An Azure Monitor client signed in as this environment's account, in its cloud.</summary>
    public AzureMonitor.AzureMonitorClient CreateAzureMonitorClient() => new(_auth, Cloud, _handler);
}
