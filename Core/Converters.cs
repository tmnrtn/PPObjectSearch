using System.Globalization;
using System.Windows.Data;

namespace PPObjectSearch.Core;

/// <summary>Negates a bool, for the times a control is enabled by the absence of something.</summary>
public sealed class NotBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool flag || !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool flag || !flag;
}

/// <summary>Collapses a panel that is driven by a collection's count rather than a flag.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>Visible when the bound flag is false - for a control that belongs to the state before
/// something has happened.</summary>
public sealed class NotBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && flag
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>True only for an actual false - a null (nothing loaded yet) is not "active".</summary>
public sealed class IsFalseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>Collapses an element whose bound value is null or an empty string.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 }
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// The badge text for an environment type. Pass "short" for the sidebar, where Developer reads
/// as DEV.
/// </summary>
public sealed class EnvironmentSkuToBadgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isShort = string.Equals(parameter as string, "short", StringComparison.OrdinalIgnoreCase);

        return value switch
        {
            Dataverse.EnvironmentSku.Production => "PROD",
            Dataverse.EnvironmentSku.Default => "DEFAULT",
            Dataverse.EnvironmentSku.Sandbox => "SANDBOX",
            Dataverse.EnvironmentSku.Developer => isShort ? "DEV" : "DEVELOPER",
            Dataverse.EnvironmentSku.Trial => "TRIAL",
            Dataverse.EnvironmentSku.Teams => "TEAMS",
            _ => "UNKNOWN"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// Prefixes a value with the converter parameter, or yields nothing for an empty value - for the
/// " · sub type" after a type name.
/// </summary>
public sealed class PrefixConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? string.Empty : $"{parameter}{value}";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// Binds a RadioButton group to an enum: checked when the value equals the parameter, and
/// checking one sets the value to it.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null
            ? Enum.Parse(targetType, parameter.ToString()!)
            : System.Windows.Data.Binding.DoNothing;
}

/// <summary>An em dash in place of an empty value, so a blank cell reads as "nothing" rather than "not loaded".</summary>
public sealed class EmptyToDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? "—" : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>" · patch" after the managed state when the operation was on a patch; nothing otherwise.</summary>
public sealed class PatchLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "· patch" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>"Yes" / "No" for a flag, "—" when it is not known.</summary>
public sealed class YesNoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => "Yes",
        false => "No",
        _ => "—"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
