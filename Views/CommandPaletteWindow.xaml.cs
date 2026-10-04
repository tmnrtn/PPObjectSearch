using System.Windows;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class CommandPaletteWindow : Window
{
    private bool _closing;

    public CommandPaletteWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;

        // A palette is gone the moment focus leaves it.
        Deactivated += (_, _) => CloseOnce();
    }

    private CommandPaletteViewModel? ViewModel => DataContext as CommandPaletteViewModel;

    /// <summary>Near the top of the owner, centred - where a palette is expected.</summary>
    public void PlaceOver(Window owner)
    {
        Owner = owner;
        Left = owner.Left + (owner.ActualWidth - Width) / 2;
        Top = owner.Top + 70;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                e.Handled = true;
                break;
            case Key.Down:
                ViewModel?.Move(1);
                ResultList.ScrollIntoView(ViewModel?.Selected);
                e.Handled = true;
                break;
            case Key.Up:
                ViewModel?.Move(-1);
                ResultList.ScrollIntoView(ViewModel?.Selected);
                e.Handled = true;
                break;
            case Key.Enter:
                Run();
                e.Handled = true;
                break;
        }
    }

    private void Result_DoubleClick(object sender, MouseButtonEventArgs e) => Run();

    private void Run()
    {
        // Closed first, so whatever the command opens is not covered by the palette or closed with it.
        var viewModel = ViewModel;
        CloseOnce();
        viewModel?.Execute();
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
