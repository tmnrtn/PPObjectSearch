using PPObjectSearch.Core;

namespace PPObjectSearch.Tests.CoreAndModels;

public class TextDiffTests
{
    private static string Text(IReadOnlyList<DiffRun>? runs) => runs is null ? "<null>" : string.Concat(runs.Select(r => r.Text));

    private static string N(string s) => s.Replace("\r\n", "\n");

    private static string Lines(IEnumerable<string> lines) => string.Join("\n", lines);

    /// <summary>What each side reads as, put back together from the rows - must be the input.</summary>
    private static (string Left, string Right) Rebuild(IReadOnlyList<DiffRow> rows) =>
        (Lines(rows.Where(r => r.Left is not null).Select(r => Text(r.Left))),
         Lines(rows.Where(r => r.Right is not null).Select(r => Text(r.Right))));

    [Fact]
    public void A_line_inserted_near_the_top_of_a_large_text_is_one_added_row()
    {
        // Past the old 1,200-line limit lines were paired by position, so every line after the
        // insertion read as modified.
        var before = Enumerable.Range(0, 5000).Select(i => $"  \"line{i}\": {i},").ToList();
        var after = before.Take(3).Append("  \"inserted\": true,").Concat(before.Skip(3)).ToList();

        var rows = TextDiff.Compare(Lines(before), Lines(after));

        Assert.Equal(5001, rows.Count);
        var added = Assert.Single(rows, r => r.Kind != DiffKind.Unchanged);
        Assert.Equal(DiffKind.Added, added.Kind);
        Assert.Equal("  \"inserted\": true,", Text(added.Right));
    }

    [Fact]
    public void Scattered_changes_in_large_texts_are_aligned_not_paired_by_position()
    {
        var random = new Random(42);
        var before = Enumerable.Range(0, 3000).Select(i => $"row {i}").ToList();
        var after = new List<string>(before);

        // Insertions, deletions and edits all through the text, so the middle stays large.
        for (var i = 0; i < 40; i++)
        {
            var at = random.Next(after.Count);
            switch (i % 3)
            {
                case 0: after.Insert(at, $"new {i}"); break;
                case 1: after.RemoveAt(at); break;
                default: after[at] = after[at] + " edited"; break;
            }
        }

        var rows = TextDiff.Compare(Lines(before), Lines(after));

        Assert.Equal((Lines(before), Lines(after)), Rebuild(rows));
        Assert.True(rows.Count(r => r.Kind != DiffKind.Unchanged) <= 60,
            "a handful of edits should not read as thousands of changed lines");
    }

    [Fact]
    public void Texts_with_almost_nothing_in_common_still_return()
    {
        var before = Enumerable.Range(0, 3000).Select(i => $"a{i}").ToList();
        var after = Enumerable.Range(0, 3000).Select(i => $"b{i}").ToList();

        var rows = TextDiff.Compare(Lines(before), Lines(after));

        Assert.Equal((Lines(before), Lines(after)), Rebuild(rows));
    }

    [Fact]
    public void Compare_identical_text_marks_every_row_unchanged()
    {
        var rows = TextDiff.Compare("a\nb\nc", "a\nb\nc");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(DiffKind.Unchanged, r.Kind));
        Assert.Equal(new[] { "a", "b", "c" }, rows.Select(r => Text(r.Left)));
        Assert.Equal(new[] { "a", "b", "c" }, rows.Select(r => Text(r.Right)));
        Assert.All(rows, r => Assert.All(r.Left!.Concat(r.Right!), run => Assert.False(run.Changed)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Compare_two_empty_values_returns_no_rows(string? before, string? after)
    {
        Assert.Empty(TextDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_from_nothing_marks_every_line_added_with_no_left_side()
    {
        var rows = TextDiff.Compare(null, "x\ny");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(DiffKind.Added, r.Kind);
            Assert.Null(r.Left);
            Assert.True(Assert.Single(r.Right!).Changed);
        });
        Assert.Equal(new[] { "x", "y" }, rows.Select(r => Text(r.Right)));
    }

    [Fact]
    public void Compare_to_nothing_marks_every_line_removed_with_no_right_side()
    {
        var rows = TextDiff.Compare("x\ny", "");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(DiffKind.Removed, r.Kind);
            Assert.Null(r.Right);
            Assert.True(Assert.Single(r.Left!).Changed);
        });
    }

    [Fact]
    public void Compare_inserted_line_is_added_between_unchanged_lines()
    {
        var rows = TextDiff.Compare("a\nc", "a\nb\nc");

        Assert.Equal(new[] { DiffKind.Unchanged, DiffKind.Added, DiffKind.Unchanged }, rows.Select(r => r.Kind));
        Assert.Equal("b", Text(rows[1].Right));
        Assert.Null(rows[1].Left);
    }

    [Fact]
    public void Compare_deleted_line_is_removed_between_unchanged_lines()
    {
        var rows = TextDiff.Compare("a\nb\nc", "a\nc");

        Assert.Equal(new[] { DiffKind.Unchanged, DiffKind.Removed, DiffKind.Unchanged }, rows.Select(r => r.Kind));
        Assert.Equal("b", Text(rows[1].Left));
        Assert.Null(rows[1].Right);
    }

    [Fact]
    public void Compare_changed_line_is_modified_with_only_the_differing_word_highlighted()
    {
        var rows = TextDiff.Compare("a\nhello world\nc", "a\nhello there\nc");

        Assert.Equal(new[] { DiffKind.Unchanged, DiffKind.Modified, DiffKind.Unchanged }, rows.Select(r => r.Kind));
        Assert.Equal(new[] { new DiffRun("hello ", false), new DiffRun("world", true) }, rows[1].Left);
        Assert.Equal(new[] { new DiffRun("hello ", false), new DiffRun("there", true) }, rows[1].Right);
    }

    [Fact]
    public void Compare_keeps_punctuation_around_a_changed_json_value_out_of_the_highlight()
    {
        var rows = TextDiff.Compare("\"name\": \"old\",", "\"name\": \"new\",");

        var row = Assert.Single(rows);
        Assert.Equal(DiffKind.Modified, row.Kind);
        Assert.Equal(new[] { new DiffRun("\"name\": \"", false), new DiffRun("old", true), new DiffRun("\",", false) }, row.Left);
        Assert.Equal(new[] { new DiffRun("\"name\": \"", false), new DiffRun("new", true), new DiffRun("\",", false) }, row.Right);
    }

    [Fact]
    public void Compare_completely_different_line_is_one_changed_run_per_side()
    {
        var row = Assert.Single(TextDiff.Compare("abc", "xyz"));

        Assert.Equal(DiffKind.Modified, row.Kind);
        Assert.Equal(new[] { new DiffRun("abc", true) }, row.Left);
        Assert.Equal(new[] { new DiffRun("xyz", true) }, row.Right);
    }

    [Fact]
    public void Compare_pairs_removed_and_added_runs_into_modified_rows_and_leaves_the_excess_removed()
    {
        var rows = TextDiff.Compare("a\nb\nc", "x");

        Assert.Equal(new[] { DiffKind.Modified, DiffKind.Removed, DiffKind.Removed }, rows.Select(r => r.Kind));
        Assert.Equal("a", Text(rows[0].Left));
        Assert.Equal("x", Text(rows[0].Right));
        Assert.Equal(new[] { "b", "c" }, rows.Skip(1).Select(r => Text(r.Left)));
    }

    [Fact]
    public void Compare_pairs_removed_and_added_runs_and_leaves_the_excess_added()
    {
        var rows = TextDiff.Compare("x", "a\nb");

        Assert.Equal(new[] { DiffKind.Modified, DiffKind.Added }, rows.Select(r => r.Kind));
        Assert.Equal("b", Text(rows[1].Right));
    }

    [Fact]
    public void Compare_treats_crlf_and_lf_line_endings_as_the_same()
    {
        var rows = TextDiff.Compare("a\r\nb", "a\nb");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(DiffKind.Unchanged, r.Kind));
    }

    [Fact]
    public void Compare_is_case_sensitive()
    {
        var row = Assert.Single(TextDiff.Compare("Value", "value"));
        Assert.Equal(DiffKind.Modified, row.Kind);
    }

    [Fact]
    public void Compare_above_the_alignment_limit_pairs_lines_by_position()
    {
        var left = Enumerable.Range(0, 1300).Select(i => $"line {i}").ToList();
        var right = left.ToList();
        right[5] = "changed";
        right.Add("extra");

        var rows = TextDiff.Compare(string.Join("\n", left), string.Join("\n", right));

        Assert.Equal(1301, rows.Count);
        Assert.Equal(DiffKind.Modified, rows[5].Kind);
        Assert.Equal(DiffKind.Added, rows[1300].Kind);
        Assert.Equal("extra", Text(rows[1300].Right));
        Assert.Equal(1299, rows.Count(r => r.Kind == DiffKind.Unchanged));
    }

    [Fact]
    public void Compare_above_the_alignment_limit_still_realigns_after_an_insertion()
    {
        var left = Enumerable.Range(0, 1300).Select(i => $"line {i}").ToList();
        var right = new[] { "inserted" }.Concat(left).ToList();

        var rows = TextDiff.Compare(string.Join("\n", left), string.Join("\n", right));

        // The shared tail is set aside, so the insertion is one added row and the rest match.
        Assert.Equal(1301, rows.Count);
        Assert.Equal(DiffKind.Added, rows[0].Kind);
        Assert.All(rows.Skip(1), r => Assert.Equal(DiffKind.Unchanged, r.Kind));
    }

    [Fact]
    public void Compare_very_long_line_is_highlighted_whole_rather_than_word_by_word()
    {
        var words = Enumerable.Range(0, 900).Select(i => $"w{i}").ToList();
        var before = string.Join(" ", words);
        words[450] = "changed";
        var after = string.Join(" ", words);

        var row = Assert.Single(TextDiff.Compare(before, after));

        Assert.Equal(DiffKind.Modified, row.Kind);
        Assert.Equal(new[] { new DiffRun(before, true) }, row.Left);
        Assert.Equal(new[] { new DiffRun(after, true) }, row.Right);
    }

    [Fact]
    public void Prettify_null_returns_empty_string()
    {
        Assert.Equal(string.Empty, TextDiff.Prettify(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" plain text ")]
    [InlineData("hello {not json}")]
    public void Prettify_returns_non_json_non_xml_text_unchanged(string value)
    {
        Assert.Equal(value, TextDiff.Prettify(value));
    }

    [Fact]
    public void Prettify_indents_a_json_object()
    {
        var result = TextDiff.Prettify("{\"a\":1,\"b\":[1,2]}");

        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    1,\n    2\n  ]\n}", N(result));
    }

    [Fact]
    public void Prettify_indents_a_json_array_with_surrounding_whitespace()
    {
        Assert.Equal("[\n  1\n]", N(TextDiff.Prettify("  [1]  ")));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("  [1, 2")]
    public void Prettify_returns_invalid_json_untouched(string value)
    {
        Assert.Equal(value, TextDiff.Prettify(value));
    }

    [Fact]
    public void Prettify_indents_xml()
    {
        var result = TextDiff.Prettify("<a><b>1</b><c /></a>");

        Assert.Equal("<a>\n  <b>1</b>\n  <c />\n</a>", N(result));
    }

    [Fact]
    public void Prettify_returns_invalid_xml_untouched()
    {
        Assert.Equal(" <a><b></a>", TextDiff.Prettify(" <a><b></a>"));
    }

    [Fact]
    public void Prettified_json_diffs_to_the_single_changed_property_line()
    {
        var rows = TextDiff.Compare(
            TextDiff.Prettify("{\"a\":1,\"b\":2,\"c\":3}"),
            TextDiff.Prettify("{\"a\":1,\"b\":5,\"c\":3}"));

        var modified = Assert.Single(rows, r => r.Kind != DiffKind.Unchanged);
        Assert.Equal(DiffKind.Modified, modified.Kind);
        Assert.Equal("2", Assert.Single(modified.Left!, r => r.Changed).Text);
        Assert.Equal("5", Assert.Single(modified.Right!, r => r.Changed).Text);
    }
}
