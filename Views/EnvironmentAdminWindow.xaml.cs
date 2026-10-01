using System.Windows;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

/// <summary>Users, security roles, mailboxes and queues in one environment - see the XAML.</summary>
public partial class EnvironmentAdminWindow : Window
{
    public EnvironmentAdminWindow()
    {
        InitializeComponent();
    }

    private EnvironmentAdminViewModel? ViewModel => DataContext as EnvironmentAdminViewModel;

    /// <summary>Alt+Left and Alt+Right, as in a browser. Alt makes the key arrive as a system key.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.Alt || ViewModel is not { } vm) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Left && vm.BackCommand.CanExecute(null)) { vm.BackCommand.Execute(null); e.Handled = true; }
        else if (key == Key.Right && vm.ForwardCommand.CanExecute(null)) { vm.ForwardCommand.Execute(null); e.Handled = true; }
    }

    /// <summary>The mouse's back and forward buttons.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (ViewModel is not { } vm) return;

        if (e.ChangedButton == MouseButton.XButton1 && vm.BackCommand.CanExecute(null)) { vm.BackCommand.Execute(null); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2 && vm.ForwardCommand.CanExecute(null)) { vm.ForwardCommand.Execute(null); e.Handled = true; }
    }
}
