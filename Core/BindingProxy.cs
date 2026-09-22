using System.Windows;

namespace PPObjectSearch.Core;

/// <summary>
/// Carries a DataContext to places the visual tree does not reach. A DataGrid's columns are not
/// part of the tree, so a RelativeSource binding from one finds nothing; a proxy held in the
/// window's resources gives the column something to bind through.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
