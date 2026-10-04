using PPObjectSearch.Core;

namespace PPObjectSearch.Tests.CoreAndModels;

public class FuzzyMatchTests
{
    [Theory]
    [InlineData("cmpd", "Compare data")]
    [InlineData("sol hist", "Solution history")]
    [InlineData("SH", "Solution history")]
    [InlineData("", "anything")]
    public void Letters_in_order_match(string query, string text)
    {
        Assert.NotNull(FuzzyMatch.Score(query, text));
    }

    [Theory]
    [InlineData("xyz", "Compare data")]
    [InlineData("dc", "Compare data x")]
    [InlineData("a", "")]
    public void Letters_out_of_order_or_missing_do_not(string query, string text)
    {
        Assert.Null(FuzzyMatch.Score(query, text));
    }

    [Fact]
    public void Word_starts_and_runs_rank_above_scattered_letters()
    {
        Assert.True(FuzzyMatch.Score("cd", "Compare data") > FuzzyMatch.Score("cd", "Cancel undo"));
        Assert.True(FuzzyMatch.Score("hist", "Solution history") > FuzzyMatch.Score("hist", "Check this out"));
        Assert.True(FuzzyMatch.Score("users", "Users") > FuzzyMatch.Score("users", "Users and their roles"));
        Assert.True(FuzzyMatch.Score("rc", "RecentChanges") > FuzzyMatch.Score("rc", "Records"));
    }
}
