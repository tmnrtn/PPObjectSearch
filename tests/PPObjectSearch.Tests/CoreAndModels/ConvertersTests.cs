using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;

namespace PPObjectSearch.Tests.CoreAndModels;

public class ConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static object Convert(IValueConverter converter, object? value, object? parameter = null)
        => converter.Convert(value!, typeof(object), parameter!, Culture);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, true)]
    [InlineData("true", true)]
    public void NotBool_negates_and_treats_non_bool_as_false(object? value, bool expected)
    {
        var converter = new NotBoolConverter();
        Assert.Equal(expected, Convert(converter, value));
        Assert.Equal(expected, converter.ConvertBack(value!, typeof(bool), null!, Culture));
    }

    [Theory]
    [InlineData(1, Visibility.Visible)]
    [InlineData(42, Visibility.Visible)]
    [InlineData(0, Visibility.Collapsed)]
    [InlineData(-1, Visibility.Collapsed)]
    [InlineData(null, Visibility.Collapsed)]
    [InlineData("3", Visibility.Collapsed)]
    public void CountToVisibility_is_visible_only_for_a_positive_int(object? value, Visibility expected)
    {
        Assert.Equal(expected, Convert(new CountToVisibilityConverter(), value));
    }

    [Theory]
    [InlineData(true, Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    [InlineData(null, Visibility.Visible)]
    public void NotBoolToVisibility_collapses_only_for_true(object? value, Visibility expected)
    {
        Assert.Equal(expected, Convert(new NotBoolToVisibilityConverter(), value));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    [InlineData("false", false)]
    public void IsFalse_is_true_only_for_an_actual_false(object? value, bool expected)
    {
        Assert.Equal(expected, Convert(new IsFalseConverter(), value));
    }

    [Theory]
    [InlineData(null, Visibility.Collapsed)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData(" ", Visibility.Visible)]
    [InlineData("x", Visibility.Visible)]
    [InlineData(0, Visibility.Visible)]
    public void EmptyToCollapsed_collapses_null_and_empty_string(object? value, Visibility expected)
    {
        Assert.Equal(expected, Convert(new EmptyToCollapsedConverter(), value));
    }

    [Theory]
    [InlineData(EnvironmentSku.Production, null, "PROD")]
    [InlineData(EnvironmentSku.Default, null, "DEFAULT")]
    [InlineData(EnvironmentSku.Sandbox, null, "SANDBOX")]
    [InlineData(EnvironmentSku.Developer, null, "DEVELOPER")]
    [InlineData(EnvironmentSku.Developer, "short", "DEV")]
    [InlineData(EnvironmentSku.Developer, "SHORT", "DEV")]
    [InlineData(EnvironmentSku.Developer, "long", "DEVELOPER")]
    [InlineData(EnvironmentSku.Production, "short", "PROD")]
    [InlineData(EnvironmentSku.Trial, null, "TRIAL")]
    [InlineData(EnvironmentSku.Teams, null, "TEAMS")]
    [InlineData(EnvironmentSku.Unknown, null, "UNKNOWN")]
    public void EnvironmentSkuToBadge_maps_each_sku(EnvironmentSku sku, string? parameter, string expected)
    {
        Assert.Equal(expected, Convert(new EnvironmentSkuToBadgeConverter(), sku, parameter));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Production")]
    [InlineData(1)]
    public void EnvironmentSkuToBadge_reads_non_sku_values_as_unknown(object? value)
    {
        Assert.Equal("UNKNOWN", Convert(new EnvironmentSkuToBadgeConverter(), value));
    }

    [Theory]
    [InlineData("Cloud Flow", " · ", " · Cloud Flow")]
    [InlineData(5, "#", "#5")]
    [InlineData("x", null, "x")]
    [InlineData("", " · ", "")]
    [InlineData(null, " · ", "")]
    public void Prefix_prepends_the_parameter_unless_the_value_is_empty(object? value, string? parameter, string expected)
    {
        Assert.Equal(expected, Convert(new PrefixConverter(), value, parameter));
    }

    [Theory]
    [InlineData(DiffKind.Added, "Added", true)]
    [InlineData(DiffKind.Added, "Removed", false)]
    [InlineData(DiffKind.Added, "added", false)]
    [InlineData(null, "Added", false)]
    [InlineData(DiffKind.Added, null, false)]
    public void EnumEquals_is_checked_when_the_value_matches_the_parameter(object? value, string? parameter, bool expected)
    {
        Assert.Equal(expected, Convert(new EnumEqualsConverter(), value, parameter));
    }

    [Fact]
    public void EnumEquals_ConvertBack_parses_the_parameter_when_checked()
    {
        var result = new EnumEqualsConverter().ConvertBack(true, typeof(DiffKind), "Removed", Culture);
        Assert.Equal(DiffKind.Removed, result);
    }

    [Theory]
    [InlineData(false, "Removed")]
    [InlineData(null, "Removed")]
    [InlineData(true, null)]
    public void EnumEquals_ConvertBack_does_nothing_when_unchecked_or_without_parameter(object? value, string? parameter)
    {
        var result = new EnumEqualsConverter().ConvertBack(value!, typeof(DiffKind), parameter!, Culture);
        Assert.Same(Binding.DoNothing, result);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData("", "—")]
    [InlineData(" ", " ")]
    [InlineData("value", "value")]
    public void EmptyToDash_substitutes_a_dash_for_empty(object? value, object expected)
    {
        Assert.Equal(expected, Convert(new EmptyToDashConverter(), value));
    }

    [Fact]
    public void EmptyToDash_passes_non_string_values_through_unchanged()
    {
        Assert.Equal(0, Convert(new EmptyToDashConverter(), 0));
    }

    [Theory]
    [InlineData(true, "· patch")]
    [InlineData(false, "")]
    [InlineData(null, "")]
    public void PatchLabel_shows_only_for_true(object? value, string expected)
    {
        Assert.Equal(expected, Convert(new PatchLabelConverter(), value));
    }

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    [InlineData(null, "—")]
    [InlineData("true", "—")]
    public void YesNo_maps_flags_and_unknown(object? value, string expected)
    {
        Assert.Equal(expected, Convert(new YesNoConverter(), value));
    }

    public static TheoryData<IValueConverter> OneWayConverters() => new()
    {
        new CountToVisibilityConverter(),
        new NotBoolToVisibilityConverter(),
        new IsFalseConverter(),
        new EmptyToCollapsedConverter(),
        new EnvironmentSkuToBadgeConverter(),
        new PrefixConverter(),
        new EmptyToDashConverter(),
        new PatchLabelConverter(),
        new YesNoConverter()
    };

    [Theory]
    // Converters aren't serializable, so the rows can't be listed one by one at discovery.
    [MemberData(nameof(OneWayConverters), DisableDiscoveryEnumeration = true)]
    public void One_way_converters_do_nothing_on_ConvertBack(IValueConverter converter)
    {
        Assert.Same(Binding.DoNothing, converter.ConvertBack("anything", typeof(object), null!, Culture));
    }
}
