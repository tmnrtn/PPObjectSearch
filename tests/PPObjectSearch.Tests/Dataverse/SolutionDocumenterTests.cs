using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class SolutionDocumenterTests
{
    private static readonly Guid Flow = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Variable = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid Secret = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid Step = Guid.Parse("a0000000-0000-0000-0000-000000000004");

    private static readonly SolutionInfo Solution = new()
    {
        SolutionId = Guid.NewGuid(), UniqueName = "contoso_core", FriendlyName = "Contoso Core", Version = "1.2.0.0", PublisherName = "Contoso"
    };

    private static SolutionComponentItem Item(string name, int type, Guid id, int? category = null) => new()
    {
        Name = name, ComponentTypeName = type switch { 29 => "Process", 380 => "Environment Variable Definition", _ => "Plug-in step" },
        ComponentType = type, ObjectId = id, ProcessCategory = category
    };

    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, $"workflows({Flow})", """
            {"statecode":1,"clientdata":"{\"properties\":{\"definition\":{\"triggers\":{\"When_a_row_is_added\":{\"type\":\"OpenApiConnectionWebhook\",\"inputs\":{\"host\":{\"apiId\":\"/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps\",\"operationId\":\"SubscribeWebhookTrigger\"}}}},\"actions\":{\"Send_an_email\":{\"type\":\"OpenApiConnection\",\"inputs\":{\"host\":{\"apiId\":\"/providers/Microsoft.PowerApps/apis/shared_office365\",\"operationId\":\"SendEmailV2\"}}}}}}}"}
            """)
        .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Variable})", """
            {"schemaname":"contoso_ApiUrl","type":100000000,"type@OData.Community.Display.V1.FormattedValue":"Text","defaultvalue":"https://dev|api",
             "environmentvariabledefinition_environmentvariablevalue":[]}
            """)
        .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Secret})", """
            {"schemaname":"contoso_Key","type":100000005,"defaultvalue":"/subscriptions/x/vaults/v/secrets/s",
             "environmentvariabledefinition_environmentvariablevalue":[{"environmentvariablevalueid":"a0000000-0000-0000-0000-000000000009","value":"/subscriptions/x/vaults/v/secrets/prod"}]}
            """)
        .OnJson(HttpMethod.Get, "sdkmessageprocessingsteps?", $$$"""
            {"value":[{"sdkmessageprocessingstepid":"{{{Step}}}","name":"Contoso.Validate: Update of account","stage":20,
              "stage@OData.Community.Display.V1.FormattedValue":"Pre-operation","mode":0,"mode@OData.Community.Display.V1.FormattedValue":"Synchronous",
              "rank":1,"filteringattributes":"name,accountnumber","_sdkmessageid_value@OData.Community.Display.V1.FormattedValue":"Update",
              "sdkmessagefilterid":{"primaryobjecttypecode":"account"}}]}
            """)
        .OnJson(HttpMethod.Get, "RetrieveMissingDependencies", """{"EntityCollection":[]}""");

    private static IReadOnlyList<SolutionComponentItem> Items() =>
    [
        Item("Notify on new account", 29, Flow, category: 5),
        Item("contoso_ApiUrl", 380, Variable),
        Item("contoso_Key", 380, Secret),
        Item("Contoso.Validate: Update of account", 92, Step)
    ];

    [Fact]
    public async Task Each_section_is_documented_and_secrets_are_redacted()
    {
        var pages = await new SolutionDocumenter(Fakes.Dataverse(Handler()))
            .BuildAsync(Solution, Items(), "Dev", new DocumentationOptions());

        var index = pages[0].Markdown;
        Assert.Contains("# Contoso Core", index);
        Assert.Contains("| Version | 1.2.0.0 |", index);
        Assert.Contains("| Environment Variable Definition | 2 |", index);
        Assert.Contains("Nothing outside the solution", index);
        Assert.DoesNotContain("Not documented", index);

        var flow = pages.Single(p => p.Section == "Cloud flows").Markdown;
        Assert.Contains("State: on", flow);
        Assert.DoesNotContain("**Connectors:** none", flow);
        Assert.Contains("**Connectors:**", flow);
        Assert.Contains("```mermaid", flow);

        var variables = pages.Single(p => p.Section == "Environment variables").Markdown;
        Assert.Contains("`https://dev\\|api`", variables);
        Assert.Contains("_not set_", variables);
        Assert.DoesNotContain("vaults", variables);
        Assert.Contains("_(secret - redacted)_", variables);

        var steps = pages.Single(p => p.Section == "Plug-in steps").Markdown;
        Assert.Contains("| Update | account | Pre-operation | Synchronous | 1 | name,accountnumber |", steps);
    }

    [Fact]
    public async Task Sections_can_be_left_out_and_a_failing_one_is_named()
    {
        var options = new DocumentationOptions { Flows = false, PluginSteps = false, Dependencies = false };
        var handler = new FakeHttpHandler(); // nothing answers: every read fails

        var pages = await new SolutionDocumenter(Fakes.Dataverse(handler)).BuildAsync(Solution, Items(), "Dev", options);

        Assert.Single(pages);
        Assert.Contains("## Not documented", pages[0].Markdown);
        Assert.Contains("environment variables:", pages[0].Markdown);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("sdkmessageprocessingsteps") || r.Url.Contains("workflows"));
    }

    [Fact]
    public async Task Output_is_one_file_or_an_index_with_a_file_per_component()
    {
        var pages = await new SolutionDocumenter(Fakes.Dataverse(Handler()))
            .BuildAsync(Solution, Items(), "Dev", new DocumentationOptions());

        var single = SolutionDocumenter.Render(pages, filePerComponent: false);
        var readme = Assert.Single(single).Value;
        Assert.Contains("## Cloud flows", readme);
        Assert.Contains("### Notify on new account", readme);

        var many = SolutionDocumenter.Render(pages, filePerComponent: true);
        Assert.Contains("cloud-flows/notify-on-new-account.md", many.Keys);
        Assert.Contains("- [Notify on new account](cloud-flows/notify-on-new-account.md)", many["README.md"]);
        Assert.Equal("contoso-validate-update-of-account", DocPage.Slug("Contoso.Validate: Update of account"));
    }
}
