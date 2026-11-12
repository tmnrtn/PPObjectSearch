using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Services;

/// <summary>An audit history as a table: one line per column changed, with the values whole.</summary>
public class AuditHistoryExportTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_column_changed_is_a_line_and_a_change_with_none_is_one_too()
    {
        var longValue = new string('x', AuditChange.LongValue + 1);
        AuditRecord[] records =
        [
            new()
            {
                When = When, By = "Ana Lee", Action = "Update",
                Changes = [new AuditChange("statuscode", "Active", "On hold") { DisplayName = "Status Reason" }, new AuditChange("description", null, longValue)]
            },
            new() { When = When.AddDays(-1), By = "Ben Cole", Action = "Delete" }
        ];

        var rows = AuditHistoryExport.Rows(records, withRow: false);

        Assert.Equal(7, AuditHistoryExport.Headers(withRow: false).Count);
        Assert.Equal(3, rows.Count);
        var local = When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal([local, "Ana Lee", "Update", "Status Reason", "statuscode", "Active", "On hold"], rows[0]);
        Assert.Equal(longValue, rows[1][6]);
        Assert.Equal(["Ben Cole", "Delete", null, null, null, null], rows[2].Skip(1));
    }

    [Fact]
    public void A_history_of_more_than_one_row_says_which_on_each_line()
    {
        var rows = AuditHistoryExport.Rows([new AuditRecord { When = When, Action = "Update", Row = "Current value" }], withRow: true);

        Assert.Equal("Row", AuditHistoryExport.Headers(withRow: true)[3]);
        Assert.Equal("Current value", rows[0][3]);
    }

    [Fact]
    public void A_rows_changes_are_named_by_its_tables_display_names()
    {
        var named = AuditHistoryViewModel.Named(
            [new AuditRecord { When = When, Changes = [new AuditChange("new_rate", "5", "6"), new AuditChange("statecode", "0", "1")] }],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NEW_RATE"] = "Rate" });

        Assert.Equal(["Rate", "Status"], named[0].Changes.Select(c => c.ColumnLabel));
    }
}
