using static Passenger.App.ThemeColours;

namespace Passenger.App;

/// <summary>A VS Code colour theme as a Passenger theme of its one variant: its workbench colours on the tokens they
/// correspond to (<see cref="Workbench"/>), its token and semantic token colours on the code's (<see cref="Code"/>), and every
/// other colour worked out from what it gives (<see cref="ThemePalettes"/>), every word and mark held to WCAG AA on what it
/// is drawn on, so a colour the theme gives too faint is moved in lightness just far enough to read. Pure.</summary>
public static class VsCodeColours
{
    /// <summary>Passenger's tokens and the workbench colours each takes, the first a theme gives winning.</summary>
    public static readonly IReadOnlyList<(string Token, string[] Keys)> Workbench =
    [
        ("Frame", ["editorGroupHeader.tabsBackground", "statusBar.background", "activityBar.background"]),
        ("Panel", ["sideBar.background", "panel.background"]),
        ("Field", ["input.background", "dropdown.background"]),
        ("FieldBorder", ["input.border", "dropdown.border"]),
        ("Popup", ["menu.background", "editorWidget.background", "quickInput.background"]),
        ("PopupBorder", ["menu.border", "editorWidget.border", "widget.border"]),
        ("Separator", ["menu.separatorBackground", "sideBarSectionHeader.border"]),
        ("Text", ["foreground", "sideBar.foreground"]),
        ("TextMuted", ["descriptionForeground"]),
        ("Icon", ["icon.foreground"]),
        ("Link", ["textLink.foreground"]),
        ("LinkHover", ["textLink.activeForeground"]),
        ("Error", ["errorForeground", "editorError.foreground"]),
        ("Warning", ["editorWarning.foreground", "list.warningForeground"]),
        ("TextSelection", ["selection.background", "editor.selectionBackground"]),
        ("RowSelected", ["list.activeSelectionBackground", "list.inactiveSelectionBackground"]),
        ("Accent", ["button.background", "focusBorder", "activityBarBadge.background"]),
        ("AccentText", ["button.foreground", "activityBarBadge.foreground"]),
        ("AccentHover", ["button.hoverBackground"]),
        ("Focus", ["focusBorder"]),
        ("ScrollThumb", ["scrollbarSlider.background"]),
        ("ScrollThumbHover", ["scrollbarSlider.hoverBackground"]),
        ("Menu.Selected", ["menu.selectionBackground", "list.activeSelectionBackground"]),
        ("Menu.SelectedText", ["menu.selectionForeground", "list.activeSelectionForeground"]),
        ("TitleBar", ["titleBar.activeBackground"]),
        ("TitleBar.Text", ["titleBar.activeForeground"]),
        ("Stripe.Selected", ["activityBar.activeBackground"]),
        ("Stripe.SelectedIcon", ["activityBar.foreground"]),
        ("Tab.Active", ["tab.activeBackground"]),
        ("Tab.ActiveText", ["tab.activeForeground"]),
        ("Modified", ["editorCursor.foreground"]),
        ("Run", ["debugIcon.startForeground", "testing.iconPassed"]),
        ("Test.Passed", ["testing.iconPassed"]),
        ("Test.Failed", ["testing.iconFailed"]),
        ("Test.Skipped", ["testing.iconSkipped"]),
        ("Vcs.Added", ["editorGutter.addedBackground"]),
        ("Vcs.Modified", ["editorGutter.modifiedBackground"]),
        ("Vcs.Deleted", ["editorGutter.deletedBackground"]),
        ("Vcs.File.Modified", ["gitDecoration.modifiedResourceForeground"]),
        ("Vcs.File.Added", ["gitDecoration.addedResourceForeground", "gitDecoration.untrackedResourceForeground"]),
        ("Vcs.File.Deleted", ["gitDecoration.deletedResourceForeground"]),
        ("Vcs.File.Unversioned", ["gitDecoration.untrackedResourceForeground"]),
        ("Vcs.File.Conflicted", ["gitDecoration.conflictingResourceForeground"]),
        ("Terminal.Background", ["terminal.background", "panel.background"]),
        ("Terminal.Foreground", ["terminal.foreground"]),
        ("Terminal.Cursor", ["terminalCursor.foreground", "editorCursor.foreground"]),
        ("Terminal.Selection", ["terminal.selectionBackground"]),
        ("Terminal.Ansi0", ["terminal.ansiBlack"]),
        ("Terminal.Ansi1", ["terminal.ansiRed"]),
        ("Terminal.Ansi2", ["terminal.ansiGreen"]),
        ("Terminal.Ansi3", ["terminal.ansiYellow"]),
        ("Terminal.Ansi4", ["terminal.ansiBlue"]),
        ("Terminal.Ansi5", ["terminal.ansiMagenta"]),
        ("Terminal.Ansi6", ["terminal.ansiCyan"]),
        ("Terminal.Ansi7", ["terminal.ansiWhite"]),
        ("Terminal.Ansi8", ["terminal.ansiBrightBlack"]),
        ("Terminal.Ansi9", ["terminal.ansiBrightRed"]),
        ("Terminal.Ansi10", ["terminal.ansiBrightGreen"]),
        ("Terminal.Ansi11", ["terminal.ansiBrightYellow"]),
        ("Terminal.Ansi12", ["terminal.ansiBrightBlue"]),
        ("Terminal.Ansi13", ["terminal.ansiBrightMagenta"]),
        ("Terminal.Ansi14", ["terminal.ansiBrightCyan"]),
        ("Terminal.Ansi15", ["terminal.ansiBrightWhite"]),
        ("Editor.Gutter", ["editorGutter.background"]),
        ("Editor.PopupBorder", ["editorWidget.border", "editorHoverWidget.border"]),
        ("Editor.Text", ["editor.foreground"]),
        ("Editor.LineNumber", ["editorLineNumber.foreground"]),
        ("Editor.LineNumberCurrent", ["editorLineNumber.activeForeground"]),
        ("Editor.CurrentLine", ["editor.lineHighlightBackground"]),
        ("Editor.Selection", ["editor.selectionBackground"]),
        ("Editor.SelectionInactive", ["editor.inactiveSelectionBackground"]),
        ("Editor.Caret", ["editorCursor.foreground"]),
        ("Editor.SearchMatch", ["editor.findMatchHighlightBackground"]),
        ("Editor.SearchCurrent", ["editor.findMatchBackground"]),
        ("Editor.Error", ["editorError.foreground"]),
        ("Editor.Warning", ["editorWarning.foreground"]),
        ("Editor.Info", ["editorInfo.foreground"]),
        ("Editor.Breakpoint", ["debugIcon.breakpointForeground"]),
        ("Editor.ExecutionLine", ["editor.stackFrameHighlightBackground"]),
        ("Editor.InlayHint", ["editorInlayHint.foreground"]),
        ("Editor.InlayHintBack", ["editorInlayHint.background"]),
        ("Editor.UsageRead", ["editor.wordHighlightBackground"]),
        ("Editor.UsageWrite", ["editor.wordHighlightStrongBackground"]),
        ("Editor.MatchedBrace", ["editorBracketMatch.background"]),
        ("Editor.Whitespace", ["editorWhitespace.foreground"]),
        ("Editor.LinkUnderline", ["editorLink.activeForeground"]),
        ("Diff.Added", ["diffEditor.insertedLineBackground", "diffEditor.insertedTextBackground"]),
        ("Diff.Deleted", ["diffEditor.removedLineBackground", "diffEditor.removedTextBackground"]),
        ("Diff.AddedWord", ["diffEditor.insertedTextBackground"]),
        ("Diff.DeletedWord", ["diffEditor.removedTextBackground"]),
    ];

    /// <summary>The code's kinds and how a theme colours each: the semantic token types it is (C#'s through its language
    /// server), then TextMate scope stacks a grammar gives it, outermost first, the first a rule colours winning; a kind the
    /// theme colours no way takes the one after <c>or</c>.</summary>
    public static readonly IReadOnlyList<(string Kind, string[] Semantic, string[][] Scopes, string? Or)> Code =
    [
        ("Keyword", ["keyword"], [["source.cs", "keyword.other"], ["source.cs", "storage.modifier"], ["source.cs", "keyword"]], null),
        ("ControlKeyword", ["controlKeyword"], [["source.cs", "keyword.control"]], "Keyword"),
        ("Comment", ["comment"], [["source.cs", "comment.line.double-slash"], ["source.cs", "comment"]], null),
        ("DocComment", [], [["source.cs", "comment.block.documentation"], ["source.cs", "comment.documentation"]], "Comment"),
        ("String", ["string"], [["source.cs", "string.quoted.double"], ["source.cs", "string"]], null),
        ("StringEscape", ["stringEscapeCharacter"], [["source.cs", "string.quoted.double", "constant.character.escape"]], "String"),
        ("Number", ["number"], [["source.cs", "constant.numeric"]], null),
        ("Preprocessor", ["preprocessorKeyword"], [["source.cs", "meta.preprocessor", "keyword.preprocessor"], ["source.cs", "keyword.preprocessor"], ["source.cs", "meta.preprocessor"]], "Keyword"),
        ("Class", ["class"], [["source.cs", "entity.name.type.class"], ["source.cs", "entity.name.type"], ["source.cs", "support.class"]], null),
        ("Interface", ["interface"], [["source.cs", "entity.name.type.interface"]], "Class"),
        ("Struct", ["struct"], [["source.cs", "entity.name.type.struct"]], "Class"),
        ("Enum", ["enum"], [["source.cs", "entity.name.type.enum"]], "Class"),
        ("Delegate", ["delegate"], [["source.cs", "entity.name.type.delegate"]], "Class"),
        ("TypeParameter", ["typeParameter"], [["source.cs", "entity.name.type.type-parameter"], ["source.cs", "entity.name.type.parameter"]], "Class"),
        ("Method", ["method", "function"], [["source.cs", "entity.name.function"], ["source.cs", "support.function"]], null),
        ("Property", ["property"], [["source.cs", "variable.other.property"], ["source.cs", "variable.other.object.property"], ["source.cs", "support.variable.property"]], null),
        ("Event", ["event"], [["source.cs", "variable.other.event"]], "Property"),
        ("Field", ["field"], [["source.cs", "entity.name.variable.field"], ["source.cs", "variable.other.object.property"]], "Property"),
        ("Constant", ["constant"], [["source.cs", "variable.other.constant"], ["source.cs", "constant.other"]], "Field"),
        ("EnumMember", ["enumMember"], [["source.cs", "variable.other.enummember"], ["source.cs", "constant.other.enum"]], "Constant"),
        ("Tag", [], [["text.xml", "meta.tag.xml", "entity.name.tag.xml"], ["text.html.basic", "meta.tag", "entity.name.tag.html"]], null),
        ("AttributeName", [], [["text.xml", "meta.tag.xml", "entity.other.attribute-name.localname.xml"], ["text.html.basic", "meta.tag", "entity.other.attribute-name.html"]], null),
        ("AttributeValue", [], [["text.xml", "meta.tag.xml", "string.quoted.double.xml"], ["text.html.basic", "meta.tag", "string.quoted.double.html"]], "String"),
        ("Entity", [], [["text.xml", "constant.character.entity.xml"], ["text.html.basic", "constant.character.entity"]], "StringEscape"),
        ("JsonKey", [], [["source.json", "meta.structure.dictionary.json", "string.json", "support.type.property-name.json"]], "Property"),
        ("YamlKey", [], [["source.yaml", "meta.mapping.yaml", "entity.name.tag.yaml"]], "Tag"),
        ("Heading", [], [["text.html.markdown", "markup.heading.markdown", "entity.name.section.markdown"], ["text.html.markdown", "markup.heading.markdown"]], "Keyword"),
        ("Code", [], [["text.html.markdown", "markup.inline.raw.string.markdown"], ["text.html.markdown", "markup.fenced_code.block.markdown"]], "String"),
        ("Quote", [], [["text.html.markdown", "markup.quote.markdown"]], "Comment"),
        ("LinkDestination", [], [["text.html.markdown", "markup.underline.link.markdown"]], null),
        ("Parameter", ["parameter"], [["source.cs", "variable.parameter"]], null),
        ("Local", ["variable"], [["source.cs", "variable.other.readwrite"], ["source.cs", "variable.other.local"]], null),
        ("Namespace", ["namespace"], [["source.cs", "entity.name.namespace"], ["source.cs", "entity.name.type.namespace"]], null),
        ("Operator", ["operator"], [["source.cs", "keyword.operator"]], null),
    ];

    /// <summary>The theme's token and semantic colours alone, as a code colour scheme under any theme: each kind it colours,
    /// in its own variant as its theme draws it and in the other moved in lightness to read on that variant's grounds, and
    /// how its comments and parameters are set.</summary>
    public static ThemeFile ToScheme(VsCodeTheme theme)
    {
        ThemeFile whole = ToTheme(theme);
        (Dictionary<string, uint> syntax, _, _) = CodeOf(theme, Ground(theme));
        uint[] grounds = theme.Dark ? [0xFFFFFFFF, 0xFFECECEC] : [0xFF2D2D2D, 0xFF1E1E1E];
        Dictionary<string, TokenValue> own = new(StringComparer.Ordinal);
        Dictionary<string, TokenValue> other = new(StringComparer.Ordinal);
        foreach (string kind in syntax.Keys.Where(k => Code.Any(c => c.Kind == k)))
        {
            uint drawn = ((ColourValue)whole.Both[$"Syntax.{kind}"]).Argb;
            own[$"Syntax.{kind}"] = new ColourValue(drawn, 1);
            other[$"Syntax.{kind}"] = new ColourValue(Legible(drawn, grounds, 4.6, !theme.Dark), 1);
        }
        Dictionary<string, TokenValue> styles = new(whole.Both.Where(t => t.Key.StartsWith("SyntaxStyle.", StringComparison.Ordinal)), StringComparer.Ordinal);
        return new ThemeFile(theme.Name, string.Empty, styles, theme.Dark ? own : other, theme.Dark ? other : own);
    }

    /// <summary>The theme as a Passenger theme of its one variant, its every colour token given.</summary>
    public static ThemeFile ToTheme(VsCodeTheme theme)
    {
        bool dark = theme.Dark;
        Dictionary<string, uint> colors = new(theme.Colors, StringComparer.Ordinal);
        uint ground = Ground(theme);
        // A rule naming no scope gives the editor's words where the workbench colours do not.
        VsCodeRule? plain = theme.TokenColors.LastOrDefault(r => r.Selectors.Count == 0 && r.Foreground is not null);
        uint text = Opaque(Over(Colour(colors, "editor.foreground") ?? plain?.Foreground ?? Colour(colors, "foreground") ?? (dark ? 0xFFD4D4D4 : 0xFF333333), ground));
        (Dictionary<string, uint> syntax, bool italicComments, string? parameterStyle) = CodeOf(theme, ground);
        // Its bracket colours, one to six of them: the first three always, the rest as far as it gives them in turn.
        if (Colour(colors, "editorBracketHighlight.foreground1") is { } brace1)
        {
            uint before = brace1;
            for (int level = 1; level <= 6; level++)
            {
                uint? named = Colour(colors, $"editorBracketHighlight.foreground{level}") is { } own && own >> 24 != 0 ? own : null;
                if (level > 3 && named is null)
                {
                    break;
                }
                before = named ?? before;
                syntax[$"Brace{level}"] = Over(before, ground);
            }
        }
        if (Colour(colors, "textLink.foreground") is { } link)
        {
            syntax["Link"] = Over(link, ground);
        }

        uint Hue(uint fallback, params string[] keys) => keys.Select(k => Colour(colors, k)).FirstOrDefault(c => c is not null) is { } given ? Opaque(Over(given, ground)) : fallback;
        uint red = Hue(dark ? 0xFFCD3131 : 0xFFCD3131, "terminal.ansiRed", "errorForeground", "editorError.foreground");
        uint green = Hue(dark ? 0xFF0DBC79 : 0xFF00BC00, "terminal.ansiGreen", "gitDecoration.addedResourceForeground");
        uint yellow = Hue(dark ? 0xFFE5E510 : 0xFF949800, "terminal.ansiYellow", "editorWarning.foreground");
        uint blue = Hue(dark ? 0xFF2472C8 : 0xFF0451A5, "terminal.ansiBlue", "textLink.foreground");
        uint purple = Hue(dark ? 0xFFBC3FBC : 0xFFBC05BC, "terminal.ansiMagenta");
        uint cyan = Hue(dark ? 0xFF11A8CD : 0xFF0598BC, "terminal.ansiCyan");
        uint orange = syntax.TryGetValue("Number", out uint number) ? number : Mix(red, yellow, 0.5);
        uint accent = Hue(syntax.GetValueOrDefault("Keyword", blue), "button.background", "focusBorder", "activityBarBadge.background");
        ThemePalette palette = new(
            Ground: ground,
            Text: text,
            Muted: Hue(Mix(text, ground, 0.35), "descriptionForeground", "editorLineNumber.activeForeground"),
            Accent: accent,
            Glance: Hue(accent, "editorCursor.foreground"),
            Red: red, Orange: orange, Yellow: yellow, Green: green, Cyan: cyan, Blue: blue, Purple: purple,
            Pink: Hue(Mix(red, purple, 0.5), "terminal.ansiBrightMagenta"),
            Comment: syntax.GetValueOrDefault("Comment", Mix(text, ground, 0.45)),
            Member: syntax.GetValueOrDefault("Property", text));

        Dictionary<string, uint> given = new(StringComparer.Ordinal);
        foreach ((string token, string[] keys) in Workbench)
        {
            if (keys.Select(k => Colour(colors, k)).FirstOrDefault(c => c is not null) is { } colour)
            {
                given[token] = colour;
            }
        }
        given["Editor.Text"] = text;
        Dictionary<string, TokenValue> tokens = ThemePalettes.Tokens(palette, dark, given, syntax, italicComments);
        if (parameterStyle is not null)
        {
            tokens["SyntaxStyle.Parameter"] = new TextStyleValue(parameterStyle);
        }
        return new ThemeFile(theme.Name, string.Empty, tokens, new Dictionary<string, TokenValue>(StringComparer.Ordinal), new Dictionary<string, TokenValue>(StringComparer.Ordinal))
        {
            Shows = dark ? ThemeShows.Dark : ThemeShows.Light,
        };
    }

    /// <summary>The editor's ground the theme gives, opaque, or VS Code's own for its variant.</summary>
    private static uint Ground(VsCodeTheme theme) =>
        Opaque(Over(theme.Colors.TryGetValue("editor.background", out uint given) ? given : theme.Dark ? 0xFF1E1E1E : 0xFFFFFFFF, theme.Dark ? 0xFF000000 : 0xFFFFFFFF));

    /// <summary>The colours the theme gives the code's kinds, drawn over its ground, and how its comments and parameters are
    /// set: a kind it colours no way taking the one it falls back on.</summary>
    private static (Dictionary<string, uint> Syntax, bool ItalicComments, string? ParameterStyle) CodeOf(VsCodeTheme theme, uint ground)
    {
        Dictionary<string, uint> syntax = new(StringComparer.Ordinal);
        bool italicComments = false;
        string? parameterStyle = null;
        foreach ((string kind, string[] semantic, string[][] scopes, string? or) in Code)
        {
            if (Coloured(semantic, scopes, out string? style) is { } colour)
            {
                syntax[kind] = Opaque(Over(colour, ground));
                italicComments |= kind == "Comment" && style is not null && style.Contains("italic", StringComparison.Ordinal);
                parameterStyle = kind == "Parameter" ? Style(style) : parameterStyle;
            }
            else if (or is not null && syntax.TryGetValue(or, out uint other))
            {
                syntax[kind] = other;
            }
        }
        return (syntax, italicComments, parameterStyle);

        uint? Coloured(string[] semantic, string[][] scopes, out string? style)
        {
            style = null;
            if (theme.SemanticHighlighting)
            {
                foreach (string type in semantic)
                {
                    VsCodeStyle? own = theme.SemanticColors.GetValueOrDefault($"{type}:csharp") ?? theme.SemanticColors.GetValueOrDefault(type);
                    if (own?.Foreground is { } colour)
                    {
                        style = own.Italic == true ? "italic" : null;
                        return colour;
                    }
                }
            }
            foreach (string[] stack in scopes)
            {
                (uint? colour, string? font) = VsCodeScopes.Resolve(theme.TokenColors, stack);
                if (colour is not null)
                {
                    style = font;
                    return colour;
                }
            }
            return null;
        }
    }

    /// <summary>How a TextMate rule's font style sets words, in a theme's words, or null where it sets them plainly.</summary>
    private static string? Style(string? fontStyle)
    {
        bool bold = fontStyle?.Contains("bold", StringComparison.Ordinal) == true;
        bool italic = fontStyle?.Contains("italic", StringComparison.Ordinal) == true;
        return (bold, italic) switch
        {
            (true, true) => "Bold Italic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => null,
        };
    }

    private static uint? Colour(Dictionary<string, uint> colors, string key) => colors.TryGetValue(key, out uint colour) ? colour : null;
}
