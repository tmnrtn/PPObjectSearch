using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.Admin;

/// <summary>Why a user in the Entra group is not in the Dataverse team - mirroring the LINQPad script's rules.</summary>
public class GroupOnlyDiagnosisTests
{
    private static readonly Guid Oid = Guid.NewGuid();

    private static EntraUser Entra(bool? enabled = true, string userType = "Member") =>
        new(Oid.ToString(), "Alice", "alice@contoso.com", null, enabled, userType);

    private static MemberUser Record(Guid? oid, bool disabled = false) =>
        new(Guid.NewGuid(), "Alice", "alice@contoso.com", null, oid, disabled, "Read-Write", null);

    [Fact]
    public void Disabled_in_entra_comes_first()
    {
        var d = MembershipPlanner.DiagnoseGroupOnly(Entra(enabled: false), null, null, membershipType: 2);

        Assert.Equal(GroupOnlyCategory.EntraDisabled, d.Category);
    }

    [Theory]
    [InlineData(1, "Guest", true)]    // Members only: guests excluded
    [InlineData(1, "Member", false)]
    [InlineData(3, "Member", true)]   // Guests only: members excluded
    [InlineData(3, "Guest", false)]
    [InlineData(2, "Member", true)]   // Owners only
    [InlineData(0, "Guest", false)]   // Members and guests
    [InlineData(null, "Guest", false)]
    public void Membership_type_can_exclude_a_user(int? membershipType, string userType, bool excluded)
    {
        var d = MembershipPlanner.DiagnoseGroupOnly(Entra(userType: userType), null, null, membershipType);

        Assert.Equal(excluded, d.Category == GroupOnlyCategory.MembershipTypeExcluded);
    }

    [Fact]
    public void No_user_record_is_not_provisioned()
    {
        var d = MembershipPlanner.DiagnoseGroupOnly(Entra(), null, null, 0);

        Assert.Equal(GroupOnlyCategory.NotProvisioned, d.Category);
        Assert.Equal("Not found", d.FoundBy);
        Assert.Null(d.SystemUser);
    }

    [Fact]
    public void A_record_found_only_by_upn_is_linked_to_another_entra_id()
    {
        var other = Guid.NewGuid();
        var d = MembershipPlanner.DiagnoseGroupOnly(Entra(), null, Record(other), 0);

        Assert.Equal(GroupOnlyCategory.ObjectIdMismatch, d.Category);
        Assert.Equal("UPN", d.FoundBy);
        Assert.Contains(other.ToString(), d.Detail);
        Assert.Contains(Oid.ToString(), d.Detail);
    }

    [Fact]
    public void A_disabled_record_is_reported()
    {
        var d = MembershipPlanner.DiagnoseGroupOnly(Entra(), Record(Oid, disabled: true), null, 0);

        Assert.Equal(GroupOnlyCategory.DataverseDisabled, d.Category);
        Assert.Equal("Object id", d.FoundBy);
    }

    [Fact]
    public void An_enabled_linked_record_should_be_added_by_the_sync()
    {
        var record = Record(Oid);
        var d = MembershipPlanner.DiagnoseGroupOnly(Entra(), record, null, 0);

        Assert.Equal(GroupOnlyCategory.ShouldBeAdded, d.Category);
        Assert.Same(record, d.SystemUser);
    }

    [Theory]
    [InlineData(GroupOnlyCategory.NotProvisioned, true, true)]
    [InlineData(GroupOnlyCategory.DataverseDisabled, true, true)]
    [InlineData(GroupOnlyCategory.ObjectIdMismatch, false, true)]
    [InlineData(GroupOnlyCategory.EntraDisabled, false, false)]
    [InlineData(GroupOnlyCategory.MembershipTypeExcluded, false, false)]
    [InlineData(GroupOnlyCategory.ShouldBeAdded, false, false)]
    public void Only_missing_or_disabled_records_are_pulled_in(GroupOnlyCategory category, bool canPullIn, bool needsUserSync)
    {
        Assert.Equal(canPullIn, MembershipPlanner.CanPullIn(category));
        Assert.Equal(needsUserSync, MembershipPlanner.NeedsUserSync(category));
    }

    [Fact]
    public void Every_category_has_a_readable_label()
    {
        foreach (var category in Enum.GetValues<GroupOnlyCategory>())
        {
            Assert.NotEqual(category.ToString(), MembershipPlanner.Describe(category));
            Assert.Equal(MembershipPlanner.Describe(category),
                new GroupOnlyDiagnosis(category, "d", "x", null).Label);
        }
    }

    [Fact]
    public void The_provisioning_script_lists_one_command_per_user()
    {
        var script = MembershipPlanner.BuildUserSyncScript(
            [("oid-1", "Alice", "No Dataverse user"), ("oid-2", "Bob", "Dataverse user disabled")], "env-123");

        Assert.Contains("Add-PowerAppsAccount", script);
        Assert.Contains("Add-AdminPowerAppsSyncUser -EnvironmentName env-123 -PrincipalObjectId oid-1   # Alice (No Dataverse user)", script);
        Assert.Contains("-PrincipalObjectId oid-2", script);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void The_provisioning_script_leaves_a_placeholder_without_an_environment_id(string? environmentId)
    {
        var script = MembershipPlanner.BuildUserSyncScript([("oid-1", "Alice", "x")], environmentId);

        Assert.Contains("-EnvironmentName <environment-id>", script);
    }
}
