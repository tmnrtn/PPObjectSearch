using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.Admin;

public class MembershipPlannerTests
{
    private static MemberUser User(string name, Guid? objectId = null, string? upn = null, bool? disabled = false, Guid? applicationId = null) =>
        new(Guid.NewGuid(), name, upn, null, objectId, disabled, "Read-Write", applicationId);

    private static EntraUser Entra(Guid id, string name, string? upn, bool? enabled = true) =>
        new(id.ToString(), name, upn, null, enabled);

    // ---------------------------------------------------------------- MatchEntra

    [Fact]
    public void MatchEntra_pairs_on_object_id_first()
    {
        var oid = Guid.NewGuid();
        var rows = MembershipPlanner.MatchEntra([User("Alice", oid, "alice@old")], [Entra(oid, "Alice", "alice@new")]);

        var row = Assert.Single(rows);
        Assert.Equal(EntraMatchStatus.Both, row.Status);
        Assert.Equal("Object id", row.MatchedOn);
    }

    [Fact]
    public void MatchEntra_falls_back_to_upn_case_insensitively_when_object_id_differs()
    {
        var rows = MembershipPlanner.MatchEntra([User("Bob", Guid.NewGuid(), "bob@contoso.com")],
                                                [Entra(Guid.NewGuid(), "Bob", "BOB@Contoso.com")]);

        var row = Assert.Single(rows);
        Assert.Equal(EntraMatchStatus.Both, row.Status);
        Assert.Equal("UPN", row.MatchedOn);
    }

    [Fact]
    public void MatchEntra_matches_on_upn_when_dataverse_has_no_object_id()
    {
        var rows = MembershipPlanner.MatchEntra([User("Carol", null, "carol@x")], [Entra(Guid.NewGuid(), "Carol", "carol@x")]);

        Assert.Equal("UPN", Assert.Single(rows).MatchedOn);
    }

    [Fact]
    public void MatchEntra_reports_unmatched_users_on_each_side()
    {
        var rows = MembershipPlanner.MatchEntra([User("Team person", Guid.NewGuid(), "t@x")],
                                                [Entra(Guid.NewGuid(), "Group person", "g@x")]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(EntraMatchStatus.DataverseOnly, rows.Single(r => r.Name == "Team person").Status);
        Assert.Equal(EntraMatchStatus.EntraOnly, rows.Single(r => r.Name == "Group person").Status);
    }

    [Fact]
    public void MatchEntra_does_not_let_a_upn_match_claim_a_user_already_matched_by_id()
    {
        var oid = Guid.NewGuid();
        var byId = User("Alice", oid, "alice@x");
        var duplicate = User("Alice (old account)", Guid.NewGuid(), "alice@x");

        var rows = MembershipPlanner.MatchEntra([byId, duplicate], [Entra(oid, "Alice", "alice@x")]);

        Assert.Equal(EntraMatchStatus.Both, rows.Single(r => r.Dataverse == byId).Status);
        Assert.Equal(EntraMatchStatus.DataverseOnly, rows.Single(r => r.Dataverse == duplicate).Status);
    }

    [Fact]
    public void MatchEntra_sorts_by_name()
    {
        var rows = MembershipPlanner.MatchEntra([User("zed"), User("Amy")], [Entra(Guid.NewGuid(), "mike", "m@x")]);

        Assert.Equal(["Amy", "mike", "zed"], rows.Select(r => r.Name));
    }

    [Fact]
    public void MatchEntra_row_prefers_entra_upn_and_dataverse_name()
    {
        var oid = Guid.NewGuid();
        var row = Assert.Single(MembershipPlanner.MatchEntra([User("DV Name", oid, "dv@x")], [Entra(oid, "Entra Name", "entra@x")]));

        Assert.Equal("DV Name", row.Name);
        Assert.Equal("entra@x", row.Upn);
        Assert.Equal(oid.ToString(), row.EntraObjectId);
    }

    [Fact]
    public void MatchEntra_of_two_empty_sides_is_empty() =>
        Assert.Empty(MembershipPlanner.MatchEntra([], []));

    // ---------------------------------------------------------------- Diagnose

    public static TheoryData<string, DiagnosisCategory> DiagnosisCases => new()
    {
        { "no-oid-no-upn", DiagnosisCategory.NoObjectId },
        { "no-oid-upn-found", DiagnosisCategory.NoObjectId },
        { "stale-oid", DiagnosisCategory.StaleObjectId },
        { "deleted", DiagnosisCategory.EntraNotFound },
        { "says-member", DiagnosisCategory.EntraSaysMember },
        { "check-failed", DiagnosisCategory.CheckFailed },
        { "entra-disabled", DiagnosisCategory.EntraDisabled },
        { "dv-disabled", DiagnosisCategory.DataverseDisabled },
        { "live", DiagnosisCategory.LiveNonMember },
    };

    [Theory]
    [MemberData(nameof(DiagnosisCases))]
    public void Diagnose_categorises_why_a_user_was_left_behind(string scenario, DiagnosisCategory expected)
    {
        var oid = Guid.NewGuid();
        var live = Entra(oid, "x", "x@x");

        var diagnosis = scenario switch
        {
            "no-oid-no-upn" => MembershipPlanner.Diagnose(User("n", null), null, null, null),
            "no-oid-upn-found" => MembershipPlanner.Diagnose(User("n", null), null, live, false),
            "stale-oid" => MembershipPlanner.Diagnose(User("n", Guid.NewGuid()), null, live, false),
            "deleted" => MembershipPlanner.Diagnose(User("n", oid), null, null, null),
            "says-member" => MembershipPlanner.Diagnose(User("n", oid), live, null, true),
            "check-failed" => MembershipPlanner.Diagnose(User("n", oid), live, null, null),
            "entra-disabled" => MembershipPlanner.Diagnose(User("n", oid), live with { AccountEnabled = false }, null, false),
            "dv-disabled" => MembershipPlanner.Diagnose(User("n", oid, disabled: true), live, null, false),
            "live" => MembershipPlanner.Diagnose(User("n", oid), live, null, false),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        Assert.Equal(expected, diagnosis.Category);
        Assert.False(string.IsNullOrWhiteSpace(diagnosis.Detail));
        Assert.Equal(MembershipPlanner.Describe(expected), diagnosis.Label);
    }

    [Fact]
    public void Diagnose_says_member_outranks_disabled()
    {
        var oid = Guid.NewGuid();
        var diagnosis = MembershipPlanner.Diagnose(User("n", oid), Entra(oid, "x", "x@x", enabled: false), null, true);

        Assert.Equal(DiagnosisCategory.EntraSaysMember, diagnosis.Category);
    }

    [Theory]
    [InlineData(true, false, "Object id")]
    [InlineData(false, true, "UPN")]
    [InlineData(false, false, "Not found")]
    public void Diagnose_records_how_the_entra_user_was_found(bool byId, bool byUpn, string expected)
    {
        var oid = Guid.NewGuid();
        var entra = Entra(oid, "x", "x@x");

        var diagnosis = MembershipPlanner.Diagnose(User("n", oid), byId ? entra : null, byUpn ? entra : null, false);

        Assert.Equal(expected, diagnosis.FoundBy);
    }

    [Fact]
    public void Diagnose_stale_object_id_names_both_ids()
    {
        var dvId = Guid.NewGuid();
        var entraId = Guid.NewGuid();

        var diagnosis = MembershipPlanner.Diagnose(User("n", dvId), null, Entra(entraId, "x", "x@x"), false);

        Assert.Contains(dvId.ToString(), diagnosis.Detail);
        Assert.Contains(entraId.ToString(), diagnosis.Detail);
    }

    [Theory]
    [InlineData(DiagnosisCategory.EntraDisabled, true, true)]
    [InlineData(DiagnosisCategory.EntraNotFound, true, true)]
    [InlineData(DiagnosisCategory.StaleObjectId, true, true)]
    [InlineData(DiagnosisCategory.NoObjectId, true, false)]
    [InlineData(DiagnosisCategory.DataverseDisabled, true, false)]
    [InlineData(DiagnosisCategory.LiveNonMember, true, false)]
    [InlineData(DiagnosisCategory.EntraSaysMember, false, false)]
    [InlineData(DiagnosisCategory.CheckFailed, false, false)]
    public void Removal_is_offered_and_ticked_only_where_the_evidence_supports_it(DiagnosisCategory category, bool canRemove, bool tickedByDefault)
    {
        Assert.Equal(canRemove, MembershipPlanner.CanRemove(category));
        Assert.Equal(tickedByDefault, MembershipPlanner.IsRemovableByDefault(category));
    }

    [Fact]
    public void Every_diagnosis_category_has_a_readable_label()
    {
        foreach (var category in Enum.GetValues<DiagnosisCategory>())
        {
            Assert.NotEqual(category.ToString(), MembershipPlanner.Describe(category));
        }
    }

    // ---------------------------------------------------------------- PlanQueueSync

    [Fact]
    public void PlanQueueSync_adds_team_members_missing_from_the_queue()
    {
        var alice = User("Alice");

        var row = Assert.Single(MembershipPlanner.PlanQueueSync([alice], []));

        Assert.Equal(QueuePlanStatus.Add, row.Status);
        Assert.True(row.IncludedByDefault);
    }

    [Fact]
    public void PlanQueueSync_removes_queue_members_not_in_the_team()
    {
        var bob = User("Bob");

        var row = Assert.Single(MembershipPlanner.PlanQueueSync([], [bob]));

        Assert.Equal(QueuePlanStatus.Remove, row.Status);
        Assert.True(row.IncludedByDefault);
    }

    [Fact]
    public void PlanQueueSync_leaves_users_in_both_alone()
    {
        var carol = User("Carol");

        Assert.Equal(QueuePlanStatus.InBoth, Assert.Single(MembershipPlanner.PlanQueueSync([carol], [carol])).Status);
    }

    [Fact]
    public void PlanQueueSync_does_not_add_disabled_or_application_users()
    {
        var disabled = User("Disabled", disabled: true);
        var app = User("App", applicationId: Guid.NewGuid());

        var rows = MembershipPlanner.PlanQueueSync([disabled, app], []);

        Assert.All(rows, r => Assert.Equal(QueuePlanStatus.Skip, r.Status));
    }

    [Fact]
    public void PlanQueueSync_leaves_a_disabled_user_already_in_both()
    {
        var disabled = User("Disabled", disabled: true);

        Assert.Equal(QueuePlanStatus.InBoth, Assert.Single(MembershipPlanner.PlanQueueSync([disabled], [disabled])).Status);
    }

    [Fact]
    public void PlanQueueSync_lists_application_users_for_removal_but_unticked()
    {
        var app = User("Automation", applicationId: Guid.NewGuid());

        var row = Assert.Single(MembershipPlanner.PlanQueueSync([], [app]));

        Assert.Equal(QueuePlanStatus.Remove, row.Status);
        Assert.False(row.IncludedByDefault);
    }

    [Fact]
    public void PlanQueueSync_removes_disabled_users_not_in_the_team()
    {
        var row = Assert.Single(MembershipPlanner.PlanQueueSync([], [User("Gone", disabled: true)]));

        Assert.Equal(QueuePlanStatus.Remove, row.Status);
        Assert.True(row.IncludedByDefault);
    }

    [Fact]
    public void PlanQueueSync_ignores_duplicate_rows_for_the_same_user()
    {
        var alice = User("Alice");

        Assert.Single(MembershipPlanner.PlanQueueSync([alice, alice], []));
    }

    [Fact]
    public void PlanQueueSync_additive_only_keeps_queue_members_not_in_the_team()
    {
        var gone = User("Gone");
        var app = User("Automation", applicationId: Guid.NewGuid());

        var rows = MembershipPlanner.PlanQueueSync([], [gone, app], additiveOnly: true);

        Assert.All(rows, r => Assert.Equal(QueuePlanStatus.Keep, r.Status));
        Assert.DoesNotContain(rows, r => r.Status == QueuePlanStatus.Remove);
    }

    [Fact]
    public void PlanQueueSync_additive_only_still_adds_and_skips_as_usual()
    {
        var both = User("Both");
        var add = User("Add");
        var skip = User("Skip", disabled: true);

        var rows = MembershipPlanner.PlanQueueSync([both, add, skip], [both, User("Extra")], additiveOnly: true);

        Assert.Equal(QueuePlanStatus.Add, rows.Single(r => r.User == add).Status);
        Assert.Equal(QueuePlanStatus.Skip, rows.Single(r => r.User == skip).Status);
        Assert.Equal(QueuePlanStatus.InBoth, rows.Single(r => r.User == both).Status);
        Assert.Equal(QueuePlanStatus.Keep, rows.Single(r => r.User.FullName == "Extra").Status);
    }

    [Fact]
    public void PlanQueueSync_additive_only_orders_kept_after_adds_and_before_skips()
    {
        var both = User("A both");
        var rows = MembershipPlanner.PlanQueueSync(
            [both, User("B add"), User("C skip", disabled: true)],
            [both, User("D keep")],
            additiveOnly: true);

        Assert.Equal(
            [QueuePlanStatus.Add, QueuePlanStatus.Keep, QueuePlanStatus.Skip, QueuePlanStatus.InBoth],
            rows.Select(r => r.Status));
    }

    [Fact]
    public void PlanQueueSync_orders_adds_then_removes_then_skips_then_unchanged()
    {
        var both = User("A both");
        var rows = MembershipPlanner.PlanQueueSync(
            [both, User("B add"), User("C skip", disabled: true)],
            [both, User("D remove")]);

        Assert.Equal(
            [QueuePlanStatus.Add, QueuePlanStatus.Remove, QueuePlanStatus.Skip, QueuePlanStatus.InBoth],
            rows.Select(r => r.Status));
    }
}
