using System.ComponentModel;
using PPObjectSearch.Models;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The table picker for a reference-data comparison: ticks, filtering and the counts that follow them.</summary>
public class EntityPickerViewModelTests
{
    private static EntitySummary Table(string logicalName, string? displayName, bool managed) =>
        new(logicalName, displayName, logicalName + "s", logicalName + "id", "name", managed, IsActivity: false);

    private static EntityPickerViewModel Picker(params string[] alreadyAdded) => new(
        [
            Table("account", "Account", managed: true),
            Table("contact", "Contact", managed: true),
            Table("new_country", "Country", managed: false),
            Table("new_region", null, managed: false)
        ],
        alreadyAdded);

    private static EntityPick Pick(EntityPickerViewModel picker, string logicalName) =>
        picker.Items.Single(i => i.LogicalName == logicalName);

    private static string[] Shown(EntityPickerViewModel picker) =>
        picker.ItemsView.Cast<EntityPick>().Select(p => p.LogicalName).ToArray();

    [Fact]
    public void Every_table_is_listed_and_one_already_added_reads_as_ticked_but_not_chosen()
    {
        var picker = Picker("NEW_COUNTRY");

        var country = Pick(picker, "new_country");

        Assert.Equal(4, picker.Items.Count);
        Assert.True(country.IsAlreadyAdded);
        Assert.True(country.IsTicked);
        Assert.False(country.IsSelected);
        Assert.Equal("Already added", country.StateLabel);
        Assert.Equal("Managed", Pick(picker, "account").StateLabel);
        Assert.Equal("Unmanaged", Pick(picker, "new_region").StateLabel);
        Assert.Equal("0 selected · 4 of 4 tables shown", picker.Summary);
        Assert.Equal("Add tables", picker.AddLabel);
        Assert.False(picker.HasSelection);
    }

    [Fact]
    public void A_table_without_a_display_name_is_labelled_by_its_logical_name()
    {
        var picker = Picker();

        Assert.Equal("new_region", Pick(picker, "new_region").DisplayLabel);
        Assert.Equal("new_region", Pick(picker, "new_region").Label);
        Assert.Equal("Country", Pick(picker, "new_country").DisplayLabel);
        Assert.Equal("Country (new_country)", Pick(picker, "new_country").Label);
    }

    [Fact]
    public void Ticking_tables_updates_the_count_and_the_add_button()
    {
        var picker = Picker();
        var changed = new List<string?>();
        picker.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Pick(picker, "account").IsTicked = true;

        Assert.Equal("Add 1 table", picker.AddLabel);
        Assert.Contains(nameof(EntityPickerViewModel.AddLabel), changed);

        Pick(picker, "new_country").IsTicked = true;

        Assert.Equal(2, picker.SelectedCount);
        Assert.True(picker.HasSelection);
        Assert.Equal("Add 2 tables", picker.AddLabel);
        Assert.Equal("2 selected · 4 of 4 tables shown", picker.Summary);
        Assert.Equal(["account", "new_country"], picker.SelectedEntities().Select(e => e.LogicalName));
    }

    [Fact]
    public void A_table_already_added_cannot_be_ticked_again()
    {
        var picker = Picker("account");
        var account = Pick(picker, "account");

        account.IsTicked = false;
        account.IsTicked = true;

        Assert.False(account.IsSelected);
        Assert.True(account.IsTicked);
        Assert.Empty(picker.SelectedEntities());
    }

    [Fact]
    public void Ticking_a_table_raises_its_ticked_state_too()
    {
        var pick = Pick(Picker(), "contact");
        var changed = new List<string?>();
        pick.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pick.IsSelected = true;
        pick.IsSelected = true;

        Assert.Equal([nameof(EntityPick.IsSelected), nameof(EntityPick.IsTicked)], changed);
    }

    [Fact]
    public void Clearing_the_selection_unticks_every_table()
    {
        var picker = Picker();
        Pick(picker, "account").IsSelected = true;
        Pick(picker, "new_region").IsSelected = true;

        picker.ClearSelectionCommand.Execute(null);

        Assert.All(picker.Items, i => Assert.False(i.IsSelected));
        Assert.Equal(0, picker.SelectedCount);
    }

    [Fact]
    public void Search_text_matches_the_logical_or_display_name()
    {
        var picker = Picker();

        picker.SearchText = "COUNT";
        Assert.Equal(["account", "new_country"], Shown(picker));

        picker.SearchText = "region";
        Assert.Equal(["new_region"], Shown(picker));
        Assert.Equal("0 selected · 1 of 4 tables shown", picker.Summary);

        picker.SearchText = "nothing like it";
        Assert.Empty(Shown(picker));

        picker.SearchText = " ";
        Assert.Equal(4, Shown(picker).Length);
    }

    [Fact]
    public void Hiding_managed_tables_keeps_a_ticked_one_and_counts_the_rest()
    {
        var picker = Picker();
        Pick(picker, "contact").IsSelected = true;

        picker.HideManaged = true;

        Assert.Equal(["contact", "new_country", "new_region"], Shown(picker));
        Assert.Equal("1 selected · 1 managed tables hidden", picker.Summary);

        picker.HideManaged = true;
        picker.HideManaged = false;

        Assert.Equal(4, Shown(picker).Length);
    }

    [Fact]
    public void Setting_the_same_search_twice_does_not_refresh_again()
    {
        var picker = Picker();
        picker.SearchText = "acc";
        var changed = 0;
        ((INotifyPropertyChanged)picker).PropertyChanged += (_, _) => changed++;

        picker.SearchText = "acc";

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Anything_but_a_table_pick_is_filtered_out()
    {
        var picker = Picker();

        Assert.False(picker.ItemsView.Filter("not a pick"));
    }
}
