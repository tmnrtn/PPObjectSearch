using System.Windows;

namespace PPObjectSearch.Views;

public partial class SolutionHistoryWindow : Window
{
    public SolutionHistoryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }
}
