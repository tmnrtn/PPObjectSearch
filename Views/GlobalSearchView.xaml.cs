using System.Windows;

namespace PPObjectSearch.Views;

public partial class GlobalSearchView : ScreenView
{
    public GlobalSearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }
}
