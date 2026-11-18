using System.IO;
using System.Net;
using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Dataverse;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.Tests.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The entity relationship diagram screen: reading the solutions' tables, shaping the diagram, and saving it.</summary>
public sealed class ErdViewModelTests : IDisposable
{
    private static readonly Guid CaseId = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000001");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ppos-erd-" + Guid.NewGuid().ToString("N"));
    private readonly SolutionInfo _solution = TestSessions.Solution("Case Management", "ect_cases");
    private readonly List<string> _suggested = new();

    public ErdViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A solution holding ect_case, read the serial way, and every table's display name.</summary>
    private static FakeHttpHandler Handler() => DataverseClientErdTests.Case(new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "range operators not supported")
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", $$"""
                {"value":[{"msdyn_componenttype":1,"msdyn_componenttypename":"Entity","msdyn_name":"ect_case",
                  "msdyn_schemaname":"ect_case","msdyn_objectid":"{{CaseId}}"}]}
                """)
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,category", """{"value":[]}""")
            .OnJson(HttpMethod.Get, "EntityDefinitions?$select=MetadataId,LogicalName,DisplayName", """
                {"value":[{"MetadataId":"bbbbbbbb-0000-0000-0000-000000000001","LogicalName":"contact",
                  "DisplayName":{"UserLocalizedLabel":{"Label":"Contact"}}}]}
                """),
        $"EntityDefinitions({CaseId})");

    private ErdViewModel Erd(FakeHttpHandler handler, string? target = null) =>
        new(TestSessions.Connected(handler, [], solution: _solution))
        {
            Measure = ErdTests.Measure,
            Render = _ => null,
            PickFile = (_, suggested) =>
            {
                _suggested.Add(suggested);
                return target;
            }
        };

    [Fact]
    public void The_solution_on_screen_starts_ticked_and_nothing_is_drawn()
    {
        var erd = Erd(Handler());

        Assert.Equal("Case Management", erd.SolutionLabel);
        Assert.True(erd.DrawCommand.CanExecute(null));
        Assert.False(erd.HasDiagram);
        Assert.False(erd.SaveSvgCommand.CanExecute(null));
        Assert.Equal("Nothing drawn yet", erd.EmptyHeading);
    }

    [Fact]
    public void With_no_solution_ticked_there_is_nothing_to_draw()
    {
        var erd = Erd(Handler());

        erd.ClearCommand.Execute(null);

        Assert.False(erd.DrawCommand.CanExecute(null));
        Assert.Equal("No solution ticked", erd.SolutionLabel);
    }

    [Fact]
    public async Task Draw_reads_the_solutions_tables_and_lays_them_out()
    {
        var erd = Erd(Handler());

        await erd.DrawCommand.ExecuteAsync(null);

        var table = Assert.Single(erd.Tables);
        Assert.True(table.IsChosen);
        Assert.Equal("ect_case", table.Detail);
        Assert.True(erd.HasDiagram);

        // ect_case, and contact outside the solution - named from the display names read.
        Assert.Equal(["Case", "Contact"], erd.Placed!.Boxes.Select(b => b.Box.Title).Order());
        Assert.Contains("1 table(s) and 2 relationship(s), with 1 table(s) outside the solutions.", erd.Status);
        Assert.Equal("Case Management", erd.Placed.Title);
    }

    [Fact]
    public async Task Changing_what_is_shown_lays_the_diagram_out_again_without_reading()
    {
        var handler = Handler();
        var erd = Erd(handler);
        await erd.DrawCommand.ExecuteAsync(null);
        var requests = handler.Requests.Count;

        erd.ShowOutsideTables = false;
        await erd.RelayoutAsync();

        Assert.Equal(["ect_case"], erd.Placed!.Boxes.Select(b => b.Box.LogicalName));
        Assert.Equal(requests, handler.Requests.Count);
    }

    [Fact]
    public async Task Drawing_again_reads_no_table_twice_and_keeps_a_table_unticked()
    {
        var handler = Handler();
        var erd = Erd(handler);
        await erd.DrawCommand.ExecuteAsync(null);
        erd.Tables[0].IsChosen = false;

        await erd.DrawCommand.ExecuteAsync(null);

        Assert.Single(handler.Requests, r => r.Url.Contains("/Attributes"));
        Assert.False(erd.Tables[0].IsChosen);
        Assert.False(erd.HasDiagram);
        Assert.Equal("No tables ticked", erd.EmptyHeading);
    }

    [Fact]
    public async Task Saving_as_svg_writes_the_diagram_under_the_solutions_name()
    {
        var path = Path.Combine(_folder, "diagram.svg");
        var erd = Erd(Handler(), path);
        await erd.DrawCommand.ExecuteAsync(null);

        erd.SaveSvgCommand.Execute(null);

        Assert.Equal(["ect_cases-erd.svg"], _suggested);
        Assert.StartsWith("<svg", File.ReadAllText(path));
        Assert.Equal($"Saved {path}.", erd.Status);
    }

    [Fact]
    public async Task Saving_as_markdown_fences_the_mermaid()
    {
        var path = Path.Combine(_folder, "diagram.md");
        var erd = Erd(Handler(), path);
        await erd.DrawCommand.ExecuteAsync(null);

        erd.SaveMermaidCommand.Execute(null);

        var text = File.ReadAllText(path);
        Assert.StartsWith("```mermaid\n%% Case Management - drawn by PPObjectSearch", text.ReplaceLineEndings("\n"));
        Assert.Contains("contact ||--o{ ect_case : \"ect_contactid\"", text);
        Assert.EndsWith("```\n", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Saving_as_mermaid_writes_the_diagram_alone()
    {
        var path = Path.Combine(_folder, "diagram.mmd");
        var erd = Erd(Handler(), path);
        await erd.DrawCommand.ExecuteAsync(null);

        erd.SaveMermaidCommand.Execute(null);

        Assert.StartsWith("%% Case Management", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_cancelled_save_writes_nothing()
    {
        var erd = Erd(Handler(), target: null);
        await erd.DrawCommand.ExecuteAsync(null);
        var status = erd.Status;

        erd.SaveSvgCommand.Execute(null);

        Assert.Equal(status, erd.Status);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task A_failed_read_says_so()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_solutioncomponentsummaries", HttpStatusCode.Forbidden, "no access");
        var erd = Erd(handler);

        await erd.DrawCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not read the tables - ", erd.Status);
        Assert.False(erd.IsBusy);
    }

    [Fact]
    public async Task Double_clicking_a_table_outside_the_solutions_says_so()
    {
        var erd = Erd(Handler());
        await erd.DrawCommand.ExecuteAsync(null);
        var contact = erd.Placed!.Boxes.Single(b => b.Box.LogicalName == "contact");

        erd.OpenTableAt(contact.X + 5, contact.Y + 5);

        Assert.Equal("contact is outside the solutions drawn.", erd.Status);
    }

    [Fact]
    public void Zoom_stays_within_its_limits()
    {
        var erd = Erd(Handler());

        erd.Zoom = 50;
        Assert.Equal(ErdViewModel.MaxZoom, erd.Zoom);

        erd.Zoom = 0;
        Assert.Equal(ErdViewModel.MinZoom, erd.Zoom);
        Assert.Equal("10%", erd.ZoomLabel.Replace(" ", string.Empty));
    }
}
