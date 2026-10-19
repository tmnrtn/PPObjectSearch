using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>The table data window's server filter: the literal follows the column's type as its metadata states it.</summary>
public class TableRowFilterTests
{
    private static EntityColumn Column(string name, string type, string? display = null) => new(name, display, type, false, false);

    private static string? Build(EntityColumn column, TableFilterOperator op, string? value)
    {
        var filter = TableRowFilter.Build(column, op, value, out var error);
        Assert.True(filter is not null, error);
        return filter;
    }

    private static string Error(EntityColumn column, TableFilterOperator op, string? value)
    {
        Assert.Null(TableRowFilter.Build(column, op, value, out var error));
        return Assert.IsType<string>(error);
    }

    private static readonly LookupNavigation[] Parent = [new("new_ParentId", "new_group", "new_name")];

    private static readonly LookupNavigation[] Customer =
    [
        new("parentcustomerid_account", "account", "name"),
        new("parentcustomerid_contact", "contact", "fullname")
    ];

    private static string? Named(EntityColumn column, TableFilterOperator op, string value, IReadOnlyList<LookupNavigation> names)
    {
        var filter = TableRowFilter.Build(column, op, value, out var error, names);
        Assert.True(filter is not null, error);
        return filter;
    }

    [Theory]
    [InlineData(TableFilterOperator.Contains, "contains(new_ParentId/new_name,'O''Neil')")]
    [InlineData(TableFilterOperator.BeginsWith, "startswith(new_ParentId/new_name,'O''Neil')")]
    [InlineData(TableFilterOperator.Equals, "new_ParentId/new_name eq 'O''Neil'")]
    [InlineData(TableFilterOperator.NotEquals, "new_ParentId/new_name ne 'O''Neil'")]
    public void A_lookup_filters_on_the_name_of_the_row_it_points_at(TableFilterOperator op, string expected)
    {
        Assert.Equal(expected, Named(Column("new_parentid", "LookupType", "Parent"), op, "O'Neil", Parent));
    }

    [Fact]
    public void A_customer_lookup_matches_a_name_in_any_table_it_points_at()
    {
        var column = Column("parentcustomerid", "CustomerType");

        Assert.Equal("(contains(parentcustomerid_account/name,'Contoso') or contains(parentcustomerid_contact/fullname,'Contoso'))",
            Named(column, TableFilterOperator.Contains, "Contoso", Customer));

        // A row points at one table; the others compare as null, which differs - so every one must differ.
        Assert.Equal("(parentcustomerid_account/name ne 'Contoso' and parentcustomerid_contact/fullname ne 'Contoso')",
            Named(column, TableFilterOperator.NotEquals, "Contoso", Customer));
    }

    [Fact]
    public void A_lookup_compared_with_a_guid_compares_ids_and_needs_no_names()
    {
        var id = Guid.NewGuid();
        var column = Column("new_parentid", "LookupType");

        Assert.False(TableRowFilter.NeedsLookupNames(column, TableFilterOperator.Equals, id.ToString()));
        Assert.False(TableRowFilter.NeedsLookupNames(column, TableFilterOperator.IsEmpty, null));
        Assert.True(TableRowFilter.NeedsLookupNames(column, TableFilterOperator.Equals, "Contoso"));
        Assert.True(TableRowFilter.NeedsLookupNames(column, TableFilterOperator.Contains, id.ToString()));
        Assert.False(TableRowFilter.NeedsLookupNames(Column("new_name", "StringType"), TableFilterOperator.Contains, "x"));

        Assert.Equal($"_new_parentid_value ne {id}", Named(column, TableFilterOperator.NotEquals, id.ToString(), Parent));
        Assert.Equal(TableFilterOperator.Contains, TableRowFilter.OperatorsFor(column)[0]);
    }

    [Fact]
    public void A_row_id_column_is_still_compared_by_id_only()
    {
        var column = Column("new_thingid", "UniqueidentifierType");

        Assert.DoesNotContain(TableFilterOperator.Contains, TableRowFilter.OperatorsFor(column));
        Assert.Equal("\"Contoso\" is not a row id (GUID).", Error(column, TableFilterOperator.Equals, "Contoso"));
    }

    [Theory]
    [InlineData(TableFilterOperator.Contains, "contains(new_name,'O''Brien & 50%')")]
    [InlineData(TableFilterOperator.Equals, "new_name eq 'O''Brien & 50%'")]
    [InlineData(TableFilterOperator.NotEquals, "new_name ne 'O''Brien & 50%'")]
    [InlineData(TableFilterOperator.BeginsWith, "startswith(new_name,'O''Brien & 50%')")]
    public void Text_is_quoted_with_its_quotes_doubled(TableFilterOperator op, string expected)
    {
        Assert.Equal(expected, Build(Column("new_name", "StringType"), op, " O'Brien & 50% "));
    }

    /// <summary>
    /// msdyn_solutioncomponentsummary.msdyn_modifiedon holds dates, but its metadata says text -
    /// and a date comparison against it is refused. It filters as the text it is.
    /// </summary>
    [Fact]
    public void A_date_held_in_a_text_column_filters_as_text()
    {
        var column = Column("msdyn_modifiedon", "StringType", "Modified on");

        Assert.DoesNotContain(TableFilterOperator.On, TableRowFilter.OperatorsFor(column));
        Assert.Equal(TableFilterOperator.Contains, TableRowFilter.OperatorsFor(column)[0]);
        Assert.Equal("contains(msdyn_modifiedon,'2026-10-19')", Build(column, TableFilterOperator.Contains, "2026-10-19"));
        Assert.Contains("cannot be compared", Error(column, TableFilterOperator.OnOrAfter, "2026-10-19"));
    }

    [Theory]
    [InlineData(TableFilterOperator.On, "Microsoft.Dynamics.CRM.On(PropertyName='modifiedon',PropertyValue='2026-10-19')")]
    [InlineData(TableFilterOperator.OnOrBefore, "Microsoft.Dynamics.CRM.OnOrBefore(PropertyName='modifiedon',PropertyValue='2026-10-19')")]
    [InlineData(TableFilterOperator.OnOrAfter, "Microsoft.Dynamics.CRM.OnOrAfter(PropertyName='modifiedon',PropertyValue='2026-10-19')")]
    public void A_real_date_column_compares_by_day(TableFilterOperator op, string expected)
    {
        Assert.Equal(expected, Build(Column("modifiedon", "DateTimeType"), op, "2026-10-19"));
    }

    [Fact]
    public void A_date_that_is_not_one_is_refused()
    {
        Assert.Equal("\"tomorrow\" is not a date.", Error(Column("modifiedon", "DateTimeType"), TableFilterOperator.On, "tomorrow"));
    }

    [Theory]
    [InlineData("IntegerType", TableFilterOperator.GreaterThan, "10", "new_n gt 10")]
    [InlineData("DecimalType", TableFilterOperator.LessThan, "2.5", "new_n lt 2.5")]
    [InlineData("MoneyType", TableFilterOperator.Equals, "1,000", "new_n eq 1000")]
    [InlineData("BigIntType", TableFilterOperator.NotEquals, "-3", "new_n ne -3")]
    public void Numbers_go_bare_in_invariant_form(string type, TableFilterOperator op, string value, string expected)
    {
        Assert.Equal(expected, Build(Column("new_n", type), op, value));
    }

    [Fact]
    public void A_number_column_refuses_text()
    {
        Assert.Equal("\"ten\" is not a number.", Error(Column("new_n", "IntegerType", "Count"), TableFilterOperator.Equals, "ten"));
    }

    [Fact]
    public void A_choice_compares_by_its_whole_number()
    {
        var column = Column("statuscode", "StatusType");

        Assert.Equal("statuscode eq 1", Build(column, TableFilterOperator.Equals, "1"));
        Assert.DoesNotContain(TableFilterOperator.GreaterThan, TableRowFilter.OperatorsFor(column));
        Assert.Equal("\"1.5\" is not a choice's number.", Error(column, TableFilterOperator.Equals, "1.5"));
        Assert.True(TableRowFilter.IsChoice(column));
        Assert.False(TableRowFilter.IsChoice(Column("new_n", "IntegerType")));
    }

    [Fact]
    public void A_multi_select_choice_is_searched_for_one_value()
    {
        Assert.Equal("Microsoft.Dynamics.CRM.ContainValues(PropertyName='new_days',PropertyValues=['3'])",
            Build(Column("new_days", "MultiSelectPicklistType"), TableFilterOperator.Contains, "3"));
        Assert.Equal("\"Mon\" is not a choice's number.", Error(Column("new_days", "MultiSelectPicklistType"), TableFilterOperator.Contains, "Mon"));
    }

    [Theory]
    [InlineData("Yes", "new_on eq true")]
    [InlineData("false", "new_on eq false")]
    [InlineData("1", "new_on eq true")]
    public void Yes_and_no_become_true_and_false(string value, string expected)
    {
        Assert.Equal(expected, Build(Column("new_on", "BooleanType"), TableFilterOperator.Equals, value));
    }

    [Fact]
    public void A_yes_no_column_refuses_anything_else()
    {
        Assert.Equal("\"maybe\" is not yes or no.", Error(Column("new_on", "BooleanType"), TableFilterOperator.Equals, "maybe"));
    }

    [Fact]
    public void A_lookup_compares_its_value_column_with_a_row_id()
    {
        var id = Guid.NewGuid();
        var column = Column("parentaccountid", "LookupType");

        Assert.Equal($"_parentaccountid_value eq {id}", Build(column, TableFilterOperator.Equals, id.ToString("B")));
        Assert.Equal("_parentaccountid_value eq null", Build(column, TableFilterOperator.IsEmpty, null));
        Assert.Equal("The name of the row parentaccountid points at cannot be filtered on here - type the row's id (GUID) instead.",
            Error(column, TableFilterOperator.Equals, "Contoso"));
    }

    [Fact]
    public void Empty_and_not_empty_need_no_value()
    {
        var column = Column("new_name", "StringType");

        Assert.Equal("new_name eq null", Build(column, TableFilterOperator.IsEmpty, null));
        Assert.Equal("new_name ne null", Build(column, TableFilterOperator.IsNotEmpty, "ignored"));
        Assert.False(TableRowFilter.NeedsValue(TableFilterOperator.IsEmpty));
        Assert.True(TableRowFilter.NeedsValue(TableFilterOperator.Contains));
    }

    [Fact]
    public void A_missing_value_says_what_to_type()
    {
        Assert.Equal("Type a number to compare Rate with.", Error(Column("new_rate", "DecimalType", "Rate"), TableFilterOperator.Equals, "  "));
    }

    [Fact]
    public void Filters_combine_with_and_leaving_out_the_empty()
    {
        Assert.Null(TableRowFilter.And(null, " "));
        Assert.Equal("a eq 1", TableRowFilter.And(null, "a eq 1"));
        Assert.Equal("(a eq 1) and (statecode eq 0)", TableRowFilter.And("a eq 1", "statecode eq 0"));
    }

    [Theory]
    [InlineData(TableFilterOperator.Contains, "contains")]
    [InlineData(TableFilterOperator.NotEquals, "does not equal")]
    [InlineData(TableFilterOperator.OnOrBefore, "on or before")]
    [InlineData(TableFilterOperator.IsNotEmpty, "is not empty")]
    public void Operators_read_as_words(TableFilterOperator op, string label)
    {
        Assert.Equal(label, TableRowFilter.Label(op));
    }

    [Fact]
    public void Each_kind_of_column_has_a_hint_for_its_value()
    {
        Assert.Equal("text", TableRowFilter.ValueHint(Column("new_name", "StringType")));
        Assert.Equal("a date, such as 2026-10-19", TableRowFilter.ValueHint(Column("modifiedon", "DateTimeType")));
        Assert.Equal("the choice's label or number", TableRowFilter.ValueHint(Column("statecode", "StateType")));
        Assert.Equal("yes or no", TableRowFilter.ValueHint(Column("new_on", "BooleanType")));
        Assert.Equal("the related row's name, or its id", TableRowFilter.ValueHint(Column("ownerid", "OwnerType")));
        Assert.Equal("a row id (GUID)", TableRowFilter.ValueHint(Column("new_thingid", "UniqueidentifierType")));
        Assert.Equal("a number", TableRowFilter.ValueHint(Column("new_n", "DoubleType")));
        Assert.Equal("the choice's number", TableRowFilter.ValueHint(Column("new_days", "MultiSelectPicklistType")));
    }

    [Fact]
    public void Markdown_cells_keep_pipes_and_line_breaks_from_ending_them()
    {
        Assert.Equal(@"a \| b<br>c\\d", TableDataExport.Cell("a | b\r\nc\\d"));
        Assert.Equal(string.Empty, TableDataExport.Cell(null));
    }

    [Fact]
    public void A_csv_export_escapes_values_that_would_run_as_formulas()
    {
        var lines = TableDataExport.CsvLines(["Name", "Rate"], [["=HYPERLINK(\"x\")", "5"], ["Plain, with comma", null]]).ToList();

        Assert.Equal(["Name,Rate", "\"'=HYPERLINK(\"\"x\"\")\",5", "\"Plain, with comma\","], lines);
    }

    [Fact]
    public void A_markdown_export_is_a_table_under_its_notes()
    {
        var text = TableDataExport.Markdown("Tax code (new_taxcode)", ["Environment: contoso"], ["Name", "Rate"], [["Standard", "20"]]);

        Assert.Equal(
            "# Tax code (new_taxcode)" + Environment.NewLine + Environment.NewLine +
            "Environment: contoso  " + Environment.NewLine + Environment.NewLine +
            "| Name | Rate |" + Environment.NewLine +
            "| --- | --- |" + Environment.NewLine +
            "| Standard | 20 |" + Environment.NewLine, text);
    }
}
