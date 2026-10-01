using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace PPObjectSearch.Core;

/// <summary>What a piece of source is written in, as far as highlighting goes.</summary>
public enum CodeLanguage
{
    None,
    JavaScript,
    Html,
    Css,
    Xml,
    Json
}

/// <summary>
/// Syntax highlighting for the source viewer, from AvalonEdit's built-in definitions recoloured
/// for this app's light and dark themes - their own colours are for a white background, and
/// some (black braces, navy numbers) all but vanish on a dark one.
///
/// The definitions are AvalonEdit's shared instances, recoloured in place. That is deliberate:
/// HTML imports the JavaScript definition's rules for script blocks, colours and all, so
/// recolouring JavaScript recolours script inside HTML too. (AvalonEdit's HTML does not
/// highlight style blocks at all.)
/// </summary>
public static class CodeHighlighting
{
    /// <summary>Beyond these, highlighting costs more than it gives - typically minified files.</summary>
    public const int MaxHighlightedLength = 2_000_000;
    public const int MaxHighlightedLineLength = 20_000;

    private enum Role
    {
        Comment,
        String,
        Number,
        Keyword,
        Special,
        Tag,
        Attribute,

        /// <summary>Braces, colons and the like: the editor's own text colour.</summary>
        Plain
    }

    /// <summary>Every named colour in the five definitions, by what it highlights.</summary>
    private static readonly Dictionary<string, Role> Roles = new(StringComparer.Ordinal)
    {
        ["Comment"] = Role.Comment,

        ["String"] = Role.String,
        ["Character"] = Role.String,
        ["AttributeValue"] = Role.String,
        ["CData"] = Role.String,
        ["Value"] = Role.String,

        ["Digits"] = Role.Number,
        ["Number"] = Role.Number,

        ["JavaScriptKeyWords"] = Role.Keyword,
        ["JavaScriptLiterals"] = Role.Keyword,
        ["Bool"] = Role.Keyword,
        ["Null"] = Role.Keyword,
        ["DocType"] = Role.Keyword,
        ["XmlDeclaration"] = Role.Keyword,

        ["JavaScriptIntrinsics"] = Role.Special,
        ["JavaScriptGlobalFunctions"] = Role.Special,
        ["Regex"] = Role.Special,
        ["Entity"] = Role.Special,
        ["EntityReference"] = Role.Special,
        ["Entities"] = Role.Special,
        ["BrokenEntity"] = Role.Special,
        ["Class"] = Role.Special,

        ["XmlTag"] = Role.Tag,
        ["HtmlTag"] = Role.Tag,
        ["Tags"] = Role.Tag,
        ["ScriptTag"] = Role.Tag,
        ["JavaScriptTag"] = Role.Tag,
        ["JScriptTag"] = Role.Tag,
        ["VBScriptTag"] = Role.Tag,
        ["UnknownScriptTag"] = Role.Tag,
        ["Selector"] = Role.Tag,
        ["Slash"] = Role.Tag,

        ["AttributeName"] = Role.Attribute,
        ["Attributes"] = Role.Attribute,
        ["UnknownAttribute"] = Role.Attribute,
        ["Property"] = Role.Attribute,
        ["FieldName"] = Role.Attribute,

        ["Punctuation"] = Role.Plain,
        ["CurlyBraces"] = Role.Plain,
        ["Colon"] = Role.Plain,
        ["Assignment"] = Role.Plain,
    };

    // GitHub's light and dark syntax colours: readable on both, and familiar.
    private static readonly Dictionary<Role, (Color Light, Color Dark)> Palette = new()
    {
        [Role.Comment] = (Hex("#6E7781"), Hex("#8B949E")),
        [Role.String] = (Hex("#0A3069"), Hex("#A5D6FF")),
        [Role.Number] = (Hex("#0550AE"), Hex("#79C0FF")),
        [Role.Keyword] = (Hex("#CF222E"), Hex("#FF7B72")),
        [Role.Special] = (Hex("#8250DF"), Hex("#D2A8FF")),
        [Role.Tag] = (Hex("#116329"), Hex("#7EE787")),
        [Role.Attribute] = (Hex("#0550AE"), Hex("#79C0FF")),
    };

    /// <summary>The AvalonEdit definition name behind each language.</summary>
    internal static string? DefinitionName(CodeLanguage language) => language switch
    {
        CodeLanguage.JavaScript => "JavaScript",
        CodeLanguage.Html => "HTML",
        CodeLanguage.Css => "CSS",
        CodeLanguage.Xml => "XML",
        CodeLanguage.Json => "Json",
        _ => null
    };

    private static bool? _paletteIsDark;
    private static readonly object Gate = new();

    /// <summary>
    /// The definition for <paramref name="language"/>, coloured for the current theme; null for
    /// <see cref="CodeLanguage.None"/>.
    /// </summary>
    public static IHighlightingDefinition? For(CodeLanguage language, bool isDark)
    {
        if (DefinitionName(language) is not { } name) return null;

        EnsurePalette(isDark);
        return HighlightingManager.Instance.GetDefinition(name);
    }

    /// <summary>The colour a named highlighting colour takes in a theme; null for the editor's text colour.</summary>
    internal static Color? ColourFor(string namedColour, bool isDark) =>
        Roles.TryGetValue(namedColour, out var role) && Palette.TryGetValue(role, out var colours)
            ? isDark ? colours.Dark : colours.Light
            : null;

    /// <summary>The named colours of a definition that the palette does not know - none, ideally.</summary>
    internal static IEnumerable<string> UnmappedColours(CodeLanguage language) =>
        DefinitionName(language) is { } name && HighlightingManager.Instance.GetDefinition(name) is { } definition
            ? definition.NamedHighlightingColors.Select(c => c.Name).Where(n => !Roles.ContainsKey(n))
            : Enumerable.Empty<string>();

    /// <summary>
    /// Whether text is worth highlighting: not empty, and not so large - or so long-lined, as a
    /// minified file is - that colouring it would make the viewer crawl.
    /// </summary>
    public static bool ShouldHighlight(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxHighlightedLength) return false;

        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            if (i - lineStart > MaxHighlightedLineLength) return false;
            lineStart = i + 1;
        }

        return true;
    }

    /// <summary>Recolours every definition for the theme, once per change of theme.</summary>
    private static void EnsurePalette(bool isDark)
    {
        lock (Gate)
        {
            if (_paletteIsDark == isDark) return;

            foreach (var language in Enum.GetValues<CodeLanguage>())
            {
                if (DefinitionName(language) is not { } name) continue;
                if (HighlightingManager.Instance.GetDefinition(name) is not { } definition) continue;

                foreach (var colour in definition.NamedHighlightingColors)
                {
                    if (colour.IsFrozen) continue;

                    colour.Foreground = ColourFor(colour.Name, isDark) is { } c ? new SimpleHighlightingBrush(c) : null;
                }
            }

            _paletteIsDark = isDark;
        }
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
