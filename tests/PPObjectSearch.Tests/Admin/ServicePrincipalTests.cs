using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PPObjectSearch.Auth;
using PPObjectSearch.Core;

namespace PPObjectSearch.Tests.Admin;

/// <summary>Signing in as a service principal from environment variables, for the command line.</summary>
public sealed class ServicePrincipalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppos-sp-" + Guid.NewGuid().ToString("N"));

    public ServicePrincipalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static Func<string, string?> Variables(params (string Name, string? Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    /// <summary>A throwaway self-signed certificate with its key, saved as a .pfx.</summary>
    private string WritePfx(string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=ppos-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(_dir, "app.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Without_a_client_id_there_is_no_service_principal(string? clientId)
    {
        Assert.Null(ServicePrincipal.FromEnvironment(Variables((ServicePrincipal.ClientIdVariable, clientId))));
    }

    [Fact]
    public void A_client_id_without_a_tenant_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServicePrincipal.FromEnvironment(Variables((ServicePrincipal.ClientIdVariable, "app"), (ServicePrincipal.SecretVariable, "s"))));

        Assert.Contains(ServicePrincipal.TenantVariable, ex.Message);
    }

    [Fact]
    public void A_client_id_without_a_secret_or_certificate_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServicePrincipal.FromEnvironment(Variables(
            (ServicePrincipal.ClientIdVariable, "app"), (ServicePrincipal.TenantVariable, "contoso.onmicrosoft.com"), (ServicePrincipal.SecretVariable, ""))));

        Assert.Contains(ServicePrincipal.CertificateVariable, ex.Message);
    }

    [Fact]
    public void A_secret_signs_in_to_the_public_cloud_unless_another_is_named()
    {
        var common = new (string, string?)[]
        {
            (ServicePrincipal.ClientIdVariable, "11111111-2222-3333-4444-555555555555"),
            (ServicePrincipal.TenantVariable, "contoso.onmicrosoft.com"),
            (ServicePrincipal.SecretVariable, "not-a-real-secret")
        };

        var publicCloud = ServicePrincipal.FromEnvironment(Variables(common));
        var government = ServicePrincipal.FromEnvironment(Variables([.. common, (ServicePrincipal.CloudVariable, "https://contoso.crm.microsoftdynamics.us")]));

        Assert.Same(Clouds.Public, publicCloud!.Cloud);
        Assert.Same(Clouds.UsGccHigh, government!.Cloud);
    }

    [Fact]
    public void A_certificate_file_is_loaded_with_its_password()
    {
        var path = WritePfx("pfx-password");

        var context = ServicePrincipal.FromEnvironment(Variables(
            (ServicePrincipal.ClientIdVariable, "11111111-2222-3333-4444-555555555555"),
            (ServicePrincipal.TenantVariable, "contoso.onmicrosoft.com"),
            (ServicePrincipal.CertificateVariable, path),
            (ServicePrincipal.CertificatePasswordVariable, "pfx-password"),
            (ServicePrincipal.CloudVariable, "https://contoso.crm.dynamics.cn")));

        Assert.Same(Clouds.China, context!.Cloud);
    }

    [Fact]
    public void A_certificate_with_the_wrong_password_is_refused()
    {
        var path = WritePfx("pfx-password");

        Assert.ThrowsAny<CryptographicException>(() => ServicePrincipal.FromEnvironment(Variables(
            (ServicePrincipal.ClientIdVariable, "app"),
            (ServicePrincipal.TenantVariable, "contoso.onmicrosoft.com"),
            (ServicePrincipal.CertificateVariable, path),
            (ServicePrincipal.CertificatePasswordVariable, "wrong"))));
    }
}
