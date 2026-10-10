using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>Environment tabs for the view models that hang off one, and the waits their background loads need.</summary>
public static class AdminSessions
{
    /// <summary>The Power Platform API's per-user environment list, which says what type an environment is.</summary>
    public const string EnvironmentList = "BusinessAppPlatform/environments?";

    /// <summary>A tab that has not signed in: it cannot say whether writes are allowed.</summary>
    public static EnvironmentSessionViewModel Disconnected(string url = Fakes.EnvironmentUrl) =>
        new(new AuthenticationService(), new AppSettings(), new TabState { EnvironmentUrl = url });

    /// <summary>A tab whose client talks to <paramref name="handler"/>, as though signed in.</summary>
    public static EnvironmentSessionViewModel Connected(FakeHttpHandler handler, string url = Fakes.EnvironmentUrl)
    {
        var session = Disconnected(url);
        session.UseConnectedClient(Fakes.Dataverse(handler, url));
        return session;
    }

    /// <summary>Has the Power Platform API list the environment at <paramref name="url"/> as <paramref name="sku"/> - "Sandbox", "Production".</summary>
    public static FakeHttpHandler OnEnvironmentType(this FakeHttpHandler handler, string sku, string url = Fakes.EnvironmentUrl) =>
        handler.OnJson(HttpMethod.Get, EnvironmentList, JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    name = "00000000-0000-0000-0000-0000000000e1",
                    properties = new { displayName = "Contoso", environmentSku = sku, linkedEnvironmentMetadata = new { instanceApiUrl = url } }
                }
            }
        }));

    /// <summary>Waits for a load nobody can await - one a selection started - to reach <paramref name="condition"/>.</summary>
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(5);
        }
    }
}
