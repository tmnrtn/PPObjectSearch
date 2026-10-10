using System.IO;
using System.Text;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

public sealed class CsvExporterTests : IDisposable
{
    private const string Header =
        "Name,Display name,Schema name,Object type,Sub type,Related table,State,Customizable,Owner,Created,Modified,Object id,Maker portal link";

    private readonly string _path = Path.GetTempFileName();

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { /* best effort: a temp file left behind is harmless */ }
    }

    private string WriteAndRead(params SolutionComponentItem[] items)
    {
        CsvExporter.Write(_path, items);
        return File.ReadAllText(_path, Encoding.UTF8);
    }

    private static SolutionComponentItem Item(
        string name = "n",
        string type = "Table",
        string? displayName = null,
        string? subType = null,
        string? owner = null,
        string? makerUrl = null) => new()
        {
            Name = name,
            ComponentTypeName = type,
            DisplayName = displayName,
            SubType = subType,
            Owner = owner,
            MakerUrl = makerUrl
        };

    [Fact]
    public void Write_with_no_items_writes_only_the_header()
    {
        Assert.Equal(Header + Environment.NewLine, WriteAndRead());
    }

    [Fact]
    public void Write_starts_the_file_with_a_utf8_byte_order_mark()
    {
        CsvExporter.Write(_path, new[] { Item(name: "Café") });

        var bytes = File.ReadAllBytes(_path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Contains("Café", File.ReadAllText(_path, Encoding.UTF8));
    }

    [Fact]
    public void Write_writes_every_column_in_header_order()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var item = new SolutionComponentItem
        {
            Name = "account",
            DisplayName = "Account",
            SchemaName = "Account",
            ComponentTypeName = "Table",
            SubType = "Standard",
            PrimaryEntityName = "account",
            IsManaged = true,
            IsCustomizable = true,
            Owner = "Tom",
            CreatedOn = new DateTimeOffset(2024, 1, 2, 3, 4, 59, TimeSpan.Zero),
            ModifiedOn = new DateTimeOffset(2025, 12, 31, 23, 59, 0, TimeSpan.Zero),
            ObjectId = id,
            MakerUrl = "https://make.example/x"
        };

        var lines = WriteAndRead(item).Split(Environment.NewLine);

        Assert.Equal(Header, lines[0]);
        Assert.Equal(
            "account,Account,Account,Table,Standard,account,Managed,Yes,Tom,2024-01-02 03:04,2025-12-31 23:59," + id + ",https://make.example/x",
            lines[1]);
    }

    [Fact]
    public void Write_leaves_missing_values_empty_and_writes_unmanaged_and_not_customizable()
    {
        var lines = WriteAndRead(Item(name: "x", type: "Form")).Split(Environment.NewLine);

        // Empty object id (Guid.Empty) and null dates export as empty fields.
        Assert.Equal("x,,,Form,,,Unmanaged,No,,,,,", lines[1]);
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("line1\rline2", "\"line1\rline2\"")]
    [InlineData("plain", "plain")]
    public void Write_quotes_fields_containing_commas_quotes_or_newlines(string value, string expected)
    {
        var content = WriteAndRead(Item(name: "n", displayName: value));

        var row = content.Substring(Header.Length + Environment.NewLine.Length);
        Assert.StartsWith("n," + expected + ",", row);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-1,2", "\"'-1,2\"")]
    [InlineData("\t=1+1", "'\t=1+1")]
    [InlineData("  =1+1", "'  =1+1")]
    [InlineData("a=b", "a=b")]
    public void Write_neutralises_values_excel_would_read_as_formulas(string value, string expected)
    {
        var content = WriteAndRead(Item(name: "n", owner: value));

        var row = content.Substring(Header.Length + Environment.NewLine.Length);
        // Owner is the ninth column; the columns before it are "n,,,Table,,,Unmanaged,No,".
        Assert.StartsWith("n,,,Table,,,Unmanaged,No," + expected + ",", row);
    }

    [Fact]
    public void Write_puts_leading_columns_ahead_of_each_item()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            CsvExporter.Write(path,
                new[] { (Item(name: "n"), (IReadOnlyList<string?>)new[] { "Dev", "https://dev.crm.dynamics.com", "Default" }) },
                new[] { "Environment", "Environment URL", "Solution" });

            var lines = File.ReadAllLines(path);
            Assert.StartsWith("Environment,Environment URL,Solution,Name,", lines[0]);
            Assert.StartsWith("Dev,https://dev.crm.dynamics.com,Default,n,", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Line_escapes_every_field_the_same_way_for_every_export()
    {
        Assert.Equal("Status,'=cmd|' /C calc'!A0,\"two\nlines\",,\"a,b\"",
            CsvExporter.Line("Status", "=cmd|' /C calc'!A0", "two\nlines", null, "a,b"));
    }

    [Fact]
    public void Write_writes_one_row_per_item_in_order()
    {
        var lines = WriteAndRead(Item(name: "first"), Item(name: "second"), Item(name: "third"))
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.Equal(new[] { "first", "second", "third" }, lines.Skip(1).Select(l => l.Split(',')[0]));
    }

    [Fact]
    public void Write_overwrites_an_existing_file()
    {
        File.WriteAllText(_path, "old content that is longer than nothing");

        Assert.Equal(Header + Environment.NewLine, WriteAndRead());
    }
}
