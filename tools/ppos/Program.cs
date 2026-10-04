using PPObjectSearch.Auth;
using PPObjectSearch.Cli;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;

// ppos: the app's read-only checks for a pipeline. See 'ppos help'.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

// A service principal when one is configured; otherwise the account signed in to the app.
var servicePrincipal = ServicePrincipal.FromEnvironment(Environment.GetEnvironmentVariable);
var interactive = new AuthenticationService();

async Task<DataverseClient> Connect(string url, CancellationToken ct)
{
    var auth = servicePrincipal ?? new EnvironmentAuthContext(interactive);
    if (servicePrincipal is null) await auth.EnsureTenantAsync(url, ct);
    else if (PPObjectSearch.Core.Clouds.ForEnvironment(url) is { } cloud) auth.UseCloud(cloud);
    return new DataverseClient(auth, url);
}

var runner = new CliRunner(
    Console.Out,
    Console.Error,
    Connect,
    () => AppSettings.Load().ReferenceDataConfigurations ?? []);

return await runner.RunAsync(args, cancel.Token);
