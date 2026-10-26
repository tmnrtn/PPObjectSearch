using System.Windows;

namespace PPObjectSearch.Views;

public partial class ContentSearchView : ScreenView
{
    public ContentSearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => TermBox.Focus();
    }

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
