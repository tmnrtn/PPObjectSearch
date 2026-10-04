using PPObjectSearch.Models;
using PPObjectSearch.Services;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

public class ReferenceDataComparerTests
{
    private static readonly EntityColumn Code = Col("new_code");

    /// <summary>A plan keyed on new_code comparing a single value column.</summary>
    private static EntityComparePlan KeyedPlan(EntityColumn value, bool matchLookupsByName = true) =>
        Plan(new[] { Code }, new[] { value }, matchLookupsByName);

    /// <summary>Compares one keyed row per side holding the given value, and returns its row.</summary>
    private static RecordComparison CompareOne(EntityColumn column, string? source, string? target, bool matchLookupsByName = true)
    {
        var plan = KeyedPlan(column, matchLookupsByName);

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").With(column.SelectName, source).Build() },
            new[] { Row(G(2)).With("new_code", "A").With(column.SelectName, target).Build() });

        return Assert.Single(result.Rows);
    }

    // ---- presence ----------------------------------------------------------------------------

    [Fact]
    public void Compare_reports_a_row_missing_from_the_target_as_only_in_source()
    {
        var plan = KeyedPlan(Col("new_name"));
        var source = Row(G(1)).With("new_code", "A").Named("Alpha").Build();

        var result = ReferenceDataComparer.Compare(plan, new[] { source }, Array.Empty<DataRecord>());

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.OnlyInSource, row.Status);
        Assert.Equal("A", row.Key);
        Assert.Same(source, row.Source);
        Assert.Null(row.Target);
        Assert.Empty(row.Differences);
        Assert.Equal("Only in source", row.StatusLabel);
        Assert.Equal("Missing from target", row.DifferenceSummary);
        Assert.Equal(G(1).ToString(), row.SourceId);
        Assert.Equal(string.Empty, row.TargetId);
        Assert.Equal("Alpha", row.Name);
        Assert.Equal(1, result.SourceRowCount);
        Assert.Equal(0, result.TargetRowCount);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Compare_reports_a_row_missing_from_the_source_as_only_in_target()
    {
        var plan = KeyedPlan(Col("new_name"));
        var target = Row(G(2)).With("new_code", "B").Named("Beta").Build();

        var result = ReferenceDataComparer.Compare(plan, Array.Empty<DataRecord>(), new[] { target });

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.OnlyInTarget, row.Status);
        Assert.Null(row.Source);
        Assert.Same(target, row.Target);
        Assert.Empty(row.Differences);
        Assert.Equal("Only in target", row.StatusLabel);
        Assert.Equal("Missing from source", row.DifferenceSummary);
        Assert.Equal(string.Empty, row.SourceId);
        Assert.Equal(G(2).ToString(), row.TargetId);
        Assert.Equal("Beta", row.Name);
    }

    [Fact]
    public void A_row_missing_from_a_truncated_source_is_not_offered_for_deletion()
    {
        var plan = KeyedPlan(Col("new_name"));
        var target = Row(G(2)).With("new_code", "B").Build();

        var result = ReferenceDataComparer.Compare(plan, Array.Empty<DataRecord>(), new[] { target }, sourceTruncated: true);

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.OnlyInTarget, row.Status);
        Assert.True(row.IsWriteBlocked);
        Assert.Contains("row cap", row.WriteBlockedReason);
        Assert.True(result.SourceTruncated);
    }

    [Fact]
    public void A_row_missing_from_a_truncated_target_is_not_offered_for_creation()
    {
        var plan = KeyedPlan(Col("new_name"));
        var source = Row(G(1)).With("new_code", "A").Build();

        var result = ReferenceDataComparer.Compare(plan, new[] { source }, Array.Empty<DataRecord>(), targetTruncated: true);

        Assert.True(Assert.Single(result.Rows).IsWriteBlocked);
    }

    [Fact]
    public void Truncating_one_side_does_not_block_the_other_direction_or_matched_rows()
    {
        var plan = KeyedPlan(Col("new_name"));
        var source = new[]
        {
            Row(G(1)).With("new_code", "A").Named("x").Build(),
            Row(G(3)).With("new_code", "C").Named("old").Build()
        };
        var target = new[] { Row(G(3)).With("new_code", "C").Named("new").Build() };

        var result = ReferenceDataComparer.Compare(plan, source, target, sourceTruncated: true);

        Assert.All(result.Rows, r => Assert.False(r.IsWriteBlocked));
    }

    [Fact]
    public void Rows_whose_key_is_not_unique_are_never_written()
    {
        var plan = KeyedPlan(Col("new_name"));
        var source = new[] { Row(G(1)).With("new_code", "A").With("new_name", "one").Build() };
        var target = new[]
        {
            Row(G(2)).With("new_code", "A").With("new_name", "two").Build(),
            Row(G(3)).With("new_code", "A").With("new_name", "three").Build()
        };

        var result = ReferenceDataComparer.Compare(plan, source, target);

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.Different, row.Status);
        Assert.Contains("not unique", row.WriteBlockedReason);
    }

    [Fact]
    public void A_row_whose_key_changed_is_one_update_not_a_create_and_a_delete()
    {
        var plan = KeyedPlan(Col("new_name"));
        var source = Row(G(7)).With("new_code", "A1").With("new_name", "Alpha").Build();
        var target = Row(G(7)).With("new_code", "A0").With("new_name", "Alpha").Build();

        var result = ReferenceDataComparer.Compare(plan, new[] { source }, new[] { target });

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.Different, row.Status);
        Assert.True(row.IsKeyChanged);
        Assert.Equal("A1", row.Key);
        Assert.Equal("A0", row.TargetKey);
        Assert.Equal("Key changed", row.StatusLabel);
        Assert.Same(source, row.Source);
        Assert.Same(target, row.Target);

        var key = Assert.Single(row.Differences);
        Assert.Equal("new_code", key.Column.LogicalName);
        Assert.Equal("A1", key.SourceValue);
        Assert.Equal("A0", key.TargetValue);

        var item = Assert.Single(ReferenceDataWriter.Plan(result.Rows, new ReconcileOptions(true, true, true)));
        Assert.Equal(ReconcileAction.Update, item.Action);
    }

    [Fact]
    public void A_key_change_carries_value_differences_too()
    {
        var plan = KeyedPlan(Col("new_name"));
        var source = Row(G(7)).With("new_code", "A1").With("new_name", "New").Build();
        var target = Row(G(7)).With("new_code", "A0").With("new_name", "Old").Build();

        var row = Assert.Single(ReferenceDataComparer.Compare(plan, new[] { source }, new[] { target }).Rows);

        Assert.Equal(new[] { "new_name", "new_code" }, row.Differences.Select(d => d.Column.LogicalName));
    }

    [Fact]
    public void Different_ids_on_each_side_stay_a_create_and_a_delete()
    {
        var plan = KeyedPlan(Col("new_name"));

        var result = ReferenceDataComparer.Compare(plan,
            new[] { Row(G(1)).With("new_code", "A1").Build() },
            new[] { Row(G(2)).With("new_code", "A0").Build() });

        Assert.Equal(
            new[] { RecordCompareStatus.OnlyInSource, RecordCompareStatus.OnlyInTarget },
            result.Rows.Select(r => r.Status));
    }

    [Fact]
    public void Compare_reports_matching_rows_as_same()
    {
        var row = CompareOne(Col("new_name"), "Alpha", "Alpha");

        Assert.Equal(RecordCompareStatus.Same, row.Status);
        Assert.Empty(row.Differences);
        Assert.Equal(0, row.DifferenceCount);
        Assert.Equal("Match", row.StatusLabel);
        Assert.Equal(string.Empty, row.DifferenceSummary);
    }

    [Fact]
    public void Compare_lists_only_the_differing_columns_in_value_column_order()
    {
        var plan = Plan(
            new[] { Code },
            new[] { Col("new_name"), Col("new_amount", "DecimalType"), Col("new_notes", "MemoType") });

        var source = Row(G(1)).With("new_code", "A").With("new_name", "Alpha").With("new_amount", "1.5").With("new_notes", "x").Build();
        var target = Row(G(2)).With("new_code", "A").With("new_name", "Alpha").With("new_amount", "2").With("new_notes", "y").Build();

        var row = Assert.Single(ReferenceDataComparer.Compare(plan, new[] { source }, new[] { target }).Rows);

        Assert.Equal(RecordCompareStatus.Different, row.Status);
        Assert.Equal("Values differ", row.StatusLabel);
        Assert.Equal(2, row.DifferenceCount);
        Assert.Equal("new_amount, new_notes", row.DifferenceSummary);

        var amount = row.Differences[0];
        Assert.True(amount.IsDifferent);
        Assert.Equal("1.5", amount.SourceValue);
        Assert.Equal("2", amount.TargetValue);
        Assert.Equal("new_amount", amount.ColumnLabel);
        Assert.Equal("Decimal", amount.TypeLabel);
    }

    [Fact]
    public void Compare_of_empty_inputs_produces_nothing()
    {
        var result = ReferenceDataComparer.Compare(KeyedPlan(Col("new_name")), Array.Empty<DataRecord>(), Array.Empty<DataRecord>());

        Assert.Empty(result.Rows);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.SourceRowCount);
        Assert.Equal(0, result.TargetRowCount);
    }

    [Fact]
    public void Compare_with_no_value_columns_compares_presence_only()
    {
        var plan = Plan(new[] { Code }, Array.Empty<EntityColumn>());

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").With("new_name", "x").Build() },
            new[] { Row(G(2)).With("new_code", "A").With("new_name", "y").Build() });

        Assert.Equal(RecordCompareStatus.Same, Assert.Single(result.Rows).Status);
    }

    // ---- value normalisation -----------------------------------------------------------------

    [Theory]
    [InlineData("DecimalType", "1.0000", "1.0")]
    [InlineData("DecimalType", "1.0000", "1")]
    [InlineData("DecimalType", "-0.50", "-0.5")]
    [InlineData("MoneyType", "12.5000", "12.5")]
    [InlineData("DoubleType", "1E-05", "0.00001")]
    [InlineData("IntegerType", "042", "42")]
    [InlineData("BigIntType", " 7 ", "7")]
    [InlineData("DateTimeType", "2024-03-01T10:00:00Z", "2024-03-01T11:00:00+01:00")]
    [InlineData("DateTimeType", "2024-03-01T10:00:00Z", "2024-03-01T05:00:00-05:00")]
    [InlineData("DateTimeType", "2024-03-01T10:00:00Z", "2024-03-01T10:00:00.000Z")]
    [InlineData("DateTimeType", "2024-03-01T10:00:00", "2024-03-01T10:00:00Z")]
    [InlineData("BooleanType", "1", "true")]
    [InlineData("BooleanType", "True", "true")]
    [InlineData("BooleanType", "0", "false")]
    [InlineData("BooleanType", "False", "0")]
    [InlineData("StringType", null, "")]
    [InlineData("DecimalType", null, "  ")]
    [InlineData("UniqueidentifierType", "6F9619FF-8B86-D011-B42D-00C04FC964FF", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("UniqueidentifierType", "{6f9619ff-8b86-d011-b42d-00c04fc964ff}", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("PicklistType", "100000000", "100000000")]
    public void Compare_treats_equivalent_values_as_the_same(string type, string? source, string? target)
    {
        var row = CompareOne(Col("new_value", type), source, target);

        Assert.Equal(RecordCompareStatus.Same, row.Status);
    }

    [Theory]
    [InlineData("StringType", "Alpha", "alpha")]
    [InlineData("StringType", "a b", "a  b")]
    [InlineData("StringType", "  Alpha ", "Alpha")]
    [InlineData("StringType", "Code ", "Code")]
    [InlineData("MemoType", "line\n", "line")]
    [InlineData("StringType", "   ", null)]
    [InlineData("StringType", "x", null)]
    [InlineData("StringType", null, "x")]
    [InlineData("DecimalType", "1.5", "1.50001")]
    [InlineData("MoneyType", "0", "0.01")]
    [InlineData("IntegerType", "1", "2")]
    [InlineData("DecimalType", "0", null)]
    [InlineData("DateTimeType", "2024-03-01T10:00:00Z", "2024-03-01T10:00:00+01:00")]
    [InlineData("DateTimeType", "2024-03-01T10:00:00Z", "2024-03-01T10:00:01Z")]
    [InlineData("BooleanType", "true", "false")]
    [InlineData("BooleanType", "1", "0")]
    [InlineData("PicklistType", "100000000", "100000001")]
    [InlineData("UniqueidentifierType", "6f9619ff-8b86-d011-b42d-00c04fc964ff", "6f9619ff-8b86-d011-b42d-00c04fc964fe")]
    public void Compare_reports_genuinely_different_values(string type, string? source, string? target)
    {
        var row = CompareOne(Col("new_value", type), source, target);

        Assert.Equal(RecordCompareStatus.Different, row.Status);
        Assert.Equal("new_value", row.DifferenceSummary);
    }

    [Fact]
    public void Compare_treats_trailing_decimal_zeros_as_equal()
    {
        Assert.Equal(RecordCompareStatus.Same, CompareOne(Col("new_rate", "DecimalType"), "1.0000", "1.0").Status);
    }

    [Fact]
    public void Compare_treats_text_case_changes_as_differences()
    {
        Assert.Equal(RecordCompareStatus.Different, CompareOne(Col("new_name"), "Widget", "WIDGET").Status);
    }

    [Fact]
    public void Compare_shows_values_as_they_stand_rather_than_normalised()
    {
        var row = CompareOne(Col("new_name"), " Alpha ", "alpha");

        var column = Assert.Single(row.Differences);
        Assert.Equal(" Alpha ", column.SourceValue);
        Assert.Equal("alpha", column.TargetValue);
    }

    [Fact]
    public void Compare_judges_choices_on_their_value_not_their_label()
    {
        var status = Col("new_colour", "PicklistType");
        var plan = KeyedPlan(status);

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").With("new_colour", "1", "Red").Build() },
            new[] { Row(G(2)).With("new_code", "A").With("new_colour", "1", "Rouge").Build() });

        Assert.Equal(RecordCompareStatus.Same, Assert.Single(result.Rows).Status);
    }

    // ---- lookups ------------------------------------------------------------------------------

    private static RecordComparison CompareLookup(
        Guid? sourceId, string? sourceLabel,
        Guid? targetId, string? targetLabel,
        bool matchLookupsByName)
    {
        var parent = Lookup("new_parentid");
        var plan = KeyedPlan(parent, matchLookupsByName);

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").WithLookup("new_parentid", sourceId, sourceLabel).Build() },
            new[] { Row(G(2)).With("new_code", "A").WithLookup("new_parentid", targetId, targetLabel).Build() });

        return Assert.Single(result.Rows);
    }

    [Fact]
    public void Compare_matches_lookups_by_label_when_ids_differ()
    {
        var row = CompareLookup(G(10), "Parent", G(20), "Parent", matchLookupsByName: true);

        Assert.Equal(RecordCompareStatus.Same, row.Status);
    }

    [Fact]
    public void Compare_reports_lookups_with_different_ids_when_matching_by_id()
    {
        var row = CompareLookup(G(10), "Parent", G(20), "Parent", matchLookupsByName: false);

        Assert.Equal(RecordCompareStatus.Different, row.Status);
        var column = Assert.Single(row.Differences);
        Assert.Equal("_new_parentid_value", column.Column.SelectName);
        Assert.Equal($"Parent  ({G(10)})", column.SourceValue);
        Assert.Equal($"Parent  ({G(20)})", column.TargetValue);
    }

    [Fact]
    public void Compare_reports_a_renamed_lookup_target_when_matching_by_label()
    {
        var row = CompareLookup(G(10), "Parent", G(10), "Parent (old)", matchLookupsByName: true);

        Assert.Equal(RecordCompareStatus.Different, row.Status);
    }

    [Fact]
    public void Compare_ignores_a_renamed_lookup_target_when_matching_by_id()
    {
        var row = CompareLookup(G(10), "Parent", G(10), "Parent (old)", matchLookupsByName: false);

        Assert.Equal(RecordCompareStatus.Same, row.Status);
    }

    [Fact]
    public void Compare_trims_lookup_labels()
    {
        Assert.Equal(RecordCompareStatus.Same, CompareLookup(G(10), " Parent ", G(20), "Parent", true).Status);
    }

    [Fact]
    public void Compare_falls_back_to_the_lookup_id_where_there_is_no_label()
    {
        Assert.Equal(RecordCompareStatus.Same, CompareLookup(G(10), null, G(10), null, true).Status);
        Assert.Equal(RecordCompareStatus.Different, CompareLookup(G(10), null, G(20), null, true).Status);
    }

    [Fact]
    public void Compare_treats_two_empty_lookups_as_the_same()
    {
        Assert.Equal(RecordCompareStatus.Same, CompareLookup(null, null, null, null, true).Status);
        Assert.Equal(RecordCompareStatus.Same, CompareLookup(null, null, null, null, false).Status);
    }

    [Fact]
    public void Compare_reports_a_lookup_set_on_one_side_only()
    {
        Assert.Equal(RecordCompareStatus.Different, CompareLookup(G(10), "Parent", null, null, true).Status);
    }

    [Fact]
    public void Compare_matches_lookup_ids_regardless_of_case()
    {
        var parent = Lookup("new_parentid");
        var plan = KeyedPlan(parent, matchLookupsByName: false);
        var id = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").With("_new_parentid_value", id).Build() },
            new[] { Row(G(2)).With("new_code", "A").With("_new_parentid_value", id.ToUpperInvariant()).Build() });

        Assert.Equal(RecordCompareStatus.Same, Assert.Single(result.Rows).Status);
    }

    // ---- keys ---------------------------------------------------------------------------------

    [Fact]
    public void Compare_matches_rows_on_the_key_rather_than_the_record_id()
    {
        var plan = KeyedPlan(Col("new_name"));

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").With("new_name", "x").Build() },
            new[] { Row(G(99)).With("new_code", "A").With("new_name", "x").Build() });

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.Same, row.Status);
        Assert.Equal(G(1).ToString(), row.SourceId);
        Assert.Equal(G(99).ToString(), row.TargetId);
    }

    [Fact]
    public void Compare_joins_composite_key_parts()
    {
        var plan = Plan(new[] { Code, Col("new_region") }, new[] { Col("new_name") });

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").With("new_region", "EU").Build() },
            new[] { Row(G(2)).With("new_code", "A").With("new_region", "US").Build() });

        Assert.Equal(new[] { "A | EU", "A | US" }, result.Rows.Select(r => r.Key).ToArray());
        Assert.Equal(RecordCompareStatus.OnlyInSource, result.Rows[0].Status);
        Assert.Equal(RecordCompareStatus.OnlyInTarget, result.Rows[1].Status);
    }

    [Fact]
    public void Compare_normalises_key_values_before_matching()
    {
        var rate = Col("new_rate", "DecimalType");
        var plan = Plan(new[] { rate }, new[] { Col("new_name") });

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_rate", "1.50").Build() },
            new[] { Row(G(2)).With("new_rate", "1.5000").Build() });

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.Same, row.Status);
        Assert.Equal("1.5", row.Key);
    }

    [Fact]
    public void Compare_matches_a_lookup_key_by_label()
    {
        var parent = Lookup("new_parentid");
        var plan = Plan(new[] { parent }, new[] { Col("new_name") });

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).WithLookup("new_parentid", G(10), "Parent").Build() },
            new[] { Row(G(2)).WithLookup("new_parentid", G(20), "Parent").Build() });

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.Same, row.Status);
        Assert.Equal("Parent", row.Key);
    }

    [Fact]
    public void Compare_matches_key_values_regardless_of_case()
    {
        // Dataverse keys are case-insensitive, so the key index is too.
        var plan = KeyedPlan(Col("new_name"));

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "abc").Build() },
            new[] { Row(G(2)).With("new_code", "ABC").Build() });

        Assert.Equal(RecordCompareStatus.Same, Assert.Single(result.Rows).Status);
    }

    [Fact]
    public void Compare_warns_about_duplicate_source_keys_and_keeps_the_first_row()
    {
        var plan = Plan(new[] { Code }, new[] { Col("new_name") }, keyLabel: "Code key");

        var first = Row(G(1)).With("new_code", "A").With("new_name", "first").Build();
        var second = Row(G(2)).With("new_code", "A").With("new_name", "second").Build();
        var third = Row(G(3)).With("new_code", "a").With("new_name", "third").Build();

        var result = ReferenceDataComparer.Compare(plan, new[] { first, second, third }, Array.Empty<DataRecord>());

        var row = Assert.Single(result.Rows);
        Assert.Same(first, row.Source);
        Assert.Equal(3, result.SourceRowCount);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(
            "new_thing: 2 source row(s) share a key on Code key and were skipped - the key is not unique there.",
            warning);
    }

    [Fact]
    public void Compare_warns_about_duplicate_keys_on_each_side_separately()
    {
        var plan = KeyedPlan(Col("new_name"));

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").Build() },
            new[] { Row(G(2)).With("new_code", "A").Build(), Row(G(3)).With("new_code", "A").Build() });

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("1 target row(s)", warning);
        Assert.Equal(2, result.TargetRowCount);
        Assert.Equal(RecordCompareStatus.Same, Assert.Single(result.Rows).Status);
    }

    [Fact]
    public void Compare_keeps_rows_with_an_empty_key_apart()
    {
        var plan = KeyedPlan(Col("new_name"));

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", null).Build(), Row(G(2)).With("new_code", "  ").Build() },
            new[] { Row(G(3)).Build() });

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.StartsWith("(no key) ", r.Key));
        Assert.Equal(2, result.Rows.Count(r => r.Status == RecordCompareStatus.OnlyInSource));
        Assert.Equal(1, result.Rows.Count(r => r.Status == RecordCompareStatus.OnlyInTarget));
    }

    [Fact]
    public void Compare_matches_empty_key_rows_only_when_they_share_a_record_id()
    {
        var plan = KeyedPlan(Col("new_name"));

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_name", "x").Build() },
            new[] { Row(G(1)).With("new_name", "y").Build() });

        var row = Assert.Single(result.Rows);
        Assert.Equal(RecordCompareStatus.Different, row.Status);
        Assert.Equal($"(no key) {G(1)}", row.Key);
    }

    [Fact]
    public void Compare_sorts_by_status_then_key()
    {
        var plan = KeyedPlan(Col("new_name"));

        DataRecord R(int id, string code, string name) => Row(G(id)).With("new_code", code).With("new_name", name).Build();

        var source = new[] { R(1, "d", "same"), R(2, "B", "src"), R(3, "a", "src"), R(4, "c", "one"), R(5, "e", "same") };
        var target = new[] { R(11, "E", "same"), R(12, "c", "two"), R(13, "z", "t"), R(14, "D", "same"), R(15, "y", "t") };

        var result = ReferenceDataComparer.Compare(plan, source, target);

        Assert.Equal(
            new[]
            {
                (RecordCompareStatus.OnlyInSource, "a"),
                (RecordCompareStatus.OnlyInSource, "B"),
                (RecordCompareStatus.OnlyInTarget, "y"),
                (RecordCompareStatus.OnlyInTarget, "z"),
                (RecordCompareStatus.Different, "c"),
                (RecordCompareStatus.Same, "d"),
                (RecordCompareStatus.Same, "e")
            },
            result.Rows.Select(r => (r.Status, r.Key)).ToArray());
    }

    // ---- CompareColumns / RecordComparison ----------------------------------------------------

    [Fact]
    public void CompareColumns_flags_nothing_when_one_side_is_missing()
    {
        var plan = KeyedPlan(Col("new_name"));
        var source = Row(G(1)).With("new_code", "A").With("new_name", "Alpha").Build();

        var columns = ReferenceDataComparer.CompareColumns(plan, source, null);

        var column = Assert.Single(columns);
        Assert.False(column.IsDifferent);
        Assert.Equal("Alpha", column.SourceValue);
        Assert.Null(column.TargetValue);
    }

    [Fact]
    public void CompareColumns_with_neither_side_returns_empty_values()
    {
        var columns = ReferenceDataComparer.CompareColumns(KeyedPlan(Col("new_name")), null, null);

        var column = Assert.Single(columns);
        Assert.False(column.IsDifferent);
        Assert.Null(column.SourceValue);
        Assert.Null(column.TargetValue);
    }

    [Fact]
    public void AllColumns_lists_every_compared_column_and_agrees_with_the_differences()
    {
        var plan = Plan(new[] { Code }, new[] { Col("new_name"), Col("new_amount", "MoneyType"), Col("new_flag", "BooleanType") });

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").With("new_name", "x").With("new_amount", "1.00").With("new_flag", "true").Build() },
            new[] { Row(G(2)).With("new_code", "A").With("new_name", "y").With("new_amount", "1").With("new_flag", "true").Build() });

        var row = Assert.Single(result.Rows);
        var all = row.AllColumns();

        Assert.Equal(new[] { "new_name", "new_amount", "new_flag" }, all.Select(c => c.Column.LogicalName).ToArray());
        Assert.Equal(new[] { true, false, false }, all.Select(c => c.IsDifferent).ToArray());
        Assert.Equal(
            row.Differences.Select(d => d.Column.LogicalName),
            all.Where(c => c.IsDifferent).Select(c => c.Column.LogicalName));
    }

    [Fact]
    public void AllColumns_rejudges_lookups_when_the_plan_setting_changes()
    {
        var parent = Lookup("new_parentid");
        var plan = KeyedPlan(parent, matchLookupsByName: true);

        var result = ReferenceDataComparer.Compare(
            plan,
            new[] { Row(G(1)).With("new_code", "A").WithLookup("new_parentid", G(10), "Parent").Build() },
            new[] { Row(G(2)).With("new_code", "A").WithLookup("new_parentid", G(20), "Parent").Build() });

        var row = Assert.Single(result.Rows);
        Assert.False(Assert.Single(row.AllColumns()).IsDifferent);

        plan.MatchLookupsByName = false;

        Assert.True(Assert.Single(row.AllColumns()).IsDifferent);
    }

    [Fact]
    public void Record_comparison_name_prefers_the_source_primary_name()
    {
        var plan = KeyedPlan(Col("new_name"));

        var both = Comparison(plan, RecordCompareStatus.Same,
            Row(G(1)).Named("Source name").Build(), Row(G(2)).Named("Target name").Build());
        var targetOnly = Comparison(plan, RecordCompareStatus.OnlyInTarget, null, Row(G(2)).Named("Target name").Build());
        var unnamedSource = Comparison(plan, RecordCompareStatus.Same, Row(G(1)).Build(), Row(G(2)).Named("Target name").Build());

        Assert.Equal("Source name", both.Name);
        Assert.Equal("Target name", targetOnly.Name);
        Assert.Equal("Target name", unnamedSource.Name);
    }

    [Fact]
    public void Record_comparison_exposes_the_plan_labels()
    {
        var plan = Plan(new[] { Code }, new[] { Col("new_name") }, keyLabel: "Code key");
        var row = Comparison(plan, RecordCompareStatus.Same);

        Assert.Equal("Thing (new_thing)", row.EntityLabel);
        Assert.Equal("new_thing", row.EntityLogicalName);
        Assert.Equal("Code key", row.KeyLabel);
    }
}
