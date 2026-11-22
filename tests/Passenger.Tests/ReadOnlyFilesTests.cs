using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A file the editor shows read-only (here decompiled code, as Go to Declaration opens it) is never edited or written,
/// whatever asks: Save greys there, as on a file with nothing to save, Replace in Files names it as not replaced, and the
/// Edit menu's changes, the find bar's replace row and the colour picker grey saying why, as what is typed there says it;
/// what the disk holds still reaches its tab. A console's output refuses changes so too. A NuGet package's file, read-only by
/// where it lies, is never written open or not: Replace in Files, an agent's replace and edit, and a rename leave it.</summary>
public sealed class ReadOnlyFilesTests : IDisposable
{
    private const string Trims = "root = true\n[*]\ntrim_trailing_whitespace = true\ninsert_final_newline = true\n";

    /// <summary>The editor actions that change the text, which an editor refusing changes greys.</summary>
    internal static readonly HashSet<string> Changing = [EditorActions.Backspace, EditorActions.Delete, EditorActions.DeleteWordLeft,
        EditorActions.DeleteWordRight, EditorActions.NewLine, EditorActions.StartNewLine, EditorActions.StartNewLineBefore, EditorActions.Indent,
        EditorActions.Outdent, EditorActions.Duplicate, EditorActions.DeleteLine, EditorActions.MoveLineUp, EditorActions.MoveLineDown,
        EditorActions.ToggleLineComment, EditorActions.ToggleBlockComment, EditorActions.Comment, EditorActions.Uncomment, EditorActions.JoinLines,
        EditorActions.ToggleCase, EditorActions.CutToLineEnd, EditorActions.Undo, EditorActions.Redo, EditorActions.Cut, EditorActions.Paste];

    private readonly string folder = TestFolders.Create("read-only-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A file shown read-only refuses every change, saying why: a decompiled tab's Save greys and leaves its
    /// bytes and time, an unmodified file's Save writes nothing, every Edit menu action that would change a decompiled
    /// file or the Run console's output greys, typing and an input method's text say so in the status bar, the Edit
    /// menu greys over a read-only text box while Copy takes its text, and the colour picker's Choose greys
    /// there.</summary>
    [AvaloniaFact]
    public void A_file_shown_read_only_refuses_every_change_saying_why()
    {
        Flow.StepApart<ReadOnlyFilesTests>("a decompiled tab's Save is greyed and leaves its bytes and time as they were", t => t.A_decompiled_tabs_save_is_greyed_and_leaves_its_bytes_and_time_as_they_were());
        Flow.StepApart<ReadOnlyFilesTests>("an unmodified file's Save is greyed and writes nothing", t => t.An_unmodified_files_save_is_greyed_and_writes_nothing());
        Flow.StepApart<ReadOnlyFilesTests>("every action of the Edit menu that would change a decompiled file greys saying it is read only", t => t.Every_action_of_the_edit_menu_that_would_change_a_decompiled_file_greys_saying_it_is_read_only());
        Flow.StepApart<ReadOnlyFilesTests>("typing into a decompiled file says in the status bar it is read only and an input method's text too", t => t.Typing_into_a_decompiled_file_says_in_the_status_bar_it_is_read_only_and_an_input_methods_text_too());
        Flow.StepApart<ReadOnlyFilesTests>("every action of the Edit menu that would change the Run console's output greys saying it is read only its program's input taking its own", t => t.Every_action_of_the_edit_menu_that_would_change_the_Run_consoles_output_greys_saying_it_is_read_only_its_programs_input_taking_its_own());
        Flow.StepApart<ReadOnlyFilesTests>("the Edit menu's changes grey over a read only text box saying so and copy takes its text", t => t.The_edit_menus_changes_grey_over_a_read_only_text_box_saying_so_and_copy_takes_its_text());
        Flow.StepApart<ReadOnlyFilesTests>("the colour picker's Choose is greyed in a read only tab and enter writes nothing", t => t.The_colour_pickers_choose_is_greyed_in_a_read_only_tab_and_enter_writes_nothing());
    }

    /// <summary>A package's files and a read-only file refuse a person's or an agent's change, saying whose they are:
    /// Replace in Files names a read-only open file as not replaced and replaces nothing in a package folder, an
    /// agent's replace and edit leave a package folder in the open folder, a rename reaching a package's code is
    /// refused whole, and an open package file's refusals name the package.</summary>
    [AvaloniaFact]
    public void A_packages_files_refuse_a_replace_a_rename_and_an_agents_edit_saying_whose_they_are()
    {
        Flow.StepApart<ReadOnlyFilesTests>("Replace in Files over the open files names a read only one as not replaced and leaves it", t => t.Replace_in_files_over_the_open_files_names_a_read_only_one_as_not_replaced_and_leaves_it());
        Flow.StepApart<ReadOnlyFilesTests>("Replace in Files over a package folder replaces nothing there saying whose files they are", t => t.Replace_in_files_over_a_package_folder_replaces_nothing_there_saying_whose_files_they_are());
        Flow.StepApart<ReadOnlyFilesTests>("an agent's Replace in Files and edit leave a package folder in the open folder saying whose files they are", t => t.An_agents_replace_in_files_and_edit_leave_a_package_folder_in_the_open_folder_saying_whose_files_they_are());
        Flow.StepApart<ReadOnlyFilesTests>("a rename reaching a package's code is refused whole saying whose file it is", t => t.A_rename_reaching_a_packages_code_is_refused_whole_saying_whose_file_it_is());
        Flow.StepApart<ReadOnlyFilesTests>("an open package file's refusals name the package whether typed into or an agent renames there", t => t.An_open_package_files_refusals_name_the_package_whether_typed_into_or_an_agent_renames_there());
    }

    private void A_decompiled_tabs_save_is_greyed_and_leaves_its_bytes_and_time_as_they_were()
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

    private void An_unmodified_files_save_is_greyed_and_writes_nothing()
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

    private void Replace_in_files_over_the_open_files_names_a_read_only_one_as_not_replaced_and_leaves_it()
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

    /// <summary>Replace All over a directory of a NuGet package folder (an in-repository .nuget/packages, which a directory
    /// scope walks though the folder's index leaves it out) asks nothing and replaces nothing there, saying whose files they
    /// are, every one left as NuGet restored it.</summary>
    private void Replace_in_files_over_a_package_folder_replaces_nothing_there_saying_whose_files_they_are()
    {
        string kit = WriteKitShop();
        MainWindow window = OpenShop(languageServer: false);
        List<string> asked = [];
        window.Confirm = (_, question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        Ui.Press(window, Ui.Shortcut(AppActions.ReplaceInFiles));
        Passenger.App.Navigation.FindInFilesPopup popup = Assert.IsType<Passenger.App.Navigation.FindInFilesPopup>(window.Popup);
        popup.ScopeKind = SearchScopeKind.Directory;
        popup.Directory = Path.Combine(folder, ".nuget", "packages");
        popup.Text = "Count";
        Ui.Settle();
        Ui.WaitFor(() => popup.Searching.IsCompleted, "the search for Count in the package folder");
        Assert.Equal([kit], popup.Matches.Select(m => m.Path).Distinct());
        popup.Replacement = "Sum";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.Contains("replaced", StringComparison.OrdinalIgnoreCase) == true, () => $"Replace All's word in the status bar, which says '{window.Status}'");
        Assert.Equal($"Nothing replaced; not replaced: Kit.cs ({KitsOwn})", window.Status);
        Assert.True(asked.Count == 0, $"Replace All asked to replace in a package's files it leaves: {string.Join("; ", asked)}");
        Assert.True(File.ReadAllText(kit) == Kit, $"Replace in Files wrote a package's file: {File.ReadAllText(kit)}");
        window.Close();
    }

    /// <summary>An agent's replace_in_files over the open folder replaces in its own files and passes over the folder's
    /// .nuget/packages, which its index leaves out; over that directory it replaces nothing, saying whose files they are,
    /// and edit refuses a package's file so too, each left as NuGet restored it.</summary>
    private void An_agents_replace_in_files_and_edit_leave_a_package_folder_in_the_open_folder_saying_whose_files_they_are()
    {
        string kit = WriteKitShop();
        string catalog = Path.Combine(folder, "Shop", "Catalog.cs");
        string shown = Path.GetRelativePath(PathNames.Canonical(folder), kit);
        MainWindow window = OpenShop(languageServer: false);
        Passenger.App.Mcp.PassengerTools agent = new(window);

        Assert.Equal($"Replaced 1 match in 1 file: {Path.Combine("Shop", "Catalog.cs")}",
            Ui.Await(agent.ReplaceInFiles("Count", "Sum", matchCase: true), "replace_in_files' answer over the open folder"));
        Assert.Equal(Catalog.Replace("Count", "Sum", StringComparison.Ordinal), File.ReadAllText(catalog));
        ModelContextProtocol.McpException replaced = Assert.Throws<ModelContextProtocol.McpException>(() => Ui.Await(
            agent.ReplaceInFiles("Count", "Sum", matchCase: true, scope: "directory", directory: Path.Combine(".nuget", "packages")), "replace_in_files' answer over the package folder"));
        Assert.Equal($"Nothing was replaced; not replaced: {shown} ({KitsOwn})", replaced.Message);
        ModelContextProtocol.McpException edited = Assert.Throws<ModelContextProtocol.McpException>(() => Ui.Await(agent.Edit(shown, "Count", "Sum"), "edit's answer on the package's file"));
        Assert.Equal($"{shown} is {KitsOwn}, so nothing changed", edited.Message);
        Assert.True(File.ReadAllText(kit) == Kit, $"An agent's tool wrote a package's file: {File.ReadAllText(kit)}");
        window.Close();
    }

    /// <summary>A rename whose edits reach a NuGet package's contentFiles code, which NuGet's props compile into the project
    /// from the package folder, is refused before anything changes, saying whose file it is: by the keys and for an agent,
    /// no file written and no command left for Undo.</summary>
    private void A_rename_reaching_a_packages_code_is_refused_whole_saying_whose_file_it_is()
    {
        string kit = WriteKitShop();
        string catalog = Path.Combine(folder, "Shop", "Catalog.cs");
        // A rename waits for its server to read a file written a moment before.
        foreach (string path in (string[])[kit, catalog])
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        }
        Restore.Run(Path.Combine(PathNames.Canonical(folder), "Shop", "Shop.csproj"));
        MainWindow window = OpenShop(languageServer: true);
        window.OpenFile(catalog);
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        string refused = $"Rename failed: Kit.cs is {KitsOwn}";

        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Tally.Cou");
        editor.Focus();
        Ui.Run(window, AppActions.Rename);
        Ui.Settle();
        Assert.True(window.Assist.Renaming is not null, "Rename opened no box on Count");
        window.KeyTextInput("Sum");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal(refused, window.Status);
        Assert.True(window.EditorArea.Commands.ToUndo(null) is null, "The rename refused left a command for Undo to take back");

        ModelContextProtocol.McpException renamed = Assert.Throws<ModelContextProtocol.McpException>(() => Ui.Await(
            new Passenger.App.Mcp.PassengerTools(window).Rename("Sum", Path.Combine("Shop", "Catalog.cs"), 5, 44), "rename's answer"));
        Assert.Equal(refused, renamed.Message);
        Assert.Equal(Catalog, editor.Document.Snapshot.ToString());
        Assert.Equal(Catalog, File.ReadAllText(catalog));
        Assert.True(File.ReadAllText(kit) == Kit, $"A rename wrote a package's file: {File.ReadAllText(kit)}");
        window.Close();
    }

    /// <summary>A NuGet package's file open in the editor refuses every change in the package's words, as every refusal of
    /// it closed does: what is typed there says so in the status bar, the Edit menu's changes and Rename grey saying it, and
    /// an agent's rename starting there is refused so, the file left as NuGet restored it.</summary>
    private void An_open_package_files_refusals_name_the_package_whether_typed_into_or_an_agent_renames_there()
    {
        string kit = WriteKitShop();
        // A rename waits for its server to read a file written a moment before.
        foreach (string path in (string[])[kit, Path.Combine(folder, "Shop", "Catalog.cs")])
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        }
        Restore.Run(Path.Combine(PathNames.Canonical(folder), "Shop", "Shop.csproj"));
        MainWindow window = OpenShop(languageServer: true);
        window.OpenFile(kit);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        string refused = $"Kit.cs is {KitsOwn}";

        Assert.NotEqual(refused, window.Status);
        window.KeyTextInput("x");
        Ui.Settle();
        Assert.Equal(Kit, editor.Document.Snapshot.ToString());
        Assert.Equal(refused, window.Status);
        Assert.Equal(refused, window.Actions.WhyNot(EditorActions.Duplicate, window.Actions.Context()));
        Ui.WaitFor(() => window.Actions.WhyNot(AppActions.Rename, window.Actions.Context()) == refused,
            () => $"Rename to grey on the package's Kit.cs saying whose file it is; it says {window.Actions.WhyNot(AppActions.Rename, window.Actions.Context()) ?? "nothing"}");

        string shown = Path.GetRelativePath(PathNames.Canonical(folder), kit);
        ModelContextProtocol.McpException renamed = Assert.Throws<ModelContextProtocol.McpException>(() => Ui.Await(
            new Passenger.App.Mcp.PassengerTools(window).Rename("Sum", shown, 5, 23), "rename's answer at the package's Kit.cs"));
        Assert.Equal($"Rename was not applied: {refused}", renamed.Message);
        Assert.True(File.ReadAllText(kit) == Kit, $"A change of an open package file wrote it: {File.ReadAllText(kit)}");
        window.Close();
    }

    /// <summary>On a decompiled file, every action of the Edit menu that would change it greys saying it is read-only, as
    /// every other editor action changing text does, and those that only copy, choose or find stay enabled, Replace among
    /// them (its replace row says why); the keys of those that change it say so in the status bar and change nothing. A
    /// text box with the keys over it takes its own Cut, Paste, Undo and Redo.</summary>
    private void Every_action_of_the_edit_menu_that_would_change_a_decompiled_file_greys_saying_it_is_read_only()
    {
        const string Text = "class A\n{\n    int b;\n    int c;\n}\n";
        Write("MetadataAsSource/A.cs", Text);
        MainWindow window = Open("MetadataAsSource/A.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        const string ReadOnly = "A.cs is read-only";
        List<string> edit = EditMenu(window);
        string[] named = [EditorActions.Cut, EditorActions.Paste, EditorActions.Duplicate, EditorActions.DeleteLine, EditorActions.MoveLineUp, EditorActions.MoveLineDown];
        Assert.True(named.All(edit.Contains), $"The Edit menu lacks some of {string.Join(", ", named)}: {string.Join(", ", edit)}");

        ActionContext context = window.Actions.Context();
        List<string> offered = [];
        List<string> greyed = [];
        foreach (string id in edit.Concat(EditorActions.All.Select(a => a.Id)).Distinct())
        {
            string? why = window.Actions.WhyNot(id, context);
            if (Changing.Contains(id) ? why != ReadOnly : why is not null && !(id == AppActions.HighlightUsages && why == Refusals.NoCSharp))
            {
                (Changing.Contains(id) ? offered : greyed).Add($"{id} ({why ?? "enabled"})");
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
    }

    /// <summary>Text typed into a decompiled file, a key's or what an input method composed and committed, changes nothing
    /// and says in the status bar that the file is read-only, as a key changing it does; the composition is drawn no
    /// more.</summary>
    private void Typing_into_a_decompiled_file_says_in_the_status_bar_it_is_read_only_and_an_input_methods_text_too()
    {
        const string Text = "class A\n{\n}\n";
        Write("MetadataAsSource/A.cs", Text);
        Write("MetadataAsSource/B.cs", Text);
        MainWindow window = Open("MetadataAsSource/A.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.NotEqual("A.cs is read-only", window.Status);
        window.KeyTextInput("x");
        Ui.Settle();
        Assert.Equal(Text, editor.Document.Snapshot.ToString());
        Assert.Equal("A.cs is read-only", window.Status);

        window.OpenFile(Path.Combine(folder, "MetadataAsSource", "B.cs"));
        editor.Focus();
        editor.Selections = Passenger.Text.SelectionSet.Single("class".Length);
        Ui.Settle();
        TextInputMethodClientRequestedEventArgs asked = new() { RoutedEvent = Avalonia.Input.InputElement.TextInputMethodClientRequestedEvent };
        editor.RaiseEvent(asked);
        TextInputMethodClient client = asked.Client ?? throw new InvalidOperationException("The editor gave the input method no client");
        double after = editor.PointOf("class ".Length).X;
        client.SetPreeditText("にほん");
        Ui.Settle();
        Assert.True(editor.PointOf("class ".Length).X > after, "The input method's composition is not drawn at the caret, so its end is not checked");
        window.KeyTextInput("日本");
        Ui.Settle();
        Assert.Equal(Text, editor.Document.Snapshot.ToString());
        Assert.Equal("B.cs is read-only", window.Status);
        Assert.True(editor.PointOf("class ".Length).X == after, "The composition the input method committed is still drawn in the read-only B.cs");
        window.Close();
    }

    /// <summary>The Run console's output is read-only: every action of the Edit menu that would change it greys saying so,
    /// and what is typed there and the keys of those actions say it in the status bar, changing nothing. While its program
    /// reads its input, Paste, Backspace and New Line go to that input from the menu as from their keys, the rest greyed
    /// still; Copy, Select All and Find stay enabled throughout.</summary>
    private void Every_action_of_the_edit_menu_that_would_change_the_Run_consoles_output_greys_saying_it_is_read_only_its_programs_input_taking_its_own()
    {
        const string Output = "alpha\nbeta\n";
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        OutputView run = window.BottomPanel.Run;
        run.Append(Output);
        window.DockHost.Show(ToolWindowIds.Run);
        Ui.Settle();
        Ui.Click(run.Editor);
        Assert.Same(run.Editor, window.Actions.Context().Focused);
        AssertChangesGreyed(window, Refusals.OutputReadOnly, "the Run console");
        Assert.Null(window.Actions.WhyNot(AppActions.Find, window.Actions.Context()));

        Assert.NotEqual(Refusals.OutputReadOnly, window.Status);
        window.KeyTextInput("x");
        Ui.Settle();
        Assert.Equal(Output, run.Document.Snapshot.ToString());
        Assert.Equal(Refusals.OutputReadOnly, window.Status);
        // Save, with no file open, says so: the status bar holds other words before the key.
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal(Refusals.NoFile, window.Status);
        Ui.Run(window, EditorActions.Duplicate);
        Assert.Equal(Output, run.Document.Snapshot.ToString());
        Assert.Equal(Refusals.OutputReadOnly, window.Status);

        List<string> sent = [];
        run.Input = new ConsoleInput(sent.Add, () => { });
        AssertChangesGreyed(window, Refusals.OutputReadOnly, "the Run console, its program reading its input", EditorActions.Paste, EditorActions.Backspace, EditorActions.NewLine);
        Assert.Null(window.Actions.WhyNot(AppActions.Find, window.Actions.Context()));
        window.KeyTextInput("typo");
        Ui.Settle();
        Assert.Null(window.Actions.Run(EditorActions.Backspace));
        Ui.Await(window.Clipboard!.SetTextAsync("ed"), "the text on the clipboard");
        Assert.Null(window.Actions.Run(EditorActions.Paste));
        Ui.WaitFor(() => run.Document.Snapshot.ToString() == Output + "typed", () => $"the Edit menu's Paste to type the clipboard after \"typ\"; the console holds \"{run.Document.Snapshot}\"");
        Assert.Equal(Refusals.OutputReadOnly, window.Actions.Run(EditorActions.Duplicate));
        Assert.Null(window.Actions.Run(EditorActions.NewLine));
        Ui.Settle();
        Assert.True(sent is ["typed\n"], $"The Edit menu's Backspace, Paste and New Line sent [{string.Join(", ", sent)}] to the program's input, not the line typed");
        run.Input = null;
        window.Close();
    }

    /// <summary>A text box shown only to read and copy from, here IDE Internal Errors' details, greys the Edit menu's Undo,
    /// Redo, Cut and Paste saying its text is read-only, where they stayed enabled and ran as nothing; Copy and Select All
    /// stay enabled and copy from it.</summary>
    private void The_edit_menus_changes_grey_over_a_read_only_text_box_saying_so_and_copy_takes_its_text()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        // Thrown on purpose, for a details box to show; cleared as the test ends.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => throw new InvalidOperationException("A posted job went wrong"));
        Ui.Settle();
        try
        {
            ErrorsPopup popup = window.ShowErrors();
            Ui.Settle();
            TextBox details = popup.GetVisualDescendants().OfType<TextBox>().Single(box => box.IsReadOnly);
            Assert.Contains("A posted job went wrong", details.Text, StringComparison.Ordinal);
            Ui.Click(details);
            ActionContext context = window.Actions.Context();
            Assert.Same(details, context.Focused);

            List<string> edit = EditMenu(window);
            string[] changing = [EditorActions.Undo, EditorActions.Redo, EditorActions.Cut, EditorActions.Paste];
            Assert.True(changing.Append(EditorActions.Copy).Append(EditorActions.SelectAll).All(edit.Contains), $"The Edit menu lacks some of Undo, Redo, Cut, Paste, Copy and Select All: {string.Join(", ", edit)}");
            string[] offered = [.. changing.Where(id => window.Actions.WhyNot(id, context) != Refusals.TextReadOnly)
                .Select(id => $"{id} ({window.Actions.WhyNot(id, context) ?? "enabled"})")];
            Assert.True(offered.Length == 0, $"Over IDE Internal Errors' read-only details, these are offered or greyed for another reason: {string.Join(", ", offered)}");
            Assert.True(window.Actions.WhyNot(EditorActions.Copy, context) is null && window.Actions.WhyNot(EditorActions.SelectAll, context) is null,
                "Copy or Select All greyed over a read-only text box, which they only read");

            Assert.Null(window.Actions.Run(EditorActions.SelectAll));
            Assert.Null(window.Actions.Run(EditorActions.Copy));
            Ui.Settle();
            string? copied = Ui.Await(window.Clipboard!.TryGetTextAsync(), "the clipboard's text");
            Assert.Equal(details.Text, copied);
        }
        finally
        {
            IdeErrors.Clear();
            window.Close();
        }
    }

    /// <summary>Walks every editor action, those of the Edit menu among them, as the editor with the keys in front has them:
    /// each that would change its text greys saying <paramref name="why"/> but those its own input takes
    /// (<paramref name="taken"/>), and each that only reads, Copy and Select All among them, is enabled.</summary>
    internal static void AssertChangesGreyed(MainWindow window, string why, string where, params string[] taken)
    {
        List<string> edit = EditMenu(window);
        string[] named = [EditorActions.Undo, EditorActions.Cut, EditorActions.Copy, EditorActions.Paste, EditorActions.Duplicate, EditorActions.DeleteLine,
            EditorActions.SelectAll, EditorActions.MoveLineUp, EditorActions.MoveLineDown];
        Assert.True(named.All(edit.Contains), $"The Edit menu lacks some of {string.Join(", ", named)}: {string.Join(", ", edit)}");
        ActionContext context = window.Actions.Context();
        List<string> offered = [];
        List<string> greyed = [];
        foreach (string id in EditorActions.All.Select(a => a.Id))
        {
            string? whyNot = window.Actions.WhyNot(id, context);
            bool refused = Changing.Contains(id) && !taken.Contains(id);
            if (refused ? whyNot != why : whyNot is not null)
            {
                (refused ? offered : greyed).Add($"{id} ({whyNot ?? "enabled"})");
            }
        }
        Assert.True(offered.Count == 0, $"In {where}, which nothing there can change, these are offered or greyed for another reason: {string.Join(", ", offered)}");
        Assert.True(greyed.Count == 0, $"In {where}, these only read or go to its own input but are greyed: {string.Join(", ", greyed)}");
    }

    /// <summary>The actions of the main menu's Edit menu, in order.</summary>
    private static List<string> EditMenu(MainWindow window) => [.. ActionsIn(MainMenu.Build(window.Actions.All).Single(m => m.Title == "Edit"))];

    private static IEnumerable<string> ActionsIn(MenuEntry menu) => menu.Action is { } id ? [id] : (menu.Items ?? []).SelectMany(ActionsIn);

    private void The_colour_pickers_choose_is_greyed_in_a_read_only_tab_and_enter_writes_nothing()
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

    /// <summary>The package's code, compiled into Shop from the package folder as NuGet's props compile contentFiles.</summary>
    private const string Kit = "namespace Kit;\n\npublic static class Tally\n{\n    public static int Count(int value) => value;\n}\n";

    private const string Catalog = "namespace Shop;\n\npublic static class Catalog\n{\n    public static int Total() => Kit.Tally.Count(1);\n}\n";

    /// <summary>What every refusal here says the package's Kit.cs is.</summary>
    private const string KitsOwn = "a file of the package kit 1.0.0, as NuGet restored it for every project";

    /// <summary>Shop, whose package folder is the folder's own .nuget/packages (as a repository's nuget.config may set it),
    /// holding kit 1.0.0 as NuGet lays it out, its contentFiles' Kit.cs compiled into Shop; Kit.cs's path.</summary>
    private string WriteKitShop()
    {
        Write(".nuget/packages/kit/1.0.0/.nupkg.metadata", "{ \"version\": 2 }\n");
        string kit = Write(".nuget/packages/kit/1.0.0/contentFiles/cs/any/Kit.cs", Kit);
        Write("Shop/Shop.csproj", TestProjects.Library("<NuGetPackageRoot>$(MSBuildThisFileDirectory)../.nuget/packages/</NuGetPackageRoot>")
            .With("  <ItemGroup>\n    <Compile Include=\"../.nuget/packages/kit/1.0.0/contentFiles/cs/any/Kit.cs\" Link=\"Kit.cs\" />\n  </ItemGroup>\n"));
        Write("Shop/Catalog.cs", Catalog);
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop/Shop.csproj\" />\n</Solution>\n");
        return kit;
    }

    /// <summary>The folder of <see cref="WriteKitShop"/> open once Shop's evaluation, which names its package folder, landed.</summary>
    private MainWindow OpenShop(bool languageServer)
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: languageServer);
        Ui.WaitFor(() => window.Projects.Any(p => p.IsEvaluated), "MSBuild to evaluate Shop");
        return window;
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
