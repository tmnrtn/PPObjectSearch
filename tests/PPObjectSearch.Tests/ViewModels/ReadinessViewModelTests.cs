using System.Net;
using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The readiness check window: choosing what to check, running it, and what it shows afterwards.</summary>
public class ReadinessViewModelTests
{
    private const string DevUrl = "https://dev.crm11.dynamics.com";
    private const string ProdUrl = "https://prod.crm11.dynamics.com";
    private const string Empty = """{"value":[]}""";

    private static readonly Guid Flow = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid MissingTable = Guid.Parse("40000000-0000-0000-0000-000000000004");

    private static readonly SolutionInfo Core = new()
    {
        SolutionId = Guid.Parse("10000000-0000-0000-0000-000000000001"), UniqueName = "core", FriendlyName = "Contoso Core", Version = "1.1.0.0"
    };

    private static readonly SolutionInfo Default = new() { SolutionId = Guid.NewGuid(), UniqueName = "Default", FriendlyName = "Default Solution" };

    /// <summary>The source: one flow in the solution, which needs a table the target lacks.</summary>
    private static FakeHttpHandler Source(bool componentsFail = false)
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_componenttype ge 0", HttpStatusCode.BadRequest, "range filters are not supported");

        if (componentsFail)
        {
            return handler.OnError(HttpMethod.Get, "msdyn_solutioncomponentsummaries", HttpStatusCode.Forbidden, "no read on components");
        }

        return handler
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", $$"""
                {"value":[{"msdyn_componenttype":29,"msdyn_name":"Notify","msdyn_objectid":"{{Flow}}"}]}
                """)
            .OnJson(HttpMethod.Get, "RetrieveMissingDependencies", $$"""
                {"EntityCollection":[
                  {"requiredcomponentobjectid":"{{MissingTable}}","requiredcomponenttype":1,"dependentcomponentobjectid":"{{Flow}}","dependentcomponenttype":29}
                ]}
                """)
            .OnJson(HttpMethod.Get, "", Empty);
    }

    /// <summary>The target: a newer managed copy of the solution, and none of the source's components.</summary>
    private static FakeHttpHandler Target(bool solutionsFail = false)
    {
        var handler = solutionsFail
            ? new FakeHttpHandler().OnError(HttpMethod.Get, "solutions?", HttpStatusCode.Forbidden, "no read on solutions")
            : new FakeHttpHandler().OnJson(HttpMethod.Get, "solutions?", """
                {"value":[{"solutionid":"70000000-0000-0000-0000-000000000007","uniquename":"core","friendlyname":"Contoso Core","ismanaged":true,"version":"1.2.0.0"}]}
                """);

        return handler.OnJson(HttpMethod.Get, "", Empty);
    }

    private static (EnvironmentSessionViewModel Dev, EnvironmentSessionViewModel Prod) Pair(
        FakeHttpHandler? source = null, FakeHttpHandler? target = null) =>
        (TestSessions.Connected(source ?? Source(), DevUrl, Core), TestSessions.Connected(target ?? Target(), ProdUrl));

    /// <summary>The window, with progress reports landing as they are made.</summary>
    private static ReadinessViewModel Open(IEnumerable<EnvironmentSessionViewModel> sessions, EnvironmentSessionViewModel? source = null)
    {
        InlineSynchronizationContext.Install();
        return new ReadinessViewModel(sessions, source);
    }

    // ---------------------------------------------------------------- choosing

    [Fact]
    public void It_starts_from_the_tab_it_was_opened_from_and_its_solution()
    {
        var (dev, prod) = Pair();
        var offline = TestSessions.Disconnected("https://offline.crm11.dynamics.com");

        var vm = Open([offline, prod, dev], dev);

        Assert.Equal([prod, dev], vm.Sessions);
        Assert.Same(dev, vm.Source);
        Assert.Same(prod, vm.Target);
        Assert.Same(Core, vm.Solution);
        Assert.Equal([Core], vm.Solutions);
        Assert.True(vm.RunCommand.CanExecute(null));
        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasFindings);
        Assert.Equal("Nothing checked yet", vm.EmptyHeading);
        Assert.Equal("Check Contoso Core from dev to prod - nothing is written.", vm.EmptyText);
    }

    [Fact]
    public void Without_a_tab_to_start_from_it_picks_one_with_a_solution_chosen()
    {
        var (dev, prod) = Pair();
        var offline = TestSessions.Disconnected();

        var vm = Open([prod, dev], offline);

        Assert.Same(dev, vm.Source);
        Assert.Same(prod, vm.Target);
    }

    [Fact]
    public void With_no_solution_chosen_anywhere_the_first_tab_is_the_source()
    {
        var a = TestSessions.Connected(new FakeHttpHandler(), DevUrl, Default);
        var b = TestSessions.Connected(new FakeHttpHandler(), ProdUrl);

        var vm = Open([a, b]);

        Assert.Same(a, vm.Source);
        Assert.Null(vm.Solution);
        Assert.Empty(vm.Solutions);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.Equal("Choose a solution in dev, then Check.", vm.EmptyText);
    }

    [Fact]
    public void The_same_environment_on_both_sides_cannot_be_checked()
    {
        var (dev, prod) = Pair();
        var vm = Open([dev, prod], dev);

        vm.Target = dev;

        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.Equal("Choose the environment the solution comes from and a different one it is going to.", vm.EmptyText);

        vm.Target = null;

        Assert.Equal("Choose the environment the solution comes from and a different one it is going to.", vm.EmptyText);
    }

    [Fact]
    public void Changing_the_source_takes_its_solution_with_it()
    {
        var (dev, prod) = Pair();
        var vm = Open([dev, prod], dev);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Source = prod;

        Assert.Null(vm.Solution);
        Assert.Contains(nameof(ReadinessViewModel.Solutions), raised);
        Assert.False(vm.RunCommand.CanExecute(null));

        vm.Source = dev;

        Assert.Same(Core, vm.Solution);
        Assert.True(vm.RunCommand.CanExecute(null));

        vm.Solution = null;

        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.Equal("Choose a solution in dev, then Check.", vm.EmptyText);
    }

    [Fact]
    public void Nothing_to_choose_from_leaves_everything_empty()
    {
        var vm = Open([TestSessions.Disconnected()]);

        Assert.Empty(vm.Sessions);
        Assert.Null(vm.Source);
        Assert.Null(vm.Target);
        Assert.Empty(vm.Solutions);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void Choosing_what_is_already_chosen_changes_nothing()
    {
        var (dev, prod) = Pair();
        var vm = Open([dev, prod], dev);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Source = dev;
        vm.Target = prod;
        vm.Solution = Core;
        vm.SelectedFinding = null;

        Assert.Empty(raised);
    }

    [Fact]
    public void Clearing_the_source_clears_its_solution()
    {
        var (dev, prod) = Pair();
        var vm = Open([dev, prod], dev);

        vm.Source = null;

        Assert.Null(vm.Solution);
        Assert.Empty(vm.Solutions);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.Equal("Choose the environment the solution comes from and a different one it is going to.", vm.EmptyText);
    }

    [Fact]
    public async Task A_side_that_has_lost_its_connection_cannot_be_checked()
    {
        var target = Target();
        var (dev, prod) = Pair(target: target);
        var vm = Open([dev, prod], dev);

        TestSessions.DropClient(prod);

        Assert.False(vm.RunCommand.CanExecute(null));

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Null(vm.Report);
        Assert.Empty(target.Requests);
        Assert.Equal(string.Empty, vm.Status);
    }

    // ---------------------------------------------------------------- running

    [Fact]
    public async Task A_check_lists_its_findings_worst_first_and_sums_them_up()
    {
        var (dev, prod) = Pair();
        var vm = Open([dev, prod], dev);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Report);
        Assert.True(vm.HasFindings);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.IsRunning);
        Assert.Equal(ReadinessSeverity.Blocker, vm.Findings[0].Severity);
        Assert.Equal(vm.Findings.OrderBy(f => f.Severity).Select(f => f.Severity), vm.Findings.Select(f => f.Severity));
        Assert.Contains(vm.Findings, f => f is { Area: "Version" } && f.Message.Contains("1.2.0.0"));
        Assert.Equal($"{vm.Findings.Count} finding{(vm.Findings.Count == 1 ? "" : "s")}", vm.FindingCountLabel);
        Assert.Equal(string.Empty, vm.Status);
        Assert.StartsWith("Read 1 component of Contoso Core in ", vm.StatusLine);
        Assert.Contains($"{vm.Report.Count(ReadinessSeverity.Blocker)} blockers", vm.ReadSummary);
        Assert.True(vm.ExportCommand.CanExecute(null));
        Assert.Equal("Nothing found", vm.EmptyHeading);
    }

    [Fact]
    public async Task A_finding_about_a_component_in_the_solution_can_be_opened()
    {
        var (dev, prod) = Pair();
        var vm = Open([dev, prod], dev);
        await vm.RunCommand.ExecuteAsync(null);
        var dependency = vm.Findings.Single(f => f.Area == "Dependencies");
        var version = vm.Findings.First(f => f.Area == "Version");

        Assert.Equal(Flow, dependency.Item!.ObjectId);
        Assert.True(vm.OpenFindingCommand.CanExecute(dependency));
        Assert.False(vm.OpenFindingCommand.CanExecute(version));
        Assert.False(vm.OpenFindingCommand.CanExecute(null));

        vm.SelectedFinding = dependency;

        Assert.True(vm.OpenFindingCommand.CanExecute(null));

        TestSessions.DropClient(dev);
        vm.OpenFindingCommand.Execute(null);
        vm.OpenFindingCommand.Execute(version);
        vm.Source = null;
        vm.OpenFindingCommand.Execute(dependency);

        Assert.Same(dependency, vm.SelectedFinding);
    }

    [Fact]
    public async Task A_single_note_is_counted_in_the_singular()
    {
        var source = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_componenttype ge 0", HttpStatusCode.BadRequest, "no ranges")
            .OnJson(HttpMethod.Get, "", Empty);
        var target = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "solutions?", """
                {"value":[{"solutionid":"70000000-0000-0000-0000-000000000007","uniquename":"core","friendlyname":"Contoso Core","ismanaged":true,"version":"1.0.0.0"}]}
                """)
            .OnJson(HttpMethod.Get, "", Empty);
        var (dev, prod) = Pair(source, target);
        var vm = Open([dev, prod], dev);

        await vm.RunCommand.ExecuteAsync(null);

        var finding = Assert.Single(vm.Findings);
        Assert.Equal("Upgrades the target from 1.0.0.0 to 1.1.0.0.", finding.Message);
        Assert.Equal("1 finding", vm.FindingCountLabel);
        Assert.False(vm.HasNotChecked);
        Assert.EndsWith("0 blockers, 0 warnings, 1 note", vm.ReadSummary);
    }

    [Fact]
    public async Task Checks_that_could_not_run_are_listed_and_mentioned_when_nothing_is_found()
    {
        var (dev, prod) = Pair(target: Target(solutionsFail: true));
        var vm = Open([dev, prod], dev);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.True(vm.HasNotChecked);
        Assert.StartsWith("Not checked: the solution version: ", vm.NotChecked);
        Assert.Contains("no read on solutions", vm.NotChecked);
    }

    [Fact]
    public async Task A_check_with_no_findings_says_so_and_notes_what_did_not_run()
    {
        var source = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_componenttype ge 0", HttpStatusCode.BadRequest, "no ranges")
            .OnJson(HttpMethod.Get, "", Empty);
        var (dev, prod) = Pair(source, Target(solutionsFail: true));
        var vm = Open([dev, prod], dev);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Empty(vm.Findings);
        Assert.True(vm.IsEmpty);
        Assert.Equal("0 findings", vm.FindingCountLabel);
        Assert.Equal("Nothing found", vm.EmptyHeading);
        Assert.Equal("No blockers, warnings or notes for Contoso Core in prod. Some checks could not run - see above.", vm.EmptyText);
        Assert.StartsWith("Read 0 components of Contoso Core in ", vm.ReadSummary);
        Assert.EndsWith("0 blockers, 0 warnings, 0 notes", vm.ReadSummary);
    }

    [Fact]
    public async Task A_check_that_cannot_read_the_solution_says_why()
    {
        var (dev, prod) = Pair(Source(componentsFail: true));
        var vm = Open([dev, prod], dev);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Null(vm.Report);
        Assert.StartsWith("The check could not run - ", vm.Status);
        Assert.Contains("no read on components", vm.StatusLine);
        Assert.Equal("The check did not finish", vm.EmptyHeading);
        Assert.Equal("The status bar says why. Check again when it is sorted.", vm.EmptyText);
        Assert.False(vm.HasNotChecked);
        Assert.Equal(string.Empty, vm.NotChecked);
    }

    [Fact]
    public async Task A_running_check_can_be_stopped()
    {
        var gate = new TaskCompletionSource();
        var source = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_componenttype ge 0", HttpStatusCode.BadRequest, "no ranges")
            .OnAsync(HttpMethod.Get, "msdyn_solutioncomponentsummaries", async _ =>
            {
                await gate.Task;
                return FakeHttpHandler.Json(Empty);
            });
        var (dev, prod) = Pair(source);
        var vm = Open([dev, prod], dev);

        var run = vm.RunCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        Assert.False(vm.IsEmpty);
        Assert.Equal("Reading Contoso Core in dev...", vm.Status);
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.RunCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        gate.SetResult();
        await run;

        Assert.False(vm.IsRunning);
        Assert.Equal("Stopped.", vm.Status);
        Assert.Null(vm.Report);
    }

    // ---------------------------------------------------------------- wording

    [Fact]
    public void The_summary_counts_each_severity_in_the_singular_or_plural()
    {
        var report = new ReadinessReport { Solution = "Core", Source = "dev", Target = "prod" };
        report.Findings.Add(new ReadinessFinding { Severity = ReadinessSeverity.Blocker, Area = "a", Component = "c", Message = "m" });
        report.Findings.Add(new ReadinessFinding { Severity = ReadinessSeverity.Info, Area = "a", Component = "c", Message = "m" });
        report.Findings.Add(new ReadinessFinding { Severity = ReadinessSeverity.Info, Area = "a", Component = "c", Message = "m" });

        var text = ReadinessViewModel.Describe(report, 1, TimeSpan.FromSeconds(3));

        Assert.StartsWith("Read 1 component of Core in ", text);
        Assert.EndsWith(" - 1 blocker, 0 warnings, 2 notes", text);
    }
}
