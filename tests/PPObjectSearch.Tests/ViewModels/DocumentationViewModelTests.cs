using System.IO;
using System.Net;
using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The solution documentation export: where it goes, what it writes, and how it stops.</summary>
public sealed class DocumentationViewModelTests : IDisposable
{
    private static readonly Guid Variable = Guid.Parse("b0000000-0000-0000-0000-000000000001");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ppos-doc-" + Guid.NewGuid().ToString("N"));
    private readonly SolutionInfo _solution = TestSessions.Solution("Contoso Core", "contoso_core");

    public DocumentationViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A solution holding one environment variable, read the serial way.</summary>
    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "range operators not supported")
        .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", $$"""
            {"value":[{"msdyn_componenttype":380,"msdyn_componenttypename":"Environment Variable Definition",
              "msdyn_name":"contoso_ApiUrl","msdyn_objectid":"{{Variable}}"}]}
            """)
        .OnJson(HttpMethod.Get, "workflows?$select=workflowid,category", """{"value":[]}""")
        .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({Variable})", """
            {"schemaname":"contoso_ApiUrl","type":100000000,"type@OData.Community.Display.V1.FormattedValue":"Text",
             "defaultvalue":"https://api.contoso.com","environmentvariabledefinition_environmentvariablevalue":[]}
            """);

    private readonly List<string> _suggested = new();

    /// <summary>The documentation dialog, answering the file or folder question with the given path.</summary>
    private DocumentationViewModel Documentation(FakeHttpHandler handler, string? target)
    {
        var documentation = new DocumentationViewModel(TestSessions.Connected(handler, [], solution: _solution))
        {
            PickFile = suggested =>
            {
                _suggested.Add(suggested);
                return target;
            },
            PickFolder = () => target
        };

        documentation.Options.Tables = false;
        documentation.Options.Flows = false;
        documentation.Options.PluginSteps = false;
        documentation.Options.SecurityRoles = false;
        documentation.Options.Dependencies = false;
        return documentation;
    }

    [Fact]
    public void Without_a_solution_there_is_nothing_to_document()
    {
        var documentation = new DocumentationViewModel(TestSessions.Disconnected());

        Assert.Equal("Document solution", documentation.Title);
        Assert.Equal("No solution selected", documentation.SolutionLabel);
        Assert.False(documentation.ExportCommand.CanExecute(null));
        Assert.False(documentation.CancelCommand.CanExecute(null));
        Assert.StartsWith("Markdown, readable on GitHub", documentation.Status);
    }

    [Fact]
    public void The_heading_names_the_solution_and_the_tab()
    {
        var documentation = Documentation(new FakeHttpHandler(), target: null);

        Assert.Equal("Document Contoso Core", documentation.Title);
        Assert.Equal("Contoso Core", documentation.SolutionLabel);
        Assert.Equal("contoso", documentation.Session.Title);
        Assert.True(documentation.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task One_file_holds_the_whole_solution()
    {
        var path = Path.Combine(_folder, "contoso.md");
        var documentation = Documentation(Handler(), path);

        await documentation.ExportCommand.ExecuteAsync(null);

        var markdown = await File.ReadAllTextAsync(path);
        Assert.Contains("# Contoso Core", markdown);
        Assert.Contains("contoso_ApiUrl", markdown);
        Assert.StartsWith($"Wrote 1 section page(s) to {path} in ", documentation.Status);
        Assert.Equal(["contoso_core-1.0.0.0.md"], _suggested);
        Assert.False(documentation.IsBusy);
    }

    [Fact]
    public async Task A_file_per_component_goes_under_a_folder_named_for_the_solution()
    {
        var target = Path.Combine(_folder, "contoso_core");
        var documentation = Documentation(Handler(), _folder);
        documentation.Options.FilePerComponent = true;

        await documentation.ExportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(target, "README.md")));
        var pages = Directory.GetFiles(target, "*.md", SearchOption.AllDirectories);
        Assert.True(pages.Length > 1);
        Assert.StartsWith($"Wrote {pages.Length} file(s) to {target} in ", documentation.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_the_choice_of_where_reads_nothing(bool filePerComponent)
    {
        var handler = Handler();
        var documentation = Documentation(handler, target: null);
        documentation.Options.FilePerComponent = filePerComponent;
        var before = documentation.Status;

        await documentation.ExportCommand.ExecuteAsync(null);

        Assert.Empty(handler.Requests);
        Assert.Equal(before, documentation.Status);
    }

    [Fact]
    public async Task A_failed_read_is_reported_and_nothing_is_written()
    {
        var path = Path.Combine(_folder, "contoso.md");
        var documentation = Documentation(new FakeHttpHandler(), path);

        await documentation.ExportCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not write the documentation - ", documentation.Status);
        Assert.False(File.Exists(path));
        Assert.False(documentation.IsBusy);
    }

    [Fact]
    public async Task Stopping_an_export_says_so()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "", async _ =>
        {
            await release.Task;
            return FakeHttpHandler.Json("""{"value":[]}""");
        });
        var documentation = Documentation(handler, Path.Combine(_folder, "contoso.md"));

        var export = documentation.ExportCommand.ExecuteAsync(null);

        Assert.True(documentation.IsBusy);
        Assert.True(documentation.CancelCommand.CanExecute(null));

        documentation.CancelCommand.Execute(null);
        release.SetResult();
        await export;

        Assert.Equal("Stopped.", documentation.Status);
        Assert.False(documentation.CancelCommand.CanExecute(null));
    }
}
