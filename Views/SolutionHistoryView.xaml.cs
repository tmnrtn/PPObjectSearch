using System.Windows;

namespace PPObjectSearch.Views;

public partial class SolutionHistoryView : ScreenView
{
    public SolutionHistoryView()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }
}
