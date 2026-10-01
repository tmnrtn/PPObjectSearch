using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Search;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch.Views;

/// <summary>
/// Read-only source with syntax highlighting, line numbers and Ctrl+F search, in the app's
/// theme. A thin wrapper over AvalonEdit's editor so a view binds Code and Syntax and is done.
/// </summary>
public sealed class CodeView : UserControl
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(CodeView),
        new PropertyMetadata(null, (d, _) => ((CodeView)d).ShowCode()));

    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.Register(
        nameof(Syntax), typeof(CodeLanguage), typeof(CodeView),
        new PropertyMetadata(CodeLanguage.None, (d, _) => ((CodeView)d).ApplyHighlighting()));

    private readonly TextEditor _editor;

    public CodeView()
    {
        _editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            WordWrap = false,
            FontSize = 12,
            Padding = new Thickness(6, 6, 6, 6),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;

        // Theme brushes by resource reference, so a theme switch repaints the editor too.
        _editor.SetResourceReference(FontFamilyProperty, "MonoFont");
        _editor.SetResourceReference(BackgroundProperty, "Surface");
        _editor.SetResourceReference(ForegroundProperty, "Text");
        _editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Faint");
        _editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "Selected");
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.SelectionBorder = null;
        _editor.TextArea.SelectionCornerRadius = 0;

        SearchPanel.Install(_editor);

        Content = _editor;

        Loaded += (_, _) => ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>The text shown. Null shows nothing.</summary>
    public string? Code
    {
        get => (string?)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    /// <summary>How to highlight it; None shows plain text.</summary>
    public CodeLanguage Syntax
    {
        get => (CodeLanguage)GetValue(SyntaxProperty);
        set => SetValue(SyntaxProperty, value);
    }

    /// <summary>The editor itself, for tests and anything needing more than the two properties.</summary>
    internal TextEditor Editor => _editor;

    private void ShowCode()
    {
        _editor.Text = Code ?? string.Empty;
        _editor.ScrollToHome();
        ApplyHighlighting();
    }

    private void ApplyHighlighting()
    {
        var definition = CodeHighlighting.ShouldHighlight(Code)
            ? CodeHighlighting.For(Syntax, ThemeManager.IsDark)
            : null;

        // The definition is a shared instance recoloured in place, so after a theme change it is
        // the same object: clear it first, or the editor would not notice and redraw.
        _editor.SyntaxHighlighting = null;
        _editor.SyntaxHighlighting = definition;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyHighlighting();
}
