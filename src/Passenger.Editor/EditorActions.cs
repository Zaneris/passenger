namespace Passenger.Editor;

/// <summary>The editor's actions, by the name a keymap binds. Movements extend the selection when
/// pressed with Shift (<see cref="IsMovement"/>).</summary>
[IdentifierHome]
public static class EditorActions
{
    public const string Left = "editor.left";
    public const string Right = "editor.right";
    public const string Up = "editor.up";
    public const string Down = "editor.down";
    public const string WordLeft = "editor.wordLeft";
    public const string WordRight = "editor.wordRight";
    public const string LineStart = "editor.lineStart";
    public const string LineEnd = "editor.lineEnd";
    public const string TextStart = "editor.textStart";
    public const string TextEnd = "editor.textEnd";
    public const string PageUp = "editor.pageUp";
    public const string PageDown = "editor.pageDown";

    public const string ScrollUp = "editor.scrollUp";
    public const string ScrollDown = "editor.scrollDown";
    public const string Backspace = "editor.backspace";
    public const string Delete = "editor.delete";
    public const string DeleteWordLeft = "editor.deleteWordLeft";
    public const string DeleteWordRight = "editor.deleteWordRight";
    public const string NewLine = "editor.newLine";
    public const string StartNewLine = "editor.startNewLine";
    public const string StartNewLineBefore = "editor.startNewLineBefore";
    public const string Indent = "editor.indent";
    public const string Outdent = "editor.outdent";
    public const string Duplicate = "editor.duplicate";
    public const string DeleteLine = "editor.deleteLine";
    public const string MoveLineUp = "editor.moveLineUp";
    public const string MoveLineDown = "editor.moveLineDown";
    public const string ToggleLineComment = "editor.toggleLineComment";
    public const string ToggleBlockComment = "editor.toggleBlockComment";
    public const string Comment = "editor.comment";
    public const string Uncomment = "editor.uncomment";
    public const string JoinLines = "editor.joinLines";
    public const string ToggleCase = "editor.toggleCase";
    public const string CutToLineEnd = "editor.cutToLineEnd";
    public const string Undo = "editor.undo";
    public const string Redo = "editor.redo";
    public const string Cut = "editor.cut";
    public const string Copy = "editor.copy";
    public const string Paste = "editor.paste";
    public const string SelectAll = "editor.selectAll";
    public const string SelectNextOccurrence = "editor.selectNextOccurrence";
    public const string UnselectOccurrence = "editor.unselectOccurrence";
    public const string SelectAllOccurrences = "editor.selectAllOccurrences";
    public const string CaretPerLine = "editor.caretPerLine";
    public const string CloneCaretAbove = "editor.cloneCaretAbove";
    public const string CloneCaretBelow = "editor.cloneCaretBelow";
    public const string Escape = "editor.escape";
    public const string ToggleFold = "editor.toggleFold";
    public const string Fold = "editor.fold";
    public const string Unfold = "editor.unfold";
    public const string FoldAll = "editor.foldAll";
    public const string UnfoldAll = "editor.unfoldAll";
    public const string FoldToDefinitions = "editor.foldToDefinitions";
    public const string UnfoldDocComments = "editor.unfoldDocComments";
    public const string FoldDocComments = "editor.foldDocComments";
    public const string MatchBrace = "editor.matchBrace";

    /// <summary>Every editor action and its name for people (Rider's where it has one), which the IDE lists and runs.</summary>
    public static IReadOnlyList<(string Id, string Title)> All { get; } =
    [
        (Left, "Left"), (Right, "Right"), (Up, "Up"), (Down, "Down"), (WordLeft, "Word Left"), (WordRight, "Word Right"),
        (LineStart, "Line Start"), (LineEnd, "Line End"), (TextStart, "Text Start"), (TextEnd, "Text End"), (PageUp, "Page Up"),
        (PageDown, "Page Down"), (ScrollUp, "Scroll Up"), (ScrollDown, "Scroll Down"), (Backspace, "Backspace"), (Delete, "Delete"),
        (DeleteWordLeft, "Delete Word Left"), (DeleteWordRight, "Delete Word Right"), (NewLine, "New Line"),
        (StartNewLine, "Start New Line"), (StartNewLineBefore, "Start New Line Before"), (Indent, "Indent"), (Outdent, "Outdent"),
        (Duplicate, "Duplicate"), (DeleteLine, "Delete Line"), (MoveLineUp, "Move Line Up"), (MoveLineDown, "Move Line Down"),
        (ToggleLineComment, "Comment with Line Comment"), (ToggleBlockComment, "Comment with Block Comment"), (Comment, "Comment"), (Uncomment, "Uncomment"), (JoinLines, "Join Lines"),
        (ToggleCase, "Toggle Case"), (CutToLineEnd, "Cut to Line End"), (Undo, "Undo"), (Redo, "Redo"), (Cut, "Cut"), (Copy, "Copy"),
        (Paste, "Paste"), (SelectAll, "Select All"), (SelectNextOccurrence, "Select Next Occurrence"),
        (UnselectOccurrence, "Unselect Occurrence"), (SelectAllOccurrences, "Select All Occurrences"), (CaretPerLine, "Caret Per Line"),
        (CloneCaretAbove, "Clone Caret Above"), (CloneCaretBelow, "Clone Caret Below"), (Escape, "Escape"),
        (ToggleFold, "Toggle Folding"), (Fold, "Collapse"), (Unfold, "Expand"), (FoldAll, "Collapse All"), (UnfoldAll, "Expand All"),
        (FoldToDefinitions, "Collapse to Definitions"), (UnfoldDocComments, "Expand Doc Comments"), (FoldDocComments, "Collapse Doc Comments"),
        (MatchBrace, "Move Caret to Matching Brace"),
    ];

    private static readonly HashSet<string> Movements =
        [Left, Right, Up, Down, WordLeft, WordRight, LineStart, LineEnd, TextStart, TextEnd, PageUp, PageDown];

    public static bool IsMovement(string action) => Movements.Contains(action);
}

/// <summary>The keymaps a person chooses between in Settings, Keymap, each the platform's own (macOS's or the others'),
/// their own shortcuts kept over the one chosen.</summary>
public enum KeymapPreset
{
    /// <summary>Rider's Visual Studio keymaps, which Passenger starts with.</summary>
    VisualStudio,

    /// <summary>Rider's VS Code keymaps.</summary>
    VSCode,
}

/// <summary>The keymaps Passenger has: Rider's Visual Studio keymap on Windows and its Visual Studio OSX keymap on macOS,
/// for the actions Passenger has, plus the few platform staples those leave out (Cmd+/ to comment, with the keypad's / as
/// $default has it, Cmd+W to close); and Rider's VSCode and VSCode OSX keymaps, a key IntelliJ gives two actions told
/// apart by where it is pressed given to one (the editor's own). The keypad's keys (DIVIDE, SUBTRACT) are bound where
/// Rider binds them, beside the main keys'.</summary>
public static class DefaultKeymaps
{
    public static Keymap ForPlatform() => OperatingSystem.IsMacOS() ? VisualStudioMac() : VisualStudio();

    /// <summary>The editor's part of a keymap, the platform's.</summary>
    public static Keymap For(KeymapPreset preset) => preset switch
    {
        KeymapPreset.VSCode => OperatingSystem.IsMacOS() ? VSCodeMac() : VSCode(),
        _ => ForPlatform(),
    };

    /// <summary>A keymap's name as Settings lists it, the platform's (Rider's OSX keymaps are "(macOS)" here).</summary>
    public static string NameOf(KeymapPreset preset) => (preset, OperatingSystem.IsMacOS()) switch
    {
        (KeymapPreset.VSCode, true) => "VS Code (macOS)",
        (KeymapPreset.VSCode, false) => "VS Code",
        (_, true) => "Visual Studio (macOS)",
        _ => "Visual Studio",
    };

    public static Keymap VisualStudioMac() => new Keymap("Visual Studio (macOS)")
        .Bind(EditorActions.Left, "LEFT")
        .Bind(EditorActions.Right, "RIGHT")
        .Bind(EditorActions.Up, "UP")
        .Bind(EditorActions.Down, "DOWN")
        .Bind(EditorActions.WordLeft, "alt LEFT")
        .Bind(EditorActions.WordRight, "alt RIGHT")
        .Bind(EditorActions.LineStart, "meta LEFT", "HOME", "control A")
        .Bind(EditorActions.LineEnd, "meta RIGHT", "END", "control E")
        .Bind(EditorActions.TextStart, "meta UP", "control HOME")
        .Bind(EditorActions.TextEnd, "meta DOWN", "control END")
        .Bind(EditorActions.PageUp, "PAGE_UP")
        .Bind(EditorActions.PageDown, "PAGE_DOWN")
        .Bind(EditorActions.Backspace, "BACK_SPACE")
        .Bind(EditorActions.Delete, "DELETE")
        .Bind(EditorActions.DeleteWordLeft, "alt BACK_SPACE")
        .Bind(EditorActions.DeleteWordRight, "alt DELETE")
        .Bind(EditorActions.NewLine, "ENTER")
        .Bind(EditorActions.StartNewLine, "shift ENTER")
        .Bind(EditorActions.StartNewLineBefore, "meta ENTER")
        .Bind(EditorActions.Indent, "TAB")
        .Bind(EditorActions.Outdent, "shift TAB")
        .Bind(EditorActions.Duplicate, "control D")
        .Bind(EditorActions.DeleteLine, "meta shift L")
        .Bind(EditorActions.MoveLineUp, "alt UP")
        .Bind(EditorActions.MoveLineDown, "alt DOWN")
        .Bind(EditorActions.ToggleLineComment, "meta SLASH", "meta DIVIDE", "meta alt SLASH", "meta alt DIVIDE", "meta K , meta SLASH")
        // Its keypad key is Shift+Cmd+/, not Ctrl+Cmd+/, as Rider's Visual Studio OSX has it.
        .Bind(EditorActions.ToggleBlockComment, "control meta SLASH", "meta shift DIVIDE")
        .Bind(EditorActions.Comment, "meta K , C", "meta K , meta C")
        .Bind(EditorActions.Uncomment, "meta K , U", "meta K , meta U")
        .Bind(EditorActions.JoinLines, "control shift J")
        .Bind(EditorActions.ToggleCase, "control shift U")
        .Bind(EditorActions.CutToLineEnd, "control K")
        .Bind(EditorActions.Undo, "meta Z")
        .Bind(EditorActions.Redo, "meta shift Z", "meta Y")
        .Bind(EditorActions.Cut, "meta X")
        .Bind(EditorActions.Copy, "meta C")
        .Bind(EditorActions.Paste, "meta V")
        .Bind(EditorActions.SelectAll, "meta A")
        .Bind(EditorActions.SelectNextOccurrence, "alt shift PERIOD")
        .Bind(EditorActions.UnselectOccurrence, "alt shift COMMA")
        .Bind(EditorActions.SelectAllOccurrences, "alt shift SEMICOLON")
        .Bind(EditorActions.CaretPerLine, "shift alt G")
        .Bind(EditorActions.CloneCaretAbove, "alt shift UP")
        .Bind(EditorActions.CloneCaretBelow, "alt shift DOWN")
        .Bind(EditorActions.ToggleFold, "control M , M", "control M , control M")
        .Bind(EditorActions.Fold, "control M , S", "control M , control S")
        .Bind(EditorActions.Unfold, "control M , E", "control M , control E")
        .Bind(EditorActions.FoldAll, "control M , A", "control M , control A")
        .Bind(EditorActions.UnfoldAll, "control M , X", "control M , control X")
        .Bind(EditorActions.FoldToDefinitions, "control M , O", "control M , control O")
        .Bind(EditorActions.MatchBrace, "meta CLOSE_BRACKET")
        .Bind(EditorActions.Escape, "ESCAPE");

    public static Keymap VisualStudio() => new Keymap("Visual Studio")
        .Bind(EditorActions.Left, "LEFT")
        .Bind(EditorActions.Right, "RIGHT")
        .Bind(EditorActions.Up, "UP")
        .Bind(EditorActions.Down, "DOWN")
        .Bind(EditorActions.WordLeft, "control LEFT")
        .Bind(EditorActions.WordRight, "control RIGHT")
        .Bind(EditorActions.LineStart, "HOME")
        .Bind(EditorActions.LineEnd, "END")
        .Bind(EditorActions.TextStart, "control HOME")
        .Bind(EditorActions.TextEnd, "control END")
        .Bind(EditorActions.PageUp, "PAGE_UP")
        .Bind(EditorActions.PageDown, "PAGE_DOWN")
        .Bind(EditorActions.ScrollUp, "control UP")
        .Bind(EditorActions.ScrollDown, "control DOWN")
        .Bind(EditorActions.Backspace, "BACK_SPACE")
        .Bind(EditorActions.Delete, "DELETE")
        .Bind(EditorActions.DeleteWordLeft, "control BACK_SPACE")
        .Bind(EditorActions.DeleteWordRight, "control DELETE")
        .Bind(EditorActions.NewLine, "ENTER")
        .Bind(EditorActions.StartNewLine, "shift ENTER")
        .Bind(EditorActions.StartNewLineBefore, "control ENTER")
        .Bind(EditorActions.Indent, "TAB")
        .Bind(EditorActions.Outdent, "shift TAB")
        .Bind(EditorActions.Duplicate, "control D")
        .Bind(EditorActions.DeleteLine, "control L", "control shift L")
        .Bind(EditorActions.MoveLineUp, "alt UP")
        .Bind(EditorActions.MoveLineDown, "alt DOWN")
        .Bind(EditorActions.ToggleLineComment, "control SLASH", "control DIVIDE", "control alt SLASH", "control alt DIVIDE", "control K , control SLASH")
        .Bind(EditorActions.ToggleBlockComment, "control shift SLASH", "control shift DIVIDE")
        .Bind(EditorActions.Comment, "control K , C", "control K , control C")
        .Bind(EditorActions.Uncomment, "control K , U", "control K , control U")
        .Bind(EditorActions.JoinLines, "control shift J")
        .Bind(EditorActions.ToggleCase, "control shift U")
        .Bind(EditorActions.Undo, "control Z", "alt BACK_SPACE")
        .Bind(EditorActions.Redo, "control Y", "control shift Z")
        .Bind(EditorActions.Cut, "control X", "shift DELETE")
        .Bind(EditorActions.Copy, "control C", "control INSERT")
        .Bind(EditorActions.Paste, "control V", "shift INSERT")
        .Bind(EditorActions.SelectAll, "control A")
        .Bind(EditorActions.SelectNextOccurrence, "alt shift PERIOD")
        .Bind(EditorActions.UnselectOccurrence, "alt shift COMMA")
        .Bind(EditorActions.SelectAllOccurrences, "alt shift SEMICOLON")
        .Bind(EditorActions.CaretPerLine, "shift alt G")
        .Bind(EditorActions.CloneCaretAbove, "alt shift UP")
        .Bind(EditorActions.CloneCaretBelow, "alt shift DOWN")
        .Bind(EditorActions.ToggleFold, "control M , M", "control M , control M")
        .Bind(EditorActions.Fold, "control M , S", "control M , control S")
        .Bind(EditorActions.Unfold, "control M , E", "control M , control E")
        .Bind(EditorActions.FoldAll, "control M , A", "control M , control A")
        .Bind(EditorActions.UnfoldAll, "control M , X", "control M , control X")
        .Bind(EditorActions.FoldToDefinitions, "control M , O", "control M , control O")
        .Bind(EditorActions.MatchBrace, "control CLOSE_BRACKET")
        .Bind(EditorActions.Escape, "ESCAPE");

    // Rider's VSCode OSX keymap with what it takes from Mac OS X 10.5+ (Ctrl+B, F, P, N, A and E move as macOS's text does).
    // Its Comment with Line Comment's Cmd+K chords are VS Code's Add and Remove Line Comment, Comment and Uncomment here;
    // its Duplicate is EditorDuplicateLines, VS Code's Copy Line Down.
    public static Keymap VSCodeMac() => new Keymap(NameOf(KeymapPreset.VSCode))
        .Bind(EditorActions.Left, "LEFT", "control B")
        .Bind(EditorActions.Right, "RIGHT", "control F")
        .Bind(EditorActions.Up, "UP", "control P")
        .Bind(EditorActions.Down, "DOWN", "control N")
        .Bind(EditorActions.WordLeft, "alt LEFT")
        .Bind(EditorActions.WordRight, "alt RIGHT")
        .Bind(EditorActions.LineStart, "meta LEFT", "control A", "HOME")
        .Bind(EditorActions.LineEnd, "meta RIGHT", "control E", "END")
        .Bind(EditorActions.TextStart, "meta UP")
        .Bind(EditorActions.TextEnd, "meta DOWN")
        .Bind(EditorActions.PageUp, "PAGE_UP")
        .Bind(EditorActions.PageDown, "PAGE_DOWN")
        .Bind(EditorActions.ScrollUp, "control PAGE_UP")
        .Bind(EditorActions.ScrollDown, "control PAGE_DOWN")
        .Bind(EditorActions.Backspace, "BACK_SPACE", "shift BACK_SPACE")
        .Bind(EditorActions.Delete, "DELETE")
        .Bind(EditorActions.DeleteWordLeft, "alt BACK_SPACE")
        .Bind(EditorActions.DeleteWordRight, "alt DELETE")
        .Bind(EditorActions.NewLine, "ENTER")
        .Bind(EditorActions.StartNewLine, "meta ENTER")
        .Bind(EditorActions.StartNewLineBefore, "meta shift ENTER")
        .Bind(EditorActions.Indent, "TAB")
        .Bind(EditorActions.Outdent, "meta OPEN_BRACKET", "shift TAB")
        .Bind(EditorActions.Duplicate, "alt shift DOWN")
        .Bind(EditorActions.DeleteLine, "meta shift K")
        .Bind(EditorActions.MoveLineUp, "alt UP")
        .Bind(EditorActions.MoveLineDown, "alt DOWN")
        .Bind(EditorActions.ToggleLineComment, "meta SLASH")
        .Bind(EditorActions.ToggleBlockComment, "alt shift A")
        .Bind(EditorActions.Comment, "meta K , meta C")
        .Bind(EditorActions.Uncomment, "meta K , meta U")
        .Bind(EditorActions.JoinLines, "control J")
        .Bind(EditorActions.Undo, "meta Z")
        .Bind(EditorActions.Redo, "meta shift Z", "alt shift BACK_SPACE")
        .Bind(EditorActions.Cut, "meta X", "shift DELETE")
        .Bind(EditorActions.Copy, "meta C", "meta INSERT")
        .Bind(EditorActions.Paste, "meta V", "shift INSERT")
        .Bind(EditorActions.SelectAll, "meta A")
        .Bind(EditorActions.SelectNextOccurrence, "meta D", "meta F3")
        .Bind(EditorActions.UnselectOccurrence, "meta U")
        .Bind(EditorActions.SelectAllOccurrences, "meta F2", "meta control G", "meta shift L")
        .Bind(EditorActions.CaretPerLine, "alt shift I")
        .Bind(EditorActions.CloneCaretAbove, "meta alt shift UP", "meta alt UP")
        .Bind(EditorActions.CloneCaretBelow, "meta alt shift DOWN", "meta alt DOWN")
        .Bind(EditorActions.Fold, "meta alt OPEN_BRACKET")
        .Bind(EditorActions.Unfold, "meta alt CLOSE_BRACKET")
        .Bind(EditorActions.FoldAll, "meta K , meta 0")
        .Bind(EditorActions.UnfoldAll, "meta K , meta J")
        .Bind(EditorActions.FoldDocComments, "meta K , meta SLASH")
        .Bind(EditorActions.MatchBrace, "meta shift BACK_SLASH")
        .Bind(EditorActions.Escape, "ESCAPE");

    // Rider's VSCode keymap with what it takes from $default, Comment, Uncomment and Duplicate as on macOS.
    public static Keymap VSCode() => new Keymap(NameOf(KeymapPreset.VSCode))
        .Bind(EditorActions.Left, "LEFT")
        .Bind(EditorActions.Right, "RIGHT")
        .Bind(EditorActions.Up, "UP")
        .Bind(EditorActions.Down, "DOWN")
        .Bind(EditorActions.WordLeft, "control LEFT")
        .Bind(EditorActions.WordRight, "control RIGHT")
        .Bind(EditorActions.LineStart, "HOME")
        .Bind(EditorActions.LineEnd, "END")
        .Bind(EditorActions.TextStart, "control HOME")
        .Bind(EditorActions.TextEnd, "control END")
        .Bind(EditorActions.PageUp, "PAGE_UP")
        .Bind(EditorActions.PageDown, "PAGE_DOWN")
        .Bind(EditorActions.Backspace, "BACK_SPACE", "shift BACK_SPACE")
        .Bind(EditorActions.Delete, "DELETE")
        .Bind(EditorActions.DeleteWordLeft, "control BACK_SPACE")
        .Bind(EditorActions.DeleteWordRight, "control DELETE")
        .Bind(EditorActions.NewLine, "ENTER")
        .Bind(EditorActions.StartNewLine, "control ENTER")
        .Bind(EditorActions.StartNewLineBefore, "control shift ENTER")
        .Bind(EditorActions.Indent, "TAB")
        .Bind(EditorActions.Outdent, "control OPEN_BRACKET", "shift TAB")
        .Bind(EditorActions.Duplicate, "alt shift DOWN")
        .Bind(EditorActions.DeleteLine, "control shift K")
        .Bind(EditorActions.MoveLineUp, "alt UP")
        .Bind(EditorActions.MoveLineDown, "alt DOWN")
        .Bind(EditorActions.ToggleLineComment, "control SLASH")
        .Bind(EditorActions.ToggleBlockComment, "alt shift A")
        .Bind(EditorActions.Comment, "control K , control C")
        .Bind(EditorActions.Uncomment, "control K , control U")
        .Bind(EditorActions.JoinLines, "control shift J")
        .Bind(EditorActions.Undo, "control Z", "alt BACK_SPACE")
        .Bind(EditorActions.Redo, "control Y", "control shift Z", "alt shift BACK_SPACE")
        .Bind(EditorActions.Cut, "control X", "shift DELETE")
        .Bind(EditorActions.Copy, "control C", "control INSERT")
        .Bind(EditorActions.Paste, "control V", "shift INSERT")
        .Bind(EditorActions.SelectAll, "control A")
        .Bind(EditorActions.SelectNextOccurrence, "control D", "control F3")
        .Bind(EditorActions.UnselectOccurrence, "control U")
        .Bind(EditorActions.SelectAllOccurrences, "control shift L", "control F2")
        .Bind(EditorActions.CaretPerLine, "alt shift I")
        .Bind(EditorActions.CloneCaretAbove, "control alt UP", "control alt shift UP")
        .Bind(EditorActions.CloneCaretBelow, "control alt DOWN", "control alt shift DOWN")
        .Bind(EditorActions.Fold, "control shift OPEN_BRACKET")
        .Bind(EditorActions.Unfold, "control shift CLOSE_BRACKET")
        .Bind(EditorActions.FoldAll, "control K , control 0")
        .Bind(EditorActions.UnfoldAll, "control K , control J")
        .Bind(EditorActions.FoldDocComments, "control K , control SLASH")
        .Bind(EditorActions.MatchBrace, "control shift BACK_SLASH")
        .Bind(EditorActions.Escape, "ESCAPE");
}
