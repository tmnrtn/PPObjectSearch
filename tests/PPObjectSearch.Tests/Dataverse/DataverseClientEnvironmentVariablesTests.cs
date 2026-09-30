using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class DataverseClientEnvironmentVariablesTests
{
    private const string Api = Fakes.ApiRoot;
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";
    private const string Values = "environmentvariabledefinition_environmentvariablevalue";

    [Fact]
    public async Task Definition_with_a_value_record_resolves_current_value()
    {
        var definitionId = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"environmentvariabledefinitions({definitionId})",
            "{\"schemaname\":\"new_ApiUrl\",\"displayname\":\"API URL\",\"description\":\"Where to call\",\"type\":100000000," +
            "\"type" + Formatted + "\":\"String\",\"defaultvalue\":\"https://default\"," +
            $"\"{Values}\":[{{\"environmentvariablevalueid\":\"x\",\"value\":\"https://current\"}}]}}");
        using var client = Fakes.Dataverse(handler);

        var info = await client.GetEnvironmentVariableAsync(definitionId, isValueRecord: false);

        Assert.Equal("new_ApiUrl", info.SchemaName);
        Assert.Equal("API URL", info.DisplayName);
        Assert.Equal("Where to call", info.Description);
        Assert.Equal("String", info.TypeLabel);
        Assert.False(info.IsSecret);
        Assert.Equal("https://default", info.DefaultValue);
        Assert.Equal("https://current", info.CurrentValue);
        Assert.True(info.HasCurrentValue);
        Assert.Equal(1, info.ValueRecordCount);
        Assert.Equal("https://current", info.EffectiveValue);

        var request = Assert.Single(handler.Requests);
        Assert.Contains($"$expand={Values}($select=environmentvariablevalueid,value)", request.Url);
        Assert.Contains("FormattedValue", request.Header("Prefer"));
    }

    [Fact]
    public async Task Definition_without_value_records_falls_back_to_default()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "environmentvariabledefinitions(",
            $"{{\"schemaname\":\"new_X\",\"type\":100000001,\"defaultvalue\":\"42\",\"{Values}\":[]}}");
        using var client = Fakes.Dataverse(handler);

        var info = await client.GetEnvironmentVariableAsync(Guid.NewGuid(), false);

        Assert.False(info.HasCurrentValue);
        Assert.Equal(0, info.ValueRecordCount);
        Assert.Null(info.CurrentValue);
        Assert.Equal("100000001", info.TypeLabel);
        Assert.Equal("42", info.EffectiveValue);
    }

    [Fact]
    public async Task Multiple_value_records_are_counted_and_the_first_wins()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "environmentvariabledefinitions(",
            $"{{\"schemaname\":\"new_X\",\"{Values}\":[{{\"value\":\"first\"}},{{\"value\":\"second\"}}]}}");
        using var client = Fakes.Dataverse(handler);

        var info = await client.GetEnvironmentVariableAsync(Guid.NewGuid(), false);

        Assert.Equal(2, info.ValueRecordCount);
        Assert.Equal("first", info.CurrentValue);
        Assert.Equal("Unknown", info.TypeLabel);
    }

    [Fact]
    public async Task Secret_type_is_flagged()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "environmentvariabledefinitions(",
            "{\"schemaname\":\"new_Secret\",\"type\":100000005}");
        using var client = Fakes.Dataverse(handler);

        var info = await client.GetEnvironmentVariableAsync(Guid.NewGuid(), false);

        Assert.True(info.IsSecret);
    }

    [Fact]
    public async Task Value_record_id_is_resolved_to_its_definition_first()
    {
        var valueId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"environmentvariablevalues({valueId})",
                $"{{\"_environmentvariabledefinitionid_value\":\"{definitionId}\"}}")
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({definitionId})", "{\"schemaname\":\"new_Resolved\"}");
        using var client = Fakes.Dataverse(handler);

        var info = await client.GetEnvironmentVariableAsync(valueId, isValueRecord: true);

        Assert.Equal("new_Resolved", info.SchemaName);
        Assert.Equal(Api + $"environmentvariablevalues({valueId})?$select=_environmentvariabledefinitionid_value", handler.Requests[0].Url);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Value_record_without_a_definition_throws()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "environmentvariablevalues(", "{}");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.GetEnvironmentVariableAsync(Guid.NewGuid(), true));

        Assert.Contains("not attached to an environment variable definition", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_schema_name_reads_as_empty()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "environmentvariabledefinitions(", "{}");
        using var client = Fakes.Dataverse(handler);

        var info = await client.GetEnvironmentVariableAsync(Guid.NewGuid(), false);

        Assert.Equal(string.Empty, info.SchemaName);
    }

    [Fact]
    public async Task Definition_read_failure_propagates()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "environmentvariabledefinitions(", HttpStatusCode.NotFound, "gone");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.GetEnvironmentVariableAsync(Guid.NewGuid(), false));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }
}
