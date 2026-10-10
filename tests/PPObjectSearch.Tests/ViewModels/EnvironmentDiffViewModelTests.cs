using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>One component's definition in two environments: its layer stacks, and what the status line makes of them.</summary>
public class EnvironmentDiffViewModelTests
{
    private const string ProdUrl = "https://prod.crm11.dynamics.com";

    private static readonly Guid Script = Guid.Parse("a0000000-0000-0000-0000-000000000001");

    private static SolutionComponentItem Item(Guid? id = null, int type = 61) =>
        new() { Name = "new_script.js", ComponentTypeName = "Web Resource", ComponentType = type, ObjectId = id ?? Script };

    private static string Layers(params (int Order, string Solution, string? Json)[] layers) => JsonSerializer.Serialize(new
    {
        value = layers.Select(l => new Dictionary<string, object?>
        {
            ["msdyn_order"] = l.Order, ["msdyn_solutionname"] = l.Solution, ["msdyn_componentjson"] = l.Json
        })
    });

    private static FakeHttpHandler Answering(string layers) => new FakeHttpHandler().OnJson(HttpMethod.Get, "msdyn_componentlayers", layers);

    private static EnvironmentDiffViewModel Diff(FakeHttpHandler left, FakeHttpHandler right, SolutionComponentItem? leftItem = null,
        SolutionComponentItem? rightItem = null, string type = "Web Resource") =>
        new("new_script.js", type,
            new EnvironmentDiffSide("dev", Fakes.Dataverse(left), leftItem ?? Item(), EnvironmentSku.Sandbox),
            new EnvironmentDiffSide("prod", Fakes.Dataverse(right, ProdUrl), rightItem ?? Item(), EnvironmentSku.Production));

    [Fact]
    public void The_window_names_the_component_both_environments_and_how_they_were_paired()
    {
        var vm = Diff(new FakeHttpHandler(), new FakeHttpHandler());

        Assert.Equal("new_script.js - dev vs prod", vm.Title);
        Assert.Equal(("dev", "prod"), (vm.BeforeHeading, vm.AfterHeading));
        Assert.Equal(EnvironmentSku.Sandbox, vm.BeforeMarker);
        Assert.Equal(EnvironmentSku.Production, vm.AfterMarker);
        Assert.Equal(("new_script.js", "Web Resource"), (vm.ComponentLabel, vm.ComponentTypeName));
        Assert.Equal($"Matched on object id {Script}", vm.IdentityLabel);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void Components_created_separately_are_said_to_be_matched_by_name()
    {
        var other = Guid.NewGuid();

        var vm = Diff(new FakeHttpHandler(), new FakeHttpHandler(), rightItem: Item(other));

        Assert.Equal($"Matched by name - ids differ ({Script} / {other})", vm.IdentityLabel);
    }

    [Fact]
    public async Task The_top_layer_on_each_side_is_compared_and_the_stacks_are_kept()
    {
        var left = Answering(Layers((1, "Active", """{"content":"old","name":"s"}"""), (2, "Core", """{"content":"new","name":"s"}""")));
        var right = Answering(Layers((1, "Core", """{"content":"old","name":"s"}""")));
        var vm = Diff(left, right);

        await vm.LoadAsync();

        Assert.Equal(["Core", "Active"], vm.LeftLayers.Select(l => l.SolutionName));
        Assert.Single(vm.RightLayers);
        var change = Assert.Single(vm.Changes);
        Assert.Equal("content", change.PropertyName);
        Assert.Same(change, vm.SelectedChange);
        Assert.Equal("1 change", vm.ChangeCountLabel);
        Assert.Equal("1 property differs. dev: 2 layer(s), prod: 1 layer(s).", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Contains($"msdyn_componentid eq '{Script}'", left.Requests[0].Url);
        Assert.StartsWith(ProdUrl, right.Requests[0].Url);
    }

    [Fact]
    public async Task Several_differences_are_counted()
    {
        var vm = Diff(Answering(Layers((1, "Core", """{"a":1,"b":2}"""))), Answering(Layers((1, "Core", """{"a":3,"b":4}"""))));

        await vm.LoadAsync();

        Assert.Equal("2 properties differ. dev: 1 layer(s), prod: 1 layer(s).", vm.Status);
        Assert.Equal("2 changes", vm.ChangeCountLabel);
    }

    [Fact]
    public async Task Matching_definitions_say_so()
    {
        var vm = Diff(Answering(Layers((1, "Core", """{"a":1}"""))), Answering(Layers((1, "Core", """{"a":1}"""))));

        await vm.LoadAsync();

        Assert.Empty(vm.Changes);
        Assert.Equal("The definitions match. dev: 1 layer(s), prod: 1 layer(s).", vm.Status);
    }

    [Fact]
    public async Task A_side_whose_layers_cannot_be_read_is_named()
    {
        var left = new FakeHttpHandler().OnError(HttpMethod.Get, "msdyn_componentlayers", HttpStatusCode.Forbidden, "no layers here");
        var vm = Diff(left, Answering(Layers((1, "Core", """{"a":1}"""))));

        await vm.LoadAsync();

        Assert.StartsWith("Could not read layers - dev: ", vm.Status);
        Assert.Contains("no layers here", vm.Status);
        Assert.Empty(vm.LeftLayers);
        Assert.Single(vm.RightLayers);
    }

    [Fact]
    public async Task A_type_without_solution_layers_cannot_be_compared()
    {
        var left = new FakeHttpHandler();
        var vm = Diff(left, new FakeHttpHandler(), Item(type: 12345), Item(type: 12345), type: "Mystery");

        await vm.LoadAsync();

        Assert.Equal("Mystery is not a type Dataverse exposes solution layers for, so its definition cannot be compared.", vm.Status);
        Assert.Empty(left.Requests);
    }

    [Fact]
    public async Task Neither_side_having_a_definition_is_said_plainly()
    {
        var vm = Diff(Answering(Layers()), Answering(Layers((1, "Core", null))));

        await vm.LoadAsync();

        Assert.Equal("Neither environment returned a definition for this component.", vm.Status);
        Assert.Equal("0 changes", vm.ChangeCountLabel);
    }

    [Theory]
    [InlineData(true, "dev returned no definition, so there is nothing to compare against.")]
    [InlineData(false, "prod returned no definition, so there is nothing to compare against.")]
    public async Task One_side_without_a_definition_is_named(bool leftMissing, string expected)
    {
        var present = Layers((1, "Core", """{"a":1}"""));
        var vm = Diff(Answering(leftMissing ? Layers() : present), Answering(leftMissing ? present : Layers()));

        await vm.LoadAsync();

        Assert.Equal(expected, vm.Status);
    }

    [Fact]
    public async Task A_second_load_while_one_is_running_is_ignored()
    {
        var gate = new TaskCompletionSource();
        var left = new FakeHttpHandler().OnAsync(HttpMethod.Get, "msdyn_componentlayers", async _ =>
        {
            await gate.Task;
            return FakeHttpHandler.Json(Layers((1, "Core", """{"a":1}""")));
        });
        var right = Answering(Layers((1, "Core", """{"a":1}""")));
        var vm = Diff(left, right);

        var first = vm.LoadAsync();

        Assert.True(vm.IsBusy);
        Assert.Equal("Loading both environments...", vm.Status);
        Assert.False(vm.RefreshCommand.CanExecute(null));

        await vm.LoadAsync();
        gate.SetResult();
        await first;

        Assert.Single(left.Requests);
        Assert.Single(right.Requests);
        Assert.Single(vm.LeftLayers);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Refreshing_reads_both_sides_again_and_replaces_what_was_shown()
    {
        var left = Answering(Layers((1, "Core", """{"a":1}""")));
        var right = Answering(Layers((1, "Core", """{"a":2}""")));
        var vm = Diff(left, right);
        await vm.LoadAsync();

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, left.Requests.Count);
        Assert.Single(vm.LeftLayers);
        Assert.Single(vm.Changes);
    }
}
