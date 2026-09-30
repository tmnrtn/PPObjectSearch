using PPObjectSearch.Dataverse;
using PPObjectSearch.Graph;

namespace PPObjectSearch.Tests.Infrastructure;

public static class Fakes
{
    public const string EnvironmentUrl = "https://contoso.crm11.dynamics.com";
    public const string ApiRoot = EnvironmentUrl + "/api/data/v9.2/";

    public static DataverseClient Dataverse(FakeHttpHandler handler, string url = EnvironmentUrl) =>
        new(TestAuth.Tokens(), url, handler);

    public static GraphClient Graph(FakeHttpHandler handler) => new(TestAuth.Tokens(), handler);
}
