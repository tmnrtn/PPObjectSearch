using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;

namespace PPObjectSearch.Auth;

/// <summary>
/// Signs in as an app registration - a service principal - for unattended runs. Its settings come
/// from environment variables, so no secret is ever on a command line or in a log:
/// PPOS_TENANT_ID, PPOS_CLIENT_ID, and either PPOS_CLIENT_SECRET or PPOS_CLIENT_CERTIFICATE
/// (a .pfx path) with PPOS_CLIENT_CERTIFICATE_PASSWORD.
/// </summary>
public static class ServicePrincipal
{
    public const string TenantVariable = "PPOS_TENANT_ID";
    public const string ClientIdVariable = "PPOS_CLIENT_ID";
    public const string SecretVariable = "PPOS_CLIENT_SECRET";
    public const string CertificateVariable = "PPOS_CLIENT_CERTIFICATE";
    public const string CertificatePasswordVariable = "PPOS_CLIENT_CERTIFICATE_PASSWORD";

    /// <summary>
    /// The cloud to sign in to, by an environment URL in it - set for the US government and China
    /// clouds; the public cloud otherwise.
    /// </summary>
    public const string CloudVariable = "PPOS_CLOUD_ENVIRONMENT";

    /// <summary>Null when no client id is set - the caller signs in interactively instead.</summary>
    public static EnvironmentAuthContext? FromEnvironment(Func<string, string?> variable)
    {
        var clientId = variable(ClientIdVariable);
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        var tenant = variable(TenantVariable);
        if (string.IsNullOrWhiteSpace(tenant))
        {
            throw new InvalidOperationException($"{ClientIdVariable} is set but {TenantVariable} is not - a service principal signs in to one tenant.");
        }

        var cloud = Core.Clouds.ForEnvironment(variable(CloudVariable)) ?? Core.Clouds.Public;

        var builder = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithAuthority($"{cloud.Authority}/{tenant}", validateAuthority: false);

        if (variable(SecretVariable) is { Length: > 0 } secret)
        {
            builder = builder.WithClientSecret(secret);
        }
        else if (variable(CertificateVariable) is { Length: > 0 } path)
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, variable(CertificatePasswordVariable));
            builder = builder.WithCertificate(certificate);
        }
        else
        {
            throw new InvalidOperationException($"Set {SecretVariable}, or {CertificateVariable} (with {CertificatePasswordVariable} if it has one).");
        }

        var app = builder.Build();

        var context = EnvironmentAuthContext.FromTokenSource(async (resource, ct) =>
        {
            var result = await app.AcquireTokenForClient([$"{resource.TrimEnd('/')}/.default"]).ExecuteAsync(ct).ConfigureAwait(false);
            return result.AccessToken;
        });
        context.UseCloud(cloud);
        return context;
    }
}
