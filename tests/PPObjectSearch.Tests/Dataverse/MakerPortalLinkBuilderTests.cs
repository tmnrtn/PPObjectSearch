using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Tests.Dataverse;

public class MakerPortalLinkBuilderTests
{
    private const string EnvId = "env-123";
    private const string EnvUrl = "https://contoso.crm11.dynamics.com";
    private const string Maker = "https://make.powerapps.com";

    private static readonly Guid SolutionId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ObjectId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid AccountMetadataId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid WorkflowUnique = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private static readonly Dictionary<string, TableMetadata> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account"] = new TableMetadata(AccountMetadataId, "accounts"),
        ["roleeditorlayout"] = new TableMetadata(Guid.NewGuid(), "roleeditorlayouts"),
        ["webresource"] = new TableMetadata(Guid.NewGuid(), "webresourceset"),
        ["nosetname"] = new TableMetadata(Guid.NewGuid(), null)
    };

    private static MakerPortalLinkBuilder Builder(IDictionary<string, string>? overrides = null, string? envId = EnvId) =>
        new(envId, EnvUrl + "/", overrides, Tables);

    private static SolutionComponentItem Item(int type, string? logicalName = null, Guid? objectId = null,
        string? subType = null, string? primaryEntity = null, Guid? workflowIdUnique = null, string name = "thing") =>
        new()
        {
            Name = name,
            ComponentTypeName = "x",
            ComponentType = type,
            ComponentLogicalName = logicalName,
            ObjectId = objectId ?? ObjectId,
            SubType = subType,
            PrimaryEntityName = primaryEntity,
            WorkflowIdUnique = workflowIdUnique
        };

    private static string SolutionRoot => $"{Maker}/environments/{EnvId}/solutions/{SolutionId}";

    [Fact]
    public void Table_opens_the_table_designer_by_its_own_object_id()
    {
        var url = Builder().Build(Item(1, "entity"), SolutionId);

        Assert.Equal($"{Maker}/environments/{EnvId}/entities/{ObjectId}", url);
    }

    [Fact]
    public void Column_opens_its_parent_tables_designer()
    {
        var url = Builder().Build(Item(2, "attribute", primaryEntity: "Account"), SolutionId);

        Assert.Equal($"{Maker}/environments/{EnvId}/entities/{AccountMetadataId}", url);
    }

    [Fact]
    public void Column_whose_parent_table_is_unknown_falls_back_to_the_solution()
    {
        var url = Builder().Build(Item(2, "attribute", primaryEntity: "unknowntable"), SolutionId);

        Assert.Equal(SolutionRoot, url);
    }

    [Theory]
    [InlineData("Modern Flow")]
    [InlineData("Cloud Flow")]
    [InlineData("modern flow")]
    public void Cloud_flow_uses_the_cloudflows_segment(string subType)
    {
        var url = Builder().Build(Item(29, "workflow", subType: subType), SolutionId);

        Assert.Equal($"{SolutionRoot}/objects/cloudflows/{ObjectId}/view", url);
    }

    [Theory]
    [InlineData("Workflow")]
    [InlineData(null)]
    public void Classic_process_lands_on_the_workflows_type_list(string? subType)
    {
        var url = Builder().Build(Item(29, "workflow", subType: subType), SolutionId);

        Assert.Equal($"{SolutionRoot}/objects/workflows", url);
    }

    [Fact]
    public void Business_rule_opens_in_the_classic_process_editor()
    {
        var url = Builder().Build(Item(29, "workflow", subType: "Business Rule"), SolutionId);

        Assert.Equal($"{EnvUrl}/sfa/workflow/edit.aspx?id=%7b{ObjectId}%7d", url);
    }

    [Fact]
    public void Business_process_flow_opens_in_the_process_designer()
    {
        var url = Builder().Build(Item(29, "workflow", subType: "Business Process Flow"), SolutionId);

        Assert.Equal($"{EnvUrl}/Tools/ProcessControl/UnifiedProcessDesigner.aspx?id={ObjectId}", url);
    }

    [Fact]
    public void Agent_opens_in_Copilot_Studio()
    {
        var url = Builder().Build(Item(10100, "bot"), SolutionId);

        Assert.Equal($"https://copilotstudio.microsoft.com/environments/{EnvId}/bots/{ObjectId}/overview", url);
    }

    [Fact]
    public void Canvas_app_plays_in_its_clouds_player()
    {
        Assert.Equal($"https://apps.powerapps.com/play/e/{EnvId}/a/{ObjectId}",
            MakerPortalLinkBuilder.BuildPlayUrl(EnvId, ObjectId));
        Assert.Equal($"https://apps.high.powerapps.us/play/e/{EnvId}/a/{ObjectId}",
            MakerPortalLinkBuilder.BuildPlayUrl(EnvId, ObjectId, PPObjectSearch.Core.Clouds.UsGccHigh));
        Assert.Null(MakerPortalLinkBuilder.BuildPlayUrl(null, ObjectId));
    }

    [Fact]
    public void Generic_component_uses_its_types_entity_set_name()
    {
        var url = Builder().Build(Item(10050, "roleeditorlayout"), SolutionId);

        Assert.Equal($"{SolutionRoot}/objects/roleeditorlayouts/{ObjectId}/view", url);
    }

    [Fact]
    public void Entity_set_lookup_is_case_insensitive_on_logical_name()
    {
        var url = Builder().Build(Item(61, "WebResource"), SolutionId);

        Assert.Equal($"{SolutionRoot}/objects/webresourceset/{ObjectId}/view", url);
    }

    [Fact]
    public void Component_without_an_object_id_falls_back_to_its_types_list()
    {
        var url = Builder().Build(Item(10050, "roleeditorlayout", objectId: Guid.Empty), SolutionId);

        Assert.Equal($"{SolutionRoot}/objects/roleeditorlayouts", url);
    }

    [Theory]
    [InlineData("unheardof")]
    [InlineData("nosetname")]
    [InlineData(null)]
    public void Unresolvable_type_falls_back_to_the_solution_page(string? logicalName)
    {
        var url = Builder().Build(Item(10050, logicalName), SolutionId);

        Assert.Equal(SolutionRoot, url);
    }

    [Fact]
    public void Model_driven_app_opens_the_app_designer()
    {
        var url = Builder().Build(Item(80, "appmodule"), SolutionId);

        Assert.Equal($"{Maker}/e/{EnvId}/s/{SolutionId}/app/edit/{ObjectId}", url);
    }

    [Fact]
    public void Canvas_app_opens_the_studio()
    {
        var url = Builder().Build(Item(300, "canvasapp"), SolutionId);

        Assert.Equal(
            $"{Maker}/e/{EnvId}/canvas?action=edit&app-id=%2Fproviders%2FMicrosoft.PowerApps%2Fapps%2F{ObjectId}", url);
    }

    [Fact]
    public void Override_by_component_type_number_wins_over_the_default_route()
    {
        var overrides = new Dictionary<string, string> { ["1"] = "{envUrl}/main.aspx?etn={logicalName}&id={objectId}" };

        var url = Builder(overrides).Build(Item(1, "entity"), SolutionId);

        Assert.Equal($"{EnvUrl}/main.aspx?etn=entity&id={ObjectId}", url);
    }

    [Fact]
    public void Override_by_logical_name_is_case_insensitive()
    {
        var overrides = new Dictionary<string, string> { ["ROLEEDITORLAYOUT"] = "https://x/{entitySet}/{name}" };

        var url = Builder(overrides).Build(Item(10050, "roleeditorlayout", name: "My layout"), SolutionId);

        Assert.Equal("https://x/roleeditorlayouts/My%20layout", url);
    }

    [Fact]
    public void Override_template_fills_every_placeholder()
    {
        const string template =
            "{envId}|{envUrl}|{solutionId}|{objectId}|{componentType}|{entitySet}|{primaryEntityId}|" +
            "{primaryEntity}|{workflowIdUnique}|{logicalName}|{name}";
        var overrides = new Dictionary<string, string> { ["29"] = template };

        var item = Item(29, "workflow", subType: "Modern Flow", primaryEntity: "account",
            workflowIdUnique: WorkflowUnique, name: "A & B");

        var url = Builder(overrides).Build(item, SolutionId);

        Assert.Equal(
            $"{EnvId}|{EnvUrl}|{SolutionId}|{ObjectId}|29|cloudflows|{AccountMetadataId}|account|{WorkflowUnique}|workflow|A%20%26%20B",
            url);
    }

    [Fact]
    public void Override_needing_a_missing_value_steps_down_to_the_type_list()
    {
        var overrides = new Dictionary<string, string> { ["29"] = "https://flow/{workflowIdUnique}" };

        var url = Builder(overrides).Build(Item(29, "workflow", subType: "Modern Flow"), SolutionId);

        Assert.Equal($"{SolutionRoot}/objects/cloudflows", url);
    }

    [Fact]
    public void Environment_id_is_trimmed_and_escaped()
    {
        var builder = new MakerPortalLinkBuilder("  env 1  ", EnvUrl, null, Tables);

        var url = builder.Build(Item(1, "entity"), SolutionId);

        Assert.Equal($"{Maker}/environments/env%201/entities/{ObjectId}", url);
        Assert.Equal("env 1", builder.EnvironmentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_environment_id_means_no_links(string? envId)
    {
        var builder = Builder(envId: envId);

        Assert.False(builder.CanBuildLinks);
        Assert.Null(builder.EnvironmentId);
        Assert.Null(builder.Build(Item(1, "entity"), SolutionId));
    }

    [Fact]
    public void Environment_id_present_can_build_links()
    {
        Assert.True(Builder().CanBuildLinks);
    }

    [Fact]
    public void Builder_without_table_metadata_still_links_known_routes()
    {
        var builder = new MakerPortalLinkBuilder(EnvId, EnvUrl, null);

        Assert.Equal($"{Maker}/environments/{EnvId}/entities/{ObjectId}", builder.Build(Item(1, "entity"), SolutionId));
        Assert.Equal(SolutionRoot, builder.Build(Item(61, "webresource"), SolutionId));
    }

    [Fact]
    public void Flow_run_url_points_at_power_automate()
    {
        var url = MakerPortalLinkBuilder.BuildFlowRunUrl(" env-1 ", "flow-2", "08585 run");

        Assert.Equal("https://make.powerautomate.com/environments/env-1/flows/flow-2/runs/08585%20run", url);
    }

    [Theory]
    [InlineData(null, "f", "r")]
    [InlineData("e", "", "r")]
    [InlineData("e", "f", " ")]
    public void Flow_run_url_is_null_when_any_part_is_missing(string? env, string? flow, string? run)
    {
        Assert.Null(MakerPortalLinkBuilder.BuildFlowRunUrl(env, flow, run));
    }
}
