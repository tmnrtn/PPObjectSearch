using PPObjectSearch.Models;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Services;

/// <summary>What Table data sorts a column by: dates and numbers by value, anything else by the text shown.</summary>
public class TableDataSortKeyTests
{
    private static DataRecord Record(string column, string? raw, string? label = null) => new()
    {
        Id = Guid.NewGuid(),
        Values = new Dictionary<string, string?> { [column] = raw },
        Formatted = label is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { [column] = label }
    };

    private static EntityColumn Column(string name, string type) => new(name, null, type, false, false);

    [Fact]
    public void A_date_sorts_by_the_moment_stored_not_the_text_shown()
    {
        var column = Column("createdon", "DateTimeType");

        var july = TableDataViewModel.SortKey(Record("createdon", "2025-07-29T11:00:00Z", "29/07/2025 11:00"), column, "29/07/2025 11:00");
        var january = TableDataViewModel.SortKey(Record("createdon", "2026-01-27T14:17:00Z", "27/01/2026 14:17"), column, "27/01/2026 14:17");

        Assert.Equal(new DateTimeOffset(2025, 7, 29, 11, 0, 0, TimeSpan.Zero), july);
        Assert.True(Comparer<object>.Default.Compare(july, january) < 0);
    }

    [Fact]
    public void A_date_only_value_sorts_as_that_day()
    {
        var key = TableDataViewModel.SortKey(Record("birthdate", "1990-03-04"), Column("birthdate", "DateTimeType"), "04/03/1990");

        Assert.Equal(new DateTimeOffset(1990, 3, 4, 0, 0, 0, TimeSpan.Zero), key);
    }

    [Theory]
    [InlineData("IntegerType", "20", 20)]
    [InlineData("DecimalType", "5.25", 5.25)]
    [InlineData("MoneyType", "1500", 1500)]
    [InlineData("DoubleType", "1.5E+3", 1500)]
    public void A_number_sorts_by_its_value(string type, string raw, double expected)
    {
        var key = TableDataViewModel.SortKey(Record("n", raw, "formatted"), Column("n", type), "formatted");

        Assert.Equal((decimal)expected, key);
    }

    [Fact]
    public void Anything_else_sorts_by_the_text_shown_and_an_empty_date_or_number_as_empty()
    {
        Assert.Equal("Europe", TableDataViewModel.SortKey(Record("region", "100000001", "Europe"), Column("region", "PicklistType"), "Europe"));
        Assert.Null(TableDataViewModel.SortKey(Record("createdon", null), Column("createdon", "DateTimeType"), null));
        Assert.Null(TableDataViewModel.SortKey(Record("n", "not a number"), Column("n", "IntegerType"), "not a number"));
    }
}
