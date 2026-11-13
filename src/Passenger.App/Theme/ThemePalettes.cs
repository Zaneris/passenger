using static Passenger.App.ThemeColours;

namespace Passenger.App;

/// <summary>A variant's few colours, from which every colour token is worked out: the editor's ground, the words and quieter
/// words, the accent, the caret's glance, the hues, and the code's comment and member colours.</summary>
public sealed record ThemePalette(uint Ground, uint Text, uint Muted, uint Accent, uint Glance, uint Red, uint Orange, uint Yellow, uint Green,
    uint Cyan, uint Blue, uint Purple, uint Pink, uint Comment, uint Member);

/// <summary>Every colour token of one variant worked out from a <see cref="ThemePalette"/>, as tools/themes.py works out the
/// themes it writes: grounds a step apart, washes for what the pointer is over, and every word, icon and mark moved in
/// lightness until it meets WCAG AA on what it is drawn on (text 4.5:1, a mark 3:1). A colour given for a token is taken in
/// place of the one worked out, a ground kept only where it stays apart from the others and of the variant's own darkness,
/// a word or mark still held to AA. Tokens are named without <c>Passenger.</c>. Pure.</summary>
public static class ThemePalettes
{
    /// <summary>The code's kinds coloured from the palette, each also held to AA on the editor's ground and current line.</summary>
    public static readonly IReadOnlyList<string> SyntaxKinds =
    [
        "Keyword", "ControlKeyword", "Comment", "DocComment", "String", "StringEscape", "Number", "Preprocessor", "Class", "Interface", "Struct",
        "Enum", "Delegate", "TypeParameter", "Method", "Property", "Event", "Field", "Constant", "EnumMember", "ErrorOutput", "UserInput", "Link",
        "BuildError", "BuildWarning", "Brace1", "Brace2", "Brace3", "Tag", "AttributeName", "AttributeValue", "Entity", "JsonKey", "YamlKey",
        "Heading", "Code", "Quote", "LinkDestination",
    ];

    private const double Text = 4.6;
    private const double Mark = 3.1;
    private const double Apart = 2.5;

    /// <summary>Every colour token of a variant, with the shadows and the comments' style; <paramref name="given"/> names the
    /// colours a theme gives for tokens, <paramref name="syntax"/> those for code kinds (<c>Keyword</c>), and
    /// <paramref name="italicComments"/> whether comments are set in italics.</summary>
    public static Dictionary<string, TokenValue> Tokens(ThemePalette p, bool dark, IReadOnlyDictionary<string, uint> given,
        IReadOnlyDictionary<string, uint> syntax, bool italicComments)
    {
        Dictionary<string, uint> t = new(StringComparer.Ordinal);
        uint G(string token, uint derived) => given.TryGetValue(token, out uint colour) ? colour : derived;
        uint L(string token, uint derived, IReadOnlyList<uint> grounds, double least, bool onDark) => Legible(G(token, derived), grounds, least, onDark);
        uint Ground(string token, uint derived, params uint[] others) =>
            given.TryGetValue(token, out uint colour) && IsDark(colour) == dark && others.All(o => Math.Abs(CieLightness(colour) - CieLightness(o)) >= Apart)
                ? Opaque(colour) : Away(derived, others);
        uint Away(uint colour, uint[] others)
        {
            for (int i = 0; i < 40 && others.Any(o => Math.Abs(CieLightness(colour) - CieLightness(o)) < Apart); i++)
            {
                colour = Shift(colour, dark ? -0.01 : 0.01);
            }
            return colour;
        }

        uint text = p.Text;
        uint muted = p.Muted;
        uint editor = Opaque(p.Ground);
        uint panel = Ground("Panel", dark ? Shift(editor, 0.035) : Shift(editor, -0.03), editor);
        uint frame = Ground("Frame", dark ? Shift(editor, -0.04) : Shift(editor, -0.075), editor, panel);
        uint field = Ground("Field", dark ? Shift(editor, -0.012) : Lighter(editor), panel);
        uint list = G("List", dark ? Shift(editor, -0.02) : Lighter(editor));
        uint button = G("Button", dark ? Shift(panel, 0.03) : Lighter(editor));
        uint popup = Ground("Popup", dark ? Shift(panel, 0.025) : Lighter(editor), frame);
        uint divider = G("Divider", dark ? frame : Shift(frame, -0.04));

        uint ink = dark ? 0xFFFFFFFF : 0xFF000000;
        uint hover = dark ? Wash(Mix(text, ink, 0.4), 0x14) : Wash(text, 0x12);
        uint selected = dark ? Wash(Mix(text, ink, 0.4), 0x24) : Wash(text, 0x21);

        uint white = 0xFFFFFFFF;
        uint black = dark ? Shift(frame, -0.03) : 0xFF141414;
        uint accent = Opaque(G("Accent", p.Accent));
        uint accentText = given.TryGetValue("AccentText", out uint onAccent) ? onAccent : Ratio(white, accent) >= Ratio(black, accent) ? white : black;
        accent = Legible(accent, [accentText], Text, !IsDark(accentText));
        accent = Legible(accent, [panel], Mark - 0.05, dark);
        accentText = Legible(accentText, [accent], Text, IsDark(accent));
        uint accentHover = G("AccentHover", Shift(accent, IsDark(accentText) ? 0.04 : -0.04));
        uint row = Ground("RowSelected", Mix(panel, accent, dark ? 0.24 : 0.16), panel);
        uint banner = Mix(editor, accent, 0.12);
        uint tile = Mix(editor, panel, 0.5);
        uint[] textGrounds = [panel, frame, field, popup, list, Over(hover, panel), Over(hover, frame), Over(selected, panel), row, editor, button, banner, tile];
        uint glance = p.Glance;

        t["Frame"] = frame;
        t["Panel"] = panel;
        t["Field"] = field;
        t["FieldBorder"] = G("FieldBorder", Mix(panel, text, 0.2));
        t["List"] = list;
        t["Button"] = button;
        t["Popup"] = popup;
        t["PopupBorder"] = G("PopupBorder", Mix(popup, text, 0.16));
        t["Popup.Edge"] = t["PopupBorder"];
        t["Banner"] = banner;
        t["DropZone"] = Wash(accent, dark ? (byte)0x38 : (byte)0x24);
        t["Divider"] = divider;
        t["Separator"] = G("Separator", Mix(panel, text, 0.1));
        t["StripeSeparator"] = Mix(frame, text, 0.22);
        t["WindowEdge"] = t["FieldBorder"];

        t["Text"] = L("Text", text, textGrounds, Text, dark);
        t["TextMuted"] = L("TextMuted", muted, textGrounds, Text, dark);
        t["TextDisabled"] = Mix(panel, text, 0.36);
        t["DisabledBorder"] = t["Separator"];
        uint[] marks = [panel, frame, popup, editor];
        t["Icon"] = L("Icon", Mix(text, muted, 0.5), marks, Mark, dark);
        uint link = L("Link", p.Blue, marks, Text, dark);
        t["Link"] = link;
        t["LinkHover"] = L("LinkHover", Shift(link, dark ? 0.06 : -0.06), [panel], Text, dark);
        t["AccentText"] = accentText;
        t["BadgeText"] = dark ? field : 0xFFFFFFFF;
        t["Error"] = L("Error", p.Red, marks, Text, dark);
        t["Warning"] = L("Warning", dark ? p.Yellow : p.Orange, marks, Text, dark);
        uint selection = Over(G("Editor.Selection", Mix(editor, p.Blue, dark ? 0.3 : 0.22)), editor);
        t["TextSelection"] = Over(G("TextSelection", selection), field);

        t["Hover"] = hover;
        t["Selected"] = selected;
        t["RowSelected"] = row;
        t["Accent"] = accent;
        t["AccentHover"] = Legible(accentHover, [accentText], Text, !IsDark(accentText));
        t["Focus"] = L("Focus", glance, [.. marks, field], Mark, dark);
        t["CheckBorder"] = Mix(panel, text, 0.45);
        t["ScrollThumb"] = G("ScrollThumb", dark ? 0x59808080u : 0x33000000u);
        t["ScrollThumbHover"] = G("ScrollThumbHover", dark ? 0x8C808080u : 0x80000000u);
        t["ScrollTrackHover"] = 0x1A808080;
        uint menu = Opaque(G("Menu.Selected", accent));
        t["Menu.Selected"] = menu;
        t["Menu.SelectedText"] = L("Menu.SelectedText", Ratio(white, menu) >= Ratio(black, menu) ? white : black, [menu], Text, IsDark(menu));
        t["Menu.SelectedKeys"] = t["Menu.SelectedText"];

        // The title bar is the frame unless the theme gives its own, its words and marks read on it and its hover.
        uint bar = given.TryGetValue("TitleBar", out uint ownBar) ? Opaque(ownBar) : frame;
        bool barDark = IsDark(bar);
        uint barHover = Shift(bar, barDark ? 0.05 : -0.05);
        uint barPressed = Shift(bar, barDark ? 0.08 : -0.08);
        uint[] bars = [bar, barHover, barPressed];
        t["TitleBar"] = bar;
        t["TitleBar.Text"] = L("TitleBar.Text", text, bars, Text, barDark);
        t["TitleBar.Icon"] = Legible(t["Icon"], [bar], Mark, barDark);
        t["TitleBar.Hover"] = barHover;
        t["TitleBar.Pressed"] = barPressed;
        t["TitleBar.Rule"] = Mix(bar, t["TitleBar.Text"], 0.22);
        t["TitleBar.Run"] = Legible(p.Green, [bar], Mark, barDark);
        t["TitleBar.Stop"] = Legible(p.Red, [bar], Mark, barDark);
        t["TitleBar.HotReload"] = Legible(p.Orange, [bar], Mark, barDark);
        t["TitleBar.Focus"] = Legible(glance, bars, Mark, barDark);
        t["TitleBar.Badge"] = accent;
        t["TitleBar.BadgeText"] = accentText;
        t["WindowButton.Hover"] = barDark ? 0x1AFFFFFFu : 0x1A000000u;
        t["WindowButton.Pressed"] = barDark ? 0x33FFFFFFu : 0x33000000u;
        t["WindowButton.CloseHover"] = 0xFFE81123;
        t["WindowButton.ClosePressed"] = 0xFFF1707A;
        t["WindowButton.CloseGlyph"] = 0xFFFFFFFF;

        uint stripe = Ground("Stripe.Selected", Mix(frame, accent, dark ? 0.28 : 0.2), frame);
        t["Stripe.Selected"] = stripe;
        t["Stripe.SelectedIcon"] = L("Stripe.SelectedIcon", Mix(text, accent, 0.55), [stripe, row], Mark, dark);
        uint tab = given.TryGetValue("Tab.Active", out uint ownTab) && IsDark(ownTab) == dark ? Opaque(ownTab) : editor;
        t["Tab.Active"] = tab;
        t["Tab.ActiveText"] = L("Tab.ActiveText", text, [tab], Text, dark);
        t["Modified"] = L("Modified", glance, [frame, panel], Mark, dark);

        t["Welcome.Tile"] = tile;
        t["Welcome.TileBorder"] = Mix(tile, text, 0.1);
        t["Welcome.Chip"] = row;
        t["Welcome.ChipIcon"] = t["Stripe.SelectedIcon"];
        t["Welcome.Badge"] = accent;

        t["Run"] = L("Run", p.Green, [panel, frame], Mark, dark);
        t["HotReload"] = Legible(p.Orange, [panel, frame], Mark, dark);
        t["Test.Passed"] = L("Test.Passed", t["Run"], [panel], Mark, dark);
        t["Test.Failed"] = L("Test.Failed", p.Red, [panel], Mark, dark);
        t["Test.Skipped"] = G("Test.Skipped", Mix(muted, panel, 0.15));
        t["Test.Running"] = Legible(p.Blue, [panel], Mark, dark);
        t["Test.NotRun"] = t["CheckBorder"];

        t["Vcs.Added"] = G("Vcs.Added", p.Green);
        t["Vcs.Modified"] = G("Vcs.Modified", p.Blue);
        t["Vcs.Deleted"] = G("Vcs.Deleted", Mix(muted, panel, 0.15));
        t["Vcs.Conflict"] = p.Red;
        uint[] files = [panel, row];
        t["Vcs.File.Modified"] = L("Vcs.File.Modified", p.Blue, files, Text, dark);
        t["Vcs.File.Added"] = L("Vcs.File.Added", p.Green, files, Text, dark);
        t["Vcs.File.Deleted"] = G("Vcs.File.Deleted", Mix(muted, panel, 0.25));
        t["Vcs.File.Unversioned"] = L("Vcs.File.Unversioned", p.Orange, files, Text, dark);
        t["Vcs.File.Conflicted"] = L("Vcs.File.Conflicted", p.Red, files, Text, dark);
        uint[] lanes = [p.Blue, p.Green, p.Yellow, p.Purple, p.Red, p.Cyan];
        for (int i = 0; i < lanes.Length; i++)
        {
            t[$"Graph.{i}"] = Legible(lanes[i], [panel], 2.2, dark);
        }
        t["Vcs.Ref"] = Mix(panel, text, 0.1);
        t["Vcs.RefText"] = Legible(Mix(text, muted, 0.5), [t["Vcs.Ref"]], Text, dark);
        t["Vcs.Favorite"] = Legible(p.Yellow, [panel], Mark, dark);
        t["Vcs.Signature.Good"] = t["Run"];
        t["Vcs.Signature.Unverified"] = t["Test.Skipped"];
        t["Vcs.Signature.Bad"] = t["Test.Failed"];
        t["Vcs.Signature.Expired"] = t["Vcs.Favorite"];

        t["Picker.RingInner"] = 0xFFFFFFFF;
        t["Picker.RingOuter"] = 0xFF000000;
        t["Picker.Checker"] = 0xFFCCCCCC;

        // The terminal on its own ground (the editor's unless given), its ANSI colours the theme's or the palette's hues.
        uint terminal = given.TryGetValue("Terminal.Background", out uint ownTerminal) && IsDark(ownTerminal) == dark ? Opaque(ownTerminal) : editor;
        t["Terminal.Background"] = terminal;
        t["Terminal.Foreground"] = L("Terminal.Foreground", text, [terminal], Text, dark);
        t["Terminal.Cursor"] = G("Terminal.Cursor", glance);
        t["Terminal.Selection"] = Over(G("Terminal.Selection", selection), terminal);
        uint[] ansi = [p.Red, p.Green, p.Yellow, p.Blue, p.Purple, p.Cyan];
        double up = dark ? 0.07 : -0.042;
        t["Terminal.Ansi0"] = G("Terminal.Ansi0", dark ? Mix(editor, text, 0.2) : Shift(text, -0.1));
        for (int i = 1; i <= ansi.Length; i++)
        {
            t[$"Terminal.Ansi{i}"] = G($"Terminal.Ansi{i}", dark ? ansi[i - 1] : Legible(ansi[i - 1], [terminal], 3.0, dark));
            t[$"Terminal.Ansi{i + 8}"] = G($"Terminal.Ansi{i + 8}", Shift(t[$"Terminal.Ansi{i}"], up));
        }
        t["Terminal.Ansi7"] = G("Terminal.Ansi7", Mix(muted, text, 0.3));
        t["Terminal.Ansi8"] = G("Terminal.Ansi8", Mix(editor, text, 0.42));
        t["Terminal.Ansi15"] = G("Terminal.Ansi15", dark ? Shift(text, 0.08) : Mix(editor, text, 0.32));

        t["File.Folder"] = Mix(text, muted, 0.6);
        t["File.SolutionFolder"] = p.Purple;
        t["File.Plain"] = t["File.Folder"];
        (string Name, uint Hue)[] icons =
        [
            ("Red", p.Red), ("Orange", p.Orange), ("Yellow", p.Yellow), ("Green", p.Green), ("Cyan", p.Cyan), ("Blue", p.Blue), ("Purple", p.Purple),
            ("Pink", p.Pink), ("Maroon", Shift(Mix(p.Red, p.Orange, 0.45), -0.08)), ("Grey", Mix(text, muted, 0.8)),
        ];
        foreach ((string name, uint hue) in icons)
        {
            t[$"FileIcon.{name}"] = Legible(hue, [panel], Mark, dark);
        }

        t["Editor.Background"] = editor;
        t["Editor.Gutter"] = given.TryGetValue("Editor.Gutter", out uint gutter) && IsDark(gutter) == dark ? Over(gutter, editor) : editor;
        t["Editor.PopupBorder"] = G("Editor.PopupBorder", t["FieldBorder"]);

        // The editor's own colour scheme.
        uint current = Over(G("Editor.CurrentLine", Shift(editor, dark ? 0.025 : -0.018)), editor);
        uint execution = Over(G("Editor.ExecutionLine", Mix(editor, p.Blue, 0.18)), editor);
        uint[] code = [editor, current];
        t["Editor.Text"] = L("Editor.Text", text, [editor, current, selection, execution], Text, dark);
        t["Editor.LineNumber"] = L("Editor.LineNumber", Mix(muted, editor, 0.25), [t["Editor.Gutter"]], Text, dark);
        t["Editor.LineNumberCurrent"] = L("Editor.LineNumberCurrent", glance, [t["Editor.Gutter"]], Text, dark);
        t["Editor.CurrentLine"] = current;
        t["Editor.Selection"] = selection;
        t["Editor.SelectionInactive"] = Over(G("Editor.SelectionInactive", Mix(editor, text, 0.08)), editor);
        t["Editor.Caret"] = L("Editor.Caret", glance, code, Mark, dark);
        t["Editor.SearchMatch"] = Over(G("Editor.SearchMatch", Mix(editor, p.Yellow, dark ? 0.18 : 0.28)), editor);
        t["Editor.SearchCurrent"] = Over(G("Editor.SearchCurrent", Mix(editor, p.Yellow, dark ? 0.45 : 0.55)), editor);
        t["Editor.Error"] = G("Editor.Error", p.Red);
        t["Editor.Warning"] = G("Editor.Warning", dark ? p.Yellow : p.Orange);
        t["Editor.Info"] = G("Editor.Info", muted);
        t["Editor.Breakpoint"] = G("Editor.Breakpoint", p.Red);
        t["Editor.BreakpointLine"] = Mix(editor, p.Red, 0.12);
        t["Editor.BreakpointMuted"] = t["Icon"];
        t["Editor.Tracepoint"] = p.Yellow;
        t["Editor.ExecutionLine"] = execution;
        t["Editor.ExecutionPoint"] = glance;
        t["Editor.ExceptionBack"] = Mix(editor, p.Red, 0.18);
        t["Editor.InlineValue"] = Legible(muted, [editor, execution], Text, dark);
        uint hintBack = Over(G("Editor.InlayHintBack", Mix(editor, text, 0.06)), editor);
        t["Editor.InlayHint"] = L("Editor.InlayHint", muted, [hintBack], Text, dark);
        t["Editor.InlayHintBack"] = hintBack;
        t["Editor.UsageRead"] = Over(G("Editor.UsageRead", Mix(editor, p.Blue, 0.14)), editor);
        t["Editor.UsageWrite"] = Over(G("Editor.UsageWrite", Mix(editor, p.Pink, 0.14)), editor);
        t["Editor.UsageReadMark"] = p.Blue;
        t["Editor.UsageWriteMark"] = p.Pink;
        t["Editor.MatchedBrace"] = Over(G("Editor.MatchedBrace", Mix(editor, p.Cyan, 0.2)), editor);
        t["Editor.SmartStepTarget"] = Mix(editor, p.Purple, 0.2);
        t["Editor.SmartStepChosen"] = Mix(editor, p.Purple, 0.38);
        t["Editor.SmartStepBorder"] = p.Purple;
        t["Editor.Whitespace"] = G("Editor.Whitespace", Mix(editor, text, 0.2));
        t["Editor.SoftWrapSign"] = Mix(editor, text, 0.38);
        t["Editor.LinkUnderline"] = G("Editor.LinkUnderline", p.Blue);
        t["Editor.Bookmark"] = p.Blue;
        foreach ((string name, uint? tone) in ((string, uint?)[])[("Added", p.Green), ("Modified", p.Blue), ("Deleted", null), ("Conflict", p.Red)])
        {
            uint other = tone ?? text;
            t[$"Diff.{name}"] = Over(G($"Diff.{name}", Mix(editor, other, (tone is null ? 0.08 : 0.15) - (dark ? 0 : 0.01))), editor);
            t[$"Diff.{name}Word"] = Over(G($"Diff.{name}Word", Mix(editor, other, (tone is null ? 0.18 : 0.32) - (dark ? 0 : 0.02))), editor);
        }

        foreach (string kind in SyntaxKinds)
        {
            t[$"Syntax.{kind}"] = Legible(syntax.TryGetValue(kind, out uint colour) ? Opaque(colour) : Fallback(kind), code, Text, dark);
        }
        t["Syntax.Excluded"] = Mix(editor, text, 0.35);

        Dictionary<string, TokenValue> tokens = new(StringComparer.Ordinal);
        foreach ((string token, uint colour) in t)
        {
            tokens[token] = new ColourValue(colour, 1);
        }
        tokens["PopupShadow"] = new ShadowValue(dark ? "0 6 20 0 #80000000" : "0 6 20 0 #2E000000");
        tokens["Welcome.TileShadow"] = new ShadowValue("0 0 0 0 #00000000");
        tokens["SyntaxStyle.Comment"] = new TextStyleValue(italicComments ? "Italic" : "Normal");
        tokens["SyntaxStyle.DocComment"] = new TextStyleValue(italicComments ? "Italic" : "Normal");
        return tokens;

        uint Fallback(string kind) => kind switch
        {
            "Keyword" or "ControlKeyword" or "YamlKey" => p.Purple,
            "Comment" or "Quote" => p.Comment,
            "DocComment" => Mix(p.Comment, p.Yellow, 0.35),
            "String" or "AttributeValue" or "Code" or "UserInput" => p.Green,
            "StringEscape" or "Entity" => p.Pink,
            "Number" or "Constant" => p.Orange,
            "Preprocessor" => Mix(muted, text, 0.2),
            "Class" or "Interface" or "TypeParameter" or "AttributeName" => p.Yellow,
            "Struct" or "Enum" or "Delegate" => Mix(p.Yellow, text, 0.25),
            "Method" => p.Blue,
            "Property" or "Field" or "EnumMember" or "JsonKey" => p.Member,
            "Event" => p.Pink,
            "ErrorOutput" or "BuildError" => p.Red,
            "BuildWarning" => dark ? p.Yellow : p.Orange,
            "Brace1" => p.Yellow,
            "Brace2" => p.Purple,
            "Brace3" or "Link" or "LinkDestination" => p.Blue,
            "Tag" => p.Blue,
            "Heading" => p.Red,
            _ => text,
        };

        uint Lighter(uint ground) => Lightness(ground) < 0.98 ? Shift(ground, 0.02) : ground;
    }
}
