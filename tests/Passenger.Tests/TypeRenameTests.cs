using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Rename of a type with its file, both ways, on a real project in a Git working tree: Rename on a class named
/// like its file renames the file too (Also rename files to reflect this change, on until turned off), its tab following,
/// its uses changing and Git seeing a rename; Rename on the file in the Solution view offers to rename the class with it;
/// a file named otherwise keeps its name; and Undo takes the whole rename back at once, asked first.</summary>
public sealed class TypeRenameTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string Order = "namespace Shop;\n\npublic class Order\n{\n    public int Total;\n}\n";

    private const string Till = "namespace Shop;\n\npublic static class Till\n{\n    public static Order Open() => new Order();\n\n    public static int Sum(Order order) => order.Total;\n}\n";

    private const string Shapes = "namespace Shop;\n\npublic class Circle\n{\n}\n\npublic class Square\n{\n    public Circle Inside() => new Circle();\n}\n";

    private readonly string folder;

    public TypeRenameTests(RestoredFolders folders) => folder = PathNames.Canonical(folders.Take("type-rename-", made =>
    {
        string project = PathNames.Canonical(made);
        TestFolders.Write(project, "Shop.csproj", TestProjects.Library());
        TestFolders.Write(project, ".gitignore", "bin/\nobj/\n");
        TestFolders.Write(project, "Order.cs", Order);
        TestFolders.Write(project, "Till.cs", Till);
        TestFolders.Write(project, "Shapes.cs", Shapes);
        // The files a test renames are the project's from the start: a rename waits for its server to read one written a moment before.
        TestFolders.Write(project, "Form1.cs", "namespace Shop;\n\npublic partial class Form1\n{\n    public int Width;\n}\n");
        TestFolders.Write(project, "Form1.Designer.cs", "namespace Shop;\n\npublic partial class Form1\n{\n    public int Height;\n}\n");
        TestFolders.Write(project, "Form1.resx", "<root />\n");
        TestFolders.Write(project, "Basket.cs", "namespace Shop;\n\npublic partial class Basket\n{\n    public int Count;\n}\n");
        TestFolders.Write(project, "Basket.Lines.cs", "namespace Shop;\n\npublic partial class Basket\n{\n    public int Lines;\n}\n");
        TestFolders.Write(project, "BasketExtras.cs", "namespace Shop;\n\npublic partial class Basket\n{\n    public int Extras;\n}\n");
        TestGit.Init(project);
        return project;
    }));

    public void Dispose() => TestFolders.Delete(folder);

    private string OrderPath => Path.Combine(folder, "Order.cs");

    private string PurchasePath => Path.Combine(folder, "Purchase.cs");

    /// <summary>Rename on a class named like its file renames the file with it, its tab, breakpoint and bookmark following,
    /// its uses changing and Git seeing a rename; then Undo in the editor takes back what was typed after it on its own,
    /// and the next Undo the class, its file and the other file together, asked first, and Redo makes it all again.</summary>
    [Gate]
    [AvaloniaFact]
    public void Renaming_a_class_named_like_its_file_renames_the_file_Git_sees_a_rename_and_undo_and_redo_take_it_back_whole()
    {
        string till = Path.Combine(folder, "Till.cs");
        MainWindow window = Open("Order.cs");
        List<(string Title, string Text)> asked = Answer(window);
        OpenDocument document = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Total;");
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Run(window, AppActions.ToggleBookmark);

        RenameAt(window, "class Ord", "Purchase");

        Assert.False(File.Exists(OrderPath), $"Order.cs is still there after its class was renamed: {window.Status}");
        Assert.True(File.Exists(PurchasePath), $"Order.cs was not renamed with its class: {window.Status}");
        Assert.True(ReferenceEquals(document, Assert.Single(window.EditorArea.Documents)), "The renamed file's tab was closed and opened again");
        Assert.Equal(PurchasePath, document.Path);
        Assert.Equal("Purchase.cs", window.EditorArea.Active?.Name);
        Assert.Equal(4, Assert.Single(window.Debugger.Breakpoints.In(PurchasePath)).Line);
        Assert.Empty(window.Debugger.Breakpoints.In(OrderPath));
        Assert.Equal(PurchasePath, Assert.Single(window.Bookmarks.All).Path);
        Assert.Equal(Order.Replace("Order", "Purchase", StringComparison.Ordinal), File.ReadAllText(PurchasePath));
        Assert.Equal(Till.Replace("Order", "Purchase", StringComparison.Ordinal), File.ReadAllText(till));

        Ui.WaitFor(() => window.Vcs.Changes.Any(c => c.Kind == ChangeKind.Renamed && c.Path == PurchasePath && c.From == OrderPath), "Git to list Order.cs renamed to Purchase.cs");
        string status = Git("status", "--porcelain");
        Assert.True(status.Split('\n').Any(l => l.StartsWith('R') && l.EndsWith("Order.cs -> Purchase.cs", StringComparison.Ordinal)), $"git status shows no rename:\n{status}");

        editor.Focus();
        Ui.PlaceAfter(editor, "Total;");
        window.KeyTextInput(" // kept");
        Ui.Run(window, EditorActions.Undo);
        Assert.Empty(asked);
        Assert.Equal(Order.Replace("Order", "Purchase", StringComparison.Ordinal), document.Text.Snapshot.ToString());

        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Rename Order to Purchase? It changes Purchase.cs and Till.cs.")], asked);
        Assert.True(File.Exists(OrderPath), $"Undo did not rename Purchase.cs back: {window.Status}");
        Assert.False(File.Exists(PurchasePath), "Undo left Purchase.cs");
        Assert.Equal(OrderPath, document.Path);
        Assert.Equal("Order.cs", window.EditorArea.Active?.Name);
        Assert.Equal(Order, document.Text.Snapshot.ToString());
        Assert.Equal(Till, File.ReadAllText(till));
        window.EditorArea.SaveAll();
        _ = window.Vcs.RefreshAsync();
        Ui.WaitFor(() => window.Vcs.Changes.Count == 0, "Git to list nothing changed once the rename was undone");
        Assert.Equal(string.Empty, Git("status", "--porcelain"));

        Ui.Run(window, EditorActions.Redo);
        Assert.Equal(("Redo", "Redo Rename Order to Purchase? It changes Order.cs and Till.cs."), asked[^1]);
        Assert.True(File.Exists(PurchasePath), $"Redo did not rename Order.cs again: {window.Status}");
        Assert.Equal(PurchasePath, document.Path);
        Assert.Equal(Order.Replace("Order", "Purchase", StringComparison.Ordinal), document.Text.Snapshot.ToString());
        Assert.Equal(Till.Replace("Order", "Purchase", StringComparison.Ordinal), File.ReadAllText(till));
        window.Close();
    }

    /// <summary>Every part of a partial class in a file named after it is renamed with it, a part named otherwise is not;
    /// then, with Also rename files to reflect this change turned off, only the code is renamed, and the option stays off
    /// for the next rename.</summary>
    [Gate]
    [AvaloniaFact]
    public void Each_partial_part_named_after_its_class_is_renamed_with_it_and_with_the_file_option_turned_off_only_the_code_is_and_it_stays_off()
    {
        MainWindow window = Open("Basket.Lines.cs");

        IReadOnlyList<string> offered = RenameAt(window, "class Bask", "Cart");

        Assert.Equal(["Basket.Lines.cs", "Basket.cs"], offered.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(folder, "Cart.cs")), $"Basket.cs was not renamed with its class: {window.Status}");
        Assert.True(File.Exists(Path.Combine(folder, "Cart.Lines.cs")), $"Basket.Lines.cs was not renamed with its class: {window.Status}");
        Assert.Equal("Cart.Lines.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public partial class Cart", File.ReadAllText(Path.Combine(folder, "BasketExtras.cs")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, "Basket.cs")), "Basket.cs is still there");

        window.OpenFile(OrderPath);
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Ord");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.RenameOffersFiles.Count > 0, "the rename to offer Order.cs");
        CheckBox option = Option(window, "Also rename files to reflect this change");
        Assert.True(option.IsChecked, "Renaming a class named like its file does not rename the file unless asked, as Rider's does");
        Ui.Click(option);
        Assert.False(option.IsChecked, "A click did not clear Also rename files to reflect this change");
        window.KeyTextInput("Purchase");
        Ui.Press(window, "ENTER");
        Ui.Settle();

        Assert.False(File.Exists(PurchasePath), "The file was renamed with the option turned off");
        Assert.Equal("Order.cs", window.EditorArea.Active?.Name);
        Assert.Equal(Order.Replace("class Order", "class Purchase", StringComparison.Ordinal), window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(Till.Replace("Order", "Purchase", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "Till.cs")));

        window.OpenFile(Path.Combine(folder, "Till.cs"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Ti");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.RenameOffersFiles.Count > 0, "the rename to offer Till.cs");
        Assert.False(Option(window, "Also rename files to reflect this change").IsChecked, "The file option turned off came back on");
        Ui.Press(window, "ESCAPE");
        Assert.Null(window.Assist.Renaming);
        window.Close();
    }

    /// <summary>Rename on a file in the Solution view offers to rename the class it is named after, which renames it and its
    /// uses, every row but the file's own staying the row it was, and Undo there takes both back, asked first; a form's
    /// files nested under it go along; and a file holding types
    /// of other names keeps its name when one of them is renamed, is renamed alone in the Solution view, and named back by
    /// Undo there.</summary>
    [AvaloniaFact]
    public void Renaming_a_file_in_the_solution_view_offers_to_rename_its_class_takes_a_forms_nested_files_and_renames_a_file_of_other_types_alone()
    {
        MainWindow window = Open("Till.cs");
        List<(string Title, string Text)> asked = Answer(window);
        Choose(window, OrderPath);
        Ui.Run(window, AppActions.Rename);
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        Assert.Equal("Order.cs", popup.Text);
        CheckBox option = popup.Option ?? throw new Xunit.Sdk.XunitException("Renaming Order.cs did not offer to rename the class Order");
        Assert.Equal("Rename type 'Order' to match the new file name", option.Content);
        Assert.True(option.IsChecked, "Rider renames the type with its file unless asked not to");
        window.KeyTextInput("Purchase.cs");
        FilesFollowed.Check(window, "Renaming Order.cs with its class", [OrderPath, PurchasePath], () =>
        {
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        });

        Assert.False(File.Exists(OrderPath), "Order.cs is still there");
        Assert.Equal(Order.Replace("Order", "Purchase", StringComparison.Ordinal), File.ReadAllText(PurchasePath));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(Till.Replace("Order", "Purchase", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Path == PurchasePath);
        Ui.WaitFor(() => window.Vcs.Changes.Any(c => c.Kind == ChangeKind.Renamed && c.Path == PurchasePath), "Git to list Order.cs renamed");

        Choose(window, PurchasePath);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Rename Order to Purchase? It changes Till.cs and Purchase.cs.")], asked);
        Assert.True(File.Exists(OrderPath), $"Undo did not rename Purchase.cs back: {window.Status}");
        Assert.False(File.Exists(PurchasePath), "Undo left Purchase.cs");
        Assert.Equal(Order, File.ReadAllText(OrderPath));
        Assert.Equal(Till, editor.Document.Snapshot.ToString());

        Choose(window, Path.Combine(folder, "Form1.cs"));
        Ui.Run(window, AppActions.Rename);
        popup = Assert.IsType<NamePopup>(window.Popup);
        Assert.True(popup.Option?.IsChecked == true, "Renaming Form1.cs did not offer to rename the class Form1 with it");
        window.KeyTextInput("Window1.cs");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        Assert.Equal(["Window1.Designer.cs", "Window1.cs", "Window1.resx"], Directory.GetFiles(folder, "Window1*").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(folder, "Form1*"));
        Assert.Contains("public partial class Window1", File.ReadAllText(Path.Combine(folder, "Window1.Designer.cs")), StringComparison.Ordinal);

        string shapes = Path.Combine(folder, "Shapes.cs");
        string figures = Path.Combine(folder, "Figures.cs");
        window.OpenFile(shapes);
        Ui.Settle();
        RenameAt(window, "class Circ", "Ring", offered: false);
        Assert.True(File.Exists(shapes), "Shapes.cs was renamed with a class it is not named after");
        Assert.Contains("public class Ring", window.EditorArea.Active?.Text.Snapshot.ToString(), StringComparison.Ordinal);

        window.EditorArea.SaveAll();
        Choose(window, shapes);
        Ui.Run(window, AppActions.Rename);
        popup = Assert.IsType<NamePopup>(window.Popup);
        Assert.Null(popup.Option);
        window.KeyTextInput("Figures.cs");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        Assert.False(File.Exists(shapes), "Shapes.cs is still there");
        Assert.Equal(Shapes.Replace("Circle", "Ring", StringComparison.Ordinal), File.ReadAllText(figures));
        Assert.Equal("Figures.cs", window.EditorArea.Active?.Name);

        asked.Clear();
        Choose(window, figures);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Rename Shapes.cs to Figures.cs? It changes Figures.cs.")], asked);
        Assert.True(File.Exists(shapes), $"Undo did not name Figures.cs back: {window.Status}");
        Assert.Equal("Shapes.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    /// <summary>A line typed at the top of a class's file while the class's rename is out goes in above it, and the rename
    /// still lands on the class: its declaration and uses renamed and its file renamed with it, where the place the rename
    /// was asked at, and the places the server named the class's files by, no longer named the class.</summary>
    [AvaloniaFact]
    public void A_line_typed_in_a_classs_file_while_its_rename_is_out_still_renames_the_class_and_its_file()
    {
        MainWindow window = Open("Order.cs");
        OpenDocument document = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "class Ord");
        editor.Focus();
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.RenameOffersFiles.Count > 0, "the rename to offer Order.cs");
        window.KeyTextInput("Purchase");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        editor.ApplyEdits([new TextEdit(0, 0, "// typed meanwhile\n")]);
        Ui.Settle();

        Assert.True(File.Exists(PurchasePath), $"Order.cs was not renamed with its class, a line typed meanwhile: {window.Status}");
        Assert.False(File.Exists(OrderPath), "Order.cs is still there");
        Assert.Equal(PurchasePath, document.Path);
        Assert.Equal("// typed meanwhile\n" + Order.Replace("Order", "Purchase", StringComparison.Ordinal), document.Text.Snapshot.ToString());
        Assert.Equal(Till.Replace("Order", "Purchase", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "Till.cs")));
        window.Close();
    }

    /// <summary>A rename reads and writes the closed files it changes off the window's thread: renaming a method used in
    /// three closed files of 2 MB each costs that thread no more than renaming one used in three small ones, where a read of
    /// one of them there costs at least its bytes; and the rename is still one step of Undo, which puts every file back.</summary>
    [AvaloniaFact]
    public void A_rename_across_large_closed_files_costs_the_window_thread_what_one_across_small_ones_does_and_undo_takes_it_back_whole()
    {
        const int Large = 2 * 1024 * 1024;
        const string Filler = "// a line of filler, with nothing in it to rename\n";
        string filler = string.Concat(Enumerable.Repeat(Filler, Large / Filler.Length + 1));
        TestFolders.Write(folder, "Tally.cs", "namespace Shop;\n\npublic static class Tally\n{\n    public static int Few(int value) => value;\n\n    public static int Many(int value) => value;\n}\n");
        List<string> written = [Path.Combine(folder, "Tally.cs")];
        for (int i = 0; i < 3; i++)
        {
            written.Add(TestFolders.Write(folder, $"Small{i}.cs", $"namespace Shop;\n\npublic static class Small{i}\n{{\n    public static int Use() => Tally.Few({i});\n}}\n"));
            written.Add(TestFolders.Write(folder, $"Large{i}.cs", $"namespace Shop;\n\npublic static class Large{i}\n{{\n    public static int Use() => Tally.Many({i});\n}}\n" + filler));
        }
        // Long written: a rename waits for its server to read a file written a moment before, which the measure would count.
        foreach (string path in written)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        }
        string large = Path.Combine(folder, "Large0.cs");
        string before = File.ReadAllText(large);
        MainWindow window = Open("Tally.cs");
        List<(string Title, string Text)> asked = Answer(window);
        try
        {
            long few = Cost("int Fe", "Fewer");
            long many = Cost("int Ma", "More");
            Assert.True(many < few + Large,
                $"A rename across three closed files of 2 MB allocated {many / 1024} KB on the window's thread, across three small ones {few / 1024} KB: the files are read or written there, freezing the window as they grow");
            Assert.Equal(before.Replace("Tally.Many(", "Tally.More(", StringComparison.Ordinal), File.ReadAllText(large));
            Assert.Contains("Tally.Fewer(1)", File.ReadAllText(Path.Combine(folder, "Small1.cs")), StringComparison.Ordinal);

            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => window.Status == "Undone: Rename Many to More", () => $"Undo of the rename across the large files; the status says '{window.Status}'");
            (string title, string text) = Assert.Single(asked);
            Assert.Equal("Undo", title);
            Assert.True(text.StartsWith("Undo Rename Many to More? It changes ", StringComparison.Ordinal) && new[] { "Large0.cs", "Large1.cs", "Large2.cs", "Tally.cs" }.All(n => text.Contains(n, StringComparison.Ordinal)),
                $"Undo asked '{text}', not once for the whole rename");
            Assert.Equal(before, File.ReadAllText(large));
            Assert.Contains("Tally.Fewer(1)", File.ReadAllText(Path.Combine(folder, "Small1.cs")), StringComparison.Ordinal);
            Assert.Contains("int Many(int value)", window.EditorArea.Active?.Text.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.Contains("int Fewer(int value)", window.EditorArea.Active?.Text.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }

        // What renaming the method at the end of a piece of Tally.cs allocated on the window's thread, from Enter till done.
        long Cost(string after, string name)
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
            window.EditorArea.CodeEditor.Focus();
            Ui.Run(window, AppActions.Rename);
            Ui.Settle();
            Assert.True(window.Assist.Renaming is not null, "Rename opened no box");
            window.KeyTextInput(name);
            long start = GC.GetAllocatedBytesForCurrentThread();
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Status == "Renamed in 4 files", () => $"the rename to {name}; the status says '{window.Status}'");
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
    }

    /// <summary>Undo pressed in the open file as soon as a rename across closed files shows there, while those files are
    /// still being written, waits for the rename and then takes all of it back, asked once: the open file's change is
    /// never undone on its own, which would leave the rename's Undo refused and the solution half renamed.</summary>
    [AvaloniaFact]
    public void Undo_pressed_while_a_rename_writes_its_closed_files_waits_for_it_and_takes_the_whole_rename_back()
    {
        IReadOnlyList<string> uses = WriteTally(6);
        MainWindow window = Open("Tally.cs");
        List<(string Title, string Text)> asked = Answer(window);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task held = window.EditorArea.Commands.HoldNextWrite(release.Task);
            OpenRenameBox(window, "int Ma", "More");
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => held.IsCompleted, "the rename's writes of its closed files to be held");
            Assert.Contains("int More(int value)", Text(window), StringComparison.Ordinal);

            Ui.Run(window, EditorActions.Undo, settle: false);
            Ui.Hold(TimeSpan.FromMilliseconds(300), "a moment in which Undo would take the open file's step on its own");
            Assert.True(Text(window).Contains("int More(int value)", StringComparison.Ordinal),
                $"Undo took the open file's part of the rename on its own while its closed files were written: {window.Status}");
            release.SetResult();
            Ui.WaitFor(() => window.Status == "Undone: Rename Many to More", () => $"Undo of the whole rename once it was made; the status says '{window.Status}'");
            (string title, string text) = Assert.Single(asked);
            Assert.Equal("Undo", title);
            Assert.StartsWith("Undo Rename Many to More? It changes ", text, StringComparison.Ordinal);
            Assert.Contains("int Many(int value)", Text(window), StringComparison.Ordinal);
            foreach (string use in uses)
            {
                Assert.True(File.ReadAllText(use).Contains("Tally.Many(", StringComparison.Ordinal), $"{Path.GetFileName(use)} kept the rename after its Undo: {File.ReadAllText(use)}");
            }
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }

    /// <summary>A rename asked for while an Undo's writes of closed files are held waits for them, then is worked out on the
    /// files as Undo left them: never made on the text before the Undo, nor only in the open file while Undo puts the
    /// closed files back, either of which leaves the solution half renamed.</summary>
    [AvaloniaFact]
    public void A_rename_asked_for_while_an_undos_writes_are_held_waits_for_them_and_renames_every_use_as_undo_left_it()
    {
        IReadOnlyList<string> uses = WriteTally(4);
        MainWindow window = Open("Tally.cs");
        Answer(window);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            RenameAt(window, "int Ma", "More", offered: false);
            Assert.True(window.Status == "Renamed in 5 files", $"The first rename: {window.Status}");
            Task held = window.EditorArea.Commands.HoldNextWrite(release.Task);
            Ui.Run(window, EditorActions.Undo, settle: false);
            Ui.PumpUntil(() => held.IsCompleted, "Undo's writes of the closed files to be held");
            Assert.Contains("int Many(int value)", Text(window), StringComparison.Ordinal);

            OpenRenameBox(window, "int Ma", "Most");
            Ui.Press(window, "ENTER", settle: false);
            Ui.Hold(TimeSpan.FromMilliseconds(500), "a moment in which the rename would be asked of the files before Undo wrote them");
            release.SetResult();
            Ui.WaitFor(() => window.Status == "Renamed in 5 files" || window.Status?.StartsWith("Rename was not applied", StringComparison.Ordinal) == true,
                () => $"the second rename to be made or refused; the status says '{window.Status}'");
            string name = window.Status == "Renamed in 5 files" ? "Most" : "Many";
            Assert.Contains($"int {name}(int value)", Text(window), StringComparison.Ordinal);
            foreach (string use in uses)
            {
                Assert.True(File.ReadAllText(use).Contains($"Tally.{name}(", StringComparison.Ordinal),
                    $"{Path.GetFileName(use)} does not call Tally.{name} as Tally.cs declares it ({window.Status}): {File.ReadAllText(use)}");
            }
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }

    /// <summary>A line typed at the top of a partial class's open file while the Solution view renames the file with its
    /// class goes in above it, and the class's other part named after it is renamed with it all the same.</summary>
    [AvaloniaFact]
    public void A_line_typed_in_a_file_the_solution_view_renames_with_its_class_still_takes_the_classs_other_part_along()
    {
        MainWindow window = Open("Basket.cs");
        OpenDocument document = window.EditorArea.Active!;
        Choose(window, Path.Combine(folder, "Basket.cs"));
        Ui.Run(window, AppActions.Rename);
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        Assert.True(popup.Option?.IsChecked == true, "Renaming Basket.cs did not offer to rename the class Basket with it");
        window.KeyTextInput("Cart.cs");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// typed meanwhile\n")]);
        Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        Ui.Settle();

        Assert.Equal(["Cart.Lines.cs", "Cart.cs"], Directory.GetFiles(folder, "Cart*").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(folder, "Basket.*"));
        Assert.Equal(Path.Combine(folder, "Cart.cs"), document.Path);
        Assert.StartsWith("// typed meanwhile\nnamespace Shop;\n\npublic partial class Cart\n", document.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("public partial class Cart", File.ReadAllText(Path.Combine(folder, "BasketExtras.cs")), StringComparison.Ordinal);
        window.Close();
    }

    private MainWindow Open(string file)
    {
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        window.OpenFile(Path.Combine(folder, file));
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        return window;
    }

    /// <summary>Tally.cs declaring Few and Many, and closed files each calling Tally.Many, long written: a rename refuses a
    /// file written a moment before, which its server may not have read. The closed files.</summary>
    private IReadOnlyList<string> WriteTally(int count)
    {
        List<string> uses = [];
        string tally = TestFolders.Write(folder, "Tally.cs", "namespace Shop;\n\npublic static class Tally\n{\n    public static int Few(int value) => value;\n\n    public static int Many(int value) => value;\n}\n");
        for (int i = 0; i < count; i++)
        {
            uses.Add(TestFolders.Write(folder, $"Use{i}.cs", $"namespace Shop;\n\npublic static class Use{i}\n{{\n    public static int Call() => Tally.Many({i});\n}}\n"));
        }
        foreach (string path in uses.Append(tally))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        }
        return uses;
    }

    /// <summary>Rename's box opened at the end of a piece of the active file and the new name typed, Enter left to the
    /// caller; nothing waited for but the box, so writes held meanwhile stay held.</summary>
    private static void OpenRenameBox(MainWindow window, string after, string name)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, AppActions.Rename, settle: false);
        Ui.PumpUntil(() => window.Assist.Renaming is not null, "Rename's box");
        window.KeyTextInput(name);
    }

    private static string Text(MainWindow window) => window.EditorArea.Active?.Text.Snapshot.ToString() ?? string.Empty;

    /// <summary>Rename at the end of a piece of the active file, the new name typed into its box and Enter pressed, once
    /// the box has offered the files named after the type (or, when none is to be, once it has had the chance); the
    /// files it offered.</summary>
    private static IReadOnlyList<string> RenameAt(MainWindow window, string after, string name, bool offered = true)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, AppActions.Rename);
        Ui.Settle();
        Assert.True(window.Assist.Renaming is not null, "Rename opened no box");
        Assert.True(offered == window.Assist.RenameOffersFiles.Count > 0,
            $"The rename {(offered ? "did not offer" : "offered")} to rename files: [{string.Join(", ", window.Assist.RenameOffersFiles)}]");
        IReadOnlyList<string> files = window.Assist.RenameOffersFiles;
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        return files;
    }

    private static CheckBox Option(MainWindow window, string text) =>
        window.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.IsEffectivelyVisible && (string?)c.Content == text)
        ?? throw new Xunit.Sdk.XunitException($"No check box '{text}' shows");

    /// <summary>Clicks a file's row in the Explorer, its folders opened down to it, as a person chooses it before a key.</summary>
    private static void Choose(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        ExplorerNode row = window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
        Ui.ClickRow(window, row);
    }

    /// <summary>Agrees to every question the window asks, keeping each one's title and text in order.</summary>
    private static List<(string Title, string Text)> Answer(MainWindow window)
    {
        List<(string, string)> asked = [];
        window.Confirm = (title, text, _) =>
        {
            asked.Add((title, text));
            return Task.FromResult(true);
        };
        return asked;
    }

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}
