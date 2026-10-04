using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class ConnectionReferenceTests
{
    private static readonly Guid Id = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static ConnectionReferenceInfo Reference(string name, string? connection, string connector = "shared_office365") => new()
    {
        Id = Guid.NewGuid(),
        LogicalName = name,
        DisplayName = name.ToUpperInvariant(),
        ConnectorId = $"/providers/Microsoft.PowerApps/apis/{connector}",
        ConnectionId = connection
    };

    [Fact]
    public void A_flows_definition_names_the_references_it_uses()
    {
        const string clientData = """
            {"properties":{"connectionReferences":{
              "shared_office365":{"runtimeSource":"embedded","connection":{"connectionReferenceLogicalName":"new_outlook"},"api":{"name":"shared_office365"}},
              "shared_office365-1":{"connection":{"connectionReferenceLogicalName":"NEW_OUTLOOK"}},
              "shared_sharepointonline":{"connection":{"connectionReferenceLogicalName":"new_sharepoint"}},
              "shared_embedded":{"connection":{"name":"shared-embedded-123"}}
            },"definition":{}}}
            """;

        Assert.Equal(["new_outlook", "new_sharepoint"], DataverseClient.ConnectionReferenceNames(clientData));
        Assert.Empty(DataverseClient.ConnectionReferenceNames("not json"));
        Assert.Empty(DataverseClient.ConnectionReferenceNames(null));
    }

    [Fact]
    public async Task References_are_read_with_their_connector_and_connection()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "connectionreferences?", $$"""
            {"value":[{"connectionreferenceid":"{{Id}}","connectionreferencelogicalname":"new_outlook",
              "connectionreferencedisplayname":"Outlook","connectorid":"/providers/Microsoft.PowerApps/apis/shared_office365",
              "connectionid":"abc123"}]}
            """);

        var reference = Assert.Single(await Fakes.Dataverse(handler).GetConnectionReferencesByNameAsync(["new_outlook", "o'brien"]));

        Assert.Equal("Outlook", reference.Label);
        Assert.Equal("shared_office365", reference.Connector);
        Assert.True(reference.HasConnection);
        Assert.Contains("connectionreferencelogicalname eq 'o''brien'", Uri.UnescapeDataString(handler.Requests[0].Url));
    }

    [Fact]
    public void Health_says_what_is_wrong_with_a_reference()
    {
        var connected = new ConnectionInfo { Name = "c1", Status = "Connected" };
        var failing = new ConnectionInfo { Name = "c2", Status = "Error", StatusMessage = "Token expired" };

        Assert.Equal("No connection", new ConnectionReferenceRow { Reference = Reference("a", null) }.HealthLabel);
        Assert.Equal("Connected", new ConnectionReferenceRow { Reference = Reference("a", "c1"), Connection = connected, ConnectionsKnown = true }.HealthLabel);
        Assert.Equal("Error - Token expired", new ConnectionReferenceRow { Reference = Reference("a", "c2"), Connection = failing, ConnectionsKnown = true }.HealthLabel);
        Assert.Equal(ConnectionHealth.NotVisible, new ConnectionReferenceRow { Reference = Reference("a", "c3"), ConnectionsKnown = true }.Health);
        Assert.Equal(ConnectionHealth.Unknown, new ConnectionReferenceRow { Reference = Reference("a", "c3") }.Health);

        Assert.True(new ConnectionReferenceRow { Reference = Reference("a", null) }.IsBroken);
        Assert.False(new ConnectionReferenceRow { Reference = Reference("a", "c3") }.IsBroken);
    }

    [Fact]
    public void Deployment_settings_are_written_as_pac_expects_them()
    {
        var variables = new[]
        {
            new EnvironmentVariableInfo { SchemaName = "new_B", TypeLabel = "Text", CurrentValue = "https://x/?a=1&b=2", HasCurrentValue = true },
            new EnvironmentVariableInfo { SchemaName = "new_A", TypeLabel = "Text" }
        };
        var references = new[] { Reference("new_outlook", "abc"), Reference("new_sharepoint", null, "shared_sharepointonline") };

        var json = DeploymentSettings.Build(variables, references, withValues: true);
        using var doc = JsonDocument.Parse(json);
        var vars = doc.RootElement.GetProperty("EnvironmentVariables");
        var refs = doc.RootElement.GetProperty("ConnectionReferences");

        Assert.Equal("new_A", vars[0].GetProperty("SchemaName").GetString());
        Assert.Equal("", vars[0].GetProperty("Value").GetString());
        Assert.Equal("https://x/?a=1&b=2", vars[1].GetProperty("Value").GetString());
        Assert.Contains("a=1&b=2", json);
        Assert.Equal("abc", refs[0].GetProperty("ConnectionId").GetString());
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_sharepointonline", refs[1].GetProperty("ConnectorId").GetString());
        Assert.Equal((1, 1), DeploymentSettings.Blanks(variables, references, withValues: true));

        var template = JsonDocument.Parse(DeploymentSettings.Build(variables, references, withValues: false)).RootElement;
        Assert.Equal("", template.GetProperty("EnvironmentVariables")[1].GetProperty("Value").GetString());
        Assert.Equal("", template.GetProperty("ConnectionReferences")[0].GetProperty("ConnectionId").GetString());
        Assert.Equal((2, 2), DeploymentSettings.Blanks(variables, references, withValues: false));
    }

    [Fact]
    public async Task Connections_are_listed_with_their_status_and_owner()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/connections?", """
                {"value":[
                  {"name":"abc","properties":{"displayName":"me@contoso.com","apiId":"/providers/Microsoft.PowerApps/apis/shared_office365",
                    "statuses":[{"status":"Error","error":{"message":"Token expired"}}],"createdBy":{"userPrincipalName":"me@contoso.com"}}},
                  {"name":"def","properties":{"statuses":[{"status":"Connected"}]}}
                ]}
                """);

        var connections = await new PowerAutomateClient(TestAuth.Tokens(), handler).GetConnectionsAsync("env-1");

        Assert.Equal(2, connections.Count);
        Assert.Equal("Token expired", connections[0].StatusMessage);
        Assert.Equal("me@contoso.com", connections[0].Owner);
        Assert.False(connections[0].IsConnected);
        Assert.True(connections[1].IsConnected);
        Assert.Contains("environments/env-1/connections", handler.Requests[0].Url);
    }
}
