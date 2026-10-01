using System.Windows.Media;
using PPObjectSearch.Core;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.CoreAndModels;

public class CodeHighlightingTests
{
    public static TheoryData<CodeLanguage> Languages => new()
    {
        CodeLanguage.JavaScript, CodeLanguage.Html, CodeLanguage.Css, CodeLanguage.Xml, CodeLanguage.Json
    };

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_has_a_definition(CodeLanguage language)
    {
        Assert.NotNull(CodeHighlighting.For(language, isDark: false));
    }

    [Fact]
    public void Plain_text_has_no_definition() =>
        Assert.Null(CodeHighlighting.For(CodeLanguage.None, isDark: false));

    /// <summary>
    /// An unmapped colour keeps AvalonEdit's light-theme colour, which can vanish on a dark
    /// background - so a new AvalonEdit colour name must be given a role, not slip through.
    /// </summary>
    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_named_colour_has_a_role(CodeLanguage language)
    {
        Assert.Empty(CodeHighlighting.UnmappedColours(language));
    }

    [Theory]
    [InlineData("Comment")]
    [InlineData("String")]
    [InlineData("JavaScriptKeyWords")]
    [InlineData("XmlTag")]
    [InlineData("FieldName")]
    public void Light_and_dark_themes_colour_differently(string namedColour)
    {
        var light = CodeHighlighting.ColourFor(namedColour, isDark: false);
        var dark = CodeHighlighting.ColourFor(namedColour, isDark: true);

        Assert.NotNull(light);
        Assert.NotNull(dark);
        Assert.NotEqual(light, dark);
    }

    [Theory]
    [InlineData("CurlyBraces")]
    [InlineData("Punctuation")]
    [InlineData("Colon")]
    public void Punctuation_takes_the_editors_own_text_colour(string namedColour)
    {
        Assert.Null(CodeHighlighting.ColourFor(namedColour, isDark: false));
        Assert.Null(CodeHighlighting.ColourFor(namedColour, isDark: true));
    }

    [Fact]
    public void Dark_colours_are_light_enough_to_read_on_a_dark_background()
    {
        foreach (var name in new[] { "Comment", "String", "Digits", "JavaScriptKeyWords", "Regex", "XmlTag", "AttributeName" })
        {
            var colour = CodeHighlighting.ColourFor(name, isDark: true)!.Value;
            var luminance = (0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B) / 255;

            Assert.True(luminance > 0.45, $"{name} is too dark for the dark theme ({colour}).");
        }
    }

    [Fact]
    public void Switching_theme_recolours_the_shared_definition()
    {
        var definition = CodeHighlighting.For(CodeLanguage.JavaScript, isDark: true)!;
        var darkComment = definition.GetNamedColor("Comment").Foreground!.GetColor(null);

        CodeHighlighting.For(CodeLanguage.JavaScript, isDark: false);
        var lightComment = definition.GetNamedColor("Comment").Foreground!.GetColor(null);

        Assert.Equal(CodeHighlighting.ColourFor("Comment", true), darkComment);
        Assert.Equal(CodeHighlighting.ColourFor("Comment", false), lightComment);
    }

    [Fact]
    public void Script_inside_html_uses_the_recoloured_javascript_colours()
    {
        var html = CodeHighlighting.For(CodeLanguage.Html, isDark: true)!;
        var javaScript = CodeHighlighting.For(CodeLanguage.JavaScript, isDark: true)!;

        // HTML's script rule set imports JavaScript's rules - the very same colour objects.
        var scriptColours = html.GetNamedRuleSet("JavaScriptSet").Spans.Select(s => s.SpanColor)
            .Concat(html.GetNamedRuleSet("JavaScriptSet").Rules.Select(r => r.Color))
            .Where(c => c is not null)
            .ToList();

        Assert.NotEmpty(scriptColours);
        Assert.Contains(scriptColours, c => ReferenceEquals(c, javaScript.GetNamedColor("Comment")));
        Assert.Contains(scriptColours, c => ReferenceEquals(c, javaScript.GetNamedColor("JavaScriptKeyWords")));
    }

    // ---------------------------------------------------------------- when to highlight

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("var x = 1;\nvar y = 2;", true)]
    public void Ordinary_text_is_highlighted(string? text, bool expected) =>
        Assert.Equal(expected, CodeHighlighting.ShouldHighlight(text));

    [Fact]
    public void A_minified_line_is_not_highlighted()
    {
        var minified = "var a=1;" + new string('x', CodeHighlighting.MaxHighlightedLineLength) + "\nok";

        Assert.False(CodeHighlighting.ShouldHighlight(minified));
    }

    [Fact]
    public void A_last_line_without_a_newline_is_measured_too()
    {
        var text = "short\n" + new string('x', CodeHighlighting.MaxHighlightedLineLength + 1);

        Assert.False(CodeHighlighting.ShouldHighlight(text));
    }

    [Fact]
    public void A_line_exactly_at_the_limit_is_highlighted()
    {
        var text = new string('x', CodeHighlighting.MaxHighlightedLineLength) + "\nend";

        Assert.True(CodeHighlighting.ShouldHighlight(text));
    }

    [Fact]
    public void A_very_large_file_is_not_highlighted()
    {
        var line = new string('x', 100) + "\n";
        var text = string.Concat(Enumerable.Repeat(line, CodeHighlighting.MaxHighlightedLength / line.Length + 1));

        Assert.False(CodeHighlighting.ShouldHighlight(text));
    }

    // ---------------------------------------------------------------- web resource types

    [Theory]
    [InlineData(1, CodeLanguage.Html)]
    [InlineData(2, CodeLanguage.Css)]
    [InlineData(3, CodeLanguage.JavaScript)]
    [InlineData(4, CodeLanguage.Xml)]
    [InlineData(9, CodeLanguage.Xml)]   // XSL
    [InlineData(11, CodeLanguage.Xml)]  // SVG
    [InlineData(12, CodeLanguage.Xml)]  // RESX
    [InlineData(5, CodeLanguage.None)]  // PNG
    [InlineData(10, CodeLanguage.None)] // ICO
    [InlineData(99, CodeLanguage.None)]
    public void Each_web_resource_type_gets_its_language(int type, CodeLanguage expected) =>
        Assert.Equal(expected, ObjectDetailsViewModel.LanguageFor(type));
}
