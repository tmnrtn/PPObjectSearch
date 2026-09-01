using System.Windows;

namespace PPObjectSearch.Views;

public partial class GlobalSearchWindow : Window
{
    public GlobalSearchWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }
}
