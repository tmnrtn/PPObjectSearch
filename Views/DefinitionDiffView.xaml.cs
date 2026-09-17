using System.Windows.Controls;

namespace PPObjectSearch.Views;

/// <summary>
/// The changed-property list plus the two value panes, shared by the layer diff and the
/// environment diff. Its DataContext is a <see cref="ViewModels.DefinitionDiffViewModel"/>.
/// </summary>
public partial class DefinitionDiffView : UserControl
{
    private bool _syncing;

    public DefinitionDiffView()
    {
        InitializeComponent();

        // The two panes only read as a diff while their lines stay opposite each other, so
        // whichever one the user scrolls drags the other along with it.
        BeforePane.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnBeforeScrolled));
        AfterPane.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnAfterScrolled));
    }

    private void OnBeforeScrolled(object sender, ScrollChangedEventArgs e) => Mirror(e, AfterPane);

    private void OnAfterScrolled(object sender, ScrollChangedEventArgs e) => Mirror(e, BeforePane);

    private void Mirror(ScrollChangedEventArgs e, RichTextBox target)
    {
        if (_syncing) return;
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;

        _syncing = true;
        try
        {
            target.ScrollToVerticalOffset(e.VerticalOffset);
            target.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
        finally
        {
            _syncing = false;
        }
    }
}
