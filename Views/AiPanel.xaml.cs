using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

/// <summary>The AI assistant beside a screen, in the main window or a screen's own window.</summary>
public partial class AiPanel : UserControl
{
    private bool _atEnd = true;

    public AiPanel()
    {
        InitializeComponent();
        // What a question would be about can change while the panel is away: read it again on showing.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            (DataContext as AiAssistantViewModel)?.Refresh();
            Dispatcher.BeginInvoke(() => Question.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
        DataContextChanged += (_, _) =>
        {
            if (IsVisible) (DataContext as AiAssistantViewModel)?.Refresh();
        };
    }

    /// <summary>The item selected on the screen may have changed: the suggestions follow it.</summary>
    private void Panel_MouseEnter(object sender, MouseEventArgs e) => (DataContext as AiAssistantViewModel)?.Refresh();

    /// <summary>Enter asks; Shift+Enter starts a new line.</summary>
    private void Question_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var box = (TextBox)sender;
            var caret = box.CaretIndex;
            box.Text = box.Text.Insert(caret, Environment.NewLine);
            box.CaretIndex = caret + Environment.NewLine.Length;
        }
        else if (DataContext is AiAssistantViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
        }

        e.Handled = true;
    }

    /// <summary>The answer stays in view as it streams, unless the user has scrolled up to read.</summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            _atEnd = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;
        }
        else if (_atEnd)
        {
            Scroller.ScrollToEnd();
        }
    }

    /// <summary>
    /// The answers are rich text boxes, each with a scroller of its own that would take the wheel
    /// even with nothing to scroll; the conversation scrolls instead.
    /// </summary>
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>Shows the screen's content exactly as a question would send it now.</summary>
    private void WhatsSent_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantViewModel vm || vm.PreviewScreen() is not { } screen) return;

        var environments = screen.Environments.Count == 0
            ? string.Empty
            : $"Environments: {string.Join(", ", screen.Environments.Select(env => env.Title))}\n\n";

        var text = new TextBox
        {
            Text = environments + screen.Content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 10, 12, 10)
        };
        text.SetResourceReference(FontFamilyProperty, "MonoFont");

        var window = new Window
        {
            Title = $"What's sent - {screen.Title}",
            Width = 860,
            Height = 620,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Child = text }
        };
        window.Style = (Style)FindResource("DialogWindow");
        ((Border)window.Content).Style = (Style)FindResource("WindowBody");
        window.Show();
    }
}
