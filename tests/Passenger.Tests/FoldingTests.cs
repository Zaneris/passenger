using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Code folding through the window, as a person folds: Rider's Ctrl+M chords, the gutter's chevrons, and a
/// caret that goes into folded code unfolding it. The regions come from the file's own parse.</summary>
public sealed class FoldingTests : IDisposable
{
    private const string Source =
        "using System;\nusing System.Linq;\n\nclass Shapes\n{\n    int Area(int w, int h)\n    {\n        int area = w * h;\n        return area;\n    }\n\n    int Twice(int x) => x * 2;\n}\n";

    private readonly string folder = TestFolders.Create("fold-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_method_folds_to_its_signature_the_caret_steps_past_it_and_going_into_it_unfolds_it()
    {
        string path = Path.Combine(folder, "Shapes.cs");
        File.WriteAllText(path, Source);
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        // The usings, the class and the method; the usings open folded.
        Ui.WaitFor(() => editor.FoldRegions.Count == 3, "the file's regions");
        Assert.Equal(FoldKind.Imports, Assert.Single(editor.FoldRegions, r => r.Folded).Kind);
        editor.Focus();
        Ui.Press(window, "control M , control X");
        Assert.DoesNotContain(editor.FoldRegions, r => r.Folded);
        int twice = Source.IndexOf("int Twice", StringComparison.Ordinal);
        double before = editor.PointOf(twice).Y;

        editor.GoTo(new TextPosition(7, 8));
        editor.Focus();
        Ui.Press(window, "control M , control S");
        FoldRegion method = Assert.Single(editor.FoldRegions, r => r.Folded);
        Assert.Equal(Source.IndexOf("int h)", StringComparison.Ordinal) + "int h)".Length, method.Start);
        Assert.Equal("{...}", method.Placeholder);
        Assert.Equal(new TextPosition(5, "    int Area(int w, int h)".Length), editor.CaretPosition);
        Assert.Equal(before - (4 * editor.LineHeight), editor.PointOf(twice).Y, 3);
        Assert.Equal(editor.FoldRegions, window.EditorArea.Active!.FoldRegions);

        Ui.Press(window, "DOWN");
        Assert.Equal(10, editor.CaretPosition.Line);
        Ui.Press(window, "UP");
        Assert.Equal(5, editor.CaretPosition.Line);

        Ui.Press(window, "control M , control M");
        Assert.DoesNotContain(editor.FoldRegions, r => r.Folded);
        Assert.Equal(before, editor.PointOf(twice).Y, 3);

        Ui.Press(window, "control M , control A");
        Assert.All(editor.FoldRegions, r => Assert.True(r.Folded));
        editor.GoTo(new TextPosition(8, 8));
        Ui.Settle();
        Assert.Single(editor.FoldRegions, r => r.Folded);
        Assert.Equal(new TextPosition(8, 8), editor.CaretPosition);

        Point chevron = editor.TranslatePoint(editor.GutterColumnPoint(GutterColumn.Folding, 5), window)!.Value;
        window.MouseDown(chevron, MouseButton.Left);
        window.MouseUp(chevron, MouseButton.Left);
        Ui.Settle();
        Assert.Equal(method.Start, Assert.Single(editor.FoldRegions, r => r.Folded && r.Start != editor.FoldRegions[0].Start).Start);
        window.Close();
    }

    /// <summary>A folded method is one line to every line command, as the person sees it: Move Line takes it whole and
    /// steps whole over a folded method beside it, both staying folded, and Duplicate, Copy, Cut and Delete Line take
    /// its body with its signature. Each command leaves every method whole.</summary>
    [AvaloniaFact]
    public void Line_commands_take_a_folded_method_whole_and_step_over_one_whole_leaving_every_body_whole()
    {
        const string Area = "    int Area(int w, int h)\n    {\n        int area = w * h;\n        return area;\n    }\n";
        const string Perimeter = "    int Perimeter(int w, int h)\n    {\n        return 2 * (w + h);\n    }\n";
        const string Twice = "    int Twice(int x) => x * 2;\n";
        static string Shapes(params string[] members) => "class Shapes\n{\n" + string.Concat(members) + "}\n";
        string path = Path.Combine(folder, "Shapes.cs");
        File.WriteAllText(path, Shapes(Area, Perimeter, Twice));
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => editor.FoldRegions.Count == 3, "the file's regions");
        editor.Focus();
        Ui.Run(window, EditorActions.FoldToDefinitions);

        // Each command at a member's signature, the caret's column kept; then the text, the caret and the folded methods.
        void At(string member, int nth, string action)
        {
            string text = editor.Document.Snapshot.ToString();
            int found = -1;
            for (int i = 0; i <= nth; i++)
            {
                found = text.IndexOf(member.Split('\n')[0], found + 1, StringComparison.Ordinal);
            }
            Assert.True(found >= 0, $"The text has no {nth + 1}. '{member.Split('\n')[0].Trim()}' to run {action} at");
            editor.Selections = SelectionSet.Single(found + 8);
            Ui.Run(window, action);
            Ui.Settle();
        }
        void Shows(string what, string expected, string[] folded, string caretOn)
        {
            TextSnapshot text = editor.Document.Snapshot;
            Assert.True(expected == text.ToString(), $"After {what} the text is not the members whole:\n{text}");
            string Named(FoldRegion r) => text.GetLineText(text.GetLineFromOffset(r.Start)).Trim();
            string[] shown = [.. Folded(editor).Select(Named)];
            Assert.True(folded.SequenceEqual(shown), $"After {what} the folded methods are [{string.Join(", ", shown)}], not [{string.Join(", ", folded)}]");
            string on = text.GetLineText(editor.CaretPosition.Line).Trim();
            Assert.True(caretOn == on && editor.CaretPosition.Column == 8, $"After {what} the caret is on '{on}' at {editor.CaretPosition.Column}, not on '{caretOn}' at 8");
        }

        At(Area, 0, EditorActions.MoveLineDown);
        Shows("Move Line Down on folded Area", Shapes(Perimeter, Area, Twice), ["int Perimeter(int w, int h)", "int Area(int w, int h)"], "int Area(int w, int h)");
        At(Area, 0, EditorActions.MoveLineUp);
        Shows("Move Line Up on folded Area", Shapes(Area, Perimeter, Twice), ["int Area(int w, int h)", "int Perimeter(int w, int h)"], "int Area(int w, int h)");
        At(Twice, 0, EditorActions.MoveLineUp);
        Shows("Move Line Up past folded Perimeter", Shapes(Area, Twice, Perimeter), ["int Area(int w, int h)", "int Perimeter(int w, int h)"], "int Twice(int x) => x * 2;");
        At(Twice, 0, EditorActions.MoveLineDown);
        Shows("Move Line Down past folded Perimeter", Shapes(Area, Perimeter, Twice), ["int Area(int w, int h)", "int Perimeter(int w, int h)"], "int Twice(int x) => x * 2;");

        At(Perimeter, 0, EditorActions.Copy);
        Assert.Equal(Perimeter, Ui.Clipboard(window));
        At(Area, 0, EditorActions.Duplicate);
        Shows("Duplicate on folded Area", Shapes(Area, Area, Perimeter, Twice), ["int Area(int w, int h)", "int Perimeter(int w, int h)"], "int Area(int w, int h)");
        Assert.Equal(7, editor.CaretPosition.Line);
        At(Area, 0, EditorActions.DeleteLine);
        Shows("Delete Line on folded Area", Shapes(Area, Perimeter, Twice), ["int Perimeter(int w, int h)"], "int Area(int w, int h)");
        At(Perimeter, 0, EditorActions.Cut);
        Assert.Equal(Perimeter, Ui.Clipboard(window));
        Shows("Cut on folded Perimeter", Shapes(Area, Twice), [], "int Twice(int x) => x * 2;");
        At(Area, 0, EditorActions.Paste);
        Shows("Paste of the cut method above Area", Shapes(Perimeter, Area, Twice), [], "int Area(int w, int h)");
        window.Close();
    }

    /// <summary>Folding is what makes a long method cheap to scroll past: with its 5,000 lines folded, a key typed below it
    /// colours the lines on screen, never the folded ones between them, and a frame with nothing new (a caret's blink)
    /// colours nothing.</summary>
    [AvaloniaFact]
    public void Typing_below_five_thousand_folded_lines_colours_only_the_lines_on_screen()
    {
        StringBuilder text = new("class Big\n{\n    void Long()\n    {\n");
        for (int i = 0; i < 5000; i++)
        {
            text.Append("        int x").Append(i).Append(" = ").Append(i).Append(" * Math.Max(1, 2);\n");
        }
        text.Append("    }\n\n    void Short()\n    {\n        int y = 0;\n    }\n}\n");
        string source = text.ToString();
        EditorRig rig = new(source);
        using CSharpHighlighter csharp = new(rig.Document);
        Asking asking = new(csharp);
        rig.Editor.Highlighter = asking;
        Ui.WaitFor(() => csharp.Folds.Count == 3, "the file's regions");
        int body = source.IndexOf("Long()", StringComparison.Ordinal) + "Long()".Length;
        int below = source.IndexOf("int y", StringComparison.Ordinal);
        TextSnapshot snapshot = rig.Document.Snapshot;
        // The body's lines, from its brace to its closer, all hidden behind the signature.
        int hiddenStart = snapshot.GetLineStart(3);
        int hiddenEnd = snapshot.GetLineStart(5005);

        long unfolded = ColouredPerKey(rig, asking, below, hiddenStart, hiddenEnd, folded: false);
        rig.Editor.SetFoldRegions([.. csharp.Folds.Select(r => r with { Folded = r.Start == body })]);
        Assert.True(rig.Editor.FoldRegions.Single(r => r.Folded).Start == body, "The method's body should be the region folded.");
        long folded = ColouredPerKey(rig, asking, below, hiddenStart, hiddenEnd, folded: true);
        Assert.True(folded <= unfolded, $"A key below the fold had {folded} characters coloured, more than the {unfolded} it has unfolded.");

        asking.Asked.Clear();
        for (int i = 0; i < 5; i++)
        {
            rig.Editor.InvalidateVisual();
            Ui.Settle();
        }
        Assert.True(asking.Asked.Count == 0, $"A frame with nothing changed asked for colours again: {string.Join(", ", asking.Asked)}.");
    }

    /// <summary>A key in a file of 20,000 lines costs its parse what the key changed: each key here (in a method or in
    /// the usings), and each member pasted in, deleted or wrapped in a #region, allocates on the parsing worker under a
    /// twentieth of what opening the file did, and after each the folds are those the same text has opened
    /// afresh.</summary>
    [AvaloniaFact]
    public void Typing_in_twenty_thousand_lines_parses_what_each_key_changed_and_the_folds_stay_those_of_the_whole_text()
    {
        string source = Members(200);
        string path = Path.Combine(folder, "Members.cs");
        File.WriteAllText(path, source);
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        CSharpHighlighter syntax = document.Syntax!;
        Ui.WaitFor(() => document.FoldRegions.Count > 1400, "the file's regions");
        (long opened, int parses) = syntax.Worked;
        Assert.Equal(1, parses);
        long most = 0;
        string worst = string.Empty;
        int fresh = 0;

        void Measured(string what, Action edit)
        {
            (long bytes, int count) = syntax.Worked;
            edit();
            Ui.Settle();
            (long after, int counted) = syntax.Worked;
            long each = (after - bytes) / Math.Max(1, counted - count);
            if (each > most)
            {
                most = each;
                worst = what;
            }
        }

        void SameAsOpenedAfresh(string after)
        {
            string again = Path.Combine(folder, $"Fresh{++fresh}.cs");
            File.WriteAllText(again, document.Text.Snapshot.ToString());
            window.OpenFile(again);
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument opened = window.EditorArea.Active!;
            Assert.NotSame(document, opened);
            string Regions(Passenger.App.Workspace.OpenDocument of) => string.Join(", ", of.FoldRegions.Select(r => $"{r.Start}-{r.End} {r.Placeholder}"));
            string expected = Regions(opened);
            string actual = Regions(document);
            Assert.True(expected == actual, $"After {after} the folds are not those of the same text opened afresh:\nafresh {expected}\nkept   {actual}");
            window.EditorArea.Close(opened);
            window.EditorArea.Activate(document);
            Ui.Settle();
        }

        int Find(string text, string what, int from = 0)
        {
            int found = text.IndexOf(what, from, StringComparison.Ordinal);
            Assert.True(found >= 0, $"'{what}' is not in the text, so the edit that was to put it there did not happen");
            return found;
        }

        void TypeAt(string after, string text)
        {
            int at = Find(document.Text.Snapshot.ToString(), after) + after.Length;
            editor.Selections = SelectionSet.Single(at);
            editor.Focus();
            foreach (char c in text)
            {
                Measured($"'{c}' typed after '{after}'", () => window.KeyTextInput(c.ToString()));
            }
        }

        void Replace(string what, string from, string to, string with)
        {
            string text = document.Text.Snapshot.ToString();
            int start = Find(text, from);
            int end = Find(text, to, start) + to.Length;
            editor.Selections = SelectionSet.Single(new Selection(start, end));
            editor.Focus();
            Measured(what, () =>
            {
                if (with.Length == 0)
                {
                    Ui.Press(window, "BACK");
                }
                else
                {
                    Task copied = window.Clipboard!.SetTextAsync(with);
                    Ui.WaitFor(() => copied.IsCompleted, "the clipboard to take the text");
                    Ui.Press(window, Ui.Shortcut(EditorActions.Paste));
                }
            });
        }

        TypeAt("int value0 = seed + 100;", " int added = value0 * 2;");
        SameAsOpenedAfresh("a statement typed in a method");
        TypeAt("using System", ".IO");
        SameAsOpenedAfresh("a using typed");
        Replace("a method pasted in", "        public int Property120", "        public int Property120",
            "        /// <summary>\n        /// Pasted.\n        /// </summary>\n        public void Pasted()\n        {\n            int x = 0;\n        }\n\n        public int Property120");
        TypeAt("int x = 0", "1");
        SameAsOpenedAfresh("a method pasted in");
        Replace("a method deleted", "        /// <summary>\n        /// Method 150.", "return Local(value89);\n        }\n\n", string.Empty);
        SameAsOpenedAfresh("a method deleted");
        Replace("a #region begun", "        /// <summary>\n        /// Method 60.", "        /// <summary>\n        /// Method 60.",
            "        #region Sixty\n        /// <summary>\n        /// Method 60.");
        Replace("a #region ended", "        public int Property61", "        public int Property61", "        #endregion\n\n        public int Property61");
        SameAsOpenedAfresh("two members wrapped in a #region");
        TypeAt("int value1 = value0 * 3 + \"10:1\".Length;", " Func<int> made = () => { return 1; };");
        TypeAt("int value2 = value1 * 3 + \"30:2\".Length;", " /*");
        SameAsOpenedAfresh("a block comment left open");
        TypeAt("int value3 = value2 * 3 + \"30:3\".Length;", " */");
        TypeAt("int value4 = value3 * 3 + \"40:4\".Length;", " }");
        SameAsOpenedAfresh("a brace that ends a method early");

        Assert.True(most < opened / 20,
            $"An edit ({worst}) allocated {most / 1024:N0} KB on the parsing worker, a twentieth or more of the {opened / 1024:N0} KB opening the file did: its parse walked the whole tree");
        window.Close();
    }

    /// <summary>Every key moves the regions below it, and the parse it makes brings them again: the least of twenty
    /// keys above 2,000 doc comments allocates on the UI thread under 4 bytes for each more than above plain comments,
    /// and a region folded below keeps its fold.</summary>
    [AvaloniaFact]
    public void Typing_above_two_thousand_regions_moves_them_on_the_UI_thread_without_copying_each_again_and_again()
    {
        (long plain, int plainRegions) = UiBytesPerKey(doc: false);
        (long folding, int regions) = UiBytesPerKey(doc: true);
        int more = regions - plainRegions;
        Assert.True(more >= 2000, $"The doc comments made {more} regions more than the plain comments");
        long each = (folding - plain) / more;
        TestContext.Current.TestOutputHelper?.WriteLine($"A key allocated at least {folding:N0} bytes on the UI thread above {regions} regions, {plain:N0} above {plainRegions}: {each} bytes a region.");
        Assert.True(each < 4,
            $"A key allocated at least {folding / 1024:N0} KB on the UI thread above {regions} regions, {plain / 1024:N0} KB above {plainRegions}: {each} bytes for each region, which the window copied");
    }

    /// <summary>The least of twenty keys typed in a method at the top of a file allocates on the UI thread, its parse landing
    /// included, with 2,000 members below under doc comments or plain ones, the last folded; and the file's regions. What
    /// else lands in a key's wait goes into some keys alone (the caret's blink draws the editor again, 28 KB, in a wait past
    /// its 530 ms, as a busy runner's are), what the key costs for each region into every one.</summary>
    private (long Bytes, int Regions) UiBytesPerKey(bool doc)
    {
        string comment = doc ? "///" : "// ";
        StringBuilder text = new("class Many\n{\n    int Typed()\n    {\n        int value = 1;\n        return value;\n    }\n\n");
        for (int i = 0; i < 2000; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"    {comment} <summary>\n    {comment} Member {i}.\n    {comment} </summary>\n    int Member{i}() => {i};\n\n");
        }
        text.Append("    void Last()\n    {\n        int last = 0;\n    }\n}\n");
        string source = text.ToString();
        string path = Path.Combine(folder, doc ? "Doc.cs" : "Plain.cs");
        File.WriteAllText(path, source);
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.FoldRegions.Count > 2, "the file's regions");
        // The last method folded, far below the screen.
        editor.GoTo(new TextPosition(source.Count(c => c == '\n') - 3, 8));
        editor.Focus();
        Ui.Press(window, "control M , control S");
        int last = source.IndexOf("void Last()", StringComparison.Ordinal) + "void Last()".Length;
        Assert.Equal(last, Assert.Single(document.FoldRegions, r => r.Folded).Start);
        editor.Selections = SelectionSet.Single(source.IndexOf("= 1;", StringComparison.Ordinal) + 2);
        editor.ScrollTo(default);
        Ui.Settle();
        const int Keys = 20;
        long least = long.MaxValue;
        for (int i = 0; i < Keys; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput("1");
            Ui.Settle();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        FoldRegion folded = Assert.Single(document.FoldRegions, r => r.Folded);
        Assert.True(folded.Start == last + Keys, $"The folded method's region starts at {folded.Start} after the keys, not on its signature's end at {last + Keys}");
        int regions = document.FoldRegions.Count;
        window.Close();
        return (least, regions);
    }

    /// <summary>The open file carries its regions through a key in runs, moving region by region only those the key reaches
    /// and shifting those past it whole: through keys typed in the names of methods above, among and below 300 folded ones,
    /// Enter between two of them, a key typed at two carets far apart and its undo, the regions the file carried as each
    /// key landed are those its parse then finds, and every method stays folded.</summary>
    [AvaloniaFact]
    public void Keys_among_three_hundred_folded_methods_carry_every_region_to_where_the_parse_finds_it()
    {
        StringBuilder text = new("class Many\n{\n");
        for (int i = 0; i < 300; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    int Member{i}()\n    {{\n        return {i};\n    }}\n\n");
        }
        string source = text.Append("}\n").ToString();
        string path = Path.Combine(folder, "Folded.cs");
        File.WriteAllText(path, source);
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        CSharpHighlighter syntax = document.Syntax!;
        Ui.WaitFor(() => document.FoldRegions.Count == 301, "the file's regions");
        editor.Focus();
        Ui.Run(window, EditorActions.FoldToDefinitions);
        Assert.Equal(300, document.FoldRegions.Count(r => r.Folded));

        static string Spans(IReadOnlyList<FoldRegion> regions) => string.Join(", ", regions.Select(r => $"{r.Start}-{r.End} {r.Placeholder}"));
        int At(string after) => document.Text.Snapshot.ToString().IndexOf(after, StringComparison.Ordinal) + after.Length;
        void Carried(string what, Action key)
        {
            key();
            string carried = Spans(document.FoldRegions);
            Ui.Settle();
            string found = Spans(syntax.Folds);
            Assert.True(carried == found, $"After {what} the regions the file carried are not those its parse finds:\nfound   {found}\ncarried {carried}");
            int folded = document.FoldRegions.Count(r => r.Folded);
            Assert.True(folded == 300, $"After {what} {folded} of the 300 methods are folded");
        }

        foreach (int member in new[] { 0, 150, 299, 62, 63, 127 })
        {
            editor.Selections = SelectionSet.Single(At($"int Member{member}"));
            Carried($"a key in Member{member}'s name", () => window.KeyTextInput("x"));
        }
        editor.Selections = SelectionSet.Single(At("return 200;\n    }\n"));
        Carried("Enter between two methods", () => Ui.Press(window, "ENTER"));
        editor.Selections = SelectionSet.Create([Selection.At(At("int Member10")), Selection.At(At("int Member290"))], 0);
        Carried("a key at two carets", () => window.KeyTextInput("y"));
        Carried("its undo", () => Ui.Run(window, EditorActions.Undo));
        window.Close();
    }

    [AvaloniaFact]
    public void Resting_the_pointer_on_a_folded_methods_placeholder_shows_the_lines_it_hides_on_a_card()
    {
        string path = Path.Combine(folder, "Shapes.cs");
        File.WriteAllText(path, Source);
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => editor.FoldRegions.Count == 3, "the file's regions");
        editor.GoTo(new TextPosition(7, 8));
        editor.Focus();
        Ui.Press(window, "control M , control S");
        Assert.Null(editor.FoldPreviewText);

        // The placeholder's chip stands a character past the signature's end.
        TextSnapshot text = editor.Document.Snapshot;
        int signature = text.GetLine(5).End;
        double character = editor.PointOf(signature).X - editor.PointOf(signature - 1).X;
        Point placeholder = editor.TranslatePoint(editor.PointOf(signature) + new Point(character * 2.5, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(placeholder, RawInputModifiers.None);
        Ui.WaitFor(() => editor.FoldPreviewText is not null, "the card of the folded method's lines");
        Assert.Equal("{\n    int area = w * h;\n    return area;\n}", editor.FoldPreviewText);
        window.MouseMove(placeholder + new Point(character, 0), RawInputModifiers.None);
        Ui.Settle();
        Assert.NotNull(editor.FoldPreviewText);
        Assert.Single(editor.FoldRegions, r => r.Folded && r.Kind == FoldKind.Body);

        window.MouseMove(placeholder + new Point(0, editor.LineHeight * 4), RawInputModifiers.None);
        Ui.Settle();
        Assert.Null(editor.FoldPreviewText);
        window.MouseDown(placeholder, MouseButton.Left);
        window.MouseUp(placeholder, MouseButton.Left);
        Ui.Settle();
        Assert.DoesNotContain(editor.FoldRegions, r => r.Kind == FoldKind.Body && r.Folded);
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_opens_folded_as_it_last_closed_and_so_does_its_folder_after_a_restart()
    {
        string settings = TestFolders.Create("fold-settings-");
        try
        {
            string path = Path.Combine(folder, "Members.cs");
            File.WriteAllText(path, Defined);
            MainWindow first = Keeping(settings);
            first.OpenFile(path);
            Ui.Settle();
            CodeEditor editor = first.EditorArea.CodeEditor;
            Passenger.App.Workspace.OpenDocument document = first.EditorArea.Active!;
            Ui.WaitFor(() => document.FoldsFound, "the file's regions");
            Assert.Equal([FoldKind.FileHeader, FoldKind.Imports, FoldKind.Region], Folded(editor).Select(r => r.Kind));

            editor.Focus();
            editor.GoTo(new TextPosition(2, 0));
            Ui.Press(first, "control M , control E");
            editor.GoTo(new TextPosition(15, 8));
            Ui.Press(first, "control M , control S");
            editor.GoTo(new TextPosition(27, 8));
            Ui.Press(first, "control M , control S");
            FoldRegion[] folded = Folded(editor);
            Assert.Equal([FoldKind.FileHeader, FoldKind.DocComment, FoldKind.Body], folded.Select(r => r.Kind));

            Assert.True(first.EditorArea.Close(document), "The file did not close");
            first.OpenFile(path);
            Ui.Settle();
            Ui.WaitFor(() => first.EditorArea.Active!.FoldsFound, "the reopened file's regions");
            Assert.Equal(folded, Folded(first.EditorArea.CodeEditor));
            first.Close();

            MainWindow second = Keeping(settings);
            second.OpenFolder(folder);
            Ui.Settle();
            Assert.True(second.EditorArea.Active?.Path == Passenger.Core.PathNames.Canonical(path), "The folder did not open its file again");
            Ui.WaitFor(() => second.EditorArea.Active!.FoldsFound, "the regions of the file the folder opened again");
            Assert.Equal(folded, Folded(second.EditorArea.CodeEditor));
            second.Close();
        }
        finally
        {
            TestFolders.Delete(settings);
        }
    }

    [AvaloniaFact]
    public void A_file_opens_with_its_header_usings_and_regions_folded_and_Collapse_to_Definitions_folds_every_member_and_doc_comment()
    {
        string path = Path.Combine(folder, "Members.cs");
        File.WriteAllText(path, Defined);
        MainWindow window = Ui.OpenFolder(folder, width: 1000);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.FoldsFound, "the file's regions");
        // Every region of its kind: the namespace's, the class's and the lambda's braces are code, a member's its body.
        Assert.Equal(
            [(0, FoldKind.FileHeader), (2, FoldKind.Imports), (5, FoldKind.Code), (7, FoldKind.DocComment), (10, FoldKind.Code),
                (13, FoldKind.Region), (14, FoldKind.DocComment), (17, FoldKind.Body), (19, FoldKind.Code), (26, FoldKind.Body), (28, FoldKind.Body)],
            editor.FoldRegions.Select(r => (editor.Document.Snapshot.GetLineFromOffset(r.Start), r.Kind)));
        Assert.Equal([FoldKind.FileHeader, FoldKind.Imports, FoldKind.Region], Folded(editor).Select(r => r.Kind));

        editor.Focus();
        Ui.Press(window, "control M , control X");
        Assert.Empty(Folded(editor));
        Ui.Press(window, "control M , control O");
        Assert.All(editor.FoldRegions, r => Assert.True(r.Folded == r.Kind is FoldKind.Body or FoldKind.DocComment,
            $"Collapse to Definitions left the {r.Kind} region at line {editor.Document.Snapshot.GetLineFromOffset(r.Start)} {(r.Folded ? "folded" : "open")}"));
        // Each member is its signature alone, under the first line of its doc comment.
        TextSnapshot text = editor.Document.Snapshot;
        Assert.Equal(editor.PointOf(text.GetLineStart(14)).Y + editor.LineHeight, editor.PointOf(text.GetLineStart(17)).Y, 1);
        Assert.Equal(editor.PointOf(text.GetLineStart(17)).Y + (2 * editor.LineHeight), editor.PointOf(text.GetLineStart(26)).Y, 1);

        Assert.Equal("Collapse to Definitions", window.Actions[EditorActions.FoldToDefinitions]!.Title);
        Assert.Contains("M, O", window.Actions.Shortcut(EditorActions.FoldToDefinitions), StringComparison.Ordinal);

        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "fold by default";
        Ui.Settle();
        Assert.Equal([SettingsDialog.CodeFoldingPage], dialog.Pages);
        Assert.Equal(["File header", "Imports", "Documentation comments", "Method bodies", "Preprocessor regions", "Line and block comments", "XML tags"], dialog.Shown);
        dialog.Change(s => s with { FoldImports = false, FoldDocComments = true, FoldMethodBodies = true });
        dialog.Ok();
        Ui.Settle();
        string other = Path.Combine(folder, "Other.cs");
        File.WriteAllText(other, Defined);
        window.OpenFile(other);
        Ui.Settle();
        Ui.WaitFor(() => window.EditorArea.Active!.FoldsFound, "the other file's regions");
        Assert.Equal([FoldKind.FileHeader, FoldKind.DocComment, FoldKind.Region, FoldKind.DocComment, FoldKind.Body, FoldKind.Body, FoldKind.Body],
            Folded(window.EditorArea.CodeEditor).Select(r => r.Kind));
        window.Close();
    }

    /// <summary>Collapse All and Collapse to Definitions with the view wheeled down to the file's end and the caret left on
    /// its first line: the rows left are far fewer, and the view comes back within them, where it stood past the last of
    /// them, blank, gutter and all, until scrolled.</summary>
    [AvaloniaFact]
    public void Collapsing_with_the_view_wheeled_down_past_where_the_folded_rows_end_draws_them()
    {
        StringBuilder text = new("class Many\n{\n");
        for (int i = 0; i < 60; i++)
        {
            text.Append($"    int Method{i}()\n    {{\n        int value = {i};\n        return value;\n    }}\n\n");
        }
        text.Append("}\n");
        string path = Path.Combine(folder, "Many.cs");
        File.WriteAllText(path, text.ToString());
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => window.EditorArea.Active!.FoldsFound && editor.FoldRegions.Count == 61, "the file's regions");
        editor.Focus();
        foreach (string collapse in new[] { EditorActions.FoldAll, EditorActions.FoldToDefinitions })
        {
            Point middle = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(middle, new Vector(0, -1000));
            Ui.Settle();
            Assert.True(editor.ScrollOffset.Y >= (editor.RowsShown - 2) * editor.LineHeight, $"The wheel left the view {editor.ScrollOffset.Y / editor.LineHeight:0.#} rows down a text of {editor.RowsShown}");
            Assert.Equal(0, editor.Selections.Primary.Caret);
            Ui.Run(window, collapse);
            Assert.Contains(editor.FoldRegions, r => r.Folded);
            Ui.DrawsText(window, editor, $"{window.Actions[collapse]!.Title} with the view wheeled down to the end");
            Ui.Run(window, EditorActions.UnfoldAll);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_file_folds_by_its_elements_and_a_JSON_file_by_its_braces_and_typing_keeps_their_folds()
    {
        string project = Path.Combine(folder, "Sample.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- The build:
                   an executable. -->
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <Description>Shapes &amp; their "areas" > 0</Description>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Serilog"
                                  Version="4.0.0" />
                <Compile Include="Shapes.cs" Label="a > b" />
              </ItemGroup>
            </Project>

            """);
        // No C# language server, which folds nothing here and would restore the project's package.
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700, languageServer: false);
        window.OpenFile(project);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.FoldsFound, "the project file's regions");
        // Each element and comment over several lines folds after its first line's end, its end tag in its placeholder.
        Assert.Equal([(0, "...</Project>"), (1, "...-->"), (3, "...</PropertyGroup>"), (8, "...</ItemGroup>"), (9, "... />")], Regions(editor));
        Assert.All(editor.FoldRegions, r => Assert.Equal(editor.Document.Snapshot.GetLine(editor.Document.Snapshot.GetLineFromOffset(r.Start)).End, r.Start));

        editor.Focus();
        editor.GoTo(new TextPosition(11, 8));
        Ui.Press(window, "control M , control S");
        Assert.Equal("...</ItemGroup>", Assert.Single(editor.FoldRegions, r => r.Folded).Placeholder);
        Assert.Equal(editor.PointOf(editor.Document.Snapshot.GetLineStart(8)).Y + editor.LineHeight, editor.PointOf(editor.Document.Snapshot.GetLineStart(13)).Y, 1);
        editor.GoTo(new TextPosition(4, editor.Document.Snapshot.GetLine(4).Length));
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<Nullable>");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("enable</Nullable>");
        Ui.Settle();
        Assert.Contains("<Nullable>\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal([(0, "...</Project>"), (1, "...-->"), (3, "...</PropertyGroup>"), (5, "...</Nullable>"), (10, "...</ItemGroup>"), (11, "... />")], Regions(editor));
        Assert.Equal("...</ItemGroup>", Assert.Single(editor.FoldRegions, r => r.Folded).Placeholder);

        string json = Path.Combine(folder, "settings.json");
        File.WriteAllText(json, """
            {
              "name": "sample",
              "brace": "{ [ in a string \" ]",
              "scripts": {
                "build": "dotnet build",
                "test": "dotnet test"
              },
              "files": [
                "a.cs",
                "b.cs"
              ],
              /* A comment
                 over two lines. */
              "inline": { "a": [1, 2] }
            }

            """);
        window.OpenFile(json);
        Ui.Settle();
        Passenger.App.Workspace.OpenDocument settings = window.EditorArea.Active!;
        Ui.WaitFor(() => settings.FoldsFound, "the JSON file's regions");
        Assert.Equal([(0, "...}"), (3, "...}"), (7, "...]"), (11, "...*/")], Regions(editor));

        editor.GoTo(new TextPosition(4, 8));
        Ui.Press(window, "control M , control S");
        Assert.Equal("...}", Assert.Single(editor.FoldRegions, r => r.Folded).Placeholder);
        editor.GoTo(new TextPosition(1, editor.Document.Snapshot.GetLine(1).Length));
        Ui.Press(window, "ENTER");
        window.KeyTextInput("\"version\": [1,");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("2],");
        Ui.Settle();
        Assert.Equal([(0, "...}"), (2, "...]"), (5, "...}"), (9, "...]"), (13, "...*/")], Regions(editor));
        Assert.Equal(5, editor.Document.Snapshot.GetLineFromOffset(Assert.Single(editor.FoldRegions, r => r.Folded).Start));
        window.Close();
    }

    /// <summary>XML has no parse of its own, so a key scans the whole file again, off the UI thread: in 20,000 lines each
    /// key costs one scan, which reads the rope in place and allocates the list of the regions it finds and nothing more
    /// (no copy of the text, no string for a tag), and after each edit the folds are those of the same text opened
    /// afresh.</summary>
    [AvaloniaFact]
    public void Typing_in_twenty_thousand_lines_of_XML_scans_it_off_the_UI_thread_and_its_folds_stay_those_of_the_whole_text()
    {
        StringBuilder xml = new("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
        for (int i = 0; i < 1000; i++)
        {
            xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"  <!-- Group {i}:\n       its items. -->\n  <ItemGroup Condition=\"'$(Group)' == '{i}' &gt; 0\">\n");
            for (int j = 0; j < 10; j++)
            {
                xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"    <Compile Include=\"File{i}_{j}.cs\" Label=\"a > b\" />\n");
            }
            xml.Append(System.Globalization.CultureInfo.InvariantCulture, $"    <PackageReference Include=\"Package{i}\"\n                      Version=\"1.0.{i}\" />\n");
            xml.Append("    <None Update=\"x.json\">\n      <CopyToOutputDirectory>Always</CopyToOutputDirectory>\n    </None>\n  </ItemGroup>\n\n");
        }
        string source = xml.Append("</Project>\n").ToString();
        Assert.True(source.Count(c => c == '\n') > 20_000);
        string path = Path.Combine(folder, "Large.props");
        File.WriteAllText(path, source);
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        Passenger.Languages.Markup.FoldScan scan = (Passenger.Languages.Markup.FoldScan)document.FoldFinder!;
        Ui.WaitFor(() => document.FoldRegions.Count == 4001, "the file's regions");
        (long opened, int scans, TimeSpan time) = scan.Worked;
        Assert.Equal(1, scans);
        int fresh = 0;

        void SameAsOpenedAfresh(string after)
        {
            string again = Path.Combine(folder, $"Fresh{++fresh}.props");
            File.WriteAllText(again, document.Text.Snapshot.ToString());
            window.OpenFile(again);
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument opened = window.EditorArea.Active!;
            Ui.WaitFor(() => opened.FoldsFound, "the file opened afresh's regions");
            string Listed(Passenger.App.Workspace.OpenDocument of) => string.Join(", ", of.FoldRegions.Select(r => $"{r.Start}-{r.End} {r.Placeholder}"));
            string expected = Listed(opened);
            string actual = Listed(document);
            Assert.True(expected == actual, $"After {after} the folds are not those of the same text opened afresh");
            window.EditorArea.Close(opened);
            window.EditorArea.Activate(document);
            Ui.Settle();
        }

        int middle = source.IndexOf("File500_3.cs", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(middle);
        editor.Focus();
        const int Keys = 10;
        for (int i = 0; i < Keys; i++)
        {
            window.KeyTextInput("x");
            Ui.Settle();
        }
        (long typed, int typedScans, TimeSpan typedTime) = scan.Worked;
        Assert.Equal(1 + Keys, typedScans);
        long each = (typed - opened) / Keys;
        int regions = document.FoldRegions.Count;
        Assert.True(each < (regions * 32) + 16_384,
            $"A key's scan allocated {each / 1024:N0} KB for {regions:N0} regions of 24 bytes in {source.Length / 1024:N0} K characters: more than the regions it found");
        SameAsOpenedAfresh("keys typed in an attribute");

        int after = document.Text.Snapshot.ToString().IndexOf("    <None Update", middle, StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(after);
        window.KeyTextInput("<Content Include=\"a\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("</Content>");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal(4002, document.FoldRegions.Count);
        SameAsOpenedAfresh("an element typed in");
        string text = document.Text.Snapshot.ToString();
        int end = text.IndexOf("  </ItemGroup>", after, StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(new Selection(end, end + "  </ItemGroup>".Length));
        Ui.Press(window, "BACK");
        Assert.Equal(4001, document.FoldRegions.Count);
        SameAsOpenedAfresh("an end tag deleted");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Opening scan of {source.Length / 1024:N0} K characters, {source.Count(c => c == '\n'):N0} lines: {time.TotalMilliseconds:F1} ms, {opened / 1024:N0} KB; "
            + $"a key's scan: {(typedTime - time).TotalMilliseconds / Keys:F1} ms, {each / 1024:N0} KB.");
        window.Close();
    }

    /// <summary>A window that keeps its state in a folder of its own, over the test's folder.</summary>
    private MainWindow Keeping(string settings)
    {
        MainWindow window = new() { Width = 1000, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private static FoldRegion[] Folded(CodeEditor editor) => [.. editor.FoldRegions.Where(r => r.Folded)];

    /// <summary>The regions by the line each starts on and its placeholder.</summary>
    private static (int Line, string Placeholder)[] Regions(CodeEditor editor) =>
        [.. editor.FoldRegions.Select(r => (editor.Document.Snapshot.GetLineFromOffset(r.Start), r.Placeholder))];

    /// <summary>A file with every kind of C# region: a header, usings, a namespace, doc comments, a #region, a method with a
    /// lambda's block, and a property's accessors.</summary>
    private const string Defined =
        "// Shapes.\n// MIT licence.\nusing System;\nusing System.Linq;\n\nnamespace Sample\n{\n    /// <summary>\n    /// Shapes.\n    /// </summary>\n"
        + "    class Shapes\n    {\n        private int size;\n        #region Sizes\n        /// <summary>\n        /// The area.\n        /// </summary>\n"
        + "        int Area(int w, int h)\n        {\n            Func<int, int> twice = x =>\n            {\n                return x * 2;\n            };\n"
        + "            return twice(w * h);\n        }\n\n        int Size\n        {\n            get\n            {\n                return size;\n            }\n"
        + "        }\n        #endregion\n    }\n}\n";

    /// <summary>Members of every kind that folds, with a doc comment each, around 120 lines a member.</summary>
    private static string Members(int count)
    {
        StringBuilder text = new("using System;\nusing System.Collections.Generic;\nusing System.Linq;\n\nnamespace Long\n{\n    /// <summary>\n    /// Members of every kind that folds.\n    /// </summary>\n    public class Members\n    {\n        private static int counter;\n\n");
        for (int i = 0; i < count; i++)
        {
            if (i % 20 == 0)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"        #region Group {i / 20}\n");
            }
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"        /// <summary>\n        /// Method {i}.\n        /// </summary>\n        public static int Method{i}(int seed)\n        {{\n            int value0 = seed + {i};\n");
            text.Append("            Func<int, int> twice = x =>\n            {\n                return x * 2;\n            };\n");
            for (int j = 1; j < 90; j++)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"            int value{j} = value{j - 1} * 3 + \"{i}:{j}\".Length;\n");
            }
            text.Append("            /* A block comment\n               over two lines. */\n            int Local(int a)\n            {\n                return a + value0;\n            }\n");
            text.Append("            return Local(value89);\n        }\n\n");
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"        public int Property{i}\n        {{\n            get\n            {{\n                return counter;\n            }}\n            set {{ counter = value; }}\n        }}\n\n");
            if (i % 20 == 19)
            {
                text.Append("        #endregion\n\n");
            }
        }
        string source = text.Append("    }\n}\n").ToString();
        Assert.True(source.Count(c => c == '\n') > 20_000);
        return source;
    }

    /// <summary>Types ten letters below the folded lines and checks what each frame asked to have coloured, which must
    /// never reach into the hidden lines while they are folded; the characters asked for each key.</summary>
    private static long ColouredPerKey(EditorRig rig, Asking asking, int caret, int hiddenStart, int hiddenEnd, bool folded)
    {
        rig.PlaceCaret(caret);
        // From the top, so the lines above the fold show as well as those below it.
        rig.Editor.ScrollTo(default);
        Ui.Settle();
        asking.Asked.Clear();
        const int Keys = 10;
        for (int i = 0; i < Keys; i++)
        {
            rig.Type("z");
        }
        if (folded)
        {
            foreach ((int start, int length) in asking.Asked)
            {
                Assert.True(start + length <= hiddenStart || start >= hiddenEnd,
                    $"Colours were asked for {start}..{start + length}, which reaches into the {hiddenEnd - hiddenStart} folded characters at {hiddenStart}.");
            }
        }
        return asking.Asked.Sum(a => (long)a.Length) / Keys;
    }

    /// <summary>A highlighter that notes each range it is asked to colour.</summary>
    private sealed class Asking(IHighlighter inner) : IHighlighter
    {
        public List<(int Start, int Length)> Asked { get; } = [];

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot text, int start, int length)
        {
            Asked.Add((start, length));
            return inner.GetSpans(text, start, length);
        }
    }
}
