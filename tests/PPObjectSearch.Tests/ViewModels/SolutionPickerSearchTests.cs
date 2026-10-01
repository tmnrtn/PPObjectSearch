using PPObjectSearch.Auth;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// The environment's solution picker narrows as you type, like the Type filter - but typing must
/// never change which solution is loaded, since that reloads every object.
/// </summary>
public class SolutionPickerSearchTests
{
    private static SolutionInfo Solution(string friendly, string unique, string? publisher = null, bool managed = false) => new()
    {
        SolutionId = Guid.NewGuid(),
        FriendlyName = friendly,
        UniqueName = unique,
        PublisherName = publisher,
        IsManaged = managed,
        Version = "1.0.0.0"
    };

    private static readonly SolutionInfo Default = Solution("Default Solution", "Default", "Default Publisher");
    private static readonly SolutionInfo Core = Solution("ECT Core", "ect_core", "Kerv");
    private static readonly SolutionInfo Portal = Solution("ECT Portal", "ect_portal", "Kerv", managed: true);
    private static readonly SolutionInfo Reports = Solution("Reporting", "dvsa_reports", "DVSA");

    /// <summary>A session (never connected) with the four solutions listed and Default selected.</summary>
    private static EnvironmentSessionViewModel Session()
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings());
        foreach (var s in new[] { Default, Core, Portal, Reports }) session.Solutions.Add(s);

        // Not connected, so selecting does not try to load anything.
        session.SelectedSolution = Default;
        session.SolutionSearchText = Default.DisplayLabel;
        return session;
    }

    private static List<SolutionInfo> Listed(EnvironmentSessionViewModel session) =>
        session.SolutionsView.Cast<SolutionInfo>().ToList();

    [Fact]
    public void The_selected_solutions_own_name_lists_everything()
    {
        var session = Session();

        Assert.Equal(4, Listed(session).Count);
    }

    [Fact]
    public void Empty_text_lists_everything()
    {
        var session = Session();

        session.SolutionSearchText = "  ";

        Assert.Equal(4, Listed(session).Count);
    }

    [Theory]
    [InlineData("ect", new[] { "ECT Core", "ECT Portal" })]
    [InlineData("PORTAL", new[] { "ECT Portal" })]              // case-insensitive
    [InlineData("dvsa_reports", new[] { "Reporting" })]         // unique name
    [InlineData("kerv", new[] { "ECT Core", "ECT Portal" })]    // publisher
    [InlineData("ect core", new[] { "ECT Core" })]              // every keyword must match
    [InlineData("kerv portal", new[] { "ECT Portal" })]         // keywords across fields
    [InlineData("nothing-like-this", new string[0])]
    public void Typing_narrows_the_list(string text, string[] expected)
    {
        var session = Session();

        session.SolutionSearchText = text;

        Assert.Equal(expected, Listed(session).Select(s => s.FriendlyName));
    }

    [Fact]
    public void Typing_never_changes_the_loaded_solution()
    {
        var session = Session();

        session.SolutionSearchText = "ect";

        // The ComboBox clears its own selection when the text matches no item; that must be ignored.
        session.SelectedSolution = null;

        Assert.Same(Default, session.SelectedSolution);
    }

    [Fact]
    public void Picking_a_solution_selects_it()
    {
        var session = Session();
        session.SolutionSearchText = "portal";

        session.SelectedSolution = Portal;

        Assert.Same(Portal, session.SelectedSolution);
    }

    [Fact]
    public void Ending_a_search_shows_the_loaded_solution_and_the_whole_list_again()
    {
        var session = Session();
        session.SolutionSearchText = "reports";
        session.SelectedSolution = null;

        var raised = new List<string?>();
        session.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        session.EndSolutionSearch();

        Assert.Equal(Default.DisplayLabel, session.SolutionSearchText);
        Assert.Equal(4, Listed(session).Count);

        // The picker is told to show the loaded solution again, after the ComboBox cleared it.
        Assert.Contains(nameof(EnvironmentSessionViewModel.SelectedSolution), raised);
    }

    [Fact]
    public void A_managed_solution_is_found_by_its_display_name()
    {
        var session = Session();

        session.SolutionSearchText = "managed";

        // "(managed)" is decoration on the label, not part of the name - it does not match.
        Assert.Empty(Listed(session));
    }
}
