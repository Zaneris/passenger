using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Docking;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Terminal;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The Solution view's file commands: Cut, Copy and Paste by their keys and its Edit menu, and drags, move or copy
/// files and folders into another folder or another project's, an open file's tab, undo and edits going with it; Add,
/// Reference... adds and takes out a project reference, however the others are laid out, which the Dependencies, the
/// build and the language server follow, the rest of the project file as it was; Add, Class/Interface... writes each kind
/// of type; Open In, Terminal starts a shell in the folder; and Tools' Compare shows two files' differences.</summary>
public sealed class SolutionViewTests : IDisposable
{
    private static readonly string Library = TestProjects.Library();

    /// <summary>As Visual Studio writes one: a byte order mark, CRLF, and a blank line between its groups.</summary>
    private const string Executable = $"﻿<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <OutputType>Exe</OutputType>\r\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";

    private const string Order = "namespace Shop.App.Models;\n\npublic class Order\n{\n    public int Total;\n}\n";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("solution-view-"));
    private readonly string trash = TestFolders.Create("trash-");

    public SolutionViewTests()
    {
        Write("Shop.App/Shop.App.csproj", Executable);
        Write("Shop.App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        Write("Shop.App/Models/Order.cs", Order);
        Write("Shop.App/Models/Lines/Line.cs", "namespace Shop.App.Models.Lines;\n\npublic class Line\n{\n}\n");
        Write("Shop.Core/Shop.Core.csproj", Library);
        Write("Shop.Core/Catalog/Product.cs", "namespace Shop.Core.Catalog;\n\npublic class Product\n{\n}\n");
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n</Solution>\n");
        Trash.Folder = trash;
    }

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    [Gate]
    [AvaloniaFact]
    public void A_file_cut_and_pasted_into_another_projects_folder_moves_there_with_its_open_tab_its_undo_and_its_edit()
    {
        string order = Path.Combine(folder, "Shop.App", "Models", "Order.cs");
        MainWindow window = Open();
        window.Autosaves = false;
        window.OpenFile(order);
        OpenDocument document = Assert.Single(window.EditorArea.Documents);
        window.EditorArea.CodeEditor.GoTo(new TextPosition(4, 21));
        window.KeyTextInput(" // the sum");
        Ui.Settle();

        Ui.ClickRow(window, Show(window, order));
        Ui.Run(window, EditorActions.Cut);
        Assert.True(Show(window, order).IsCut, "The file cut is not dimmed, so nothing shows it waits to be pasted");
        Assert.Equal(order, Ui.Clipboard(window));

        string catalog = Path.Combine(folder, "Shop.Core", "Catalog");
        Ui.ClickRow(window, Show(window, catalog));
        Ui.Run(window, EditorActions.Paste);
        string moved = Path.Combine(catalog, "Order.cs");
        Assert.Equal("Moved Order.cs to Catalog", window.Status);
        Assert.False(File.Exists(order), "The file cut and pasted is still where it was");
        Assert.Equal(Order.Replace("Total;", "Total; // the sum", StringComparison.Ordinal), File.ReadAllText(moved));
        Assert.True(ReferenceEquals(document, Assert.Single(window.EditorArea.Documents)), "The moved file's tab was closed and opened again, losing its undo");
        Assert.Equal(moved, document.Path);
        Assert.Equal("Order.cs", window.EditorArea.Active?.Name);
        ExplorerNode row = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Path == moved);
        Assert.Same(row, window.ExplorerView.Selected);
        Assert.False(row.IsCut);
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Path == order);

        Ui.Run(window, EditorActions.Paste);
        Assert.StartsWith("Nothing to paste", window.Status, StringComparison.Ordinal);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Order, document.Text.Snapshot.ToString());
        window.Close();
    }

    /// <summary>Every file of a folder of 400 chosen and cut allocates on the UI thread, for each file, under twice what every
    /// file of a folder of 40 does, each row dimmed and a file's dependent going with it.</summary>
    [Gate]
    [AvaloniaFact]
    public void Cutting_every_file_of_a_folder_of_four_hundred_costs_each_file_about_what_it_does_in_a_folder_of_forty()
    {
        WriteItems("Few", 40);
        WriteItems("Many", 400);
        MainWindow window = Open();
        long few = CutEvery(window, "Few", 40);
        long many = CutEvery(window, "Many", 400);
        TestContext.Current.TestOutputHelper?.WriteLine($"Cutting each file allocated {few:N0} bytes among 40, {many:N0} among 400.");
        Assert.True(many < few * 2,
            $"Cutting every file of a folder of 400 allocated {many / 1024:N0} KB on the UI thread for each, of a folder of 40 {few / 1024:N0} KB: each file read its whole folder again");
        window.Close();
    }

    /// <summary>A folder of the app's of some files, and a component whose code-behind nests under it.</summary>
    private void WriteItems(string name, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Write($"Shop.App/{name}/Item{i}.cs", $"namespace Shop.App.{name};\n\npublic class Item{i}\n{{\n}}\n");
        }
        Write($"Shop.App/{name}/Card.razor", "<p>Card</p>\n");
        Write($"Shop.App/{name}/Card.razor.cs", $"namespace Shop.App.{name};\n\npublic partial class Card\n{{\n}}\n");
    }

    /// <summary>What choosing every row of a folder and cutting them allocates on the UI thread for each file, every row then
    /// dimmed and the component's code-behind with them.</summary>
    private long CutEvery(MainWindow window, string name, int count)
    {
        string at = Path.Combine(folder, "Shop.App", name);
        Show(window, Path.Combine(at, "Item0.cs"));
        ExplorerNode[] Rows() => [.. window.ExplorerView.Tree.Rows.Where(r => r.Path is { } path && PathNames.IsUnder(path, at))];
        Assert.Equal(count + 1, Rows().Length);
        Ui.ClickRow(window, Rows()[0]);
        Ui.Click(Ui.Row(window.ExplorerView, Rows()[^1]), modifiers: Avalonia.Input.RawInputModifiers.Shift);
        Assert.Equal(count + 1, window.ExplorerView.SelectedNodes.Count);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Ui.Run(window, EditorActions.Cut);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.StartsWith("Cut ", window.Status, StringComparison.Ordinal);
        Assert.All(Rows(), row => Assert.True(row.IsCut, $"{row.Name} was cut but is not dimmed"));
        ExplorerNode card = Assert.Single(Rows(), r => r.Name == "Card.razor");
        Ui.ClickRow(window, card);
        Ui.Press(window, "RIGHT");
        ExplorerNode behind = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Path == Path.Combine(at, "Card.razor.cs"));
        Assert.True(behind.IsCut, "The component was cut but its code-behind, which goes with it, is not dimmed");
        return allocated / count;
    }

    /// <summary>A folder copied and pasted, and files dragged, in one window: what each leaves is checked before the next.</summary>
    [Gate]
    [AvaloniaFact]
    public void A_folder_copied_and_pasted_copies_all_it_holds_named_as_Riders_Duplicate_asks_and_a_file_dragged_moves_or_with_the_copy_key_copies()
    {
        string models = Path.Combine(folder, "Shop.App", "Models");
        MainWindow window = Open();

        Ui.ChooseFromRowMenu(window, Show(window, models), "Edit", "Copy");
        Assert.False(Show(window, models).IsCut, "A copied folder is dimmed as a cut one is");
        Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Core"));
        Ui.Run(window, EditorActions.Paste);
        string copy = Path.Combine(folder, "Shop.Core", "Models");
        Assert.Equal("Copied Models to Shop.Core", window.Status);
        Assert.Equal(Order, File.ReadAllText(Path.Combine(copy, "Order.cs")));
        Assert.True(File.Exists(Path.Combine(copy, "Lines", "Line.cs")), "The folder inside the folder copied was left behind");
        Assert.Equal(Order, File.ReadAllText(Path.Combine(models, "Order.cs")));
        Assert.Same(Row(window, NodeKind.Folder, "Models", copy), window.ExplorerView.Selected);

        Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Project, "Shop.App"), "Edit", "Paste");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        Assert.Equal("Models", popup.Text);
        Ui.Press(window, "ENTER");
        Assert.Equal("Models is already there", popup.Problem);
        popup.Text = string.Empty;
        window.KeyTextInput("Archive");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, $"The copy's name was refused: {popup.Problem}");
        Assert.Equal(Order, File.ReadAllText(Path.Combine(folder, "Shop.App", "Archive", "Order.cs")));
        Assert.True(File.Exists(Path.Combine(folder, "Shop.App", "Archive", "Lines", "Line.cs")), "The duplicate left the folder inside behind");

        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(false);
        };
        File.WriteAllText(Path.Combine(copy, "Order.cs"), "changed\n");
        Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Core"));
        Ui.Run(window, EditorActions.Paste);
        Assert.Equal(["Files Already Exist"], asked);
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(copy, "Order.cs")));

        string order = Path.Combine(folder, "Shop.App", "Models", "Order.cs");
        string catalog = Path.Combine(folder, "Shop.Core", "Catalog");
        window.OpenFile(order);
        Ui.Settle();
        ExplorerNode file = Show(window, order);
        ExplorerNode target = Show(window, catalog);
        DropOverlay overlay = window.ExplorerView.GetVisualDescendants().OfType<DropOverlay>().Single();

        Point from = Ui.Centre(Ui.Row(window.ExplorerView, file), window);
        Point to = Ui.Centre(Ui.Row(window.ExplorerView, target), window);
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
        Assert.True(overlay.IsVisible, "Nothing lit up under the file dragged over the folder");
        window.MouseUp(to, MouseButton.Left);
        Ui.Settle();
        Assert.False(overlay.IsVisible, "The drop place stayed lit after the drop");
        string moved = Path.Combine(catalog, "Order.cs");
        Assert.Equal("Moved Order.cs to Catalog", window.Status);
        Assert.False(File.Exists(order), "The file dragged away is still where it was");
        Assert.Equal(Order, File.ReadAllText(moved));
        Assert.Equal(moved, Assert.Single(window.EditorArea.Documents).Path);

        string program = Path.Combine(folder, "Shop.App", "Program.cs");
        from = Ui.Centre(Ui.Row(window.ExplorerView, Show(window, moved)), window);
        to = Ui.Centre(Ui.Row(window.ExplorerView, Show(window, program)), window);
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
        window.MouseUp(to, MouseButton.Left, OperatingSystem.IsMacOS() ? RawInputModifiers.Alt : RawInputModifiers.Control);
        Ui.Settle();
        Assert.Equal("Copied Order.cs to Shop.App", window.Status);
        Assert.Equal(Order, File.ReadAllText(Path.Combine(folder, "Shop.App", "Order.cs")));
        Assert.True(File.Exists(moved), "The file copied by a drag left where it was");

        ExplorerNode lines = Show(window, Path.Combine(models, "Lines"));
        from = Ui.Centre(Ui.Row(window.ExplorerView, Show(window, models)), window);
        to = Ui.Centre(Ui.Row(window.ExplorerView, lines), window);
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
        Assert.False(overlay.IsVisible, "A folder dragged over a folder inside it lit that up as a place to drop it");
        window.MouseUp(to, MouseButton.Left);
        Ui.Settle();
        Assert.True(Directory.Exists(Path.Combine(models, "Lines")), "The folder went inside itself");
        window.Close();
    }

    [AvaloniaFact]
    public void Add_reference_adds_one_the_build_and_language_server_follow_and_remove_takes_it_out_leaving_the_project_file_as_it_was()
    {
        string solution = Path.Combine(folder, "Shop.slnx");
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        Write("Shop.Core/Order.cs", "namespace Shop.Core;\n\npublic class Order\n{\n}\n");
        Write("Shop.App/Program.cs", "System.Console.WriteLine(new Shop.Core.Order());\n");
        Restore.Run(solution);
        byte[] original = File.ReadAllBytes(app);
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Shop.App", "Program.cs"));
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error), "the language server's error for the type of a project not referenced", TimeSpan.FromMinutes(2));

        Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Project, "Shop.App"), "Add", "Reference…");
        AddReferenceDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<AddReferenceDialog>()));
        ReferenceChoice offered = Assert.Single(dialog.Choices);
        Assert.Equal(("Shop.Core", core, false), (offered.Name, offered.Path, offered.IsChosen));
        Ui.Press(dialog, "SPACE");
        Assert.True(offered.IsChosen, "Space did not check the project chosen");
        // The dialog Enter closes takes no key's release.
        Heard(window, app, () => dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null));
        Assert.Empty(window.OwnedWindows.OfType<AddReferenceDialog>());
        Assert.Equal("Added a reference to Shop.Core to Shop.App", window.Status);
        string added = Encoding.UTF8.GetString(File.ReadAllBytes(app));
        Assert.Equal(
            $"﻿<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <OutputType>Exe</OutputType>\r\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\r\n  </PropertyGroup>\r\n\r\n"
                + "  <ItemGroup>\r\n    <ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" />\r\n  </ItemGroup>\r\n\r\n</Project>\r\n",
            added);

        ExplorerNode dependencies = Dependencies(window, "Shop.App");
        Ui.ClickRow(window, dependencies);
        Ui.Press(window, "RIGHT");
        ExplorerNode reference = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.ProjectReference);
        Assert.Equal(("Shop.Core", core), (reference.Name, reference.Referenced));
        Ui.WaitFor(() => !problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error), "the language server to find the type once the project references it", TimeSpan.FromMinutes(2));
        Builds(solution, "a reference was added");
        // What the build writes (a restore's .props among it) has the solution read again: once the folder is quiet.
        Quiet(window);

        List<string> asked = Answer(window);
        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.ProjectReference));
        Heard(window, app, () => Ui.Press(window, OperatingSystem.IsMacOS() ? "BACK_SPACE" : "DELETE"));
        Assert.Equal(["Remove Reference"], asked);
        Assert.Equal("Removed the reference to Shop.Core from Shop.App", window.Status);
        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(app)),
            $"With the reference removed, the project file is not as it was before it was added:\n{Encoding.UTF8.GetString(File.ReadAllBytes(app))}");
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.ProjectReference);
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error), "the language server's error once the reference is gone", TimeSpan.FromMinutes(2));

        Heard(window, core, () => Assert.Null(Ui.Await(window.SetReferencesAsync(core, [app]), "the references set")));
        Ui.ChooseFromRowMenu(window, Dependencies(window, "Shop.App"), "Add Reference…");
        dialog = Assert.Single(window.OwnedWindows.OfType<AddReferenceDialog>());
        Assert.False(Assert.Single(dialog.Choices).CanChoose, "A project that references this one was offered, which would make a cycle");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        Ui.Settle();
        Assert.False(dialog.IsVisible, "Escape left the dialog open");
        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(app)), "Cancelling the dialog changed the project file");
        window.Close();
    }

    /// <summary>On Windows Add Reference to a project file another program holds waits for it: while it is held the status
    /// bar says it waits for that file, never that it added the reference, and once the program lets go the reference is in
    /// the file and the status bar says it was added.</summary>
    [AvaloniaFact]
    public void Add_reference_to_a_project_file_another_program_holds_says_it_waits_and_added_once_the_file_is_let_go()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace or write a file another program holds");
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        MainWindow window = Open();
        Ui.WaitFor(() => window.Projects.Any(p => p.Path == PathNames.Canonical(app) && p.IsEvaluated), "MSBuild to evaluate Shop.App");
        List<string> said = [];
        using IDisposable following = Ui.FollowStatus(window, said);
        Task<string?> adding;
        using (new FileStream(app, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            adding = window.SetReferencesAsync(app, [core]);
            Ui.PumpUntil(() => said.Any(s => s.StartsWith("Waiting", StringComparison.Ordinal) || s.StartsWith("Added", StringComparison.Ordinal)),
                "Add Reference to say it waits for the held Shop.App.csproj");
            Assert.DoesNotContain("Shop.Core.csproj", File.ReadAllText(app), StringComparison.Ordinal);
        }
        Assert.Null(Ui.Await(adding, "the reference added once Shop.App.csproj was let go"));
        Assert.Equal(["Waiting for Shop.App.csproj to be saved…", "Added a reference to Shop.Core to Shop.App"],
            said.Where(s => s.StartsWith("Waiting", StringComparison.Ordinal) || s.StartsWith("Added", StringComparison.Ordinal) || s.Contains("could not be changed", StringComparison.Ordinal)));
        Assert.Contains("Shop.Core.csproj", File.ReadAllText(app), StringComparison.Ordinal);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Add_reference_beside_one_that_shares_its_line_with_its_groups_tags_writes_a_project_MSBuild_loads_and_remove_puts_it_back()
    {
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        string lib = Path.Combine(folder, "Shop.Lib", "Shop.Lib.csproj");
        string data = Path.Combine(folder, "Shop.Data", "Shop.Data.csproj");
        Write("Shop.Lib/Shop.Lib.csproj", Library);
        Write("Shop.Data/Shop.Data.csproj", Library);
        // Shop.App's reference shares its line with its group's start and end, Shop.Core's with its group's end alone.
        string reference = "<ProjectReference Include=\"..\\Shop.Lib\\Shop.Lib.csproj\" />";
        string program = Library.Replace("    <TargetFramework>", "    <OutputType>Exe</OutputType>\n    <TargetFramework>", StringComparison.Ordinal);
        Write("Shop.App/Shop.App.csproj", program.Replace("</Project>", $"  <ItemGroup>{reference}</ItemGroup>\n</Project>", StringComparison.Ordinal));
        Write("Shop.Core/Shop.Core.csproj", Library.Replace("</Project>", $"  <ItemGroup>\n    {reference}</ItemGroup>\n</Project>", StringComparison.Ordinal));
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n"
            + "  <Project Path=\"Shop.Data/Shop.Data.csproj\" />\n  <Project Path=\"Shop.Lib/Shop.Lib.csproj\" />\n</Solution>\n");
        string[] written = [File.ReadAllText(app), File.ReadAllText(core)];
        MainWindow window = Open();
        ProjectModel Project(string path) => window.Projects.First(p => p.Path == PathNames.Canonical(path));
        Ui.WaitFor(() => Project(app).IsEvaluated && Project(core).IsEvaluated, "MSBuild to evaluate the projects as written");

        Heard(window, app, () => Assert.Null(Ui.Await(window.SetReferencesAsync(app, [lib, core]), "the references set")));
        Heard(window, core, () => Assert.Null(Ui.Await(window.SetReferencesAsync(core, [lib, data]), "the references set")));
        Assert.Equal(program.Replace("</Project>", $"  <ItemGroup>{reference}<ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" /></ItemGroup>\n</Project>", StringComparison.Ordinal),
            File.ReadAllText(app));
        Assert.Equal(Library.Replace("</Project>", $"  <ItemGroup>\n    {reference}<ProjectReference Include=\"..\\Shop.Data\\Shop.Data.csproj\" /></ItemGroup>\n</Project>", StringComparison.Ordinal),
            File.ReadAllText(core));
        foreach ((string project, string added) in new[] { (app, core), (core, data) })
        {
            Ui.WaitFor(() => Project(project).Problem is not null || Project(project).ProjectReferences.Count == 2, $"MSBuild to evaluate {Path.GetFileName(project)} again");
            Assert.True(Project(project) is { Problem: null } loaded && loaded.ProjectReferences.Contains(PathNames.Canonical(added)),
                $"MSBuild does not load {Path.GetFileName(project)} with the reference added: {Project(project).Problem}");
        }

        Heard(window, app, () => Assert.Null(Ui.Await(window.SetReferencesAsync(app, [lib]), "the references set")));
        Heard(window, core, () => Assert.Null(Ui.Await(window.SetReferencesAsync(core, [lib]), "the references set")));
        Assert.Equal<string>(written, [File.ReadAllText(app), File.ReadAllText(core)]);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void A_project_file_and_the_solution_open_with_unsaved_edits_keep_them_through_add_reference_and_new_solution_folder_with_no_question()
    {
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        string solution = Path.Combine(folder, "Shop.slnx");
        // The project names a file of its own, which a rename makes it follow.
        Write("Shop.App/Shop.App.csproj", Executable.Replace("</Project>", "  <ItemGroup>\r\n    <None Update=\"notes.txt\" CopyToOutputDirectory=\"PreserveNewest\" />\r\n  </ItemGroup>\r\n\r\n</Project>", StringComparison.Ordinal));
        Write("Shop.App/notes.txt", "notes\n");
        MainWindow window = Open();
        List<string> conflicts = [];
        // Were it asked, Keep Memory Changes: what the person typed is what they would keep.
        window.AskConflict = document =>
        {
            conflicts.Add(document.Name);
            return Task.FromResult(false);
        };

        window.OpenFile(app);
        Typing.Replace(window, ">Exe<", ">WinExe<");
        OpenDocument project = window.EditorArea.Active!;
        Assert.True(project.IsModified);
        Heard(window, app, () => Assert.Null(Ui.Await(window.SetReferencesAsync(app, [core]), "the references set")));
        Quiet(window);
        string written = File.ReadAllText(app);
        Assert.True(conflicts.Count == 0, $"Passenger's own Add Reference asked which to keep of {string.Join(", ", conflicts)}, as if another program had changed it");
        Assert.True(written.Contains("<OutputType>WinExe</OutputType>", StringComparison.Ordinal), $"The unsaved edit to the project file is not in it after Add Reference:\n{written}");
        Assert.True(written.Contains("<ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" />", StringComparison.Ordinal), $"The reference added is not in the project file:\n{written}");
        Assert.Equal(written.TrimStart('﻿'), project.Text.Snapshot.ToString());
        Assert.False(project.IsModified, "The project file shows unsaved changes it does not have");

        window.OpenFile(solution);
        Typing.Replace(window, "<Solution>", "<Solution><!-- shop -->");
        OpenDocument slnx = window.EditorArea.Active!;
        Heard(window, solution, () => Assert.Null(Ui.Await(window.AddSolutionFolderAsync(null, "docs"), "the solution folder added")));
        Quiet(window);
        written = File.ReadAllText(solution);
        Assert.True(conflicts.Count == 0, $"Passenger's own New Solution Folder asked which to keep of {string.Join(", ", conflicts)}");
        Assert.True(written.Contains("<Solution><!-- shop -->", StringComparison.Ordinal), $"The unsaved edit to the solution is not in it after New Solution Folder:\n{written}");
        Assert.True(written.Contains("<Folder Name=\"/docs/\" />", StringComparison.Ordinal), $"The folder added is not in the solution:\n{written}");
        Assert.Equal(written, slnx.Text.Snapshot.ToString());
        Assert.False(slnx.IsModified, "The solution shows unsaved changes it does not have");

        window.OpenFile(app);
        List<string> undoing = Answer(window);
        string notes = Path.Combine(folder, "Shop.App", "notes.txt");
        Heard(window, app, () => Assert.Null(Ui.Await(window.RenameAsync(notes, "readme.txt"), "the rename")));
        Quiet(window);
        Assert.Empty(conflicts);
        Assert.Contains("<None Update=\"readme.txt\"", project.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], undoing);
        Assert.True(File.Exists(notes), $"Undo did not rename readme.txt back: {window.Status}");
        window.EditorArea.SaveAll();
        Assert.True(File.ReadAllText(app).Contains("<None Update=\"notes.txt\"", StringComparison.Ordinal),
            $"Undoing the rename left the project naming readme.txt, which is gone:\n{File.ReadAllText(app)}");
        window.Close();
    }

    /// <summary>A file, folder or class its folder refuses is refused in the popup saying why, with no IDE error; once the
    /// folder takes them, Add, Class/Interface... writes each kind of type in its folder's namespace.</summary>
    [Gate]
    [AvaloniaFact]
    public void Add_refused_by_its_folder_says_why_with_no_ide_error_and_add_class_interface_writes_each_of_Riders_kinds_of_type_in_its_folders_namespace()
    {
        string models = Path.Combine(folder, "Shop.App", "Models");
        MainWindow window = Open();
        ExplorerNode row = Show(window, models);
        using (Unwritable(models))
        {
            foreach ((string item, string name, string made) in new[] { ("New File…", "Notes.txt", "Notes.txt"), ("New Folder…", "Drafts", "Drafts"), ("Class/Interface…", "Invoice", "Invoice.cs") })
            {
                Ui.ChooseFromRowMenu(window, row, "Add", item);
                NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
                window.KeyTextInput(name);
                Ui.Press(window, "ENTER");
                Assert.True(window.IsPopupOpen && popup.Problem == $"{made} could not be made: access denied",
                    $"{item} in a folder that refuses it {(window.IsPopupOpen ? $"said '{popup.Problem}'" : "closed its popup")}, not why {made} was not made");
                Assert.False(Path.Exists(Path.Combine(models, made)), $"{made} was made in a folder that refuses it");
                Ui.Press(window, "ESCAPE");
            }
        }
        Assert.Empty(IdeErrors.Errors);

        string[] declarations =
        [
            "public class Invoice\n{\n}\n",
            "public interface IInvoice\n{\n}\n",
            "public record InvoiceLine();\n",
            "public struct Money\n{\n}\n",
            "public enum Status\n{\n}\n",
        ];
        string[] names = ["Invoice", "IInvoice", "InvoiceLine", "Money", "Status"];
        for (int kind = 0; kind < names.Length; kind++)
        {
            Ui.ChooseFromRowMenu(window, Show(window, models), "Add", "Class/Interface…");
            NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
            Assert.Equal("Class", popup.Kind);
            for (int down = 0; down < kind; down++)
            {
                Ui.Press(window, "DOWN");
            }
            Assert.Equal(MainWindow.TypeKinds[kind], popup.Kind);
            window.KeyTextInput(names[kind]);
            Ui.Press(window, "ENTER");
            Assert.False(window.IsPopupOpen, $"The {popup.Kind} {names[kind]} was refused: {popup.Problem}");
            string written = Path.Combine(models, names[kind] + ".cs");
            Assert.Equal($"namespace Shop.App.Models;\n\n{declarations[kind]}", File.ReadAllText(written));
            Assert.Equal(written, window.EditorArea.Active?.Path);
        }
        Assert.Equal(["Class", "Interface", "Record", "Struct", "Enum"], MainWindow.TypeKinds);
        window.Close();
    }

    /// <summary>A folder nothing can be made in while this is held: read-only for its owner on macOS and Linux, and on
    /// Windows, whose read-only attribute a folder ignores, denied to this user for making files and folders.</summary>
    private static IDisposable Unwritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Denied(path);
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restored(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, mode);
            }
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IDisposable Denied(string path)
    {
        DirectoryInfo info = new(path);
        System.Security.AccessControl.FileSystemAccessRule deny = new(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories,
            System.Security.AccessControl.AccessControlType.Deny);
        System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        return new Restored(() =>
        {
            System.Security.AccessControl.DirectorySecurity back = info.GetAccessControl();
            back.RemoveAccessRule(deny);
            info.SetAccessControl(back);
        });
    }

    private sealed class Restored(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Three of the Solution view's menus in one window: the solution's and a project's menus show the keys of the
    /// actions their items run, Compare With and Compare Files show two files' differences, and Open In, Terminal starts a
    /// shell in the folder in a tab of its own (sh's pwd is Unix's, so not on Windows).</summary>
    [Gate]
    [AvaloniaFact]
    public void The_menus_show_their_actions_keys_compare_shows_two_files_differences_and_open_in_terminal_starts_a_shell_in_the_folder()
    {
        Write("Shop.App/Notes/a.txt", "one\ntwo\nthree\n");
        Write("Shop.App/Notes/b.txt", "one\n2\nthree\nfour\n");
        string a = Path.Combine(folder, "Shop.App", "Notes", "a.txt");
        string b = Path.Combine(folder, "Shop.App", "Notes", "b.txt");
        // Run Unit Tests is offered only on a project holding tests.
        Write("Shop.Tests/Shop.Tests.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n  </ItemGroup>\n"));
        Write("Shop.Tests/ProductTests.cs", "using Xunit;\n\nnamespace Shop.Tests;\n\npublic class ProductTests\n{\n    [Fact]\n    public void Lists() { }\n}\n");
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n  <Project Path=\"Shop.Tests/Shop.Tests.csproj\" />\n</Solution>\n");
        MainWindow window = Open();
        window.PickFiles = (_, _, _) => Task.FromResult<IReadOnlyList<string>>([b]);
        Ui.WaitFor(() => window.Tests.Tree.Methods.Any(m => m.Name == "Lists"), "the test project's test from its code");

        Keymap keymap = window.Actions.Keymap;
        Ui.RightClick(Ui.Row(window.ExplorerView, Row(window, NodeKind.Solution, "Shop")));
        Shortcut build = keymap.ShortcutsFor(AppActions.Build)[0];
        KeyGesture? shown = MenuItemOf(window, "Build Solution").InputGesture;
        window.ExplorerView.OpenMenu?.Close();
        Assert.True(shown is not null && shown.Key == build.First.Key && shown.KeyModifiers == build.First.Modifiers,
            $"The solution's Build Solution shows {shown?.ToString() ?? "no keys"}, not Build Solution's {build.Display()} as the main menu does");
        Ui.Settle();

        Ui.RightClick(Ui.Row(window.ExplorerView, Row(window, NodeKind.Project, "Shop.Tests")));
        Shortcut chord = keymap.ShortcutsFor(AppActions.RunTests)[0];
        string? keys = MenuItemOf(window, "Run Unit Tests").GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_InputGestureText")?.Text;
        window.ExplorerView.OpenMenu?.Close();
        Assert.True(keys == chord.Display(), $"A project's Run Unit Tests shows '{keys}' for its keys, not {chord.Display()} as the main menu does");
        Ui.Settle();

        Ui.ClickRow(window, Show(window, a));
        Ui.Press(window, OperatingSystem.IsMacOS() ? "meta D" : "control D");
        Ui.Landed(() => window.Frontmost is DiffWindow, "the diff window");
        DiffView view = ((DiffWindow)window.Frontmost).View;
        Assert.Equal("one\ntwo\nthree\n", view.Left.Document.Snapshot.ToString());
        Assert.Equal("one\n2\nthree\nfour\n", view.Right.Document.Snapshot.ToString());
        Assert.Equal([new DiffHunk(1, 1, 1, 1), new DiffHunk(3, 0, 3, 1)], view.Hunks);
        Assert.Equal("a.txt vs b.txt", window.Frontmost.Title);
        window.Frontmost.Close();
        Ui.Settle();

        ExplorerNode second = Show(window, b);
        Ui.ClickRow(window, Show(window, a));
        Ui.Click(Ui.Row(window.ExplorerView, second), modifiers: RawInputModifiers.Shift);
        Assert.Equal(2, window.ExplorerView.SelectedNodes.Count);
        window.ExplorerView.Rows().RaiseEvent(new ContextRequestedEventArgs());
        Ui.Settle();
        Ui.Choose(window.ExplorerView.OpenMenu ?? throw new Xunit.Sdk.XunitException("The menu key opened no menu on the two files"), "Tools", "Compare Files");
        Ui.Landed(() => window.Frontmost is DiffWindow, "the diff window of the two files chosen");
        view = ((DiffWindow)window.Frontmost).View;
        Assert.Equal("one\ntwo\nthree\n", view.Left.Document.Snapshot.ToString());
        Assert.Equal("one\n2\nthree\nfour\n", view.Right.Document.Snapshot.ToString());
        window.Frontmost.Close();
        Ui.Settle();

        if (!OperatingSystem.IsWindows())
        {
            string catalog = Path.Combine(folder, "Shop.Core", "Catalog");
            window.TerminalPanel.Shell = ("/bin/sh", []);
            Ui.ChooseFromRowMenu(window, Show(window, catalog), "Open In", "Terminal");
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal");
            Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.CursorColumn > 0, "the shell's prompt");
            Assert.Single(window.TerminalPanel.Tabs);
            window.KeyTextInput("pwd -P");
            Ui.Press(window, "ENTER");
            TerminalSession shell = window.TerminalPanel.Active!;
            // Read across the rows, as a long path wraps.
            Ui.WaitFor(() => string.Concat(shell.Screen.Lines.Select(l => l.Text())).Contains(catalog, StringComparison.Ordinal), $"pwd to print {catalog}", TimeSpan.FromSeconds(30));
        }
        window.Close();
    }

    /// <summary>Where the file system tells letter case apart (Linux), Models and models are two folders of a project, each
    /// showing its own files, and a rename of a file to the name of another in other letter case is refused before anything
    /// changes, the type it is named after too.</summary>
    [AvaloniaFact]
    public void Where_letter_case_tells_names_apart_Models_and_models_stay_two_folders_and_a_rename_onto_another_case_is_refused()
    {
        Assert.SkipWhen(PathNames.Comparison != StringComparison.Ordinal, "Where the file system ignores letter case, Models and models are one folder and order.cs is Order.cs");
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string upper = Path.Combine(folder, "Shop.App", "Models");
        string lower = Path.Combine(folder, "Shop.App", "models");
        string order = Path.Combine(upper, "Order.cs");
        string sibling = Path.Combine(upper, "order.cs");
        Write("Shop.App/models/Invoice.cs", "namespace Shop.App.models;\n\npublic class Invoice\n{\n}\n");
        Write("Shop.App/Models/order.cs", "// Another file than Order.cs here.\n");
        MainWindow window = Open();
        Ui.WaitFor(() => window.Projects.First(p => p.Path == PathNames.Canonical(app)).IsEvaluated, "MSBuild to evaluate Shop.App");
        Show(window, order);
        Show(window, Path.Combine(lower, "Invoice.cs"));
        IReadOnlyList<string> Held(string path) =>
            [.. (Row(window, NodeKind.Folder, Path.GetFileName(path), path).Children ?? []).Select(c => c.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["Lines", "Order.cs", "order.cs"], Held(upper));
        Assert.Equal(["Invoice.cs"], Held(lower));

        Assert.Equal("order.cs is already there", Ui.Await(window.RenameAsync(order, "order.cs"), "the rename"));
        int type = File.ReadAllText(order).IndexOf("Order", StringComparison.Ordinal);
        Assert.Equal("order.cs is already there", Ui.Await(window.RenameWithTypeAsync(order, "order.cs", new TypeRename.FileType("Order", order, type), renameType: true), "the rename with its type"));
        Assert.Equal(Order, File.ReadAllText(order));
        Assert.Equal("// Another file than Order.cs here.\n", File.ReadAllText(sibling));
        window.Close();
    }

    /// <summary>A web project's Properties and wwwroot, and a class library's Properties, are the folders the SDK reads: listed
    /// before the project's other folders, drawn with their own icons in the Solution view and the File System view, and
    /// refusing Rename, Delete and Cut and a drag saying why, by the row's menu, by keys and for an agent, while a file inside
    /// renames. A library's wwwroot and a Properties below a project's top are folders like any other.</summary>
    [AvaloniaFact]
    public void Properties_and_a_web_projects_wwwroot_list_first_with_their_icons_and_refuse_rename_delete_and_move_saying_why_while_a_file_inside_renames()
    {
        Write("Shop.Web/Shop.Web.csproj", TestProjects.Web());
        Write("Shop.Web/Program.cs", "WebApplication.Create().Run();\n");
        Write("Shop.Web/Api/Orders.cs", "namespace Shop.Web.Api;\n\npublic class Orders\n{\n}\n");
        Write("Shop.Web/Api/Properties/notes.txt", "Not the project's.\n");
        Write("Shop.Web/Properties/launchSettings.json", "{ \"profiles\": {} }\n");
        Write("Shop.Web/wwwroot/site.css", "body { margin: 0; }\n");
        Write("Shop.Core/Properties/launchSettings.json", "{ \"profiles\": {} }\n");
        Write("Shop.Core/wwwroot/readme.txt", "A library's folder like any other.\n");
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop.Web/Shop.Web.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n</Solution>\n");
        string web = PathNames.Canonical(Path.Combine(folder, "Shop.Web"));
        string core = PathNames.Canonical(Path.Combine(folder, "Shop.Core"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        List<string> asked = Answer(window);
        Ui.WaitFor(() => window.Projects.Count(p => p.IsEvaluated) == 2, "MSBuild to evaluate Shop.Web and Shop.Core");
        Assert.True(window.Projects.Single(p => p.Name == "Shop.Web").IsWeb, "Shop.Web, on the Web SDK, is not evaluated as a web project");
        Assert.False(window.Projects.Single(p => p.Name == "Shop.Core").IsWeb, "Shop.Core, a class library, is evaluated as a web project");

        Show(window, Path.Combine(web, "Program.cs"));
        Show(window, Path.Combine(core, "Catalog"));
        Assert.Equal(["Properties", "wwwroot", "Api"], FoldersUnder(window, "Shop.Web"));
        Assert.Equal(["Properties", "Catalog", "wwwroot"], FoldersUnder(window, "Shop.Core"));
        (string Path, string Icon)[] drawn =
        [
            (Path.Combine(web, "Properties"), IconKeys.PropertiesFolder),
            (Path.Combine(web, "wwwroot"), IconKeys.WebRootFolder),
            (Path.Combine(core, "Properties"), IconKeys.PropertiesFolder),
            (Path.Combine(core, "wwwroot"), IconKeys.Explorer),
            (Path.Combine(web, "Api", "Properties"), IconKeys.Explorer),
        ];
        foreach ((string path, string icon) in drawn)
        {
            Assert.True(ReferenceEquals(Resource(icon), Drawn(window, Show(window, path)).Data), $"The Solution view does not draw {path} with {icon}");
            Assert.True(ReferenceEquals(Resource(TokenKeys.File_Folder), Drawn(window, Show(window, path)).Foreground), $"The Solution view does not draw {path} in the folders' colour");
        }

        string webRoot = Path.Combine(web, "wwwroot");
        const string served = "wwwroot is the project's web root: ASP.NET Core serves its static files from it";
        const string profiles = "Properties holds the project's launch and publish profiles";
        foreach ((string path, string why) in (ReadOnlySpan<(string, string)>)[(webRoot, served), (Path.Combine(web, "Properties"), profiles), (Path.Combine(core, "Properties"), profiles)])
        {
            Ui.RightClick(Ui.Row(window.ExplorerView, Show(window, path)));
            MenuItem[] edit = [.. MenuItemOf(window, "Edit").Items.OfType<MenuItem>()];
            window.ExplorerView.OpenMenu?.Close();
            Ui.Settle();
            foreach (string item in (string[])["Cut", "Delete…", "Rename…"])
            {
                MenuItem? shown = edit.FirstOrDefault(i => (string?)i.Header == item);
                Assert.True(shown is { IsEnabled: false } && Equals(ToolTip.GetTip(shown), why),
                    $"{path}'s Edit menu shows {item} {(shown is null ? "not at all" : shown.IsEnabled ? "enabled" : $"greyed saying '{ToolTip.GetTip(shown)}'")}, not greyed saying '{why}'");
            }

            // Copy between the keys refused, which it takes, so each refusal is its own word in the status bar.
            string name = Path.GetFileName(path);
            Ui.ClickRow(window, Show(window, path));
            Ui.Run(window, AppActions.Rename);
            Assert.False(window.IsPopupOpen, $"Rename's keys on {path} asked a new name");
            Assert.Equal(why, window.Status);
            Ui.Run(window, EditorActions.Copy);
            Assert.Equal($"Copied {name}", window.Status);
            Ui.Press(window, "BACK_SPACE");
            Assert.Equal(why, window.Status);
            Ui.Run(window, EditorActions.Copy);
            Ui.Run(window, EditorActions.Cut);
            Assert.Equal(why, window.Status);
            Assert.False(Show(window, path).IsCut, $"Cut's keys dimmed {path} as cut");
            foreach (string action in (string[])[AppActions.Rename, EditorActions.Delete, EditorActions.Cut])
            {
                Assert.Equal(why, window.Actions.Run(action));
            }
            Assert.True(Directory.Exists(path), $"{path} went, deleted by its keys");
        }
        Assert.Empty(asked);

        ExplorerNode api = Show(window, Path.Combine(web, "Api"));
        DropOverlay overlay = window.ExplorerView.GetVisualDescendants().OfType<DropOverlay>().Single();
        Point from = Ui.Centre(Ui.Row(window.ExplorerView, Show(window, webRoot)), window);
        Point to = Ui.Centre(Ui.Row(window.ExplorerView, api), window);
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
        Assert.False(overlay.IsVisible, "wwwroot dragged over a folder lit it up as a place to move it to");
        window.MouseUp(to, MouseButton.Left);
        Ui.Settle();
        Assert.True(Directory.Exists(webRoot) && !Directory.Exists(Path.Combine(web, "Api", "wwwroot")), "A drag moved wwwroot");

        Ui.ClickRow(window, Show(window, Path.Combine(webRoot, "site.css")));
        Ui.Run(window, AppActions.Rename);
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("theme.css");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, () => $"the rename of site.css to finish: {popup.Problem}");
        Assert.True(File.Exists(Path.Combine(webRoot, "theme.css")) && !File.Exists(Path.Combine(webRoot, "site.css")),
            $"site.css in wwwroot was not renamed theme.css: {window.Status}");

        Ui.Click(window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Switch between the solution", StringComparison.Ordinal)));
        Assert.False(window.ExplorerView.Tree.IsSolutionView, "The view switch left the Solution view shown");
        foreach ((string path, string icon) in drawn)
        {
            Assert.True(ReferenceEquals(Resource(icon), Drawn(window, Show(window, path)).Data), $"The File System view does not draw {path} with {icon}");
        }
        Assert.Equal(served, Show(window, webRoot).Kept);
        window.Close();
    }

    /// <summary>The names of the folders shown directly under a project's row, in order.</summary>
    private static IReadOnlyList<string> FoldersUnder(MainWindow window, string project)
    {
        List<ExplorerNode> rows = [.. window.ExplorerView.Tree.Rows];
        ExplorerNode row = Row(window, NodeKind.Project, project);
        return [.. rows.Skip(rows.IndexOf(row) + 1).TakeWhile(r => r.Depth > row.Depth).Where(r => r.Depth == row.Depth + 1 && r.Kind == NodeKind.Folder).Select(r => r.Name)];
    }

    /// <summary>The icon and its brush a row is drawn with.</summary>
    private static (Avalonia.Media.Geometry? Data, Avalonia.Media.IBrush? Foreground) Drawn(MainWindow window, ExplorerNode row)
    {
        RowGlyphs glyphs = window.ExplorerView.GetVisualDescendants().OfType<RowGlyphs>().FirstOrDefault(i => ReferenceEquals(i.DataContext, row))
            ?? throw new InvalidOperationException($"The Explorer draws no icon on {row.Name}'s row");
        return (glyphs.Icon, glyphs.IconBrush);
    }

    private static object Resource(string key)
    {
        Application app = Application.Current!;
        return app.TryFindResource(key, app.ActualThemeVariant, out object? value) && value is not null ? value : throw new InvalidOperationException($"The theme has no {key}");
    }

    /// <summary>An item of the Explorer's open menu, by its title.</summary>
    private static MenuItem MenuItemOf(MainWindow window, string header)
    {
        ContextMenu menu = window.ExplorerView.OpenMenu ?? throw new InvalidOperationException("No menu opened on the row");
        return menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Header == header)
            ?? throw new InvalidOperationException($"The menu has no {header}: [{string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header))}]");
    }

    [AvaloniaFact]
    public void Add_reference_to_a_Windows_1252_project_keeps_its_other_bytes_and_Local_History_keeps_the_project_as_it_was()
    {
        // An older project in the system's code page, as its declaration says, with a comment of Windows-1252's letters.
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string comment = "  <!-- Café \u0096 \u0093Core\u0094 -->\n";
        byte[] original = System.Text.Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"windows-1252\"?>\n" + Library.Replace("  <PropertyGroup>", comment + "  <PropertyGroup>", StringComparison.Ordinal));
        File.WriteAllBytes(core, original);
        MainWindow window = Open();
        Heard(window, core, () => Assert.Null(Ui.Await(window.SetReferencesAsync(core, [app]), "the references set")));

        string written = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(core));
        Assert.True(written.Contains(comment, StringComparison.Ordinal), $"Adding a reference rewrote the project's Windows-1252 comment:\n{written}");
        Assert.Contains("<ProjectReference Include=\"..\\Shop.App\\Shop.App.csproj\" />", written, StringComparison.Ordinal);
        Ui.WaitFor(() => window.LocalHistory!.Snapshot().Events.Any(e => e.Path == core && e.Before is { } hash && window.LocalHistory.Content(hash) is { } before && before.AsSpan().SequenceEqual(original)),
            "Local History to keep the project as it was before the reference went in");
        window.Close();
    }

    /// <summary>A file added (a refresh of the Explorer) leaves open the Dependencies that were open, Shop.Core's, and
    /// closed Shop.App's above it, which a key by the row's depth, kind and name alone took for it.</summary>
    [Gate]
    [AvaloniaFact]
    public void A_file_added_keeps_the_open_dependencies_open_and_opens_no_other_projects()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Dependencies(window, "Shop.App");
        ExplorerNode core = Dependencies(window, "Shop.Core");
        Ui.ClickRow(window, core);
        Ui.Press(window, "RIGHT");
        Assert.True(core.IsExpanded, "Shop.Core's Dependencies did not open");

        Ui.ChooseFromRowMenu(window, Show(window, Path.Combine(folder, "Shop.Core", "Catalog")), "Add", "New File…");
        Assert.IsType<NamePopup>(window.Popup);
        window.KeyTextInput("Notes.txt");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => File.Exists(Path.Combine(folder, "Shop.Core", "Catalog", "Notes.txt")), "Notes.txt to be made");
        string Opened(string project) =>
            window.ExplorerView.Tree.Rows.First(r => r.Kind == NodeKind.Dependencies && r.Owner == Row(window, NodeKind.Project, project).Path).IsExpanded ? "open" : "closed";
        Assert.True((Opened("Shop.App"), Opened("Shop.Core")) == ("closed", "open"),
            $"After a file was added, Shop.App's Dependencies are {Opened("Shop.App")} and Shop.Core's {Opened("Shop.Core")}, where only Shop.Core's were open");
        window.Close();
    }

    /// <summary>A folder opened and closed by its chevron above the file chosen leaves the view where it was, the folder
    /// where the person clicked and the file pushed out of sight and back (#712); a row chosen by a key, or a file opened
    /// while the Explorer was closed, still comes into view.</summary>
    [AvaloniaFact]
    public void A_folder_opened_or_closed_above_the_file_chosen_leaves_the_view_where_it_was_while_a_row_chosen_by_a_key_or_opened_comes_into_view()
    {
        for (int i = 0; i < 40; i++)
        {
            Write($"Shop.App/Models/Lines/Line{i:00}.cs", $"namespace Shop.App.Models.Lines;\n\npublic class Line{i:00}\n{{\n}}\n");
        }
        for (int i = 0; i < 60; i++)
        {
            Write($"Shop.Core/Catalog/Item{i:00}.cs", $"namespace Shop.Core.Catalog;\n\npublic class Item{i:00}\n{{\n}}\n");
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        ExplorerView explorer = window.ExplorerView;
        ListBox rows = explorer.Rows();
        ScrollViewer scroller = rows.GetVisualDescendants().OfType<ScrollViewer>().First();
        ExplorerNode lines = Show(window, Path.Combine(folder, "Shop.App", "Models", "Lines"));
        Show(window, Path.Combine(folder, "Shop.Core", "Catalog", "Item00.cs"));
        Control chevron = Ui.Row(explorer, lines).GetVisualDescendants().OfType<RowGlyphs>().Single();
        ExplorerNode chosen = explorer.Tree.Rows.Last(r => InSight(r) == "in sight");
        Assert.True(chosen.IsFile, $"The last row in sight is {chosen.Name}, not a file below Lines, so the list is too short to push one out of sight");
        Ui.ClickRow(window, chosen);
        double offset = scroller.Offset.Y;

        Ui.Click(chevron, new Point(9, ListRow.Height / 2));
        Assert.True(lines.IsExpanded, "A click on Lines' chevron left it closed");
        Assert.True(scroller.Offset.Y == offset && InSight(lines) == "in sight" && InSight(chosen) != "in sight",
            $"Lines opened by its chevron scrolled the view from {offset} to {scroller.Offset.Y}, Lines {InSight(lines)} and {chosen.Name}, still chosen, "
            + $"{InSight(chosen)}: the view followed the file chosen, taking the folder opened away from where the person clicked");
        Assert.True(explorer.SelectedNodes is [{ } still] && still == chosen, $"Opening Lines chose another row than {chosen.Name}");
        Ui.Click(chevron, new Point(9, ListRow.Height / 2));
        Assert.True(!lines.IsExpanded && scroller.Offset.Y == offset && InSight(lines) == "in sight" && InSight(chosen) == "in sight",
            $"Lines closed by its chevron moved the view from {offset} to {scroller.Offset.Y}, Lines {InSight(lines)} and {chosen.Name} {InSight(chosen)}");

        Ui.Click(chevron, new Point(9, ListRow.Height / 2));
        Ui.Press(window, "END");
        ExplorerNode end = explorer.Tree.Rows[^1];
        Assert.True(explorer.SelectedNodes is [{ } ended] && ended == end && InSight(end) == "in sight",
            $"End chose [{string.Join(", ", explorer.SelectedNodes.Select(n => n.Name))}], {end.Name} {InSight(end)}: a row a key chose was left out of sight");

        string first = Path.Combine(folder, "Shop.App", "Models", "Lines", "Line00.cs");
        window.DockHost.Hide(ToolWindowIds.Explorer);
        Ui.Settle();
        window.OpenFile(first);
        Ui.Settle();
        window.DockHost.Show(ToolWindowIds.Explorer);
        ExplorerNode opened = explorer.Tree.Rows.Single(r => r.Path == PathNames.Canonical(first));
        Assert.True(explorer.SelectedNodes is [{ } shown] && shown == opened && InSight(opened) == "in sight",
            $"Line00.cs, opened while the Explorer was closed, is {InSight(opened)} as the Explorer opens again, [{string.Join(", ", explorer.SelectedNodes.Select(n => n.Name))}] chosen");
        window.Close();

        // Whether a row's whole height shows in the list's viewport, or which way it lies out of it.
        string InSight(ExplorerNode row)
        {
            Ui.Settle();
            if (rows.ContainerFromItem(row) is not { } container || container.TranslatePoint(default, scroller) is not { } top)
            {
                return "out of sight";
            }
            return top.Y < 0 ? "above the view" : top.Y + container.Bounds.Height > scroller.Viewport.Height ? "below the view" : "in sight";
        }
    }


    /// <summary>The Explorer's keys stay on the file chosen however rows come above it by code (#719): folders opened above
    /// it, pushing it out of sight and further, the rows made again, and a file written above it, the view left where it
    /// was, Down then choosing the next file; a file revealed while the Explorer has the keys takes them, and one revealed
    /// while the editor has them leaves them there.</summary>
    [AvaloniaFact]
    public void The_explorer_keeps_its_keys_on_the_file_chosen_however_rows_come_above_it_and_takes_none_it_did_not_have()
    {
        for (int i = 0; i < 40; i++)
        {
            Write($"Shop.App/Models/Lines/Line{i:00}.cs", $"namespace Shop.App.Models.Lines;\n\npublic class Line{i:00}\n{{\n}}\n");
        }
        for (int i = 0; i < 10; i++)
        {
            Write($"Shop.App/Views/View{i:00}.cs", $"namespace Shop.App.Views;\n\npublic class View{i:00}\n{{\n}}\n");
        }
        for (int i = 0; i < 60; i++)
        {
            Write($"Shop.Core/Catalog/Item{i:00}.cs", $"namespace Shop.Core.Catalog;\n\npublic class Item{i:00}\n{{\n}}\n");
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        ExplorerView explorer = window.ExplorerView;
        ScrollViewer scroller = explorer.Rows().GetVisualDescendants().OfType<ScrollViewer>().First();
        string catalog = Path.Combine(folder, "Shop.Core", "Catalog");
        ExplorerNode lines = Show(window, Path.Combine(folder, "Shop.App", "Models", "Lines"));
        ExplorerNode views = Show(window, Path.Combine(folder, "Shop.App", "Views"));
        explorer.Tree.Collapse(lines);
        explorer.Tree.Collapse(views);
        Ui.ClickRow(window, Show(window, Path.Combine(catalog, "Item20.cs")));
        double offset = scroller.Offset.Y;

        explorer.Tree.Expand(lines);
        Kept("Item20.cs", "Lines opened above it by code, pushing it out of sight");
        explorer.Tree.Expand(views);
        Kept("Item20.cs", "Views opened above it too, while it was out of sight");
        Next("Item21.cs");
        explorer.Refresh();
        Kept("Item21.cs", "the rows made again", viewKept: false);
        Next("Item22.cs");
        Write("Shop.Core/Catalog/Aaa.cs", "namespace Shop.Core.Catalog;\n\npublic class Aaa\n{\n}\n");
        Ui.WaitFor(() => explorer.Tree.Rows.Any(r => r.Name == "Aaa.cs"), "the Explorer to show Aaa.cs, written on disk above Item22.cs");
        Kept("Item22.cs", "Aaa.cs written above it");
        Next("Item23.cs");
        explorer.Reveal(Path.Combine(folder, "Shop.App", "Models", "Lines", "Line05.cs"));
        Kept("Line05.cs", "Line05.cs revealed while the Explorer had the keys", viewKept: false);
        Next("Line06.cs");

        window.OpenFile(Path.Combine(catalog, "Item40.cs"));
        Ui.WaitFor(() => window.EditorArea.CodeEditor.IsKeyboardFocusWithin, "the keys to go to Item40.cs opened");
        explorer.Reveal(Path.Combine(catalog, "Item41.cs"));
        explorer.Tree.Collapse(lines);
        Ui.Settle();
        Assert.True(window.EditorArea.CodeEditor.IsKeyboardFocusWithin && explorer.SelectedNodes is [{ Name: "Item41.cs" }],
            $"Item41.cs revealed and Lines closed above it while the editor had the keys left them with {Keys()}, "
            + $"[{string.Join(", ", explorer.SelectedNodes.Select(n => n.Name))}] chosen: the Explorer took keys it did not have");
        window.Close();

        string Keys() => window.FocusManager?.GetFocusedElement() switch
        {
            Control { DataContext: ExplorerNode row } => $"the Explorer's {row.Name}",
            { } other => other.GetType().Name,
            null => "nothing",
        };

        // The file chosen alone, with the keys on its row, and the view where it was unless the change moved it.
        void Kept(string name, string after, bool viewKept = true)
        {
            Ui.Settle();
            Assert.True(explorer.SelectedNodes is [{ } chosen] && chosen.Name == name && Keys() == $"the Explorer's {name}" && (!viewKept || scroller.Offset.Y == offset),
                $"After {after}, [{string.Join(", ", explorer.SelectedNodes.Select(n => n.Name))}] chosen, the keys with {Keys()} and the view moved from {offset} "
                + $"to {scroller.Offset.Y}: the next key pressed in the Explorer goes nowhere, or not on from {name}");
            offset = scroller.Offset.Y;
        }

        // Down chooses the next file, and the keys go with it.
        void Next(string name)
        {
            string from = Keys();
            Ui.Press(window, "DOWN");
            Assert.True(explorer.SelectedNodes is [{ } chosen] && chosen.Name == name && Keys() == $"the Explorer's {name}",
                $"Down from {from} chose [{string.Join(", ", explorer.SelectedNodes.Select(n => n.Name))}], the keys then with {Keys()}, where {name} is next");
            offset = scroller.Offset.Y;
        }
    }

    /// <summary>A solution filter naming one project of the solution, opened: its solution with that project alone loaded,
    /// the other shown unloaded, Build building the one and the language server loading the one; the filter changed on
    /// disk to name the other swaps them, and Reload Project loads the one it left out, none of which the folder keeps
    /// as the person's own unloading.</summary>
    [Gate]
    [AvaloniaFact]
    public void A_solution_filter_opens_its_solution_with_its_projects_alone_built_and_analysed_and_a_change_to_it_is_followed()
    {
        string filter = Path.Combine(folder, "Shop.App.slnf");
        Write("Shop.App.slnf", "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [\n      \"Shop.App\\\\Shop.App.csproj\"\n    ]\n  }\n}\n");
        Restore.Run(Path.Combine(folder, "Shop.slnx"));
        string app = PathNames.Canonical(Path.Combine(folder, "Shop.App", "Shop.App.csproj"));
        string core = PathNames.Canonical(Path.Combine(folder, "Shop.Core", "Shop.Core.csproj"));
        MainWindow window = Ui.OpenFolder(filter);
        try
        {
            Assert.Equal(("Shop.App", PathNames.Canonical(filter)), (window.Session.Workspace?.Name, window.Session.Workspace?.OpenedAs));
            string Loaded() => string.Join(", ", window.ExplorerView.Tree.Rows.Where(r => r.Kind == NodeKind.Project).Select(r => $"{r.Name} {(r.IsDimmed ? "unloaded" : "loaded")}"));
            Assert.Equal("Shop.App loaded, Shop.Core unloaded", Loaded());
            Assert.Equal("(unloaded)", Row(window, NodeKind.Project, "Shop.Core").Detail);

            Ui.WaitFor(() => window.CSharpService?.Session is { IsLoaded: true }, "the language server to load the filter's project", TimeSpan.FromMinutes(2));
            int Found(string name) => Ui.Await(window.CSharpService!.Session!.SymbolsNamedAsync(name, 5), $"the language server's symbols named {name}", TimeSpan.FromMinutes(1)).Symbols.Count;
            Assert.True((window.Session.DesignTimeBuilds.Builds, Found("Order"), Found("Product")) == (1, 1, 0),
                $"The language server loaded {window.Session.DesignTimeBuilds.Builds} project(s), found {Found("Order")} Order (Shop.App's) and {Found("Product")} Product (Shop.Core's), "
                + "where it loads Shop.App alone");

            window.Perform(AppActions.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the filter's build", TimeSpan.FromMinutes(2));
            Assert.StartsWith("Build succeeded", window.Status, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(folder, "Shop.App", "bin", "Debug", TestProjects.Framework, "Shop.App.dll")) && !Directory.Exists(Path.Combine(folder, "Shop.Core", "bin")),
                $"Build built {(Directory.Exists(Path.Combine(folder, "Shop.Core", "bin")) ? "Shop.Core, which the filter leaves out" : "no Shop.App")}:\n{window.BottomPanel.Build.Document.Snapshot}");

            Heard(window, filter, () => File.WriteAllText(filter, "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [ \"Shop.Core/Shop.Core.csproj\" ]\n  }\n}\n"));
            Ui.WaitFor(() => Loaded() == "Shop.App unloaded, Shop.Core loaded", () => $"the Explorer to follow the filter naming Shop.Core instead ({Loaded()})");
            Ui.WaitFor(() => window.CSharpService?.Session is { IsLoaded: true } && Found("Product") == 1 && Found("Order") == 0,
                () => "the language server to load Shop.Core in Shop.App's place", TimeSpan.FromMinutes(2));

            window.SetUnloaded(app, unloaded: false);
            Assert.Equal("Shop.App loaded, Shop.Core loaded", Loaded());
            Ui.WaitFor(() => Found("Order") == 1, "the language server to load Shop.App once reloaded", TimeSpan.FromMinutes(2));
            Assert.Equal(["Shop.App", "Shop.Core"], window.Session.Projects.Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Empty(window.Session.KeptUnloaded);
            window.SetUnloaded(core, unloaded: true);
            Assert.Equal([core], window.Session.KeptUnloaded);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file-based program beside the solution: Add Reference never offers it, since no project reference can name
    /// a C# file; and the solution's filter, opened over the solution or in a window of its own, unloads the solution's
    /// project it leaves out alone, the program loaded with its run configuration either way.</summary>
    [AvaloniaFact]
    public void A_program_beside_the_solution_is_never_offered_as_a_reference_and_stays_loaded_however_a_solution_filter_opens()
    {
        Write("scripts/tool.cs", "#!/usr/bin/env dotnet\nSystem.Console.WriteLine(\"tool\");\n");
        string filter = PathNames.Canonical(Path.Combine(folder, "Shop.App.slnf"));
        Write("Shop.App.slnf", "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [\n      \"Shop.App\\\\Shop.App.csproj\"\n    ]\n  }\n}\n");
        string tool = PathNames.Canonical(Path.Combine(folder, "scripts", "tool.cs"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects.Any(p => p.Path == tool), "scripts/tool.cs beside the solution to be a program of the folder's");
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Project, "Shop.App"), "Add", "Reference…");
            AddReferenceDialog dialog = Assert.Single(window.OwnedWindows.OfType<AddReferenceDialog>());
            Assert.True(dialog.Choices.Select(c => c.Name).SequenceEqual(["Shop.Core"]),
                $"Add Reference offered [{string.Join(", ", dialog.Choices.Select(c => c.Detail))}], where a program chosen would write a ProjectReference no build can load");
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.Settle();

            Assert.Null(window.Open(filter));
            Ui.Settle();
            Assert.Equal(filter, window.Session.Workspace?.OpenedAs);
            Loaded(window, "the filter opened over the solution");
        }
        finally
        {
            window.Close();
        }
        window = Ui.OpenFolder(filter, languageServer: false);
        try
        {
            Loaded(window, "the filter opened in a window of its own");
        }
        finally
        {
            window.Close();
        }

        void Loaded(MainWindow shown, string after)
        {
            string Said() => string.Join(", ", shown.ExplorerView.Tree.Rows.Where(r => r.Kind == NodeKind.Project).Select(r => $"{r.Name} {(r.IsDimmed ? "unloaded" : "loaded")}"));
            Ui.WaitFor(() => Said() == "Shop.App loaded, Shop.Core unloaded, tool loaded", () => $"after {after}, the Explorer to show the program loaded beside Shop.App ({Said()})");
            Assert.True(shown.Configurations.Any(c => c.Name == "tool") && shown.Session.Projects.Any(p => p.Path == tool),
                $"After {after}, the program is lost to Run and Build: its run configurations are [{string.Join(", ", shown.Configurations.Select(c => c.Name))}]");
        }
    }

    /// <summary>A project added while a solution filter is open goes in the filter's list as well as the solution, so the
    /// filter opened again loads it rather than leaving it out; one Undo takes it out of both and Redo puts it back, the
    /// project the person loaded that the filter leaves out staying loaded throughout.</summary>
    [AvaloniaFact]
    public void A_project_added_under_an_open_solution_filter_joins_its_list_so_the_filter_opened_again_loads_it()
    {
        const string Filter = "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [\n      \"Shop.App\\\\Shop.App.csproj\"\n    ]\n  }\n}\n";
        string filter = PathNames.Canonical(Path.Combine(folder, "Shop.App.slnf"));
        string solution = Path.Combine(folder, "Shop.slnx");
        Write("Shop.App.slnf", Filter);
        Write("Shop.Tools/Shop.Tools.csproj", Library);
        string listed = File.ReadAllText(solution);
        string added = Filter.Replace("\"Shop.App\\\\Shop.App.csproj\"\n", "\"Shop.App\\\\Shop.App.csproj\",\n      \"Shop.Tools\\\\Shop.Tools.csproj\"\n", StringComparison.Ordinal);
        const string All = "Shop.App loaded, Shop.Core loaded, Shop.Tools loaded";
        MainWindow window = Ui.OpenFolder(filter, languageServer: false);
        try
        {
            List<string> asked = Answer(window);
            window.SetUnloaded(PathNames.Canonical(Path.Combine(folder, "Shop.Core", "Shop.Core.csproj")), unloaded: false);
            window.PickFiles = (_, _, _) => Task.FromResult<IReadOnlyList<string>>([Path.Combine(folder, "Shop.Tools", "Shop.Tools.csproj")]);
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", "Existing Project…");
            Ui.WaitFor(() => Loaded(window) == All, () => $"Shop.Tools to be added, the rest as they were ({Loaded(window)}): {window.Status}");
            Assert.True(File.ReadAllText(filter) == added, $"Shop.Tools went in the solution but not in the filter open, which leaves it out once opened again:\n{File.ReadAllText(filter)}");
            Assert.Contains("Shop.Tools/Shop.Tools.csproj", File.ReadAllText(solution), StringComparison.Ordinal);

            Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Tools"));
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => Loaded(window) == "Shop.App loaded, Shop.Core loaded", () => $"Undo to take Shop.Tools out, the rest as they were ({Loaded(window)}): {window.Status}");
            Assert.True((File.ReadAllText(filter), File.ReadAllText(solution)) == (Filter, listed), $"Undo left the filter or the solution changed: {window.Status}\n{File.ReadAllText(filter)}");

            Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.App"));
            Ui.Run(window, EditorActions.Redo);
            Ui.WaitFor(() => Loaded(window) == All, () => $"Redo to add Shop.Tools again ({Loaded(window)}): {window.Status}");
            Assert.Equal(added, File.ReadAllText(filter));
            Assert.Equal(["Undo", "Redo"], asked);
        }
        finally
        {
            window.Close();
        }
        window = Ui.OpenFolder(filter, languageServer: false);
        try
        {
            Assert.Equal("Shop.App loaded, Shop.Core unloaded, Shop.Tools loaded", Loaded(window));
        }
        finally
        {
            window.Close();
        }

        static string Loaded(MainWindow shown) =>
            string.Join(", ", shown.ExplorerView.Tree.Rows.Where(r => r.Kind == NodeKind.Project).Select(r => $"{r.Name} {(r.IsDimmed ? "unloaded" : "loaded")}"));
    }

    /// <summary>Projects renamed, moved and removed while a solution filter is open take the filter's list with them, each
    /// in the command of Undo that changes the solution (Undo of a move and of the removal putting it back, Redo of the
    /// removal taking it out again): opened again, the filter loads the project renamed under its new name and the one
    /// moved where it went, and names none the solution let go, the project it leaves out still unloaded.</summary>
    [AvaloniaFact]
    public void Projects_renamed_moved_and_removed_under_an_open_solution_filter_take_its_list_with_them_so_the_filter_opened_again_loads_them()
    {
        const string Filter = "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [\n      \"Shop.App\\\\Shop.App.csproj\",\n"
            + "      \"Shop.Core\\\\Shop.Core.csproj\",\n      \"Shop.Tools\\\\Shop.Tools.csproj\"\n    ]\n  }\n}\n";
        Write("Shop.Tools/Shop.Tools.csproj", Library);
        Write("Shop.Docs/Shop.Docs.csproj", Library);
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n"
            + "  <Project Path=\"Shop.Docs/Shop.Docs.csproj\" />\n  <Project Path=\"Shop.Tools/Shop.Tools.csproj\" />\n</Solution>\n");
        Write("Shop.App.slnf", Filter);
        string src = PathNames.Canonical(Directory.CreateDirectory(Path.Combine(folder, "src")).FullName);
        string apps = PathNames.Canonical(Directory.CreateDirectory(Path.Combine(folder, "apps")).FullName);
        string filter = PathNames.Canonical(Path.Combine(folder, "Shop.App.slnf"));
        string solution = Path.Combine(folder, "Shop.slnx");
        string renamed = Filter.Replace("Shop.Core\\\\Shop.Core.csproj", "Shop.Core\\\\Shop.Domain.csproj", StringComparison.Ordinal);
        string Moved(string into) => renamed.Replace("\"Shop.App\\\\Shop.App.csproj\"", $"\"{into}\\\\Shop.App\\\\Shop.App.csproj\"", StringComparison.Ordinal);
        string moved = Moved("apps");
        string removed = moved.Replace(",\n      \"Shop.Tools\\\\Shop.Tools.csproj\"", string.Empty, StringComparison.Ordinal);
        MainWindow window = Ui.OpenFolder(filter, languageServer: false);
        try
        {
            List<string> asked = [];
            window.Confirm = (title, _, _) =>
            {
                asked.Add(title);
                return Task.FromResult(title != "Delete Project Files");
            };
            Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Core"));
            Ui.Run(window, AppActions.Rename);
            NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
            popup.Text = string.Empty;
            window.KeyTextInput("Shop.Domain");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => Loaded(window) == "Shop.App loaded, Shop.Docs unloaded, Shop.Domain loaded, Shop.Tools loaded",
                () => $"Shop.Core to be renamed Shop.Domain, still loaded ({Loaded(window)}): {window.Status}");
            Assert.True(File.ReadAllText(filter) == renamed, $"The filter open still names Shop.Core, so opened again it leaves Shop.Domain out:\n{File.ReadAllText(filter)}");

            CutAndPaste(src);
            Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.App"));
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => window.Status?.StartsWith("Undone", StringComparison.Ordinal) == true, () => $"Undo to move Shop.App back: {window.Status}");
            Assert.True(File.Exists(Path.Combine(folder, "Shop.App", "Shop.App.csproj")) && File.ReadAllText(filter) == renamed,
                $"Undo left Shop.App in src, or the filter naming it there: {window.Status}\n{File.ReadAllText(filter)}");
            CutAndPaste(apps);

            string listed = File.ReadAllText(solution);
            Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Tools"));
            Ui.Press(window, "BACK_SPACE");
            Ui.WaitFor(() => Loaded(window) == "Shop.App loaded, Shop.Docs unloaded, Shop.Domain loaded", () => $"Shop.Tools to be removed ({Loaded(window)}): {window.Status}");
            Assert.True(File.ReadAllText(filter) == removed, $"The filter open still names Shop.Tools, which the solution let go:\n{File.ReadAllText(filter)}");
            Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Docs"));
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => window.Status?.StartsWith("Undone", StringComparison.Ordinal) == true && Loaded(window) == "Shop.App loaded, Shop.Docs unloaded, Shop.Domain loaded, Shop.Tools loaded",
                () => $"Undo to put Shop.Tools back ({Loaded(window)}): {window.Status}");
            Assert.True((File.ReadAllText(filter), File.ReadAllText(solution)) == (moved, listed), $"Undo left the filter or the solution without Shop.Tools: {window.Status}\n{File.ReadAllText(filter)}");
            Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Docs"));
            Ui.Run(window, EditorActions.Redo);
            Ui.WaitFor(() => window.Status?.StartsWith("Redone", StringComparison.Ordinal) == true && Loaded(window) == "Shop.App loaded, Shop.Docs unloaded, Shop.Domain loaded",
                () => $"Redo to remove Shop.Tools again ({Loaded(window)}): {window.Status}");
            Assert.Equal(removed, File.ReadAllText(filter));
            Assert.Equal(["Undo", "Remove Project", "Delete Project Files", "Undo", "Redo"], asked);
        }
        finally
        {
            window.Close();
        }
        window = Ui.OpenFolder(filter, languageServer: false);
        try
        {
            Assert.Equal("Shop.App loaded, Shop.Docs unloaded, Shop.Domain loaded", Loaded(window));
            Assert.Equal(Path.Combine(apps, "Shop.App", "Shop.App.csproj"), Row(window, NodeKind.Project, "Shop.App").Path);
        }
        finally
        {
            window.Close();
        }

        // Cut and pasted in the File System view, where a project's folder is a row of its own.
        void CutAndPaste(string into)
        {
            Button views = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Switch between the solution", StringComparison.Ordinal));
            Ui.Click(views);
            Ui.ClickRow(window, Show(window, Path.Combine(folder, "Shop.App")));
            Ui.Run(window, EditorActions.Cut);
            Ui.ClickRow(window, Show(window, into));
            Ui.Run(window, EditorActions.Paste);
            Assert.Equal($"Moved Shop.App to {Path.GetFileName(into)}", window.Status);
            Assert.True(File.ReadAllText(filter) == Moved(Path.GetFileName(into)), $"The filter open still names Shop.App where it was, so opened again it leaves it out:\n{File.ReadAllText(filter)}");
            Ui.Click(views);
            Ui.WaitFor(() => Loaded(window) == "Shop.App loaded, Shop.Docs unloaded, Shop.Domain loaded, Shop.Tools loaded",
                () => $"Shop.App moved to stay loaded ({Loaded(window)}): {window.Status}");
        }

        static string Loaded(MainWindow shown) => string.Join(", ", shown.ExplorerView.Tree.Rows.Where(r => r.Kind == NodeKind.Project)
            .Select(r => $"{r.Name} {(!r.IsDimmed ? "loaded" : r.Detail == "(unloaded)" ? "unloaded" : "missing")}").Order(StringComparer.Ordinal));
    }

    /// <summary>A file deleted takes its own row out, and Unload Project and Reload Project make the project's row again,
    /// dimmed and then open to open: every other row shown stays the row it was, rather than every row of the tree being
    /// made again for one.</summary>
    [Gate]
    [AvaloniaFact]
    public void Delete_unload_and_reload_make_again_only_the_rows_they_touch()
    {
        string order = PathNames.Canonical(Path.Combine(folder, "Shop.App", "Models", "Order.cs"));
        string core = PathNames.Canonical(Path.Combine(folder, "Shop.Core", "Shop.Core.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Show(window, order);
        Show(window, Path.Combine(folder, "Shop.Core", "Catalog", "Product.cs"));
        TreeRows<ExplorerNode> rows = window.ExplorerView.Tree.Rows;
        List<ExplorerNode> before = [.. rows];

        Ui.ClickRow(window, Show(window, order));
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => !File.Exists(order) && !rows.Any(r => r.Path == order), "Order.cs deleted and its row gone");
        Assert.True(rows.SequenceEqual(before.Where(r => r.Path != order), ReferenceEqualityComparer.Instance),
            $"Deleting Order.cs made rows other than its own again: [{string.Join(", ", rows.Where(r => !before.Contains(r, ReferenceEqualityComparer.Instance)).Select(r => r.Name))}] are new");

        before = [.. rows];
        ExplorerNode loaded = Row(window, NodeKind.Project, "Shop.Core");
        Ui.ChooseFromRowMenu(window, loaded, "Unload Project");
        ExplorerNode unloaded = Row(window, NodeKind.Project, "Shop.Core");
        Assert.True(unloaded.IsDimmed && !unloaded.HasChildren, "Unload Project left Shop.Core's row loaded");
        Assert.True(rows.SequenceEqual(before.Where(r => r == loaded || !PathNames.IsUnder(r.Path ?? r.Owner ?? string.Empty, Path.GetDirectoryName(core)!))
            .Select(r => r == loaded ? unloaded : r), ReferenceEqualityComparer.Instance),
            "Unload Project made rows other than the project's own again, or left what was under it");
        Assert.Same(unloaded, window.ExplorerView.Selected);

        before = [.. rows];
        Ui.ChooseFromRowMenu(window, unloaded, "Reload Project");
        ExplorerNode reloaded = Row(window, NodeKind.Project, "Shop.Core");
        Assert.True(!reloaded.IsDimmed && reloaded.HasChildren, "Reload Project left Shop.Core's row unloaded");
        Assert.True(rows.SequenceEqual(before.Select(r => r == unloaded ? reloaded : r), ReferenceEqualityComparer.Instance),
            "Reload Project made rows other than the project's own again");
        window.Close();
    }

    /// <summary>New File (its folder made with it), New Folder, a file copied and pasted and a file cut and pasted each put in
    /// or take out the rows of what they made or moved alone: every other row shown stays the row it was, with Go to File's
    /// folder never walked again.</summary>
    [Gate]
    [AvaloniaFact]
    public void New_file_new_folder_and_paste_make_again_only_the_rows_of_what_they_make_or_move()
    {
        string models = Path.Combine(folder, "Shop.App", "Models");
        string catalog = Path.Combine(folder, "Shop.Core", "Catalog");
        string order = Path.Combine(models, "Order.cs");
        string line = Path.Combine(models, "Lines", "Line.cs");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Show(window, order);
        Show(window, line);
        Show(window, Path.Combine(catalog, "Product.cs"));
        Show(window, Path.Combine(folder, "Shop.App", "Program.cs"));

        string drafts = Path.Combine(catalog, "Drafts");
        FilesFollowed.Check(window, "New File", [drafts], () => Add(Show(window, catalog), "New File…", "Drafts/Notes.txt"));
        Assert.True(File.Exists(Path.Combine(drafts, "Notes.txt")), $"New File made no Drafts/Notes.txt: {window.Status}");
        Assert.Contains(window.FileIndex.Files, f => f.EndsWith("Notes.txt", StringComparison.Ordinal));

        string archive = Path.Combine(models, "Archive");
        FilesFollowed.Check(window, "New Folder", [archive], () => Add(Show(window, models), "New Folder…", "Archive"));
        Assert.True(Directory.Exists(archive), $"New Folder made no Archive: {window.Status}");

        FilesFollowed.Check(window, "Copy and Paste", [Path.Combine(catalog, "Order.cs")], () =>
        {
            Ui.ClickRow(window, Show(window, order));
            Ui.Run(window, EditorActions.Copy);
            Ui.ClickRow(window, Show(window, catalog));
            Ui.Run(window, EditorActions.Paste);
        });
        Assert.Equal("Copied Order.cs to Catalog", window.Status);

        FilesFollowed.Check(window, "Cut and Paste", [line, Path.Combine(archive, "Line.cs")], () =>
        {
            Ui.ClickRow(window, Show(window, line));
            Ui.Run(window, EditorActions.Cut);
            Ui.ClickRow(window, Show(window, archive));
            Ui.Run(window, EditorActions.Paste);
        });
        Assert.Equal("Moved Line.cs to Archive", window.Status);
        Assert.Contains(window.FileIndex.Files, f => f.EndsWith(Path.Combine("Archive", "Line.cs"), StringComparison.Ordinal));
        Assert.DoesNotContain(window.FileIndex.Files, f => f.EndsWith(Path.Combine("Lines", "Line.cs"), StringComparison.Ordinal));
        window.Close();

        // Add, then the item, with the name typed into its popup and Enter pressed.
        void Add(ExplorerNode row, string item, string name)
        {
            Ui.ChooseFromRowMenu(window, row, "Add", item);
            Assert.IsType<NamePopup>(window.Popup);
            window.KeyTextInput(name);
            Ui.Press(window, "ENTER");
            Assert.False(window.IsPopupOpen, $"{item} refused {name}: {(window.Popup as NamePopup)?.Problem}");
        }
    }

    /// <summary>A project moved into a folder, its move taken back and made again, then taken back and the project moved
    /// into that folder once more, and last moved out and back by another program, is shown where it went as soon as each
    /// move is done, never dimmed as missing, and loaded there as MSBuild evaluates it.</summary>
    [AvaloniaFact]
    public void A_project_moved_into_a_folder_and_back_by_Undo_Redo_and_a_move_again_is_loaded_where_it_went_each_time()
    {
        string src = PathNames.Canonical(Directory.CreateDirectory(Path.Combine(folder, "src")).FullName);
        string home = PathNames.Canonical(Path.Combine(folder, "Shop.App", "Shop.App.csproj"));
        string moved = Path.Combine(src, "Shop.App", "Shop.App.csproj");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            List<string> asked = Answer(window);
            LoadedAt(home, "the folder opened");
            MoveIntoSrc();
            ShownAt(moved, "Shop.App moved into src");

            Put(EditorActions.Undo, "Undone");
            ShownAt(home, "Undo of the move");
            Put(EditorActions.Redo, "Redone");
            ShownAt(moved, "Redo of the move");

            Put(EditorActions.Undo, "Undone");
            ShownAt(home, "Undo of the move made again");
            MoveIntoSrc();
            ShownAt(moved, "Shop.App moved into src again, where Undo took it from");
            Assert.Equal(["Undo", "Redo", "Undo"], asked);

            Directory.Move(Path.GetDirectoryName(moved)!, Path.GetDirectoryName(home)!);
            Ui.WaitFor(() => window.Projects.SingleOrDefault(p => p.Name == "Shop.App") is { Problem: not null, Path: var path } && path == moved,
                () => $"Shop.App moved out of src by another program to fail to load there: {window.Status}");
            Directory.Move(Path.GetDirectoryName(home)!, Path.GetDirectoryName(moved)!);
            LoadedAt(moved, "Shop.App moved back into src by another program");
        }
        finally
        {
            window.Close();
        }

        // Cut and pasted in the File System view, where a project's folder is a row of its own.
        void MoveIntoSrc()
        {
            Button views = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Switch between the solution", StringComparison.Ordinal));
            Ui.Click(views);
            Ui.ClickRow(window, Show(window, Path.Combine(folder, "Shop.App")));
            Ui.Run(window, EditorActions.Cut);
            Ui.ClickRow(window, Show(window, src));
            Ui.Run(window, EditorActions.Paste);
            Assert.Equal("Moved Shop.App to src", window.Status);
            Ui.Click(views);
        }

        void Put(string action, string said)
        {
            Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Core"));
            Ui.Run(window, action);
            Ui.WaitFor(() => window.Status?.StartsWith(said, StringComparison.Ordinal) == true, () => $"{action} of the move: {window.Status}");
        }

        // As soon as the move says it is done, then once evaluated there.
        void ShownAt(string path, string after)
        {
            Assert.True(Row(window, NodeKind.Project, "Shop.App") is { IsDimmed: false } row && row.Path == path,
                $"Once {after}, the Solution view shows Shop.App {Shown()} rather than at {Path.GetRelativePath(folder, path)}: {window.Status}");
            LoadedAt(path, after);
        }

        void LoadedAt(string path, string after) => Ui.WaitFor(
            () => window.Projects.SingleOrDefault(p => p.Name == "Shop.App") is { IsEvaluated: true, Exists: true } project && project.Path == path
                && Row(window, NodeKind.Project, "Shop.App") is { IsDimmed: false } row && row.Path == path,
            () => $"Shop.App to be loaded at {Path.GetRelativePath(folder, path)} after {after}, where the Solution view shows it {Shown()}: {window.Status}");

        string Shown() => string.Join(", ", window.ExplorerView.Tree.Rows.Where(r => r.Kind == NodeKind.Project && r.Name == "Shop.App")
            .Select(r => $"{(r.IsDimmed ? "dimmed" : "loaded")} at {Path.GetRelativePath(folder, r.Path!)} {r.Detail}"));
    }

    private MainWindow Open() => Ui.OpenFolder(folder);

    /// <summary>Opens the Explorer's folders down to a file or folder, as a person does to get to it, and gives its row.</summary>
    private static ExplorerNode Show(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(PathNames.Canonical(path));
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == PathNames.Canonical(path))
            ?? throw new InvalidOperationException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    private static ExplorerNode Row(MainWindow window, NodeKind kind, string name, string? path = null) =>
        window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Kind == kind && r.Name == name && (path is null || r.Path == path))
        ?? throw new InvalidOperationException($"The Explorer shows no {kind} {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");

    /// <summary>A project's Dependencies row, the project opened to show it.</summary>
    private static ExplorerNode Dependencies(MainWindow window, string project)
    {
        ExplorerNode row = Row(window, NodeKind.Project, project);
        if (!row.IsExpanded)
        {
            Ui.ClickRow(window, row);
            Ui.Press(window, "RIGHT");
        }
        return window.ExplorerView.Tree.Rows.First(r => r.Kind == NodeKind.Dependencies && r.Owner == row.Path);
    }

    /// <summary>A drag's moves from one point to another, in steps, the button held, as a hand moves a mouse.</summary>
    private static void Move(MainWindow window, Point from, Point to)
    {
        for (int step = 1; step <= 10; step++)
        {
            window.MouseMove(from + ((to - from) * (step / 10.0)), RawInputModifiers.LeftMouseButton);
        }
        Ui.Settle();
    }

    /// <summary>Does something that writes a file the folder watcher reads the solution again for (a project file, a
    /// restore's .props), and waits for it to hear of it: the rows are made again then, and one found before is gone.</summary>
    private static void Heard(MainWindow window, string path, Action change)
    {
        int heard = 0;
        void Count(object? sender, FolderChanges changes) => heard += changes.Paths.Contains(PathNames.Canonical(path)) ? 1 : 0;
        window.FolderWatcher!.Changed += Count;
        try
        {
            change();
            Ui.WaitFor(() => heard > 0, $"the folder watcher to hear of {Path.GetFileName(path)} written");
            Ui.Settle();
        }
        finally
        {
            window.FolderWatcher!.Changed -= Count;
        }
    }

    /// <summary>Waits until the folder watcher has heard nothing for a while and what it heard has landed.</summary>
    private static void Quiet(MainWindow window)
    {
        DateTime last = DateTime.UtcNow;
        void Seen(object? sender, FolderChanges changes) => last = DateTime.UtcNow;
        window.FolderWatcher!.Changed += Seen;
        try
        {
            Ui.WaitFor(() => DateTime.UtcNow - last > TimeSpan.FromSeconds(1.5), "the folder watcher to hear the last of what was written");
        }
        finally
        {
            window.FolderWatcher!.Changed -= Seen;
        }
    }

    /// <summary>Agrees to every question the window asks, keeping their titles in order.</summary>
    private static List<string> Answer(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        return asked;
    }

    /// <summary>dotnet build of the solution as it is on disk now, which fails the test naming what was done to it.</summary>
    private static void Builds(string solution, string after)
    {
        ChildResult built = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", solution]), limit: Restore.Limit)),
            $"dotnet build {solution}", Restore.Limit + TestFolders.Spare);
        Assert.True(built.Succeeded, $"Once {after}, dotnet build no longer builds the solution:\n{built.Output}{built.Error}");
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
