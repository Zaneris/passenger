using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A file the editor shows read-only (here decompiled code, as Go to Declaration opens it) is never edited or written,
/// whatever asks: Save greys there, as on a file with nothing to save, Replace in Files names it as not replaced, and the
/// Edit menu's changes, the find bar's replace row and the colour picker grey saying why; what the disk holds still reaches
/// its tab.</summary>
public sealed class ReadOnlyFilesTests : IDisposable
{
    private const string Trims = "root = true\n[*]\ntrim_trailing_whitespace = true\ninsert_final_newline = true\n";

    private readonly string folder = TestFolders.Create("read-only-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_decompiled_tabs_save_is_greyed_and_leaves_its_bytes_and_time_as_they_were()
    {
        string decompiled = Write("MetadataAsSource/A.cs", "class A   \n{\n}");
        DateTime written = File.GetLastWriteTimeUtc(decompiled);
        MainWindow window = Open("MetadataAsSource/A.cs");
        OpenDocument shown = window.EditorArea.Active!;

        window.KeyTextInput("x");
        Ui.Settle();
        Assert.False(shown.IsModified, "Typing changed a read-only file's text");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal("A.cs is read-only", window.Status);
        Assert.Equal("A.cs is read-only", window.Actions.WhyNot(AppActions.Save, window.Actions.Context()));
        Assert.True(File.ReadAllText(decompiled) == "class A   \n{\n}", "Save rewrote a read-only file with its .editorconfig's trims");
        Assert.True(File.GetLastWriteTimeUtc(decompiled) == written, "Save wrote a read-only file, so its time changed");

        // The disk's text still reaches the tab, as the language server writes the file again.
        File.WriteAllText(decompiled, "class A\n{\n    int b;\n}\n");
        Ui.WaitFor(() => shown.Text.Snapshot.ToString() == "class A\n{\n    int b;\n}\n", () => $"the read-only tab to take the disk's text; it shows '{shown.Text.Snapshot}'");
        Assert.False(shown.IsModified, "The disk's text taken left the read-only file modified");
        window.Close();
    }

    [AvaloniaFact]
    public void An_unmodified_files_save_is_greyed_and_writes_nothing()
    {
        string notes = Write("Notes.txt", "notes   \nmore");
        DateTime written = File.GetLastWriteTimeUtc(notes);
        MainWindow window = Open("Notes.txt");

        Assert.Equal("Notes.txt is saved", window.Actions.WhyNot(AppActions.Save, window.Actions.Context()));
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.True(File.ReadAllText(notes) == "notes   \nmore", "Save of an unmodified file wrote its .editorconfig's final line break");
        Assert.True(File.GetLastWriteTimeUtc(notes) == written, "Save of an unmodified file wrote it, so the next build would run again");

        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single("notes   \nmore".Length);
        window.KeyTextInput("!");
        Ui.Settle();
        Assert.Null(window.Actions.WhyNot(AppActions.Save, window.Actions.Context()));
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal("notes\nmore!\n", File.ReadAllText(notes));
        window.Close();
    }

    [AvaloniaFact]
    public void Replace_in_files_over_the_open_files_names_a_read_only_one_as_not_replaced_and_leaves_it()
    {
        string decompiled = Write("MetadataAsSource/A.cs", "class A\n{\n    // class B\n}\n");
        Write("Notes.cs", "// class notes\n");
        MainWindow window = Open("MetadataAsSource/A.cs");
        window.OpenFile(Path.Combine(folder, "Notes.cs"));
        Ui.Settle();

        Task<string> replacing = new Passenger.App.Mcp.PassengerTools(window).ReplaceInFiles("class", "klass", scope: "openFiles");
        string answer = Ui.Await(replacing, "replace_in_files' answer");
        Assert.Equal($"Replaced 1 match in 1 file: Notes.cs; not replaced: {Path.Combine("MetadataAsSource", "A.cs")} (read-only)", answer);
        Assert.Equal("class A\n{\n    // class B\n}\n", File.ReadAllText(decompiled));
        Assert.Equal("class A\n{\n    // class B\n}\n", window.EditorArea.Find(decompiled)!.Text.Snapshot.ToString());
        Assert.Equal("// klass notes\n", File.ReadAllText(Path.Combine(folder, "Notes.cs")));
        window.Close();
    }

    [AvaloniaFact]
    public void The_find_bars_replace_row_is_greyed_in_a_read_only_tab_saying_why()
    {
        Write("MetadataAsSource/A.cs", "class A\n{\n    // class B\n}\n");
        MainWindow window = Open("MetadataAsSource/A.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;

        Ui.Press(window, Ui.Shortcut(AppActions.Replace));
        window.KeyTextInput("class");
        Ui.Settle();
        FindBar bar = window.EditorArea.FindBar;
        Assert.Equal("1/2", bar.CountText);
        Assert.False(bar.FindControl<Button>("ReplaceAllButton")!.IsEnabled, "Replace All is offered in a read-only tab");
        Assert.False(bar.FindControl<Button>("ReplaceButton")!.IsEnabled, "Replace is offered in a read-only tab");
        Assert.False(bar.FindControl<TextBox>("ReplaceWith")!.IsEnabled, "The replacement can be typed in a read-only tab");
        Assert.Equal("A.cs is read-only", ToolTip.GetTip(bar.FindControl<Control>("ReplaceRow")!));
        Assert.Equal("class A\n{\n    // class B\n}\n", editor.Document.Snapshot.ToString());
        window.Close();
    }

    /// <summary>On a decompiled file, every action of the Edit menu that would change it greys saying it is read-only, as
    /// every other editor action changing text does, and those that only copy, choose or find stay enabled, Replace among
    /// them (its replace row says why); the keys of those that change it say so in the status bar and change nothing. A
    /// text box with the keys over it takes its own Cut, Paste, Undo and Redo.</summary>
    [AvaloniaFact]
    public void Every_action_of_the_edit_menu_that_would_change_a_decompiled_file_greys_saying_it_is_read_only()
    {
        const string Text = "class A\n{\n    int b;\n    int c;\n}\n";
        Write("MetadataAsSource/A.cs", Text);
        MainWindow window = Open("MetadataAsSource/A.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        const string ReadOnly = "A.cs is read-only";
        HashSet<string> changing = [EditorActions.Backspace, EditorActions.Delete, EditorActions.DeleteWordLeft, EditorActions.DeleteWordRight,
            EditorActions.NewLine, EditorActions.StartNewLine, EditorActions.StartNewLineBefore, EditorActions.Indent, EditorActions.Outdent,
            EditorActions.Duplicate, EditorActions.DeleteLine, EditorActions.MoveLineUp, EditorActions.MoveLineDown, EditorActions.ToggleLineComment,
            EditorActions.ToggleBlockComment, EditorActions.Comment, EditorActions.Uncomment, EditorActions.JoinLines, EditorActions.ToggleCase,
            EditorActions.CutToLineEnd, EditorActions.Undo, EditorActions.Redo, EditorActions.Cut, EditorActions.Paste];
        List<string> edit = [.. ActionsIn(MainMenu.Build(window.Actions.All).Single(m => m.Title == "Edit"))];
        string[] named = [EditorActions.Cut, EditorActions.Paste, EditorActions.Duplicate, EditorActions.DeleteLine, EditorActions.MoveLineUp, EditorActions.MoveLineDown];
        Assert.True(named.All(edit.Contains), $"The Edit menu lacks some of {string.Join(", ", named)}: {string.Join(", ", edit)}");

        ActionContext context = window.Actions.Context();
        List<string> offered = [];
        List<string> greyed = [];
        foreach (string id in edit.Concat(EditorActions.All.Select(a => a.Id)).Distinct())
        {
            string? why = window.Actions.WhyNot(id, context);
            if (changing.Contains(id) ? why != ReadOnly : why is not null && !(id == AppActions.HighlightUsages && why == Refusals.NoCSharp))
            {
                (changing.Contains(id) ? offered : greyed).Add($"{id} ({why ?? "enabled"})");
            }
        }
        Assert.True(offered.Count == 0, $"On the decompiled A.cs, which nothing can change, these are offered or greyed for another reason: {string.Join(", ", offered)}");
        Assert.True(greyed.Count == 0, $"On the decompiled A.cs, these only read it but are greyed: {string.Join(", ", greyed)}");

        editor.Selections = Passenger.Text.SelectionSet.Single(new Passenger.Text.Selection(Text.IndexOf("int b", StringComparison.Ordinal), Text.IndexOf("int c", StringComparison.Ordinal)));
        Assert.NotEqual(ReadOnly, window.Status);
        Ui.Run(window, EditorActions.Duplicate);
        Assert.Equal(ReadOnly, window.Status);
        foreach (string id in new[] { EditorActions.Cut, EditorActions.Paste, EditorActions.Backspace, EditorActions.NewLine, EditorActions.MoveLineUp, EditorActions.Undo })
        {
            Ui.Run(window, id);
            Assert.True(editor.Document.Snapshot.ToString() == Text, $"{id}'s key changed the decompiled A.cs: {editor.Document.Snapshot}");
        }

        Ui.Press(window, Ui.Shortcut(AppActions.Find));
        context = window.Actions.Context();
        Assert.IsAssignableFrom<TextBox>(context.Focused);
        Assert.True(window.Actions.WhyNot(EditorActions.Paste, context) is null && window.Actions.WhyNot(EditorActions.Cut, context) is null,
            "Paste or Cut greyed in the find bar's field over a read-only file, which takes them itself");
        Assert.Equal(ReadOnly, window.Actions.WhyNot(EditorActions.Duplicate, context));
        window.Close();

        static IEnumerable<string> ActionsIn(MenuEntry menu) => menu.Action is { } id ? [id] : (menu.Items ?? []).SelectMany(ActionsIn);
    }

    [AvaloniaFact]
    public void The_colour_pickers_choose_is_greyed_in_a_read_only_tab_and_enter_writes_nothing()
    {
        const string Palette = "class Palette\n{\n    static object Red = Avalonia.Media.Color.Parse(\"#FF0000\");\n}\n";
        string decompiled = Write("MetadataAsSource/Palette.cs", Palette);
        MainWindow window = Open("MetadataAsSource/Palette.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument shown = window.EditorArea.Active!;
        Ui.WaitFor(() => shown.ColourMarks.Any(m => m.Line == 2), "the swatch of the colour Color.Parse makes");

        Ui.Click(editor, editor.GutterColumnPoint(editor.Gutter!.Marks, 2));
        Assert.Equal("#FF0000", window.PickingColour);
        Button choose = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Choose"));
        Assert.False(choose.IsEnabled, "The colour picker offers Choose over a read-only file's colour");
        Assert.Equal("Palette.cs is read-only", ToolTip.GetTip(choose));
        window.KeyTextInput("#00FF00");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.Equal("#00FF00", window.PickingColour);
        Assert.Equal(Palette, editor.Document.Snapshot.ToString());
        Assert.Equal(Palette, File.ReadAllText(decompiled));
        Assert.DoesNotContain("Colour set", window.Status ?? string.Empty, StringComparison.Ordinal);
        Ui.Press(window, "ESCAPE");
        Assert.Null(window.PickingColour);
        window.Close();
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.WriteAllText(Path.Combine(folder, ".editorconfig"), Trims);
        return PathNames.Canonical(path);
    }

    /// <summary>The folder open with no C# language server, which nothing here asks of, a file shown with the keys in it.</summary>
    private MainWindow Open(string file)
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, file));
        window.EditorArea.CodeEditor.Focus();
        Ui.Settle();
        return window;
    }
}
