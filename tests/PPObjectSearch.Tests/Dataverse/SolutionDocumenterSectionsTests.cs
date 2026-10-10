using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>The solution documenter's table, flow, role and dependency sections, and how pages become files.</summary>
public class SolutionDocumenterSectionsTests
{
    private static readonly Guid Table = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid MissingTable = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid Role = Guid.Parse("b0000000-0000-0000-0000-000000000003");
    private static readonly Guid FlowOff = Guid.Parse("b0000000-0000-0000-0000-000000000004");
    private static readonly Guid FlowUnknown = Guid.Parse("b0000000-0000-0000-0000-000000000005");

    private static readonly SolutionInfo Solution = new()
    {
        SolutionId = Guid.NewGuid(), UniqueName = "contoso_core", FriendlyName = "Contoso Core", Version = "1.0.0.0", IsManaged = true
    };

    private static readonly DocumentationOptions Nothing = new()
    {
        Tables = false, Flows = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false, Dependencies = false
    };

    private static SolutionComponentItem Item(string name, int type, Guid id, string typeName, int? category = null, string? schema = null) => new()
    {
        Name = name, ComponentTypeName = typeName, ComponentType = type, ObjectId = id, ProcessCategory = category, SchemaName = schema
    };

    private static Task<IReadOnlyList<DocPage>> Build(FakeHttpHandler handler, IReadOnlyList<SolutionComponentItem> items, DocumentationOptions options,
        IProgress<string>? progress = null) =>
        new SolutionDocumenter(Fakes.Dataverse(handler)).BuildAsync(Solution, items, "Dev | EU", options, progress);

    [Fact]
    public async Task A_table_lists_its_custom_columns_relationships_and_keys()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Table})/Attributes", """
                {"value":[{"LogicalName":"new_size","AttributeType":"Integer","RequiredLevel":{"Value":"ApplicationRequired"}},
                          {"LogicalName":"createdon","AttributeType":"DateTime"},
                          {"LogicalName":"new_colour","AttributeType":"String","DisplayName":{"UserLocalizedLabel":{"Label":"Colour | shade"}}}]}
                """)
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Table})/OneToManyRelationships", """{"value":[{"SchemaName":"new_widget_parts","ReferencingEntity":"new_part"}]}""")
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Table})/ManyToOneRelationships", """{"value":[]}""")
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Table})/ManyToManyRelationships", """{"value":[]}""")
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Table})/Keys", """{"value":[{"LogicalName":"new_code_key","KeyAttributes":["new_code"]}]}""")
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Table})?", $$"""{"MetadataId":"{{Table}}","LogicalName":"new_widget"}""")
            .OnJson(HttpMethod.Get, "EntityDefinitions(", "{}");
        var items = new[]
        {
            Item("new_widget", 1, Table, "Entity", schema: "new_Widget"),
            Item("new_gone", 1, MissingTable, "Entity")
        };

        var pages = await Build(handler, items, new DocumentationOptions
        {
            Flows = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false, Dependencies = false
        });

        var table = Assert.Single(pages, p => p.Section == "Tables");
        Assert.Equal("new_widget", table.Title);
        Assert.Contains("`new_widget`", table.Markdown);
        Assert.Contains("| `new_colour` | Colour \\| shade | String |", table.Markdown);
        Assert.Contains("| `new_size` |  | Integer, required |", table.Markdown);
        Assert.DoesNotContain("createdon", table.Markdown);
        Assert.Contains("**Relationships**", table.Markdown);
        Assert.Contains("| `new_widget_parts` |", table.Markdown);
        Assert.Contains("| `new_code_key` | new_code |", table.Markdown);
        Assert.Contains("| Read from | Dev \\| EU |", pages[0].Markdown);
        Assert.Contains("| Managed | Yes |", pages[0].Markdown);
        Assert.Contains("| Entity | 2 |", pages[0].Markdown);
    }

    [Fact]
    public async Task A_table_without_relationships_or_keys_leaves_those_headings_out()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Table})/", """{"value":[]}""")
            .OnJson(HttpMethod.Get, $"EntityDefinitions({Table})?", """{"LogicalName":"new_widget"}""");

        var pages = await Build(handler, [Item("new_widget", 1, Table, "Entity")], new DocumentationOptions
        {
            Flows = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false, Dependencies = false
        });

        var table = pages.Single(p => p.Section == "Tables").Markdown;
        Assert.Contains("**Columns**", table);
        Assert.DoesNotContain("**Relationships**", table);
        Assert.DoesNotContain("**Alternate keys**", table);
    }

    private static string Workflow(int? state, string? definition) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["statecode"] = state, ["clientdata"] = definition });

    [Fact]
    public async Task Flows_say_whether_they_are_on_and_list_connectors_from_every_branch()
    {
        const string definition = """
            {"properties":{"definition":{
              "triggers":{"Every_day":{"type":"Recurrence","recurrence":{"frequency":"Day","interval":1}}},
              "actions":{
                "Left":{"type":"Compose","inputs":"a"},
                "Right":{"type":"OpenApiConnection","inputs":{"host":{"apiId":"/providers/Microsoft.PowerApps/apis/shared_teams","operationId":"PostMessage"}}},
                "Check":{"type":"If","runAfter":{"Left":["Succeeded"],"Right":["Succeeded"]},"expression":"@true",
                  "actions":{"Mail":{"type":"OpenApiConnection","inputs":{"host":{"apiId":"/providers/Microsoft.PowerApps/apis/shared_office365","operationId":"SendEmailV2"}}}},
                  "else":{"actions":{}}}
              }}}}
            """;
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"workflows({FlowOff})", Workflow(0, definition))
            .OnJson(HttpMethod.Get, $"workflows({FlowUnknown})", Workflow(null, null));
        var items = new[]
        {
            Item("Daily digest", 29, FlowOff, "Process", category: 5),
            Item("Abandoned", 29, FlowUnknown, "Process", category: 5)
        };

        var pages = await Build(handler, items, new DocumentationOptions { Tables = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false, Dependencies = false });

        var flows = pages.Where(p => p.Section == "Cloud flows").ToList();
        Assert.Equal(["Abandoned", "Daily digest"], flows.Select(p => p.Title));
        Assert.Contains("State: unknown", flows[0].Markdown);
        Assert.Contains("_No definition is stored in Dataverse._", flows[0].Markdown);
        Assert.Contains("State: off", flows[1].Markdown);
        Assert.Contains("**Connectors:** Microsoft Teams, Office 365 Outlook", flows[1].Markdown);
        Assert.Contains("**Actions:** 4", flows[1].Markdown);
    }

    [Fact]
    public async Task A_flow_with_no_trigger_or_connectors_says_none()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"workflows({FlowOff})",
            Workflow(1, """{"definition":{"actions":{"Only":{"type":"Compose","inputs":1}}}}"""));

        var pages = await Build(handler, [Item("Bare", 29, FlowOff, "Process", category: 5)],
            new DocumentationOptions { Tables = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false, Dependencies = false });

        var flow = pages.Single(p => p.Section == "Cloud flows").Markdown;
        Assert.Contains("**Trigger:** none", flow);
        Assert.Contains("**Connectors:** none", flow);
    }

    private const string Privileges = """
        {"RolePrivileges":[
          {"PrivilegeId":"c0000000-0000-0000-0000-000000000001","PrivilegeName":"prvReadnew_Widget","Depth":"Global"},
          {"PrivilegeId":"c0000000-0000-0000-0000-000000000002","PrivilegeName":"prvWriteAccount","Depth":"Basic"},
          {"PrivilegeId":"c0000000-0000-0000-0000-000000000003","PrivilegeName":"prvExportToExcel","Depth":"Local"}]}
        """;

    [Fact]
    public async Task A_role_shows_only_the_solutions_tables_when_the_solution_has_some()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"RetrieveRolePrivilegesRole(RoleId={Role})", Privileges);
        var items = new[] { Item("new_widget", 1, Table, "Entity", schema: "new_Widget"), Item("Widget manager", 20, Role, "Role") };

        var pages = await Build(handler, items, new DocumentationOptions
        {
            Tables = false, Flows = false, EnvironmentVariables = false, PluginSteps = false, Dependencies = false
        });

        var role = pages.Single(p => p.Section == "Security roles").Markdown;
        Assert.Contains("Privileges on this solution's tables:", role);
        Assert.Contains("| new_Widget |  | Organization |  |", role);
        Assert.DoesNotContain("Account", role);
    }

    [Fact]
    public async Task A_role_in_a_solution_without_tables_shows_every_table_it_reaches()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"RetrieveRolePrivilegesRole(RoleId={Role})", Privileges);

        var pages = await Build(handler, [Item("Widget manager", 20, Role, "Role")], new DocumentationOptions
        {
            Flows = false, EnvironmentVariables = false, PluginSteps = false, Dependencies = false
        });

        var role = pages.Single(p => p.Section == "Security roles").Markdown;
        Assert.Contains("Every table the role reaches:", role);
        Assert.Contains("| Account |  |  | User |", role);
        Assert.Contains("| new_Widget |", role);
    }

    [Fact]
    public async Task Missing_dependencies_are_listed_by_component_with_what_needs_them()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "RetrieveMissingDependencies", $$"""
            {"EntityCollection":{"Entities":[
              {"requiredcomponentobjectid":"{{MissingTable}}","requiredcomponenttype":1,"dependentcomponentobjectid":"{{Role}}","dependentcomponenttype":20},
              {"requiredcomponentobjectid":"{{MissingTable}}","requiredcomponenttype":1,"dependentcomponentobjectid":"{{FlowOff}}","dependentcomponenttype":29},
              {"requiredcomponentobjectid":"{{MissingTable}}","requiredcomponenttype":1,"dependentcomponentobjectid":"{{FlowUnknown}}","dependentcomponenttype":29}]}
            }
            """);

        var pages = await Build(handler, [], new DocumentationOptions { Tables = false, Flows = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false });

        var index = Assert.Single(pages).Markdown;
        Assert.Contains("## Depends on", index);
        Assert.Contains($"| Table | `{MissingTable}` | Security Role, Process |", index);
    }

    [Fact]
    public async Task Each_section_reports_progress_and_a_cancelled_build_is_not_swallowed()
    {
        var progress = new List<string>();
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "RetrieveMissingDependencies", _ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(handler, [], new DocumentationOptions(), new SyncProgress(progress)));

        Assert.Equal(
            ["Documenting tables...", "Documenting cloud flows...", "Documenting environment variables...", "Documenting plug-in steps...",
             "Documenting security roles...", "Documenting dependencies..."],
            progress);
    }

    [Fact]
    public async Task Nothing_chosen_documents_only_the_summary()
    {
        var handler = new FakeHttpHandler();

        var pages = await Build(handler, [Item("Widget manager", 20, Role, "Role")], Nothing);

        Assert.Single(pages);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("## Depends on", pages[0].Markdown);
    }

    [Fact]
    public void Pages_with_the_same_file_name_are_numbered()
    {
        var pages = new[]
        {
            new DocPage("index", "Core", "# Core\n"),
            new DocPage("Tables", "Widget", "# Widget\n"),
            new DocPage("Tables", "widget", "# widget\n"),
            new DocPage("Tables", "WIDGET", "# WIDGET\n")
        };

        var files = SolutionDocumenter.Render(pages, filePerComponent: true);

        Assert.Equal(["README.md", "tables/widget-2.md", "tables/widget-3.md", "tables/widget.md"], files.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("- [WIDGET](tables/widget-3.md)", files["README.md"]);
    }

    [Theory]
    [InlineData("", "untitled")]
    [InlineData("  ***  ", "untitled")]
    [InlineData("A -- B", "a-b")]
    [InlineData("new_widget-2", "new_widget-2")]
    public void A_slug_keeps_only_safe_characters(string text, string expected)
    {
        Assert.Equal(expected, DocPage.Slug(text));
    }

    [Fact]
    public void A_cell_flattens_line_breaks_and_escapes_pipes()
    {
        Assert.Equal("a\\|b c  d", SolutionDocumenter.Cell("a|b\nc\r\nd"));
        Assert.Equal(string.Empty, SolutionDocumenter.Cell(null));
    }

    /// <summary>Reports progress on the calling thread, so the list is complete when the build returns.</summary>
    private sealed class SyncProgress(List<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Add(value);
    }
}
