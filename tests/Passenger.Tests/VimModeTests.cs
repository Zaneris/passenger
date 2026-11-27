using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Vim mode through the window, keys pressed as a keyboard presses them (the key with its character, then the
/// character as text unless the key was taken): what only the window can show, as what each key does is held to Vim
/// itself by VimTests. The setting turns it on and off live and the status bar says the mode, an insert session is one step
/// of the window's undo, + is the system clipboard, a read-only file refuses every change, ex commands save and run in
/// every file as one command of undo, the mappings file maps keys, and the command line takes an input method's text.</summary>
public sealed class VimModeTests : IDisposable
{
    private readonly string folder = TestFolders.Create("vim-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Vim_mode_takes_its_keys_in_the_window_and_its_changes_are_the_windows_own() => Cases.All(
        ("normal mode's keys move and delete, only insert mode types, and the status bar says the mode", Vim_mode_takes_normal_modes_keys_types_only_in_insert_mode_and_says_its_mode),
        ("an insert session is one step of undo, and undo leaves the caret on a character", An_insert_session_is_one_step_of_undo_and_undo_leaves_the_caret_on_a_character),
        ("registers keep what was yanked, and + is the system clipboard", Registers_keep_what_was_yanked_and_plus_is_the_system_clipboard),
        ("a read-only file refuses Vim's every change, saying why", A_read_only_file_refuses_vims_every_change_saying_why));

    [AvaloniaFact]
    public void Ex_commands_save_and_change_every_file_as_one_command_of_undo() => Cases.All(
        (":s is one step of undo, :m and J carry marks, :w saves, :e opens and :q closes", Ex_commands_substitute_as_one_step_save_open_and_close),
        (":bufdo and :windo change every file as one command of undo", Bufdo_and_windo_change_every_file_as_one_command_of_undo));

    /// <summary>The mappings file in the state folder is read as Vim mode comes on and again as it changes: its mappings
    /// take keys in their modes, the leader it sets among them, a line it cannot read said in the status bar and passed over.</summary>
    [AvaloniaFact]
    public void The_mappings_file_maps_keys_and_is_read_again_as_it_changes()
    {
        string state = Path.Combine(folder, "state");
        Directory.CreateDirectory(state);
        string vimrc = Path.Combine(state, "vimrc");
        File.WriteAllText(vimrc, "let mapleader = \",\"\nnnoremap <leader>d dd\ninoremap jk <Esc>\n");
        (MainWindow window, CodeEditor editor) = Open("one\ntwo\nthree\n", state: state);
        try
        {
            Assert.Equal(PathNames.Canonical(vimrc), PathNames.Canonical(window.VimMappingsPath));
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.WaitFor(() => window.VimMappings.All.Count == 2, "the mappings file read as Vim mode came on");
            Keys(window, ",d", together: true);
            Assert.Equal("two\nthree\n", Text(editor));
            Keys(window, "ix");
            Keys(window, "jk", together: true);
            Assert.Equal(("NORMAL", "1:1"), Status(window));
            Assert.Equal("xtwo\nthree\n", Text(editor));

            File.WriteAllText(vimrc, "nnoremap Q dd\nnmap\n");
            Ui.WaitFor(() => window.VimMappings.All.Count == 1, "the mappings file read again as it changed");
            Assert.Equal("Vim mode passed over 1 line of vimrc, line 2: a mapping names the keys it takes", StatusWords(window));
            Keys(window, "Q");
            Assert.Equal("three\n", Text(editor));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>While the command line is open an input method composes on it: what it composes shows at the line's end
    /// in the status bar, its window opening there, never in the text, and a key it takes runs nothing; what it commits,
    /// a dead key's character that comes with no key of its own, and press and hold's accent over the letter held are
    /// typed on the line, the text left as it was until the command runs; closed, the line leaves the keys to Vim's
    /// commands again.</summary>
    [AvaloniaFact]
    public void The_command_line_takes_what_an_input_method_composes_and_commits_and_leaves_the_text_alone()
    {
        (MainWindow window, CodeEditor editor) = Open("alpha 日本 café\nsecond line\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Avalonia.Input.TextInput.TextInputMethodClientRequestedEventArgs asked = new() { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.RaiseEvent(asked);
            Avalonia.Input.TextInput.TextInputMethodClient client = asked.Client ?? throw new InvalidOperationException("The editor gave the input method no client");
            Keys(window, "/");
            Assert.True(InputMethod.GetIsInputMethodEnabled(editor), "No input method composes on the command line");
            double after = editor.PointOf(1).X;
            client.SetPreeditText("にほん");
            Ui.Settle();
            Assert.Equal("/にほん", StatusWords(window));
            Assert.True(editor.PointOf(1).X == after, "The composition was drawn in the text at the caret, moving what follows it aside");
            TextBlock words = window.FindControl<TextBlock>("StatusText")!;
            Point end = words.TranslatePoint(new Point(words.TextLayout.WidthIncludingTrailingWhitespace, 0), editor)!.Value;
            Point told = client.CursorRectangle.TopLeft;
            Assert.True(Math.Abs(told.X - end.X) < 0.5 && Math.Abs(told.Y - end.Y) < 0.5,
                $"The input method's window would open at {told}, not at the command line's end in the status bar, {end}");

            window.KeyPress(Avalonia.Input.Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Avalonia.Input.Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.Enter, null);
            Ui.Settle();
            Assert.True(StatusWords(window) == "/にほん" && editor.Selections.Primary.Caret == 0,
                $"The Enter the input method took ran the search, the status bar saying \"{StatusWords(window)}\" and the caret at {editor.Selections.Primary.Caret}");
            client.SetPreeditText(null);
            window.KeyTextInput("日本");
            Ui.Settle();
            Assert.Equal("/日本", StatusWords(window));
            Assert.Equal("alpha 日本 café\nsecond line\n", Text(editor));
            Assert.True(client.CursorRectangle.X > told.X - (words.TextLayout.WidthIncludingTrailingWhitespace / 2), "The input method's window stayed where the composition began, not at the line's end");
            Keys(window, "<CR>");
            Assert.Equal(6, editor.Selections.Primary.Caret);
            Assert.False(InputMethod.GetIsInputMethodEnabled(editor), "An input method still composes once the command line closed");

            Keys(window, ":s/caf");
            window.KeyTextInput("é");
            Ui.Settle();
            Assert.Equal(":s/café", StatusWords(window));
            Keys(window, "/cafe");
            Avalonia.Input.TextInput.TextSelection held = client.Selection;
            client.Selection = new Avalonia.Input.TextInput.TextSelection(held.End - 1, held.End);
            window.KeyTextInput("è");
            Ui.Settle();
            Assert.Equal(":s/café/cafè", StatusWords(window));
            Assert.Equal("alpha 日本 café\nsecond line\n", Text(editor));
            Keys(window, "<CR>");
            Assert.Equal("alpha 日本 cafè\nsecond line\n", Text(editor));
            Keys(window, "x");
            Assert.Equal("lpha 日本 cafè\nsecond line\n", Text(editor));
        }
        finally
        {
            window.Close();
        }
    }

    private void Vim_mode_takes_normal_modes_keys_types_only_in_insert_mode_and_says_its_mode()
    {
        (MainWindow window, CodeEditor editor) = Open("one two\nthree\nfour\n");
        try
        {
            Assert.False(window.FindControl<TextBlock>("VimModeText")!.IsVisible, "The status bar shows a Vim mode with Vim mode off");
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Assert.Equal(("NORMAL", "1:1"), Status(window));

            Key(window, "W", "w");
            Key(window, "X", "x");
            Key(window, "X", "x");
            Assert.Equal("one o\nthree\nfour\n", Text(editor));
            Key(window, "U", "u");
            Ui.WaitFor(() => Text(editor) == "one wo\nthree\nfour\n", () => $"u left \"{Text(editor)}\", not the text before the last x alone");

            Key(window, "2", "2");
            // The Shift of Shift+G goes down on its own first, as a person presses it.
            window.KeyPress(Avalonia.Input.Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.LeftShift, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.Settle();
            Assert.Equal("NORMAL 2", Status(window).Mode);
            Key(window, "shift G", "G");
            Assert.Equal(("NORMAL", "2:1"), Status(window));

            Key(window, "shift Q", "Q");
            Ui.Press(window, "ENTER");
            Ui.Press(window, "TAB");
            Key(window, "K", "k");
            Assert.True(Text(editor) == "one wo\nthree\nfour\n", "normal mode's Q, Enter or Tab typed into the text");
            Assert.Equal(("NORMAL", "2:1"), Status(window));

            Key(window, "I", "i");
            Assert.Equal(("INSERT", "2:1"), Status(window));
            Key(window, "shift Z", "Z");
            Key(window, "Z", "z");
            Ui.Press(window, "ESCAPE");
            Assert.Equal("one wo\nZzthree\nfour\n", Text(editor));
            Assert.Equal(("NORMAL", "2:2"), Status(window));

            window.ApplySettings(window.Settings with { VimMode = false });
            Ui.Settle();
            Assert.False(window.FindControl<TextBlock>("VimModeText")!.IsVisible, "The status bar still shows a Vim mode once Vim mode is off");
            Assert.Equal("2:2", Status(window).Caret);
            Key(window, "Q", "q");
            Assert.True(Text(editor) == "one wo\nZqzthree\nfour\n", "with Vim mode turned off, typing typed nothing");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What insert mode types, Enter and Backspace included, is one step of undo however long it took, from the
    /// key that began it (o opening its line too) to Escape; the caret undo puts back stands on a character.</summary>
    private void An_insert_session_is_one_step_of_undo_and_undo_leaves_the_caret_on_a_character()
    {
        (MainWindow window, CodeEditor editor) = Open("alpha\nomega\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Key(window, "O", "o");
            foreach (char typed in "one two")
            {
                Key(window, typed == ' ' ? "SPACE" : char.ToUpperInvariant(typed).ToString(), typed.ToString());
            }
            Ui.Press(window, "ENTER");
            Key(window, "T", "t");
            Key(window, "X", "x");
            Ui.Press(window, "BACK_SPACE");
            Key(window, "O", "o");
            Ui.Press(window, "ESCAPE");
            Assert.Equal("alpha\none two\nto\nomega\n", Text(editor));
            Key(window, "U", "u");
            Ui.WaitFor(() => Text(editor) == "alpha\nomega\n", () => $"u took back only part of the insert session, leaving \"{Text(editor)}\"");
            Key(window, "shift A", "A");
            Key(window, "S", "s");
            Ui.Press(window, "ESCAPE");
            Key(window, "U", "u");
            Ui.WaitFor(() => Text(editor) == "alpha\nomega\n", () => $"u left \"{Text(editor)}\"");
            Assert.Equal(("NORMAL", "1:5"), Status(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A named register yanked into and appended to, then put; the black hole register keeping nothing; and + reaching
    /// the system clipboard both ways.</summary>
    private void Registers_keep_what_was_yanked_and_plus_is_the_system_clipboard()
    {
        (MainWindow window, CodeEditor editor) = Open("one\ntwo\nthree\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, "\"ayyj\"Ayy\"_ddG\"ap");
            Assert.Equal("one\nthree\none\ntwo\n", Text(editor));
            Keys(window, "gg\"+yiw");
            Ui.WaitFor(() => Ui.Await(window.Clipboard!.TryGetTextAsync(), "the clipboard's text") == "one", () => "\"+yiw put nothing on the system clipboard");
            Ui.Await(window.Clipboard!.SetTextAsync("pasted\n"), "the clipboard written");
            Keys(window, "\"+P");
            Ui.WaitFor(() => Text(editor) == "pasted\none\nthree\none\ntwo\n", () => $"\"+P put \"{Text(editor)}\", not the clipboard's line above");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file shown read-only refuses Vim mode's every change as it refuses every other, the status bar saying
    /// so in Refusals' words: x, dd, an insert, u and :s leave its text as it was and normal mode as it was, while a motion
    /// and a yank work, saying nothing, the yank put in a file that takes changes.</summary>
    private void A_read_only_file_refuses_vims_every_change_saying_why()
    {
        string decompiled = Path.Combine(folder, "MetadataAsSource");
        Directory.CreateDirectory(decompiled);
        string other = Path.Combine(folder, "Two.txt");
        File.WriteAllText(other, "x\n");
        (MainWindow window, CodeEditor editor) = Open("one two\nthree\n", into: decompiled);
        try
        {
            Assert.True(window.EditorArea.Active!.IsReadOnly, "A file under MetadataAsSource was not shown read-only");
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            foreach (string keys in new[] { "x", "dd", "i", "u", ":s/one/1/<CR>" })
            {
                Keys(window, ":set ic?<CR>");
                Assert.Equal("noignorecase", StatusWords(window));
                Keys(window, keys);
                Assert.True(Text(editor) == "one two\nthree\n" && Status(window).Mode == "NORMAL",
                    $"{keys} on a read-only file left \"{Text(editor)}\" in {Status(window).Mode}");
                Assert.Equal("One.txt is read-only", StatusWords(window));
            }
            Keys(window, ":set ic?<CR>");
            Keys(window, "wyw");
            Assert.Equal(("NORMAL", "1:5"), Status(window));
            Assert.True(StatusWords(window) == "noignorecase", $"A motion and a yank in a read-only file said \"{StatusWords(window)}\", their keys' text taken for typing");
            window.OpenFile(other);
            Ui.Settle();
            Keys(window, "p");
            Assert.Equal("xtwo\n", Text(window.EditorArea.CodeEditor));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>:s over a range is one step of undo, :m carries a line's mark with it, a mark J joins to the line above
    /// stays on its character there, its indent gone, whatever is taken before it later, : in visual mode names its lines,
    /// :w saves as Save does, :e opens a file and :q closes its tab as Close Tab does.</summary>
    private void Ex_commands_substitute_as_one_step_save_open_and_close()
    {
        string other = Path.Combine(folder, "Other.txt");
        File.WriteAllText(other, "other\n");
        (MainWindow window, CodeEditor editor) = Open("one two\nfoo two\ntwo three\n");
        try
        {
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, ":%s/two/2/g<CR>");
            Assert.Equal("one 2\nfoo 2\n2 three\n", Text(editor));
            Keys(window, "u");
            Ui.WaitFor(() => Text(editor) == "one two\nfoo two\ntwo three\n", () => $"u took back part of :s, leaving \"{Text(editor)}\"");
            Keys(window, "ggma:1m$<CR>gg'a");
            Assert.Equal(("foo two\ntwo three\none two\n", "3:1"), (Text(editor), Status(window).Caret));
            Keys(window, "ugg");
            Ui.WaitFor(() => Text(editor) == "one two\nfoo two\ntwo three\n", () => $"u took back :m, leaving \"{Text(editor)}\"");
            Keys(window, "j>>wmakJ0x`a");
            Assert.Equal(("ne two foo two\ntwo three\n", "1:13"), (Text(editor), Status(window).Caret));
            Keys(window, "uuugg");
            Ui.WaitFor(() => Text(editor) == "one two\nfoo two\ntwo three\n", () => $"u took back >>, J and x, leaving \"{Text(editor)}\"");
            Keys(window, "Vj:");
            Assert.Equal((":'<,'>", "NORMAL"), (StatusWords(window), Status(window).Mode));
            Keys(window, "s/o/0/g<CR>");
            Assert.Equal("0ne tw0\nf00 tw0\ntwo three\n", Text(editor));
            Keys(window, ":w<CR>");
            Ui.WaitFor(() => File.ReadAllText(Path.Combine(folder, "One.txt")) == Text(editor), "the file :w saved on disk");
            Keys(window, ":e Other.txt<CR>");
            Ui.WaitFor(() => window.EditorArea.Active?.Path == PathNames.Canonical(other), () => $":e left {window.EditorArea.Active?.Path} shown, not Other.txt");
            Keys(window, ":q<CR>");
            Ui.WaitFor(() => window.EditorArea.Documents.All(d => d.Path != PathNames.Canonical(other)), ":q to close Other.txt's tab");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>:bufdo runs its commands, parted at '|', in each open file in turn as one command of the IDE's undo: :s over
    /// two files and :update saving each, a read-only file passed over and said, the last file left shown, and u there taking
    /// back both; :windo runs its command in each split's file, u taking back both again.</summary>
    private void Bufdo_and_windo_change_every_file_as_one_command_of_undo()
    {
        string decompiled = Path.Combine(folder, "MetadataAsSource");
        Directory.CreateDirectory(decompiled);
        string one = Path.Combine(folder, "One.txt");
        string two = Path.Combine(folder, "Two.txt");
        string three = Path.Combine(decompiled, "Three.txt");
        File.WriteAllText(two, "one more\n");
        File.WriteAllText(three, "one gone\n");
        (MainWindow window, CodeEditor editor) = Open("one two\nthree\n");
        try
        {
            window.OpenFile(three);
            window.OpenFile(two);
            window.OpenFile(one);
            List<string> asked = [];
            window.Confirm = (_, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };
            window.ApplySettings(window.Settings with { VimMode = true });
            Ui.Settle();
            Keys(window, ":bufdo %s/one/1/ge | update<CR>");
            Assert.Equal(("1 two\nthree\n", "1 more\n", "one gone\n"), (Shown(window, one), Shown(window, two), Shown(window, three)));
            Ui.WaitFor(() => File.ReadAllText(one) == "1 two\nthree\n" && File.ReadAllText(two) == "1 more\n", "each file :update saved");
            Assert.Equal(("Two.txt", ":bufdo passed over 1 read-only file: Three.txt"), (window.EditorArea.Active!.Name, StatusWords(window)));
            Keys(window, "u");
            Ui.WaitFor(() => Shown(window, one) == "one two\nthree\n" && Shown(window, two) == "one more\n",
                () => $"u after :bufdo left \"{Shown(window, one)}\" and \"{Shown(window, two)}\", not both files as they were");
            Assert.Equal(["Undo :bufdo %s/one/1/ge | update? It changes One.txt and Two.txt."], asked);

            Assert.True(window.Perform(AppActions.SplitRight));
            window.OpenFile(one);
            Ui.Settle();
            Assert.Equal(["One.txt", "Two.txt"], window.EditorArea.Groups.Select(g => g.Active!.Name));
            Keys(window, ":windo 1s/^/> /<CR>");
            Assert.Equal(("> one two\nthree\n", "> one more\n"), (Shown(window, one), Shown(window, two)));
            Keys(window, "u");
            Ui.WaitFor(() => Shown(window, one) == "one two\nthree\n" && Shown(window, two) == "one more\n",
                () => $"u after :windo left \"{Shown(window, one)}\" and \"{Shown(window, two)}\", not both files as they were");
            Assert.Equal("Undo :windo 1s/^/> /? It changes One.txt and Two.txt.", asked[^1]);
        }
        finally
        {
            window.Close();
        }

        static string Shown(MainWindow window, string path) => window.EditorArea.Find(PathNames.Canonical(path))!.Text.Snapshot.ToString();
    }

    /// <summary>Keys in Vim's notation typed one by one as a keyboard types them: a character with the key that types it,
    /// Escape, Enter and Backspace by name.</summary>
    /// <summary>Keys pressed in turn, each let land unless <paramref name="together"/>, where they come as fast as a person
    /// types a mapping's keys, the last alone let land.</summary>
    private static void Keys(TopLevel window, string keys, bool together = false)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            string? named = keys[i] == '<' ? Array.Find(["<Esc>", "<CR>", "<BS>"], n => string.CompareOrdinal(keys, i, n, 0, n.Length) == 0) : null;
            bool settle = !together || i + (named?.Length ?? 1) >= keys.Length;
            if (named is not null)
            {
                Ui.Press(window, named switch { "<Esc>" => "ESCAPE", "<CR>" => "ENTER", _ => "BACK_SPACE" }, settle);
                i += named.Length - 1;
                continue;
            }
            char c = keys[i];
            string key = c switch
            {
                >= 'a' and <= 'z' => char.ToUpperInvariant(c).ToString(),
                >= 'A' and <= 'Z' => "shift " + c,
                >= '0' and <= '9' => c.ToString(),
                ' ' => "SPACE",
                '.' => "PERIOD",
                ',' => "COMMA",
                ';' => "SEMICOLON",
                '[' => "OPEN_BRACKET",
                ']' => "CLOSE_BRACKET",
                '{' => "shift OPEN_BRACKET",
                '}' => "shift CLOSE_BRACKET",
                '(' => "shift 9",
                ')' => "shift 0",
                '%' => "shift 5",
                '$' => "shift 4",
                '"' => "shift QUOTE",
                '>' => "shift PERIOD",
                '<' => "shift COMMA",
                '~' => "shift BACK_QUOTE",
                '_' => "shift MINUS",
                '-' => "MINUS",
                '=' => "EQUALS",
                '+' => "shift EQUALS",
                '!' => "shift 1",
                '/' => "SLASH",
                '?' => "shift SLASH",
                ':' => "shift SEMICOLON",
                '\'' => "QUOTE",
                '`' => "BACK_QUOTE",
                '&' => "shift 7",
                '@' => "shift 2",
                '|' => "shift BACK_SLASH",
                '^' => "shift 6",
                _ => throw new ArgumentException($"No key types '{c}' here", nameof(keys)),
            };
            Ui.Type(window, key, c.ToString(), settle);
        }
    }

    private (MainWindow Window, CodeEditor Editor) Open(string text, string? into = null, string name = "One.txt", string? state = null)
    {
        string path = Path.Combine(into ?? folder, name);
        File.WriteAllText(path, text);
        MainWindow window = state is null ? new() { Width = 1200, Height = 800 } : new() { Width = 1200, Height = 800, StateFolder = state };
        window.Show();
        window.OpenFolder(into ?? folder);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        return (window, editor);
    }

    /// <summary>A key that types pressed as a keyboard presses it, carrying its character.</summary>
    private static void Key(TopLevel window, string key, string typed) => Ui.Type(window, key, typed);

    private static (string Mode, string? Caret) Status(MainWindow window)
    {
        TextBlock mode = window.FindControl<TextBlock>("VimModeText")!;
        return (mode.IsVisible ? mode.Text ?? string.Empty : string.Empty, window.FindControl<TextBlock>("CaretText")!.Text);
    }

    private static string Text(CodeEditor editor) => editor.Document.Snapshot.ToString();

    /// <summary>What the status bar says on its left, where Vim mode's command line stands.</summary>
    private static string? StatusWords(MainWindow window) => window.FindControl<TextBlock>("StatusText")!.Text;
}
