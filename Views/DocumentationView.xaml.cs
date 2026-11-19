namespace PPObjectSearch.Views;

/// <summary>Documents solutions as Markdown, with a support-scoping inventory: a screen, as a tab or a window.</summary>
public partial class DocumentationView : ScreenView
{
    public DocumentationView() => InitializeComponent();

    protected override void OnScreenClosed() => (DataContext as IDisposable)?.Dispose();
}
