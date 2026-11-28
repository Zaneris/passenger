using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Win32.SafeHandles;
using Passenger.App.Debugging;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Explorer's file operations: New Class writes a class in its folder's namespace and opens it; a rename
/// moves the file or folder and its open tabs with it, its project including and removing what it did; a delete goes to
/// the Trash and closes its tab, and a project's leaves the solution; a name already there is refused with
/// why.</summary>
[AboutMacOS]
public sealed class ExplorerFilesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("files-");
    private readonly string trash = TestFolders.Create("trash-");

    public ExplorerFilesTests()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.csproj"), TestProjects.Library());
        Directory.CreateDirectory(Path.Combine(folder, "Models"));
        Trash.Folder = trash;
    }

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    /// <summary>The Explorer's New, Rename, Delete, Paste and keys change the disk, the tabs and the tree as one
    /// command of Undo, a delete the Trash refuses keeping the open file and its edit, and a folder's chevron follows what
    /// it holds, as one flow, each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void The_Explorers_new_rename_and_delete_change_the_disk_the_tabs_and_the_tree_as_one_command()
    {
        Flow.StepApart<ExplorerFilesTests>("New class rename and delete change the disk the tabs and the explorer", t => t.New_class_rename_and_delete_change_the_disk_the_tabs_and_the_explorer());
        Flow.StepApart<ExplorerFilesTests>("New class in a project with its own RootNamespace begins its namespace with it", t => t.New_class_in_a_project_with_its_own_RootNamespace_begins_its_namespace_with_it());
        Flow.StepApart<ExplorerFilesTests>("a folder made in a project keeps its item until it holds a file or goes each one command of Undo", t => t.A_folder_made_in_a_project_keeps_its_item_until_it_holds_a_file_or_goes_each_one_command_of_Undo());
        Flow.StepApart<ExplorerFilesTests>("a name with two dots in a row is made and renamed to and only a part climbing out is refused", t => t.A_name_with_two_dots_in_a_row_is_made_and_renamed_to_and_only_a_part_climbing_out_is_refused());
        Flow.StepApart<ExplorerFilesTests>("a file renamed in the explorer keeps its tab in both splits its undo its bookmark and its breakpoint", t => t.A_file_renamed_in_the_explorer_keeps_its_tab_in_both_splits_its_undo_its_bookmark_and_its_breakpoint());
        Flow.StepApart<ExplorerFilesTests>("Delete on files chosen together asks once and moves them all to the Trash and one chosen alone is asked of by name", t => t.Delete_on_files_chosen_together_asks_once_and_moves_them_all_to_the_Trash_and_one_chosen_alone_is_asked_of_by_name());
        Flow.StepApart<ExplorerFilesTests>("Delete of a file a folder or several is one command Undo brings back from the Trash and Redo sends again", t => t.Delete_of_a_file_a_folder_or_several_is_one_command_Undo_brings_back_from_the_Trash_and_Redo_sends_again());
        Flow.StepApart<ExplorerFilesTests>("Delete on a folder chosen with a file inside it moves the folder once with the file", t => t.Delete_on_a_folder_chosen_with_a_file_inside_it_moves_the_folder_once_with_the_file());
        Flow.StepApart<ExplorerFilesTests>("Delete greys on a project chosen with a file saying the project is removed chosen alone", t => t.Delete_greys_on_a_project_chosen_with_a_file_saying_the_project_is_removed_chosen_alone());
        Flow.StepApart<ExplorerFilesTests>("a delete the Trash refuses keeps the open file and its unsaved edit and one it takes goes with them kept in Local History", t => t.A_delete_the_Trash_refuses_keeps_the_open_file_and_its_unsaved_edit_and_one_it_takes_goes_with_them_kept_in_Local_History());
        Flow.StepApart<ExplorerFilesTests>("a file or folder pasted where its name is the folder holding it is refused before any question and nothing moves", t => t.A_file_or_folder_pasted_where_its_name_is_the_folder_holding_it_is_refused_before_any_question_and_nothing_moves());
        Flow.StepApart<ExplorerFilesTests>("the explorers rename delete and reveal run from its menu and from each keymaps keys", t => t.The_explorers_rename_delete_and_reveal_run_from_its_menu_and_from_each_keymaps_keys());
        Flow.StepApart<ExplorerFilesTests>("the explorers keys open and close a folder go up from its file and open a file on enter", t => t.The_explorers_keys_open_and_close_a_folder_go_up_from_its_file_and_open_a_file_on_enter());
        Flow.StepApart<ExplorerFilesTests>("a folder with nothing in it shows no chevron and gets one as a file comes and loses it as the file goes", t => t.A_folder_with_nothing_in_it_shows_no_chevron_and_gets_one_as_a_file_comes_and_loses_it_as_the_file_goes());
        Flow.StepApart<ExplorerFilesTests>("an HTML page opens in the browser from the explorer its editors menu and the keys", t => t.An_HTML_page_opens_in_the_browser_from_the_explorer_its_editors_menu_and_the_keys());
    }

    /// <summary>A project deleted, moved or renamed in the Explorer takes its folder, its imports, its directives and
    /// what its build leaves out along, one Undo putting each back, and on Windows a folder a program holds is refused
    /// naming it, as one flow, each step in an instance of its own.</summary>
    [AvaloniaFact]
    public void Projects_moved_renamed_or_deleted_in_the_Explorer_take_their_paths_and_builds_along()
    {
        Flow.StepApart<ExplorerFilesTests>("Delete on a project in its own folder moves that folder to the Trash only once asked again and one beside its solution leaves the folder", t => t.Delete_on_a_project_in_its_own_folder_moves_that_folder_to_the_Trash_only_once_asked_again_and_one_beside_its_solution_leaves_the_folder());
        Flow.StepApart<ExplorerFilesTests>("Undo of a project removed with its folder brings back both and redo takes both again", t => t.Undo_of_a_project_removed_with_its_folder_brings_back_both_and_redo_takes_both_again());
        Flow.StepApart<ExplorerFilesTests>("a folder renamed keeps out of the build the files its project removed by name or by glob and undo puts the project back", t => t.A_folder_renamed_keeps_out_of_the_build_the_files_its_project_removed_by_name_or_by_glob_and_undo_puts_the_project_back());
        Flow.StepApart<ExplorerFilesTests>("a programs directives follow the project it names and the program itself moved and one undo puts each back", t => t.A_programs_directives_follow_the_project_it_names_and_the_program_itself_moved_and_one_undo_puts_each_back());
        Flow.StepApart<ExplorerFilesTests>("a project moved deeper follows its import and hint path and another projects import into it and still builds", t => t.A_project_moved_deeper_follows_its_import_and_hint_path_and_another_projects_import_into_it_and_still_builds());
        Flow.StepApart<ExplorerFilesTests>("a web projects Properties and wwwroot list first in the file system view once evaluated and refuse delete and rename", t => t.A_web_projects_Properties_and_wwwroot_list_first_in_the_file_system_view_once_evaluated_and_refuse_delete_and_rename());
    }

    /// <summary>On Windows a folder a program works in, or one under which a program holds a file, is refused a move
    /// naming the program and the file, as one flow of Windows's steps, each in an instance of its own.</summary>
    [AvaloniaFact]
    public void On_Windows_a_folder_a_program_works_in_or_holds_a_file_of_is_refused_naming_it()
    {
        Flow.StepApart<ExplorerFilesTests>("a folder a program works in is refused on Windows naming the program", t => t.A_folder_a_program_works_in_is_refused_on_Windows_naming_the_program());
        Flow.StepApart<ExplorerFilesTests>("a folder whose file a program holds is refused on Windows naming the program and the file (\"rename\")", t => t.A_folder_whose_file_a_program_holds_is_refused_on_Windows_naming_the_program_and_the_file("rename"));
    }

    /// <summary>A folder moved, renamed or deleted waits for the design-time builds and the language server's watches
    /// working in it, and holds off evaluating what walks it, so nothing typed or built there is lost, as one flow,
    /// each step in an instance of its own.</summary>
    [AvaloniaFact]
    public void A_folder_moved_or_deleted_waits_for_the_builds_and_the_server_working_in_it()
    {
        Flow.StepApart<ExplorerFilesTests>("typing in a file while its folders delete waits for a design time build goes to the Trash with it", t => t.Typing_in_a_file_while_its_folders_delete_waits_for_a_design_time_build_goes_to_the_Trash_with_it());
        Flow.StepApart<ExplorerFilesTests>("a loaded projects folder watched for the language server goes renames and moves (\"delete\")", t => t.A_loaded_projects_folder_watched_for_the_language_server_goes_renames_and_moves("delete"));
        Flow.StepApart<ExplorerFilesTests>("a loaded projects folder watched for the language server goes renames and moves (\"move\")", t => t.A_loaded_projects_folder_watched_for_the_language_server_goes_renames_and_moves("move"));
        Flow.StepApart<ExplorerFilesTests>("a loaded projects folder renamed as the generators question is answered waits for the answer and one asked meanwhile reads nothing there", t => t.A_loaded_projects_folder_renamed_as_the_generators_question_is_answered_waits_for_the_answer_and_one_asked_meanwhile_reads_nothing_there());
        Flow.StepApart<ExplorerFilesTests>("a loaded projects folder renamed as the language server loads it again waits for that load to end", t => t.A_loaded_projects_folder_renamed_as_the_language_server_loads_it_again_waits_for_that_load_to_end());
        Flow.StepApart<ExplorerFilesTests>("a folders rename or move waits for the design time build of its project to end (true)", t => t.A_folders_rename_or_move_waits_for_the_design_time_build_of_its_project_to_end(true));
        Flow.StepApart<ExplorerFilesTests>("a projects folder renamed or deleted as its design time build lands goes before the language server is told the project (true)", t => t.A_projects_folder_renamed_or_deleted_as_its_design_time_build_lands_goes_before_the_language_server_is_told_the_project(true));
        Flow.StepApart<ExplorerFilesTests>("a folder let go of holds off evaluating the projects whose wildcards walk it until let go", t => t.A_folder_let_go_of_holds_off_evaluating_the_projects_whose_wildcards_walk_it_until_let_go());
    }

    private void New_class_rename_and_delete_change_the_disk_the_tabs_and_the_explorer()
    {
        MainWindow window = Open();
        string models = PathNames.Canonical(Path.Combine(folder, "Models"));

        Assert.Null(window.CreateClass(models, "Order"));
        string order = Path.Combine(models, "Order.cs");
        Assert.Equal("namespace Shop.Models;\n\npublic class Order\n{\n}\n", File.ReadAllText(order));
        Assert.Equal("Order.cs", window.EditorArea.Active?.Name);
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Name == "Order.cs");
        Assert.Equal("Order.cs is already there", window.CreateFile(models, "Order.cs", string.Empty));

        Assert.Null(Ui.Await(window.RenameAsync(order, "Purchase.cs"), "the rename"));
        string purchase = Path.Combine(models, "Purchase.cs");
        Assert.True(File.Exists(purchase));
        Assert.False(File.Exists(order));
        Assert.Equal(["Purchase.cs"], window.EditorArea.Documents.Select(d => d.Name));

        Assert.Null(Ui.Await(window.RenameAsync(models, "Domain"), "the rename"));
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "Domain", "Purchase.cs")), Assert.Single(window.EditorArea.Documents).Path);

        window.Confirm = (_, _, _) => Task.FromResult(true);
        string moved = Path.Combine(folder, "Domain", "Purchase.cs");
        Task<bool> deleting = window.DeleteAsync(PathNames.Canonical(moved));
        Assert.True(Ui.Await(deleting, "the delete"));
        Assert.False(File.Exists(moved));
        Assert.True(File.Exists(Path.Combine(trash, "Purchase.cs")));
        Assert.Empty(window.EditorArea.Documents);
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Name == "Purchase.cs");

        string domain = PathNames.Canonical(Path.Combine(folder, "Domain"));
        File.WriteAllText(Path.Combine(domain, "Alpha.cs"), "class Alpha { }\n");
        File.WriteAllText(Path.Combine(domain, "Beta.cs"), "class Beta { }\n");
        window.OpenFile(Path.Combine(domain, "Alpha.cs"));
        window.OpenFile(Path.Combine(domain, "Beta.cs"));
        window.OpenFile(Path.Combine(domain, "Alpha.cs"));
        Ui.Settle();
        Assert.Null(Ui.Await(window.RenameAsync(domain, "Models"), "the rename"));
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "Models", "Alpha.cs")), window.EditorArea.Active?.Path);
        Assert.Equal(2, window.EditorArea.Documents.Count);

        // Typed as a person types it: the folder first, and the file's .cs by habit.
        string project = PathNames.Canonical(folder);
        Assert.Null(window.CreateType(project, "Shapes/Circle.cs", "Class"));
        string circle = Path.Combine(project, "Shapes", "Circle.cs");
        Assert.True(File.Exists(circle), $"Shapes/Circle.cs made no Shapes/Circle.cs, but {string.Join(", ", Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(project, f)))}");
        Assert.Equal("namespace Shop.Shapes;\n\npublic class Circle\n{\n}\n", File.ReadAllText(circle));
        Assert.Equal(PathNames.Canonical(circle), window.EditorArea.Active?.Path);

        Assert.Equal("Circle.Shape is not a C# name", window.CreateType(project, "Circle.Shape", "Class"));
        window.Close();
    }

    /// <summary>New Class in a project that names its own RootNamespace begins the type's namespace with it, as the rest of its
    /// code and the SDK's item templates do, never with the project file's name.</summary>
    private void New_class_in_a_project_with_its_own_RootNamespace_begins_its_namespace_with_it()
    {
        File.Delete(Path.Combine(folder, "Shop.csproj"));
        File.WriteAllText(Path.Combine(folder, "Shop.Core.Impl.csproj"), TestProjects.Library("<RootNamespace>Shop.Core</RootNamespace>"));
        MainWindow window = Open();
        string models = PathNames.Canonical(Path.Combine(folder, "Models"));
        Ui.WaitFor(() => window.ProjectOf(models) is { IsEvaluated: true }, "the project evaluated", TimeSpan.FromMinutes(1));

        Assert.Null(window.CreateClass(models, "Order"));
        string written = File.ReadAllText(Path.Combine(models, "Order.cs"));
        Assert.True(written == "namespace Shop.Core.Models;\n\npublic class Order\n{\n}\n",
            $"New Class took a namespace other than the project's RootNamespace and the folder's:\n{written}");
        window.Close();
    }

    /// <summary>New Folder in a project keeps the folder in its project file by a Folder item, as Visual Studio does, and a
    /// folder made inside it takes its place; a class made in it, or the folder deleted, takes the item out, a rename takes it
    /// along, and each is one command Undo takes back whole, the folder made with it; a file another program writes into it
    /// takes the item out too (#2297, #2388).</summary>
    private void A_folder_made_in_a_project_keeps_its_item_until_it_holds_a_file_or_goes_each_one_command_of_Undo()
    {
        string project = Path.Combine(folder, "Shop.csproj");
        string written = File.ReadAllText(project);
        string root = PathNames.Canonical(folder);
        string reports = Path.Combine(root, "Reports");
        MainWindow window = Open();
        List<string> asked = Answer(window);

        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Kind != NodeKind.File && r.Into is { } into && PathNames.Same(into, root)));
        Assert.True(window.Perform(AppActions.NewFolder), $"New Folder did not run on the project's folder: {window.Status}");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        window.KeyTextInput("Reports");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, () => $"New Folder to make Reports: {popup.Problem}");
        Assert.True(Directory.Exists(reports), $"New Folder made no Reports: {window.Status}");
        Assert.True(Kept("Reports") == File.ReadAllText(project), $"New Folder in Shop did not keep Reports in Shop.csproj as Visual Studio does:\n{File.ReadAllText(project)}");

        Assert.Null(Ui.Await(window.CreateFolderAsync(reports, "Monthly"), "New Folder Monthly"));
        Assert.True(Kept("Reports\\Monthly") == File.ReadAllText(project), $"Monthly made in Reports was not kept in Reports' place:\n{File.ReadAllText(project)}");
        Step(EditorActions.Undo, "Undone");
        Assert.False(Directory.Exists(Path.Combine(reports, "Monthly")), $"Undo of New Folder left Monthly: {window.Status}");
        Assert.Equal(Kept("Reports"), File.ReadAllText(project));
        Step(EditorActions.Redo, "Redone");
        Assert.True(Directory.Exists(Path.Combine(reports, "Monthly")) && Kept("Reports\\Monthly") == File.ReadAllText(project),
            $"Redo of New Folder did not make Monthly again with its item: {window.Status}\n{File.ReadAllText(project)}");
        Step(EditorActions.Undo, "Undone");

        Assert.Null(window.CreateClass(reports, "Summary"));
        Assert.True(written == File.ReadAllText(project), $"Reports, holding Summary.cs, kept its Folder item, which only an empty folder needs:\n{File.ReadAllText(project)}");
        Step(EditorActions.Undo, "Undone");
        Assert.False(File.Exists(Path.Combine(reports, "Summary.cs")), $"Undo of New Class left Summary.cs: {window.Status}");
        Assert.Equal(Kept("Reports"), File.ReadAllText(project));

        Assert.Null(Ui.Await(window.RenameAsync(reports, "Summaries"), "the rename"));
        Assert.Equal(Kept("Summaries"), File.ReadAllText(project));
        string summaries = Path.Combine(root, "Summaries");
        Assert.True(Ui.Await(window.DeleteAsync(summaries), "the delete"), $"Summaries was not deleted: {window.Status}");
        Assert.True(written == File.ReadAllText(project), $"Summaries deleted left its Folder item, naming a folder no longer there:\n{File.ReadAllText(project)}");
        Step(EditorActions.Undo, "Undone");
        Assert.True(Directory.Exists(summaries) && Kept("Summaries") == File.ReadAllText(project),
            $"Undo of the delete did not bring Summaries back with its item: {window.Status}\n{File.ReadAllText(project)}");

        string notes = TestFolders.Write(root, "Notes.txt", "notes\n");
        Assert.Null(Ui.Await(Moved(window.PutAsync([notes], summaries, copy: false)), "the move"));
        Assert.True(written == File.ReadAllText(project), $"Summaries, holding the Notes.txt moved into it, kept its Folder item:\n{File.ReadAllText(project)}");
        Step(EditorActions.Undo, "Undone");
        Assert.True(File.Exists(notes) && Kept("Summaries") == File.ReadAllText(project), $"Undo of the move did not put Notes.txt and Summaries' item back: {window.Status}");
        Assert.Equal(["Undo", "Redo", "Undo", "Undo", "Delete", "Undo", "Undo"], asked);

        // Another program's file in a kept folder, which the watcher hears, lets its item go too.
        File.WriteAllText(Path.Combine(summaries, "draft.txt"), "draft\n");
        Ui.WaitFor(() => written == File.ReadAllText(project), () => $"Summaries' item to go once another program wrote a file into it:\n{File.ReadAllText(project)}");
        window.Close();

        // The project's text keeping one folder, as Visual Studio writes it.
        string Kept(string kept) => written.With($"  <ItemGroup>\n    <Folder Include=\"{kept}\\\" />\n  </ItemGroup>\n");

        void Step(string action, string done)
        {
            window.ExplorerView.Reveal(root);
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Kind != NodeKind.File && r.Into is { } into && PathNames.Same(into, root)));
            Ui.Run(window, action);
            Ui.WaitFor(() => window.Status?.StartsWith(done, StringComparison.Ordinal) == true || window.Status?.StartsWith("Cannot", StringComparison.Ordinal) == true,
                () => $"{action} to end: {window.Status}");
        }
    }

    /// <summary>A project's folder deleted while its design-time build runs waits for the build to end; what is typed into
    /// the project's open file meanwhile goes to the Trash with it, the file saved again just before it goes.</summary>
    private void Typing_in_a_file_while_its_folders_delete_waits_for_a_design_time_build_goes_to_the_Trash_with_it()
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        string release = Path.Combine(folder, "release");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(library, "Code.cs"), "class Code\n{\n}\n");
        File.WriteAllText(Path.Combine(library, "Directory.Build.targets"), Holding(release));
        Restore.Run(Path.Combine(library, "Lib.csproj"));
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        try
        {
            // Pumped, not settled: the language server's load waits for the design-time build.
            Ui.PumpUntil(() => File.Exists(Path.Combine(library, "held")), "Lib's design-time build to be held", TimeSpan.FromMinutes(2));
            string code = PathNames.Canonical(Path.Combine(library, "Code.cs"));
            window.OpenFile(code);
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Task<bool> deleting = window.DeleteAsync(PathNames.Canonical(library));
            Ui.PumpUntil(() => deleting.IsCompleted || window.Status?.StartsWith("Waiting for the design-time build of Lib", StringComparison.Ordinal) == true,
                "the delete to wait for Lib's design-time build");
            Assert.False(deleting.IsCompleted, $"The delete of Lib did not wait for its design-time build: {window.Status}");
            window.EditorArea.CodeEditor.Focus();
            window.KeyTextInput("// typed while it waited ");
            Assert.True(window.EditorArea.Active is { IsModified: true }, "Code.cs took no typing while its folder's delete waited");
            File.WriteAllText(release, "go");
            Assert.True(Ui.AwaitPumped(deleting, "the delete of Lib", TimeSpan.FromMinutes(2)), $"Lib was not deleted: {window.Status}{Holders(window, library)}");
            Assert.Equal("// typed while it waited class Code\n{\n}\n", File.ReadAllText(Path.Combine(trash, "Lib", "Code.cs")));
            Assert.Empty(window.EditorArea.Documents);
        }
        finally
        {
            File.WriteAllText(release, "go");
            window.Close();
        }
    }

    /// <summary>A loaded project's folder, whose build output is watched for the C# language server, goes to the Trash, takes a
    /// new name or moves into another folder: what watches inside it lets go first, as Windows moves and deletes no folder
    /// with one inside it watched, and nothing under it is watched once it is gone.</summary>
    private void A_loaded_projects_folder_watched_for_the_language_server_goes_renames_and_moves(string change)
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(library, "Code.cs"), "class Code\n{\n}\n");
        Restore.Run(Path.Combine(library, "Lib.csproj"));
        MainWindow window = Ui.OpenFolder(folder);
        string lib = PathNames.Canonical(library);
        Ui.WaitFor(() => window.CSharpService?.Watching?.Watching.Any(w => PathNames.Within(w.Folder, lib)) == true,
            () => $"Lib's build output to be watched for the C# language server, where {string.Join(", ", (window.CSharpService?.Watching?.Watching ?? []).Select(w => w.Folder))} are",
            TimeSpan.FromMinutes(2));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        string models = PathNames.Canonical(Path.Combine(folder, "Models"));
        string? refused = change switch
        {
            "delete" => Ui.Await(window.DeleteAsync(lib), "the delete") ? null : "it was not deleted",
            "rename" => Ui.Await(window.RenameAsync(lib, "Core"), "the rename"),
            _ => Ui.Await(window.PutAsync([lib], models, copy: false), "the move").Count > 0 ? null : "nothing was moved",
        };

        Assert.True(refused is null && !Directory.Exists(lib), $"Lib's {change} was refused: {refused}, the status bar saying {window.Status}{Holders(window, lib)}");
        string went = change switch
        {
            "delete" => Path.Combine(trash, "Lib"),
            "rename" => Path.Combine(folder, "Core"),
            _ => Path.Combine(models, "Lib"),
        };
        Assert.True(File.Exists(Path.Combine(went, "Lib.csproj")), $"Lib's {change} did not take it to {went}: {window.Status}");
        Ui.Settle();
        Assert.DoesNotContain(window.CSharpService?.Watching?.Watching ?? [], w => PathNames.Within(w.Folder, lib));
        window.Close();
    }

    /// <summary>A loaded project's folder renamed as the C# language server answers the window's question of whether its
    /// generators ran, which reads each document the projects' builds wrote, waits for the answer, and the question asked
    /// meanwhile reads nothing under the folder: on Windows a folder with a file under it open does not move. The answer is
    /// held, as a busy machine holds it.</summary>
    private void A_loaded_projects_folder_renamed_as_the_generators_question_is_answered_waits_for_the_answer_and_one_asked_meanwhile_reads_nothing_there()
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(library, "Code.cs"), "class Code\n{\n}\n");
        Restore.Run(Path.Combine(library, "Lib.csproj"));
        MainWindow window = Ui.OpenFolder(folder);
        string lib = PathNames.Canonical(library);
        Ui.WaitFor(() => window.CSharpService?.Watching?.Watching.Any(w => PathNames.Within(w.Folder, lib)) == true,
            () => $"Lib's build output to be watched for the C# language server, where {string.Join(", ", (window.CSharpService?.Watching?.Watching ?? []).Select(w => w.Folder))} are",
            TimeSpan.FromMinutes(2));
        Passenger.Lsp.RoslynSession server = window.CSharpService!.Session!;
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = server.Server.HoldNextAnswer(Passenger.Lsp.LspMethods.DispatchExtensionMessage, p => AsksGenerators(p) is not null, released.Task);
        Task<Passenger.Roslyn.Extension.GeneratorsRan> asked = server.GeneratorsRunAsync();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Task<string?> renaming;
        string? passed = null;
        try
        {
            // Pumped, not settled: the answer held holds the IDE's quiet.
            Ui.PumpUntil(() => held.IsCompleted, "the generators' question to be answered and its answer held", TimeSpan.FromMinutes(1));
            renaming = window.RenameAsync(lib, "Core");
            Ui.PumpUntil(() => renaming.IsCompleted || window.Status?.StartsWith("Waiting for the design-time build of Lib", StringComparison.Ordinal) == true,
                "the rename to wait for the generators' answer");
            Assert.False(renaming.IsCompleted, $"Lib was renamed while the generators' question, which reads its builds' documents, was still to be answered: {window.Status}");
            Task told = server.Server.HoldNext(Passenger.Lsp.LspMethods.DispatchExtensionMessage, p => (passed ??= AsksGenerators(p)) is not null, Task.CompletedTask);
            Ui.AwaitPumped(server.GeneratorsRunAsync(), "the generators' question asked as the rename waits", TimeSpan.FromMinutes(1));
            Assert.True(told.IsCompleted, "The generators' question asked as the rename waits was never sent");
        }
        finally
        {
            released.TrySetResult();
        }
        Assert.True(passed is not null && System.Text.Json.JsonSerializer.Deserialize<Passenger.Roslyn.Extension.GeneratorsAsked>(passed)?.Passing.Any(f => PathNames.Same(f, lib)) == true,
            $"The generators' question asked as Lib's rename waited did not pass over Lib, so it may read there as Lib moves: {passed}");
        Ui.AwaitPumped(asked, "the generators' question held");
        string? refused = Ui.AwaitPumped(renaming, "the rename", TimeSpan.FromMinutes(1));
        Assert.True(refused is null && File.Exists(Path.Combine(folder, "Core", "Lib.csproj")), $"Lib's rename did not take it to Core: {refused ?? window.Status}{Holders(window, lib)}");
        window.Close();
    }

    /// <summary>A loaded project's folder renamed as the C# language server loads the project again, which reads each of its
    /// C# files and its assets file, waits for that load to end, then goes: on Windows a folder with a file under it open
    /// does not move. The load's end is held, as a busy machine holds it.</summary>
    private void A_loaded_projects_folder_renamed_as_the_language_server_loads_it_again_waits_for_that_load_to_end()
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(library, "Code.cs"), "class Code\n{\n}\n");
        Restore.Run(Path.Combine(library, "Lib.csproj"));
        MainWindow window = Ui.OpenFolder(folder);
        string lib = PathNames.Canonical(library);
        string project = Path.Combine(lib, "Lib.csproj");
        Ui.WaitFor(() => window.CSharpService?.Session is { IsLoaded: true, Fed: true }, () => $"Lib to load ({window.CSharpService?.State})", TimeSpan.FromMinutes(2));
        Passenger.Lsp.RoslynSession server = window.CSharpService!.Session!;
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = server.HoldLoadEnd(project, released.Task);
        window.CSharpService.Reload([project]);
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Task<string?> renaming;
        try
        {
            Ui.PumpUntil(() => held.IsCompleted, "Lib's load again to end and its end held", TimeSpan.FromMinutes(1));
            // What the window does as the load ends (its generators asked again) is over first: only the load is left to wait for.
            Ui.Settle();
            renaming = window.RenameAsync(lib, "Core");
            Ui.PumpUntil(() => renaming.IsCompleted || window.Status?.StartsWith("Waiting for the design-time build of Lib", StringComparison.Ordinal) == true,
                "the rename to wait for Lib's load");
            Assert.False(renaming.IsCompleted, $"Lib was renamed while the language server's load of it, which reads its files, was still under way: {window.Status}");
        }
        finally
        {
            released.TrySetResult();
        }
        string? refused = Ui.AwaitPumped(renaming, "the rename", TimeSpan.FromMinutes(1));
        Assert.True(refused is null && File.Exists(Path.Combine(folder, "Core", "Lib.csproj")), $"Lib's rename did not take it to Core: {refused ?? window.Status}{Holders(window, lib)}");
        window.Close();
    }

    /// <summary>The message of a request to the extension asking whether the generators ran, or null for another request.</summary>
    private static string? AsksGenerators(object parameters) =>
        parameters is Passenger.Lsp.RoslynExtension.DispatchWorkspaceMessageParams { MessageName: Passenger.Roslyn.Extension.Handlers.GeneratorsRun } ask ? ask.Message : null;

    /// <summary>A project's folder renamed, or moved into another folder, while its design-time build runs waits for the build
    /// to end, as a delete does: the build, still writing its obj/, would make the folder again where it was, and on Windows
    /// the folder it works in would not move.</summary>
    private void A_folders_rename_or_move_waits_for_the_design_time_build_of_its_project_to_end(bool move)
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        string release = Path.Combine(folder, "release");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(library, "Code.cs"), "class Code\n{\n}\n");
        File.WriteAllText(Path.Combine(library, "Directory.Build.targets"), Holding(release));
        Restore.Run(Path.Combine(library, "Lib.csproj"));
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        string change = move ? "move" : "rename";
        try
        {
            // Pumped, not settled: the language server's load waits for the design-time build.
            Ui.PumpUntil(() => File.Exists(Path.Combine(library, "held")), "Lib's design-time build to be held", TimeSpan.FromMinutes(2));
            string lib = PathNames.Canonical(library);
            string models = PathNames.Canonical(Path.Combine(folder, "Models"));
            Task<string?> changing = move ? Moved(window.PutAsync([lib], models, copy: false)) : window.RenameAsync(lib, "Core");
            string went = move ? Path.Combine(models, "Lib") : Path.Combine(folder, "Core");
            Ui.PumpUntil(() => changing.IsCompleted || window.Status?.StartsWith("Waiting for the design-time build of Lib", StringComparison.Ordinal) == true,
                $"the {change} to wait for Lib's design-time build");
            Assert.False(changing.IsCompleted, $"The {change} of Lib did not wait for its design-time build, which still writes its obj/: {window.Status}");

            File.WriteAllText(release, "go");
            string? refused = Ui.AwaitPumped(changing, $"the {change} of Lib", TimeSpan.FromMinutes(2));
            Assert.True(refused is null && File.Exists(Path.Combine(went, "Lib.csproj")),
                $"Lib's {change} did not take it to {went}: {refused ?? "it said it did"}, the status bar saying {window.Status}{Holders(window, library)}");
            Assert.False(Directory.Exists(library), $"Lib is there again after its {change}, holding [{string.Join(", ", Left(library))}]: its design-time build wrote where it had been");
        }
        finally
        {
            File.WriteAllText(release, "go");
            window.Close();
        }
    }

    /// <summary>A project's folder renamed or deleted while its design-time build runs, the C# language server waiting for
    /// that build, goes before the server is told the project: telling it looks at the project's files, and the server then
    /// reads them, as the build lands, and on Windows a folder with a file under it open does not move. The project's file
    /// is shown, so the server asks for it first.</summary>
    private void A_projects_folder_renamed_or_deleted_as_its_design_time_build_lands_goes_before_the_language_server_is_told_the_project(bool delete)
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        string release = Path.Combine(folder, "release");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(library, "Code.cs"), "class Code\n{\n}\n");
        File.WriteAllText(Path.Combine(library, "Directory.Build.targets"), Holding(release));
        Restore.Run(Path.Combine(library, "Lib.csproj"));
        string lib = PathNames.Canonical(library);
        string change = delete ? "delete" : "rename";
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(Path.Combine(lib, "Code.cs"));
        ProjectBuilds builds = window.Session.DesignTimeBuilds;
        string project = Path.Combine(lib, "Lib.csproj");
        Task asked = builds.HoldNextAsked(project, Task.CompletedTask);
        try
        {
            // Pumped, not settled: the language server's load waits for the design-time build.
            Ui.PumpUntil(() => File.Exists(Path.Combine(library, "held")), "Lib's design-time build to be held", TimeSpan.FromMinutes(2));
            Assert.True(Ui.Pumped(() => asked.IsCompleted, TimeSpan.FromMinutes(2)),
                $"The language server never asked for Lib's design-time build, {builds.Awaited} awaited{Ui.IdeLogTail()}{Ui.ServerLogTail()}");
            Task<string?>? changing = null;
            bool? toldAfter = null;
            builds.WhileNextLook(project, () => toldAfter = changing?.IsCompleted);
            window.Confirm = (_, _, _) => Task.FromResult(true);
            changing = delete ? Deleted(window, lib) : window.RenameAsync(lib, "Core");
            Ui.PumpUntil(() => changing.IsCompleted || window.Status?.StartsWith("Waiting for the design-time build of Lib", StringComparison.Ordinal) == true,
                $"the {change} to wait for Lib's design-time build");
            Assert.False(changing.IsCompleted, $"The {change} of Lib did not wait for its design-time build: {window.Status}");

            File.WriteAllText(release, "go");
            string? refused = Ui.AwaitPumped(changing, $"the {change} of Lib", TimeSpan.FromMinutes(2));
            Assert.True(refused is null && !Directory.Exists(library), $"Lib's {change} was refused: {refused}, the status bar saying {window.Status}{Holders(window, library)}");
            Ui.PumpUntil(() => toldAfter is not null, "the language server to be told Lib", TimeSpan.FromMinutes(1));
            Assert.True(toldAfter, $"The language server was told Lib, its files looked at, as its folder's {change} waited to go: the server then reads them, and on Windows a folder with a file under it open does not move");
        }
        finally
        {
            File.WriteAllText(release, "go");
            window.Close();
        }

        static async Task<string?> Deleted(MainWindow window, string lib) => await window.DeleteAsync(lib) ? null : window.Status;
    }

    /// <summary>On Windows a round of design-time builds is over only once the processes its build session started, MSBuild's
    /// nodes, are gone: a node goes on working in the folder of the project it built last until its process is gone, and a
    /// folder going, which waits for the round that built its project, would not move while one does. Shop's and Lib's builds
    /// are held at once, so one of them runs in a node.</summary>
    [AvaloniaFact]
    public void A_round_of_design_time_builds_is_over_only_once_the_msbuild_nodes_its_session_started_are_gone()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only Windows keeps a folder a process works in from moving, which the round's end is waited for");
            return;
        }
        SkipUnlessTwoBuildsAtOnce();
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        string releaseLib = Path.Combine(folder, "release-lib");
        string releaseShop = Path.Combine(folder, "release-shop");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(library, "Code.cs"), "class Code\n{\n}\n");
        File.WriteAllText(Path.Combine(library, "Directory.Build.targets"), Holding(releaseLib));
        File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), Holding(releaseShop));
        Restore.Run(Path.Combine(library, "Lib.csproj"));
        Restore.Run(Path.Combine(folder, "Shop.csproj"));
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        try
        {
            // Pumped, not settled: the language server's load waits for the design-time builds.
            Ui.PumpUntil(() => File.Exists(Path.Combine(library, "held")) && File.Exists(Path.Combine(folder, "held")),
                "Shop's and Lib's design-time builds to be held at once, in one round", TimeSpan.FromMinutes(2));
            int host = Ui.AwaitPumped(Waiting.Apart(() => Passenger.Workspace.ProjectHosts.ForAsync(folder)), "the folder's project host").ProcessId;
            // The round's nodes, held by handles of the test's own so no id is another process's by the time it is asked.
            List<(SafeProcessHandle Process, string Line)> nodes = [];
            foreach ((int child, int parent) in Kernel32.Processes())
            {
                SafeProcessHandle process = Kernel32.OpenProcess(Kernel32.Synchronize | Kernel32.ProcessQueryLimitedInformation, false, (uint)child);
                if (parent == host && !process.IsInvalid)
                {
                    nodes.Add((process, $"{DotnetProcesses.Describe(child)?.CommandLine ?? "a process"} ({child})"));
                }
                else
                {
                    process.Dispose();
                }
            }
            try
            {
                Assert.True(nodes.Count > 0, $"The project host (process {host}) started no process of its own to build Shop and Lib at once, so no node is there to be waited for");
                (IDisposable held, Task over) = window.Session.DesignTimeBuilds.HoldOff([PathNames.Canonical(library)]);
                using (held)
                {
                    File.WriteAllText(releaseLib, "go");
                    File.WriteAllText(releaseShop, "go");
                    Ui.AwaitPumped(over, "the round that builds Lib to be over", TimeSpan.FromMinutes(2));
                    List<string> left = [];
                    foreach ((SafeProcessHandle process, string line) in nodes)
                    {
                        if (Kernel32.WaitForSingleObject(process, 0) != 0)
                        {
                            left.Add(line);
                        }
                    }
                    Assert.True(left.Count == 0, $"The round that built Lib was over while processes its session started still ran, which work in the folder of the project each built last: {string.Join("; ", left)}");
                }
            }
            finally
            {
                nodes.ForEach(n => n.Process.Dispose());
            }
        }
        finally
        {
            File.WriteAllText(releaseLib, "go");
            File.WriteAllText(releaseShop, "go");
            window.Close();
        }
    }

    /// <summary>A folder let go of for a move or a delete holds off evaluating the projects that reach it, here Shop, whose
    /// wildcards walk Models though no project lies there, until let go, the round under way waited for: a round reads
    /// what each project's evaluation read and evaluates in the project host, and on Windows a folder with a file or folder
    /// under it open does not move.</summary>
    private void A_folder_let_go_of_holds_off_evaluating_the_projects_whose_wildcards_walk_it_until_let_go()
    {
        File.WriteAllText(Path.Combine(folder, "Models", "Order.cs"), "class Order { }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        ProjectModels evaluations = window.Session.Models;
        string models = PathNames.Canonical(Path.Combine(folder, "Models"));
        string shop = PathNames.Canonical(Path.Combine(folder, "Shop.csproj"));
        TaskCompletionSource evaluating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reached = evaluations.HoldEvaluation(shop, evaluating.Task);
        File.WriteAllText(Path.Combine(folder, "Extra.cs"), "class Extra { }\n");
        window.Session.Evaluate();
        Ui.PumpUntil(() => reached.IsCompleted, "Shop's evaluation, its wildcards taking in Extra.cs, to begin");
        (IDisposable held, Task evaluated) = evaluations.HoldOff([models]);
        int before;
        try
        {
            Assert.False(evaluated.IsCompleted, "Models was let go of while Shop's evaluation, whose wildcards walk Models, was still under way");
            evaluating.SetResult();
            Ui.AwaitPumped(evaluated, "the round evaluating Shop to end");
            before = evaluations.Rounds;
            File.WriteAllText(Path.Combine(folder, "More.cs"), "class More { }\n");
            window.Session.Evaluate();
            Ui.Settle();
            Assert.True(evaluations.Rounds == before, $"A round began, looking at Shop, whose wildcards walk Models, while Models was let go of ({evaluations.Rounds - before} begun)");
        }
        finally
        {
            evaluating.TrySetResult();
            held.Dispose();
        }
        Ui.WaitFor(() => evaluations.Rounds > before, "the round held off to begin once Models was let go of");
        window.Close();
    }

    /// <summary>On Windows a folder some program works in will not go to the Trash, and the refusal names that program, so
    /// the person knows what holds it.</summary>
    private void A_folder_a_program_works_in_is_refused_on_Windows_naming_the_program()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows keeps a folder a program works in from going");
        string library = PathNames.Canonical(Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName);
        File.WriteAllText(Path.Combine(library, "Notes.txt"), "kept\n");
        string shell = Programs.OnPath("powershell.exe") ?? throw new InvalidOperationException("Windows has no powershell.exe on its PATH");
        MainWindow window = Open();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        string started = Path.Combine(folder, "started");
        using System.Diagnostics.Process working = ChildProcesses.Start(ChildProcesses.Command(shell,
            ["-NoProfile", "-Command", $"Set-Content -Path '{started}' -Value started; Start-Sleep -Seconds 120"], library));
        try
        {
            Ui.WaitFor(() => File.Exists(started), "powershell to start, working in Lib");
            Assert.False(Ui.Await(window.DeleteAsync(library), "the delete"), $"Lib went to the Trash while powershell worked in it: {window.Status}");
            Assert.True(window.Status?.Contains($"Held by powershell (process {working.Id})", StringComparison.Ordinal) == true,
                $"The refusal does not name powershell, which works in Lib, so the person cannot tell what to close: {window.Status}");
            Assert.True(File.Exists(Path.Combine(library, "Notes.txt")), "Lib lost its file although the delete was refused");
        }
        finally
        {
            ChildProcesses.Kill(working);
        }
        window.Close();
    }

    /// <summary>On Windows a folder with a file under it some program holds open will not take a new name or move, and the
    /// refusal names that program and the file, so the person knows what to close.</summary>
    private void A_folder_whose_file_a_program_holds_is_refused_on_Windows_naming_the_program_and_the_file(string change)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows keeps a folder with a file under it held open from moving");
        string library = PathNames.Canonical(Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName);
        string held = Path.Combine(Directory.CreateDirectory(Path.Combine(library, "obj")).FullName, "held.txt");
        File.WriteAllText(held, "held\n");
        string shell = Programs.OnPath("powershell.exe") ?? throw new InvalidOperationException("Windows has no powershell.exe on its PATH");
        MainWindow window = Open();
        string started = Path.Combine(folder, "started");
        using System.Diagnostics.Process holding = ChildProcesses.Start(ChildProcesses.Command(shell,
            ["-NoProfile", "-Command", $"$held = [IO.File]::Open('{held}', 'Open', 'Read', 'ReadWrite, Delete'); Set-Content -Path '{started}' -Value started; Start-Sleep -Seconds 120"], folder));
        try
        {
            Ui.WaitFor(() => File.Exists(started), "powershell to start, holding Lib/obj/held.txt open");
            string models = PathNames.Canonical(Path.Combine(folder, "Models"));
            string? refused = change == "rename" ? Ui.Await(window.RenameAsync(library, "Core"), "the rename")
                : Ui.Await(window.PutAsync([library], models, copy: false), "the move").Count > 0 ? null : window.Status;
            Assert.True(refused?.Contains($"Held by powershell (process {holding.Id}: {Path.Join("obj", "held.txt")})", StringComparison.Ordinal) == true,
                $"The {change}'s refusal does not name powershell and the file it holds under Lib, so the person cannot tell what to close: {refused ?? "it went"}");
            Assert.True(File.Exists(Path.Combine(library, "obj", "held.txt")), $"Lib is not where it was although its {change} was refused");
        }
        finally
        {
            ChildProcesses.Kill(holding);
        }
        window.Close();
    }

    /// <summary>Skips a test that holds Shop's and Lib's design-time builds at once: the project host gives MSBuild half the
    /// processors, and builds two projects at once only on two nodes; on fewer, one's hold keeps the other from starting
    /// until its Exec runs out.</summary>
    private static void SkipUnlessTwoBuildsAtOnce() => Assert.SkipWhen(Environment.ProcessorCount / 2 < 2,
        $"The project host builds two projects at once only on two MSBuild nodes, half of four processors, and this machine has {Environment.ProcessorCount}");

    /// <summary>A Directory.Build.targets holding the design-time builds of the projects below it before CoreCompile, each
    /// writing held into its project's folder, until <paramref name="release"/> is there.</summary>
    private static string Holding(string release) => $$"""
        <Project>
          <Target Name="Hold" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true' And !Exists('{{release}}')">
            <Exec Condition="'$(OS)' == 'Windows_NT'" Timeout="120000" Command="powershell -NoProfile -Command &quot;Set-Content -Path held -Value held; while (-not (Test-Path '{{release}}')) { Start-Sleep -Milliseconds 100 }&quot;" />
            <Exec Condition="'$(OS)' != 'Windows_NT'" Timeout="120000" Command="echo held &gt; held; while [ ! -f '{{release}}' ]; do sleep 0.1; done" />
          </Target>
        </Project>
        """;

    private void Delete_on_a_project_in_its_own_folder_moves_that_folder_to_the_Trash_only_once_asked_again_and_one_beside_its_solution_leaves_the_folder()
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        File.Copy(Path.Combine(folder, "Shop.csproj"), Path.Combine(library, "Lib.csproj"));
        string solution = Path.Combine(folder, "Shop.slnx");
        File.WriteAllText(solution, "<Solution>\n  <Project Path=\"Shop.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        MainWindow window = Open();
        List<string> asked = Answer(window);

        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Lib"));
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => Finished(window, "Moved"), "the project's folder to go");
        Assert.Equal("Moved Lib to the Trash", window.Status);
        Assert.Equal(["Remove Project", "Delete Project Files"], asked);
        Assert.False(Directory.Exists(library), $"The project's own folder stayed after the second question was answered, holding [{string.Join(", ", Left(library))}]");
        Assert.True(File.Exists(Path.Combine(trash, "Lib", "Lib.csproj")), "The project's folder is not in the Trash");
        string left = File.ReadAllText(solution);
        Assert.Contains("Shop.csproj", left, StringComparison.Ordinal);
        Assert.DoesNotContain("Lib.csproj", left, StringComparison.Ordinal);

        // The project and the solution share the repository's top folder, which Delete must never offer to trash.
        asked.Clear();
        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project));
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => Finished(window, "Removed"), "the project's removal");
        Assert.Equal("Removed Shop from Shop", window.Status);
        Assert.DoesNotContain("Shop.csproj", File.ReadAllText(solution), StringComparison.Ordinal);
        Assert.Equal(["Remove Project"], asked);
        Assert.True(File.Exists(Path.Combine(folder, "Shop.csproj")), "The project's file went with it");
        Assert.True(Directory.Exists(Path.Combine(folder, "Models")), "The folder beside the solution went to the Trash");
        Assert.True(Directory.EnumerateFileSystemEntries(trash).Select(Path.GetFileName).SequenceEqual(["Lib"]),
            $"Removing the project beside its solution put [{string.Join(", ", Directory.EnumerateFileSystemEntries(trash).Select(Path.GetFileName))}] in the Trash, where only Lib was");
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project);
        window.Close();
    }

    /// <summary>A project removed with its folder moved to the Trash, as the second question asks, is one command: one Undo
    /// puts the project back in the solution and brings its folder back out of the Trash, and Redo takes both away again.</summary>
    private void Undo_of_a_project_removed_with_its_folder_brings_back_both_and_redo_takes_both_again()
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        File.Copy(Path.Combine(folder, "Shop.csproj"), Path.Combine(library, "Lib.csproj"));
        File.WriteAllText(Path.Combine(library, "Shelf.cs"), "class Shelf { }\n");
        string solution = Path.Combine(folder, "Shop.slnx");
        string listed = "<Solution>\n  <Project Path=\"Shop.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n";
        File.WriteAllText(solution, listed);
        MainWindow window = Open();
        List<string> asked = Questions(window);

        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Lib"));
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => Finished(window, "Moved"), "the project's folder to go");
        Assert.False(Directory.Exists(library), $"Lib's folder stayed: {window.Status}");
        Assert.DoesNotContain("Lib.csproj", File.ReadAllText(solution), StringComparison.Ordinal);
        Assert.Equal("Remove Project Lib", window.EditorArea.Commands.ToUndo(null)?.Name);

        asked.Clear();
        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Shop"));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => window.Status?.StartsWith("Undone", StringComparison.Ordinal) == true || window.Status?.StartsWith("Cannot", StringComparison.Ordinal) == true,
            () => $"Undo of the removal: {window.Status}");
        Assert.True(asked.Count == 1 && asked[0].Contains("brings back Lib", StringComparison.Ordinal), $"Undo asked: {string.Join(" | ", asked)}");
        Assert.True(File.ReadAllText(Path.Combine(library, "Shelf.cs")) == "class Shelf { }\n" && File.Exists(Path.Combine(library, "Lib.csproj")),
            $"Undo of the removal left Lib's folder in the Trash, holding [{string.Join(", ", Left(trash))}]: {window.Status}");
        Assert.Equal(listed, File.ReadAllText(solution));
        Assert.Empty(Directory.EnumerateFileSystemEntries(trash));

        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Shop"));
        Ui.Run(window, EditorActions.Redo);
        Ui.WaitFor(() => window.Status?.StartsWith("Redone", StringComparison.Ordinal) == true || window.Status?.StartsWith("Cannot", StringComparison.Ordinal) == true,
            () => $"Redo of the removal: {window.Status}");
        Assert.False(Directory.Exists(library), $"Redo of the removal left Lib's folder: {window.Status}");
        Assert.True(File.Exists(Path.Combine(trash, "Lib", "Shelf.cs")), "Redo did not move Lib's folder to the Trash again");
        Assert.DoesNotContain("Lib.csproj", File.ReadAllText(solution), StringComparison.Ordinal);
        window.Close();
    }

    private void A_delete_the_Trash_refuses_keeps_the_open_file_and_its_unsaved_edit_and_one_it_takes_goes_with_them_kept_in_Local_History()
    {
        string notes = PathNames.Canonical(Path.Combine(folder, "Notes.txt"));
        File.WriteAllText(notes, "groceries\n");
        MainWindow window = Open();
        window.Autosaves = false;
        window.OpenFile(notes);
        window.EditorArea.CodeEditor.Focus();
        window.KeyTextInput("milk, ");
        Ui.Settle();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        // A Trash that is a file takes nothing, as one on another volume or a locked file would refuse.
        string full = Path.Combine(trash, "full");
        File.WriteAllText(full, string.Empty);
        Trash.Folder = full;

        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Name == "Notes.txt"));
        Ui.Press(window, "BACK_SPACE");
        Assert.StartsWith("Notes.txt could not be deleted", window.Status, StringComparison.Ordinal);
        Assert.True(File.Exists(notes), "The file left although the Trash refused it");
        OpenDocument open = Assert.Single(window.EditorArea.Documents);
        Assert.Equal("milk, groceries\n", open.Text.Snapshot.ToString());
        Assert.True(File.ReadAllText(notes) == "milk, groceries\n" || open.IsModified, "The unsaved edit was marked saved without being written, so it would be lost");

        Trash.Folder = trash;
        window.EditorArea.CodeEditor.Focus();
        window.KeyTextInput("eggs, ");
        Ui.Settle();
        Assert.True(window.EditorArea.Active!.IsModified);
        // The Explorer's keys back by Cmd+1, not a click on its row, which so soon after the first is a double click opening it.
        Ui.Run(window, AppActions.FocusExplorer);
        Assert.True(window.ExplorerView.Rows().IsKeyboardFocusWithin && window.ExplorerView.SelectedNodes.Select(n => n.Name).SequenceEqual(["Notes.txt"]),
            $"Focus Explorer left the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}, [{string.Join(", ", window.ExplorerView.SelectedNodes.Select(n => n.Name))}] chosen");
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => !File.Exists(notes) && window.EditorArea.Documents.Count == 0, "Notes.txt deleted and its tab closed");
        string trashed = File.ReadAllText(Path.Combine(trash, "Notes.txt"));
        Assert.True(trashed == "milk, eggs, groceries\n", $"The Trash has Notes.txt without what was typed into it before the delete, so the edit is lost: '{trashed}'");
        Passenger.App.History.LocalHistory history = window.LocalHistory!;
        Ui.WaitFor(() => history.Snapshot().Events.Any(e => e.Kind == Passenger.App.History.HistoryKind.Deleted && e.Path == notes), "Local History to keep the deletion");
        string? kept = history.Snapshot().Events.Last(e => e.Kind == Passenger.App.History.HistoryKind.Deleted && e.Path == notes).Before is { } hash
            && history.Content(hash) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        Assert.True(kept == "milk, eggs, groceries\n", $"Local History kept Notes.txt as deleted without what was typed into it: '{kept}'");
        window.Close();
    }

    private void A_folder_renamed_keeps_out_of_the_build_the_files_its_project_removed_by_name_or_by_glob_and_undo_puts_the_project_back()
    {
        // Old.cs and Stale.cs no longer compile, so the project removes them: one by its name, the other by its folder's glob.
        string project = Path.Combine(folder, "Shop.csproj");
        string written = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <Compile Remove=\"Legacy\\Old.cs\" />\n    <Compile Remove=\"Legacy\\Generated\\**\" />\n    <Folder Include=\"Legacy\\Generated\\\" />\n  </ItemGroup>\n</Project>\n";
        File.WriteAllText(project, written);
        string legacy = PathNames.Canonical(Path.Combine(folder, "Legacy"));
        Directory.CreateDirectory(Path.Combine(legacy, "Generated"));
        File.WriteAllText(Path.Combine(legacy, "Kept.cs"), "namespace Shop.Legacy;\n\npublic class Kept\n{\n}\n");
        File.WriteAllText(Path.Combine(legacy, "Old.cs"), "this was C# once\n");
        File.WriteAllText(Path.Combine(legacy, "Generated", "Stale.cs"), "and so was this\n");
        MainWindow window = Open();
        List<string> asked = Answer(window);

        window.ExplorerView.Reveal(legacy);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == legacy));
        Ui.Run(window, AppActions.Rename);
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("Archive");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        Assert.True(Directory.Exists(Path.Combine(folder, "Archive")), $"Legacy was not renamed: {window.Status}");
        Assert.Equal(written.Replace("Legacy\\", "Archive\\", StringComparison.Ordinal), File.ReadAllText(project));

        string archive = PathNames.Canonical(Path.Combine(folder, "Archive"));
        window.ExplorerView.Reveal(archive);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == archive));
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], asked);
        Assert.True(Directory.Exists(legacy), $"Undo did not rename Archive back: {window.Status}");
        Assert.Equal(written, File.ReadAllText(project));
        window.Close();
    }

    /// <summary>Rename on a file in the Explorer selects its name before its extension, so a name typed over the selection
    /// keeps the extension: Program.cs becomes Main.cs. A folder's rename selects its whole name, dots and all, and an
    /// extensionless file's too.</summary>
    /// <summary>A folder or file cut or copied and pasted where its name is the folder holding it (Docs/Docs beside Docs) is
    /// refused before any question: replacing that folder would send what was pasted to the Trash with it.</summary>
    private void A_file_or_folder_pasted_where_its_name_is_the_folder_holding_it_is_refused_before_any_question_and_nothing_moves()
    {
        string root = PathNames.Canonical(folder);
        string outer = TestFolders.Write(root, "Docs/outer.md", "outer\n");
        string inner = TestFolders.Write(root, "Docs/Docs/inner.md", "inner\n");
        string notes = TestFolders.Write(root, "Notes/Notes", "notes\n");
        MainWindow window = Open();
        List<string> asked = Questions(window);
        foreach (string held in (string[])[Path.Combine(root, "Docs", "Docs"), notes])
        {
            foreach (string action in (string[])[EditorActions.Cut, EditorActions.Copy])
            {
                Choose(held);
                Ui.Run(window, action);
                Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Kind != NodeKind.File && r.Into is { } into && PathNames.Same(into, root)));
                Ui.Run(window, EditorActions.Paste);
                string refusal = Refusals.ReplacesItsFolder(Path.GetFileName(held)).Shown;
                Ui.WaitFor(() => window.Status == refusal, () => $"{action} and Paste of {Path.GetRelativePath(root, held)} beside the folder holding it to be refused: '{window.Status}'");
            }
        }
        Assert.True(asked.Count == 0, $"A paste over the folder holding what was pasted asked [{string.Join(" | ", asked)}], where it is refused unasked");
        Assert.True(File.ReadAllText(outer) == "outer\n" && File.ReadAllText(inner) == "inner\n" && File.ReadAllText(notes) == "notes\n",
            $"A refused paste moved or changed what it would have replaced: the folder holds [{string.Join(", ", Left(root))}]");
        Assert.True(!Directory.EnumerateFileSystemEntries(trash).Any(), $"A refused paste put [{string.Join(", ", Left(trash))}] in the Trash");
        Assert.Null(window.EditorArea.Commands.ToUndo(null));
        window.Close();

        void Choose(string path)
        {
            window.ExplorerView.Reveal(path);
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == path));
        }
    }

    /// <summary>Two dots in a row inside a name are the name's own (release..notes.txt): New File, New Folder and Rename take
    /// them, and refuse only ".." as a whole name or one of its folders, which would climb out of the folder.</summary>
    private void A_name_with_two_dots_in_a_row_is_made_and_renamed_to_and_only_a_part_climbing_out_is_refused()
    {
        string root = PathNames.Canonical(folder);
        string models = Path.Combine(root, "Models");
        MainWindow window = Open();

        Assert.Null(window.CreateFile(models, "release..notes.txt", "notes\n"));
        Assert.Null(Ui.Await(window.CreateFolderAsync(models, "v1..2"), "New Folder"));
        Assert.Null(Ui.Await(window.RenameAsync(Path.Combine(models, "release..notes.txt"), "notes..old.txt"), "the rename"));
        Assert.True(File.ReadAllText(Path.Combine(models, "notes..old.txt")) == "notes\n" && Directory.Exists(Path.Combine(models, "v1..2"))
            && !File.Exists(Path.Combine(models, "release..notes.txt")), $"Names with two dots in a row were not made or renamed to: Models holds [{string.Join(", ", Left(models))}]");
        foreach (string climbing in (string[])["..", "../out.txt", "Sub/../out.txt"])
        {
            Assert.Equal($"{climbing} is not a file name", window.CreateFile(models, climbing, string.Empty));
        }
        Assert.False(File.Exists(Path.Combine(root, "out.txt")), "A name climbing out of its folder made a file outside it");
        window.Close();
    }

    /// <summary>A file-based program's directives follow what they name: the project its <c>#:project</c> names renamed in
    /// the Explorer, its value names the new folder and the program evaluates with the project there; the program cut and
    /// pasted into a deeper folder, its <c>#:project</c> and <c>#:include</c> name from there what they named before. One
    /// Undo puts each back whole: the folder or the program where it was, and the program's text as it was.</summary>
    private void A_programs_directives_follow_the_project_it_names_and_the_program_itself_moved_and_one_undo_puts_each_back()
    {
        File.Delete(Path.Combine(folder, "Shop.csproj"));
        string root = PathNames.Canonical(folder);
        TestFolders.Write(root, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(root, "Lib/Shapes.cs", "namespace Lib;\n\npublic static class Shapes\n{\n    public const string Name = \"shapes\";\n}\n");
        const string Program = "#!/usr/bin/env dotnet\n#:project ../Lib/Lib.csproj\n#:include helper.cs\nSystem.Console.WriteLine(Lib.Shapes.Name + Helper.Name);\n";
        TestFolders.Write(root, "scripts/tool.cs", Program);
        TestFolders.Write(root, "scripts/helper.cs", "static class Helper\n{\n    public const string Name = \"helper\";\n}\n");
        string cli = Directory.CreateDirectory(Path.Combine(root, "tools", "cli")).FullName;
        string tool = Path.Combine(root, "scripts", "tool.cs");
        string lib = Path.Combine(root, "Lib");
        MainWindow window = Open();
        List<string> asked = Answer(window);
        Evaluated(tool, Path.Combine(lib, "Lib.csproj"), "as written");

        Choose(lib);
        Ui.Run(window, AppActions.Rename);
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("Geometry");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, () => $"the rename to finish: {popup.Problem}");
        string geometry = Path.Combine(root, "Geometry");
        Assert.True(Directory.Exists(geometry), $"Lib was not renamed: {window.Status}");
        Assert.Equal(Program.Replace("../Lib/", "../Geometry/", StringComparison.Ordinal), File.ReadAllText(tool));
        Evaluated(tool, Path.Combine(geometry, "Lib.csproj"), "once Lib was renamed Geometry");

        Choose(geometry);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], asked);
        Assert.True(Directory.Exists(lib) && !Directory.Exists(geometry), $"Undo did not rename Geometry back: {window.Status}");
        Assert.Equal(Program, File.ReadAllText(tool));

        Choose(tool);
        Ui.Run(window, EditorActions.Cut);
        Choose(cli);
        Ui.Run(window, EditorActions.Paste);
        Assert.Equal("Moved tool.cs to cli", window.Status);
        string moved = Path.Combine(cli, "tool.cs");
        Assert.Equal(Program.Replace("../Lib/", "../../Lib/", StringComparison.Ordinal).Replace("#:include helper.cs", "#:include ../../scripts/helper.cs", StringComparison.Ordinal),
            File.ReadAllText(moved));
        Evaluated(moved, Path.Combine(lib, "Lib.csproj"), "once it moved to tools/cli");
        Assert.True(window.Projects.Single(p => p.Path == moved).Holds(Path.Combine(root, "scripts", "helper.cs")),
            "The program moved no longer compiles the helper.cs it included, left in scripts");

        Choose(cli);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo", "Undo"], asked);
        Assert.True(File.Exists(tool) && !File.Exists(moved), $"Undo did not move tool.cs back: {window.Status}");
        Assert.Equal(Program, File.ReadAllText(tool));
        window.Close();

        void Choose(string path)
        {
            window.ExplorerView.Reveal(path);
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == path));
        }

        void Evaluated(string program, string referenced, string when)
        {
            string References() => window.Projects.FirstOrDefault(p => p.Path == program) is { } found
                ? found.IsEvaluated ? string.Join(", ", found.ProjectReferences) : $"not evaluated: {found.Problem ?? "not yet"}"
                : "not a project";
            Ui.WaitFor(() => References() == referenced, () => $"{Path.GetFileName(program)}, {when}, to evaluate with its #:project's {referenced} ({References()})", TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>A project cut and pasted a folder deeper: its Import and its reference's HintPath name from there the files
    /// they named, and another project's Import of a file in its folder names it where it went, so both still build with
    /// the .NET command line; one Undo puts the folder and both project files back.</summary>
    private void A_project_moved_deeper_follows_its_import_and_hint_path_and_another_projects_import_into_it_and_still_builds()
    {
        File.Delete(Path.Combine(folder, "Shop.csproj"));
        string root = PathNames.Canonical(folder);
        const string Defines = "<Project>\n  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);{0}</DefineConstants>\n  </PropertyGroup>\n</Project>\n";
        TestFolders.Write(root, "build/common.props", string.Format(System.Globalization.CultureInfo.InvariantCulture, Defines, "COMMON"));
        TestFolders.Write(root, "native/Native.csproj", TestProjects.Library());
        TestFolders.Write(root, "native/Greeting.cs", "namespace Native;\n\npublic static class Greeting\n{\n    public const string Text = \"native\";\n}\n");
        string app = TestProjects.Library().With("  <Import Project=\"..\\..\\build\\common.props\" />\n"
            + "  <ItemGroup>\n    <Reference Include=\"Native\">\n      <HintPath>..\\..\\lib\\Native.dll</HintPath>\n    </Reference>\n  </ItemGroup>\n");
        TestFolders.Write(root, "src/App/App.csproj", app);
        TestFolders.Write(root, "src/App/App.props", string.Format(System.Globalization.CultureInfo.InvariantCulture, Defines, "APP"));
        TestFolders.Write(root, "src/App/Greeter.cs",
            "#if !COMMON\n#error build/common.props is not imported\n#endif\nnamespace App;\n\npublic static class Greeter\n{\n    public const string Text = Native.Greeting.Text;\n}\n");
        string tool = TestProjects.Library().With("  <Import Project=\"..\\App\\App.props\" />\n  <ItemGroup>\n    <ProjectReference Include=\"..\\App\\App.csproj\" />\n  </ItemGroup>\n");
        TestFolders.Write(root, "src/Tool/Tool.csproj", tool);
        TestFolders.Write(root, "src/Tool/Uses.cs", "#if !APP\n#error App.props is not imported\n#endif\nnamespace Tool;\n\npublic static class Uses\n{\n    public const string Text = App.Greeter.Text;\n}\n");
        string apps = Directory.CreateDirectory(Path.Combine(root, "src", "apps")).FullName;
        Built(Path.Combine(root, "native", "Native.csproj"), "before the test, so App has no Native.dll to reference", "-o", Path.Combine(root, "lib"));
        MainWindow window = Open();
        List<string> asked = Answer(window);

        Choose(Path.Combine(root, "src", "App"));
        Ui.Run(window, EditorActions.Cut);
        Choose(apps);
        Ui.Run(window, EditorActions.Paste);
        Assert.Equal("Moved App to apps", window.Status);
        string moved = Path.Combine(apps, "App", "App.csproj");
        string toolProject = Path.Combine(root, "src", "Tool", "Tool.csproj");
        Assert.Equal(app.Replace("..\\..\\", "..\\..\\..\\", StringComparison.Ordinal), File.ReadAllText(moved));
        Assert.Equal(tool.Replace("..\\App\\", "..\\apps\\App\\", StringComparison.Ordinal), File.ReadAllText(toolProject));
        Built(toolProject, "once App moved, so a path one of them names was left behind");

        Choose(apps);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], asked);
        Assert.True(File.Exists(Path.Combine(root, "src", "App", "App.csproj")) && !File.Exists(moved), $"Undo did not move App back: {window.Status}");
        Assert.Equal(app, File.ReadAllText(Path.Combine(root, "src", "App", "App.csproj")));
        Assert.Equal(tool, File.ReadAllText(toolProject));
        window.Close();

        void Choose(string path)
        {
            window.ExplorerView.Reveal(path);
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == path));
        }
    }

    /// <summary>A project built with the .NET command line, as a person would check it, with the options given.</summary>
    private static void Built(string project, string when, params string[] options)
    {
        ChildResult built = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", project, "-nologo", "-v:q", .. options]), limit: Restore.Limit.Scaled())),
            $"{Path.GetFileName(project)}'s build", Restore.Limit.Scaled() + TestFolders.Spare);
        Assert.True(built.Succeeded, $"{Path.GetFileName(project)} does not build {when}:\n{built.Output}{built.Error}");
    }

    private void A_file_renamed_in_the_explorer_keeps_its_tab_in_both_splits_its_undo_its_bookmark_and_its_breakpoint()
    {
        string models = PathNames.Canonical(Path.Combine(folder, "Models"));
        string order = Path.Combine(models, "Order.cs");
        File.WriteAllText(order, "namespace Shop.Models;\n\npublic class Order\n{\n    public int Total;\n}\n");
        MainWindow window = Open();
        window.Autosaves = false;
        window.OpenFile(order);
        EditorArea area = window.EditorArea;
        OpenDocument document = Assert.Single(area.Documents);
        area.CodeEditor.GoTo(new TextPosition(4, 21));
        window.KeyTextInput(" // the sum");
        area.CodeEditor.GoTo(new TextPosition(4, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Run(window, AppActions.ToggleBookmark);
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();

        bool heard = false;
        window.FolderWatcher!.Changed += (_, changes) => heard |= changes.Paths.Any(p => Path.GetFileName(p) is "Order.cs" or "Purchase.cs");
        Assert.Null(Ui.Await(window.RenameAsync(order, "Purchase.cs"), "the rename"));
        // The folder watcher hears of the move after it, and must not take it for the file deleted.
        Ui.WaitFor(() => heard, "the folder watcher to hear of the move");
        Ui.Settle();
        string purchase = Path.Combine(models, "Purchase.cs");
        Assert.False(File.Exists(order), "Order.cs is still on disk after its rename");
        Assert.Equal("namespace Shop.Models;\n\npublic class Order\n{\n    public int Total; // the sum\n}\n", File.ReadAllText(purchase));
        Assert.True(ReferenceEquals(document, Assert.Single(area.Documents)), "The renamed file was closed and opened again, losing its undo and carets");
        Assert.Equal(purchase, document.Path);
        Assert.True(area.Groups.Count == 2, "The rename closed the file's second split");
        Assert.All(area.Groups, group => Assert.Same(document, Assert.Single(group.Tabs).Document));
        Assert.Equal(["Purchase.cs", "Purchase.cs"], window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains(StyleClasses.TabName)).Select(t => t.Text));
        Assert.Empty(window.Debugger.Breakpoints.In(order));
        Assert.Equal(4, Assert.Single(window.Debugger.Breakpoints.In(purchase)).Line);
        Assert.Contains(new LineMark(4, LineMarkKind.Breakpoint), area.CodeEditor.LineMarks);
        Assert.Equal(purchase, Assert.Single(window.Bookmarks.All).Path);
        Assert.Equal([4], document.Bookmarks);

        area.CodeEditor.GoTo(new TextPosition(0, 0));
        area.CodeEditor.Focus();
        Ui.Press(window, "Enter");
        Assert.Equal(5, Assert.Single(window.Debugger.Breakpoints.In(purchase)).Line);
        area.CodeEditor.TryExecute(EditorActions.Undo);
        area.CodeEditor.TryExecute(EditorActions.Undo);
        Ui.Settle();
        Assert.Equal("namespace Shop.Models;\n\npublic class Order\n{\n    public int Total;\n}\n", document.Text.Snapshot.ToString());
        Assert.Equal(4, Assert.Single(window.Debugger.Breakpoints.In(purchase)).Line);
        window.Close();
    }

    /// <summary>An HTML page opens in the person's browser from the Explorer's Open In, from its editor's menu and from the
    /// keys Rider gives Open in Browser, the window handing the browser the file's address (headless, recorded and opened
    /// nowhere); a file that is no page is refused, saying why.</summary>
    private void An_HTML_page_opens_in_the_browser_from_the_explorer_its_editors_menu_and_the_keys()
    {
        File.WriteAllText(Path.Combine(folder, "index.html"), "<!doctype html>\n<title>Shop</title>\n");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "notes\n");
        MainWindow window = Open();
        try
        {
            string page = Row(window, "index.html").Path!;
            Uri address = new(page);
            Ui.ChooseFromRowMenu(window, Row(window, "index.html"), "Open In", "Browser");
            Assert.Equal([address], window.Browsed);

            window.OpenFile(page);
            Ui.Settle();
            Ui.ChooseFromEditorMenu(window, "Open in Browser");
            Assert.Equal([address, address], window.Browsed);
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, Ui.Shortcut(AppActions.OpenInBrowser));
            Assert.Equal([address, address, address], window.Browsed);

            window.OpenFile(Path.Combine(folder, "notes.txt"));
            Ui.Settle();
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, Ui.Shortcut(AppActions.OpenInBrowser));
            Assert.True(window.Browsed.Count == 3, $"Open in Browser opened {window.Browsed[^1]}, which is no HTML page");
            Assert.Equal("notes.txt is not an HTML page", window.Status);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Delete on a file chosen with others (Ctrl+click, Cmd+click on macOS) asks once, by their count, and moves
    /// every one to the Trash, closing their tabs and saying so in one line; a file chosen alone is asked of by its name, as
    /// before.</summary>
    private void Delete_on_files_chosen_together_asks_once_and_moves_them_all_to_the_Trash_and_one_chosen_alone_is_asked_of_by_name()
    {
        string[] names = ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"];
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }
        MainWindow window = Open();
        List<string> asked = Questions(window);
        foreach (string name in (string[])["a.txt", "b.txt", "c.txt", "e.txt"])
        {
            window.OpenFile(Path.Combine(folder, name));
        }
        Ui.Settle();

        // Each row found first, as finding one chooses it.
        ExplorerNode[] rows = [.. names.Select(name => Row(window, name))];
        Ui.ClickRow(window, rows[0]);
        AlsoChoose(window, rows[1]);
        AlsoChoose(window, rows[2]);
        Ui.Press(window, "DELETE");
        Ui.WaitFor(() => Finished(window, "Moved"), "the three files chosen to go to the Trash");
        string[] left = [.. names.Where(name => File.Exists(Path.Combine(folder, name)))];
        Assert.True(left.SequenceEqual(["d.txt", "e.txt"]), $"Delete on three files chosen left [{string.Join(", ", left)}], so the rest of those chosen were not deleted: '{window.Status}'");
        Assert.True(asked.SequenceEqual(["Move 3 files to the Trash?"]), $"Delete on three files chosen asked [{string.Join(" | ", asked)}], where one question of all three was due");
        Assert.All(["a.txt", "b.txt", "c.txt"], name => Assert.True(File.Exists(Path.Combine(trash, name)), $"{name} is not in the Trash, so it cannot be put back"));
        Assert.True(window.EditorArea.Documents.Select(d => d.Name).SequenceEqual(["e.txt"]),
            $"The tabs left are [{string.Join(", ", window.EditorArea.Documents.Select(d => d.Name))}], so a file deleted still shows as open");
        Assert.Equal("Moved a.txt, b.txt and c.txt to the Trash", window.Status);

        asked.Clear();
        Ui.ClickRow(window, rows[3]);
        Ui.Press(window, "DELETE");
        Ui.WaitFor(() => !File.Exists(Path.Combine(folder, "d.txt")) || window.Status?.Contains("could not", StringComparison.Ordinal) == true, "d.txt to go to the Trash");
        Assert.True(asked.SequenceEqual(["Move d.txt to the Trash?"]), $"Delete on a file chosen alone asked [{string.Join(" | ", asked)}], not of it by its name");
        Assert.Equal("Moved d.txt to the Trash", window.Status);
        Assert.True(File.Exists(Path.Combine(folder, "e.txt")) && window.EditorArea.Documents.Count == 1, "Delete on a file chosen alone took a file not chosen");
        window.Close();
    }

    /// <summary>The Explorer's Delete, of a file, a folder or several chosen together, is one command of global undo named
    /// after them: Undo asks by that name and brings each back from the Trash with what it held, the Trash keeping it no
    /// more, and Redo sends it there again. On Windows through the Recycle Bin itself.</summary>
    private void Delete_of_a_file_a_folder_or_several_is_one_command_Undo_brings_back_from_the_Trash_and_Redo_sends_again()
    {
        if (OperatingSystem.IsWindows())
        {
            Trash.Folder = null;
        }
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes\n");
        File.WriteAllText(Path.Combine(folder, "Models", "Order.cs"), "class Order { }\n");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a\n");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "b\n");
        MainWindow window = Open();
        List<string> asked = Questions(window);

        Deleted(["Notes.txt"], "Delete Notes.txt", () => Ui.ClickRow(window, Row(window, "Notes.txt")));
        Deleted(["Models"], "Delete Models", () => Ui.ClickRow(window, Row(window, "Models")));
        Deleted(["a.txt", "b.txt"], "Delete a.txt and b.txt", () =>
        {
            ExplorerNode a = Row(window, "a.txt");
            ExplorerNode b = Row(window, "b.txt");
            Ui.ClickRow(window, a);
            AlsoChoose(window, b);
        });
        window.Close();

        // Deletes what choose picks, then takes the delete back and makes it again, each item checked on disk and in the Trash.
        void Deleted(string[] names, string command, Action choose)
        {
            string[] paths = [.. names.Select(name => PathNames.Canonical(Path.Combine(folder, name)))];
            Dictionary<string, string> held = paths.ToDictionary(path => path, Contents);
            choose();
            Ui.Press(window, "DELETE");
            Ui.WaitFor(() => paths.All(path => !Path.Exists(path)) || window.Status?.Contains("could not", StringComparison.Ordinal) == true, $"{command} to go to the Trash");
            CommandHistory.Command done = window.EditorArea.Commands.ToUndo(null)
                ?? throw new Xunit.Sdk.XunitException($"{command} left no command for Undo to take back: {window.Status}");
            Assert.Equal(command, done.Name);
            Trashed[] went = [.. done.Deleted.Select(d => d.Went ?? throw new Xunit.Sdk.XunitException($"The Trash kept {Path.GetFileName(d.Path)} nowhere Passenger finds"))];
            Assert.True(went.Length == names.Length && went.All(Trash.Holds), $"{command} put [{string.Join(", ", went.Select(w => w.At))}] in the Trash, not each of {string.Join(", ", names)}");

            asked.Clear();
            Step(EditorActions.Undo);
            Ui.WaitFor(() => paths.All(Path.Exists) || window.Status?.Contains("Cannot", StringComparison.Ordinal) == true,
                () => $"Undo of {command} to bring each back; asked [{string.Join(" | ", asked)}], the status bar saying '{window.Status}'");
            Assert.True(asked.SequenceEqual([$"Undo {command}? It brings back {Plural.Listed(names)}."]), $"Undo of {command} asked [{string.Join(" | ", asked)}]");
            Assert.All(paths, path => Assert.True(Contents(path) == held[path], $"Undo of {command} brought {Path.GetFileName(path)} back holding {Contents(path)}, not {held[path]}"));
            Assert.True(!went.Any(Trash.Holds), $"After Undo of {command} the Trash still holds [{string.Join(", ", went.Where(Trash.Holds).Select(w => w.At))}]");

            asked.Clear();
            Step(EditorActions.Redo);
            Ui.WaitFor(() => paths.All(path => !Path.Exists(path)) || window.Status?.Contains("Cannot", StringComparison.Ordinal) == true,
                () => $"Redo of {command} to send each to the Trash again; asked [{string.Join(" | ", asked)}], the status bar saying '{window.Status}'");
            Assert.True(asked.SequenceEqual([$"Redo {command}? It deletes {Plural.Listed(names)}."]), $"Redo of {command} asked [{string.Join(" | ", asked)}]");
            CommandHistory.Command again = window.EditorArea.Commands.ToUndo(null) ?? throw new Xunit.Sdk.XunitException($"Redo of {command} left nothing to undo");
            Assert.True(again.Deleted.All(d => d.Went is { } there && Trash.Holds(there)), $"Redo of {command} left the Trash without each of {string.Join(", ", names)}");
        }

        // Undo or Redo from the Explorer, the project's row chosen in it.
        void Step(string action)
        {
            Ui.ClickRow(window, Row(window, "Shop.csproj"));
            Ui.Run(window, action);
        }

        // A file's text, or a folder's files and their texts, as one string to compare.
        static string Contents(string path) => File.Exists(path) ? File.ReadAllText(path)
            : string.Join("|", Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => $"{Path.GetRelativePath(path, file)}={File.ReadAllText(file)}"));
    }

    /// <summary>A folder chosen with a file inside it goes to the Trash once, the file with it: the question is of the
    /// folder alone, and nothing is said to have failed for the file gone before its turn.</summary>
    private void Delete_on_a_folder_chosen_with_a_file_inside_it_moves_the_folder_once_with_the_file()
    {
        string order = Path.Combine(folder, "Models", "Order.cs");
        File.WriteAllText(order, "class Order { }\n");
        MainWindow window = Open();
        List<string> asked = Questions(window);
        ExplorerNode file = Row(window, Path.Combine("Models", "Order.cs"));

        Ui.ClickRow(window, Row(window, "Models"));
        AlsoChoose(window, file);
        Ui.Press(window, "DELETE");
        Ui.WaitFor(() => Finished(window, "Moved"), "the folder chosen to go to the Trash");
        Assert.True(window.Status == "Moved Models to the Trash", $"Delete on a folder and a file in it said '{window.Status}', where the folder went once with the file");
        Assert.True(asked.SequenceEqual(["Move the folder Models and everything in it to the Trash?"]),
            $"Delete on a folder and a file in it asked [{string.Join(" | ", asked)}], where one question of the folder was due");
        Assert.False(Directory.Exists(Path.Combine(folder, "Models")), "The folder chosen stayed");
        Assert.True(File.Exists(Path.Combine(trash, "Models", "Order.cs")), "The file chosen inside the folder is not in the Trash with it, so it cannot be put back");
        Assert.True(Directory.EnumerateFileSystemEntries(trash).Select(Path.GetFileName).SequenceEqual(["Models"]),
            $"The Trash holds [{string.Join(", ", Directory.EnumerateFileSystemEntries(trash).Select(Path.GetFileName))}], so the file went apart from its folder");
        window.Close();
    }

    /// <summary>Delete on rows chosen together greys while one of them would only leave the solution (a project), which
    /// Remove takes chosen alone with its own questions: the row's menu and the keys say which row and why, and nothing is
    /// asked or deleted.</summary>
    private void Delete_greys_on_a_project_chosen_with_a_file_saying_the_project_is_removed_chosen_alone()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.slnx"), "<Solution>\n  <Project Path=\"Shop.csproj\" />\n</Solution>\n");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "groceries\n");
        MainWindow window = Open();
        List<string> asked = Questions(window);
        ExplorerNode project = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project);
        ExplorerNode notes = Row(window, "Notes.txt");

        Ui.ClickRow(window, project);
        AlsoChoose(window, notes);
        string why = Refusals.RemovedAlone("Shop", "the solution").Shown;
        Ui.RightClick(Ui.Row(window.ExplorerView, notes));
        Assert.True(window.ExplorerView.SelectedNodes.Count == 2, "A right-click on a row chosen with another chose it alone, so its menu is not of both");
        ContextMenu menu = window.ExplorerView.OpenMenu ?? throw new Xunit.Sdk.XunitException("A right-click on Notes.txt opened no menu");
        MenuItem delete = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Edit").Items.OfType<MenuItem>()
            .Single(i => (string?)i.Header is "Delete…" or "Remove…");
        menu.Close();
        Ui.Settle();
        Assert.True(!delete.IsEnabled && (string?)delete.Header == "Delete…" && Equals(ToolTip.GetTip(delete), why),
            $"The menu's {delete.Header} of a project and a file chosen is {(delete.IsEnabled ? "enabled" : "greyed")}, saying '{ToolTip.GetTip(delete)}', so the person is not told why it takes neither");
        Ui.Press(window, "DELETE");
        Assert.Equal(why, window.Status);
        Assert.True(asked.Count == 0 && File.Exists(Path.Combine(folder, "Notes.txt")) && File.ReadAllText(Path.Combine(folder, "Shop.slnx")).Contains("Shop.csproj", StringComparison.Ordinal),
            "Delete on a project and a file chosen asked or deleted something");
        window.Close();
    }

    /// <summary>The Explorer's menu and keys are the window's actions: a row's Rename and Delete run from its menu and
    /// from the keys of the keymap chosen, Visual Studio's Ctrl+R, R (⌘R, R on macOS) and Delete, VS Code's F2 and
    /// Delete, as Rider's RenameElement and $Delete are bound in each; F2 renames nothing in Visual Studio's, where
    /// Rider gives it none. Open In's file manager shows the row's file there (headless, it opens nothing).</summary>
    private void The_explorers_rename_delete_and_reveal_run_from_its_menu_and_from_each_keymaps_keys()
    {
        foreach (string name in (string[])["One.txt", "Two.txt", "Three.txt", "Four.txt"])
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }
        MainWindow window = Open();
        List<string> asked = Answer(window);
        try
        {
            Ui.ChooseFromRowMenu(window, Row(window, "One.txt"), "Edit", "Rename…");
            Renamed(window, "Uno.txt");
            Ui.ChooseFromRowMenu(window, Row(window, "Uno.txt"), "Edit", "Delete…");
            Gone(window, "Uno.txt", "the delete from the row's menu");
            Ui.ChooseFromRowMenu(window, Row(window, "Four.txt"), "Open In", FileManager.Name);
            Assert.Equal([Row(window, "Four.txt").Path!], window.ShownInFileManager);

            Pick(window, Row(window, "Two.txt"));
            Ui.Press(window, "F2");
            Assert.False(window.Popup is NamePopup, "F2 renamed a row in Visual Studio's keymap, which gives Rename Ctrl+R, R");
            Ui.Press(window, OperatingSystem.IsMacOS() ? "meta R , R" : "control R , R");
            Renamed(window, "Dos.txt");
            Pick(window, Row(window, "Dos.txt"));
            Ui.Press(window, "DELETE");
            Gone(window, "Dos.txt", "Delete in Visual Studio's keymap");

            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VSCode });
            Ui.Settle();
            Pick(window, Row(window, "Three.txt"));
            Ui.Press(window, "F2");
            Renamed(window, "Tres.txt");
            Pick(window, Row(window, "Tres.txt"));
            Ui.Press(window, "DELETE");
            Gone(window, "Tres.txt", "Delete in VS Code's keymap");
        }
        finally
        {
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
            window.Close();
        }

        void Renamed(MainWindow window, string name)
        {
            NamePopup popup = window.Popup as NamePopup ?? throw new Xunit.Sdk.XunitException($"No rename was asked for, to {name}: the status bar says '{window.Status}'");
            popup.Text = string.Empty;
            window.KeyTextInput(name);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to {name} to finish: {popup.Problem}");
            Assert.True(File.Exists(Path.Combine(folder, name)), $"Nothing was renamed {name}: {window.Status}");
        }

        void Gone(MainWindow window, string name, string what)
        {
            // The delete asks first, which Answer agrees to: nothing asked means the keys deleted nothing.
            Ui.Settle();
            Assert.True(window.Status?.StartsWith("Moved", StringComparison.Ordinal) == true || asked.Count > 0 && asked[^1] == "Delete",
                $"{what} did not delete {name}: the status bar says '{window.Status}'");
            asked.Clear();
            Ui.WaitFor(() => Finished(window, "Moved"), $"{what} to move {name} to the Trash");
            Assert.True(!File.Exists(Path.Combine(folder, name)) && File.Exists(Path.Combine(trash, name)), $"{what} left {name} out of the Trash: '{window.Status}'");
        }
    }

    private void The_explorers_keys_open_and_close_a_folder_go_up_from_its_file_and_open_a_file_on_enter()
    {
        File.WriteAllText(Path.Combine(folder, "Models", "Order.cs"), "class Order { }\n");
        MainWindow window = Open();
        ExplorerNode order = Row(window, Path.Combine("Models", "Order.cs"));
        ExplorerNode models = window.ExplorerView.Tree.Rows.Holder(order)!;
        TreeKeysCheck.Press(window, window.ExplorerView.Tree.Rows, models, "the Explorer");
        Ui.Press(window, "Down");
        Ui.Press(window, "Enter");
        Assert.True(window.EditorArea.Active?.Path == order.Path, $"Enter on a file in the Explorer did not open it: the status bar says '{window.Status}'");
        window.Close();
    }

    /// <summary>A folder with nothing in it shows no chevron in the Explorer, in a project's folder and outside any, gets one
    /// as a file comes into it and loses it as the file goes, where every folder showed one that opened to nothing; a folder
    /// holding a file shows one from the first (#2294).</summary>
    private void A_folder_with_nothing_in_it_shows_no_chevron_and_gets_one_as_a_file_comes_and_loses_it_as_the_file_goes()
    {
        TestFolders.Write(folder, "Views/Main.cs", "class Main { }\n");
        // In the project's folder, as its evaluation holds it; then in a folder of no project's, as the disk holds it.
        foreach (bool project in (bool[])[true, false])
        {
            if (!project)
            {
                File.Delete(Path.Combine(folder, "Shop.csproj"));
            }
            MainWindow window = Open();
            Ui.WaitFor(() => !Row(window, "Models").HasChildren && !Chevron(window, Row(window, "Models")), "the empty Models folder's chevron to go");
            Assert.True(Row(window, "Views").HasChildren && Chevron(window, Row(window, "Views")), "Views, holding a file, shows no chevron");
            string order = Path.Combine(folder, "Models", "Order.cs");
            File.WriteAllText(order, "class Order { }\n");
            Ui.WaitFor(() => Row(window, "Models").HasChildren && Chevron(window, Row(window, "Models")), "Models' chevron to come once Order.cs came into it");
            File.Delete(order);
            Ui.WaitFor(() => !Row(window, "Models").HasChildren && !Chevron(window, Row(window, "Models")), "Models' chevron to go once Order.cs went");
            window.Close();
        }
    }

    /// <summary>Whether a row of the Explorer draws its chevron.</summary>
    private static bool Chevron(MainWindow window, ExplorerNode row) =>
        window.ExplorerView.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext == row)?.GetVisualDescendants().OfType<RowGlyphs>().FirstOrDefault()?.HasChevron == true;

    /// <summary>A folder holding a web project and no solution opens in the File System view, where the project's
    /// Properties and, once MSBuild has evaluated the project as a web one, its wwwroot are listed before its other folders
    /// and refuse Delete and Rename saying why, Properties' Delete too while it is chosen with another folder.</summary>
    private void A_web_projects_Properties_and_wwwroot_list_first_in_the_file_system_view_once_evaluated_and_refuse_delete_and_rename()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.csproj"), TestProjects.Web());
        TestFolders.Write(folder, "Properties/launchSettings.json", "{ \"profiles\": {} }\n");
        TestFolders.Write(folder, "wwwroot/site.css", "body { margin: 0; }\n");
        MainWindow window = Open();
        List<string> asked = Answer(window);
        Assert.False(window.ExplorerView.Tree.IsSolutionView, "A folder with no solution opened in the Solution view");
        string webRoot = PathNames.Canonical(Path.Combine(folder, "wwwroot"));
        IReadOnlyList<ExplorerNode> rows = window.ExplorerView.Tree.Rows;
        Ui.WaitFor(() => rows.Any(r => r.Path == webRoot && r.SdkFolder == SdkFolder.WebRoot),
            () => $"wwwroot shown as Shop's web root once MSBuild evaluated it a web project: [{string.Join(", ", rows.Select(r => $"{r.Name} {r.SdkFolder}"))}]");
        Assert.Equal(["Properties", "wwwroot", "Models"], rows.Where(r => r is { Depth: 1, Kind: NodeKind.Folder }).Select(r => r.Name));
        Assert.Equal(SdkFolder.Properties, Row(window, "Properties").SdkFolder);

        Ui.ClickRow(window, Row(window, "wwwroot"));
        Ui.Run(window, AppActions.Rename);
        Assert.Equal("wwwroot is the project's web root: ASP.NET Core serves its static files from it", window.Status);
        Ui.ClickRow(window, Row(window, "Properties"));
        Ui.Press(window, "BACK_SPACE");
        Assert.Equal("Properties holds the project's launch and publish profiles", window.Status);
        // Chosen with another folder, Properties keeps Delete from taking either, the keys on the other.
        ExplorerNode properties = Row(window, "Properties");
        Ui.ClickRow(window, Row(window, "Models"));
        AlsoChoose(window, properties);
        Ui.Press(window, "BACK_SPACE");
        Assert.Equal("Properties holds the project's launch and publish profiles", window.Status);
        Assert.True(Directory.Exists(Path.Combine(folder, "Models")), "Delete on a folder chosen with Properties took the folder, leaving Properties");
        Assert.True(Directory.Exists(webRoot) && Directory.Exists(Path.Combine(folder, "Properties")) && asked.Count == 0, "A folder the SDK reads was renamed or deleted");
        window.Close();
    }

    /// <summary>A file's row, opened to and chosen in the Explorer by its name in the folder.</summary>
    private ExplorerNode Row(MainWindow window, string name)
    {
        string path = PathNames.Canonical(Path.Combine(folder, name));
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == path) ?? throw new InvalidOperationException($"The Explorer shows no row for {name}");
    }

    /// <summary>Chooses a row with the keys in the Explorer, as Focus Explorer and the arrows would: a click there, on the
    /// place another row was just clicked at, would be a double click, which opens the file.</summary>
    private static void Pick(MainWindow window, ExplorerNode row)
    {
        window.ExplorerView.Choose(row);
        window.ExplorerView.FocusRows();
        Ui.Settle();
        Assert.True(window.Actions.Context().ToolWindow?.Id == ToolWindowIds.Explorer, $"The Explorer did not take the keys on {row.Name}");
    }

    /// <summary>Clicks a row with the system's command key held, the key for choosing another row (Cmd on macOS, Ctrl
    /// elsewhere), failing unless it is then chosen with those before.</summary>
    private static void AlsoChoose(MainWindow window, ExplorerNode row)
    {
        int before = window.ExplorerView.SelectedNodes.Count;
        Ui.Click(Ui.Row(window.ExplorerView, row), modifiers: (RawInputModifiers)Keystroke.Command);
        Assert.True(window.ExplorerView.SelectedNodes.Count == before + 1 && window.ExplorerView.SelectedNodes.Contains(row),
            $"A click on {row.Name} with the key for choosing another held left [{string.Join(", ", window.ExplorerView.SelectedNodes.Select(n => n.Name))}] chosen");
    }

    private MainWindow Open() => Ui.OpenFolder(folder, languageServer: false);

    /// <summary>Agrees to every question the window asks, keeping what each asked in order.</summary>
    private static List<string> Questions(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        return asked;
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

    /// <summary>A move's refusal, as the status bar says it, or null once it put what it moved.</summary>
    private static async Task<string?> Moved(Task<IReadOnlyList<string>> moving) => (await moving).Count > 0 ? null : "nothing was moved";

    /// <summary>What may hold a folder that would not go or move, for a failure to name: the folders under it watched for the
    /// C# language server, and on Windows the programs holding it, or a file or folder under it, open as the failure is said
    /// (<see cref="FileHolders.Within"/>); the refusal itself names those holding it as it was refused.</summary>
    private static string Holders(MainWindow window, string folder)
    {
        string[] watched = [.. (window.CSharpService?.Watching?.Watching ?? []).Select(w => w.Folder).Where(w => PathNames.Within(w, folder))];
        string[] open = [.. FileHolders.Within(folder).Select(held => $"{(held.Process == Environment.ProcessId ? FileHolders.Itself
            : DotnetProcesses.Describe(held.Process)?.Program ?? "a program")} (process {held.Process}) holding [{string.Join(", ", held.Paths)}]")];
        return $". Watched for the C# language server under it: [{string.Join(", ", watched)}]; held open now: [{string.Join("; ", open)}]";
    }

    /// <summary>What a folder holds, by its paths inside it, for a failure to name.</summary>
    private static IEnumerable<string> Left(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(path, p)) : [];

    /// <summary>Whether the status bar says the work ended, as it does or with why it could not.</summary>
    private static bool Finished(MainWindow window, string done) =>
        window.Status is { } status && (status.StartsWith(done, StringComparison.Ordinal) || status.Contains("could not", StringComparison.Ordinal));
}
