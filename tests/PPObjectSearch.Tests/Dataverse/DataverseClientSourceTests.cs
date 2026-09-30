using System.Net.Http;
using System.Text;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class DataverseClientSourceTests
{
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private static string WebResource(int type, string? base64, string? name = "new_/script.js", string? label = null)
    {
        var fields = new Dictionary<string, object?> { ["webresourcetype"] = type, ["content"] = base64 };
        if (name is not null) fields["name"] = name;
        if (label is not null) fields["webresourcetype" + Formatted] = label;
        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public async Task Text_web_resource_is_decoded_as_utf8()
    {
        var id = Guid.NewGuid();
        var source = "console.log('héllo');";
        var bytes = Encoding.UTF8.GetBytes(source);
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"webresourceset({id})",
            WebResource(3, Convert.ToBase64String(bytes), label: "Script (JScript)"));
        using var client = Fakes.Dataverse(handler);

        var content = await client.GetWebResourceContentAsync(id);

        Assert.Equal("new_/script.js", content.Name);
        Assert.Equal(3, content.Type);
        Assert.Equal("Script (JScript)", content.TypeLabel);
        Assert.Equal(source, content.Text);
        Assert.Equal(bytes.Length, content.ByteCount);
        Assert.Equal(".js", content.FileExtension);
        Assert.Contains("$select=name,webresourcetype,content", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Byte_order_mark_is_dropped_from_text()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("<root/>")).ToArray();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "webresourceset(", WebResource(4, Convert.ToBase64String(bytes)));
        using var client = Fakes.Dataverse(handler);

        var content = await client.GetWebResourceContentAsync(Guid.NewGuid());

        Assert.Equal("<root/>", content.Text);
        Assert.Equal(bytes.Length, content.ByteCount);
    }

    [Fact]
    public async Task Binary_web_resource_reports_only_its_size()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "webresourceset(", WebResource(5, Convert.ToBase64String(bytes)));
        using var client = Fakes.Dataverse(handler);

        var content = await client.GetWebResourceContentAsync(Guid.NewGuid());

        Assert.Null(content.Text);
        Assert.Equal(7, content.ByteCount);
        Assert.Equal("Type 5", content.TypeLabel);
        Assert.Equal(string.Empty, content.FileExtension);
    }

    [Fact]
    public async Task Empty_content_is_zero_bytes_and_name_falls_back_to_id()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "webresourceset(", WebResource(1, null, name: null));
        using var client = Fakes.Dataverse(handler);

        var content = await client.GetWebResourceContentAsync(id);

        Assert.Equal(id.ToString(), content.Name);
        Assert.Equal(0, content.ByteCount);
        Assert.Equal(string.Empty, content.Text);
    }

    [Fact]
    public async Task Invalid_base64_throws_a_dataverse_exception()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "webresourceset(", WebResource(3, "!!!not base64!!!"));
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.GetWebResourceContentAsync(Guid.NewGuid()));

        Assert.Contains("not valid base64", ex.Message);
    }

    [Theory]
    [InlineData(1, true, ".html")]
    [InlineData(2, true, ".css")]
    [InlineData(3, true, ".js")]
    [InlineData(4, true, ".xml")]
    [InlineData(5, false, "")]
    [InlineData(6, false, "")]
    [InlineData(7, false, "")]
    [InlineData(8, false, "")]
    [InlineData(9, true, ".xsl")]
    [InlineData(10, false, "")]
    [InlineData(11, true, ".svg")]
    [InlineData(12, true, ".resx")]
    public void Text_types_and_extensions(int type, bool isText, string extension)
    {
        Assert.Equal(isText, DataverseClient.IsTextWebResourceType(type));
        Assert.Equal(extension, new WebResourceContent("n", type, "l", null, 0).FileExtension);
    }

    [Fact]
    public async Task Cloud_flow_definition_is_the_workflow_client_data()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"workflows({id})?$select=clientdata",
            "{\"clientdata\":\"{\\\"properties\\\":{}}\"}");
        using var client = Fakes.Dataverse(handler);

        Assert.Equal("{\"properties\":{}}", await client.GetCloudFlowDefinitionAsync(id));
    }

    [Fact]
    public async Task Cloud_flow_without_client_data_is_null()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "workflows(", "{\"clientdata\":null}");
        using var client = Fakes.Dataverse(handler);

        Assert.Null(await client.GetCloudFlowDefinitionAsync(Guid.NewGuid()));
    }
}
