using System.Text.Json;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;

namespace Passenger.Tests;

/// <summary>A language server's edit judged against the files as they are on disk before any is written, and its deletes
/// made as the Explorer's are.</summary>
public sealed class WorkspaceEditsTests
{
    /// <summary>An edit to a closed file gone from the disk, however long ago, is from a server still holding it as it was, so
    /// it is refused before anything is written, where it once failed partway at that file; an edit to a file its own create
    /// or rename puts there is not.</summary>
    [AvaloniaFact]
    public void An_edit_to_a_file_gone_from_the_disk_is_refused_saying_the_server_held_it_and_one_to_a_file_the_edit_puts_there_is_not()
    {
        string folder = TestFolders.Create("stale-gone-");
        TestFolders.Write(folder, "Shape.cs", "public class Shape\n{\n}\n");
        string shape = Path.Combine(folder, "Shape.cs");
        string circle = Path.Combine(folder, "Circle.cs");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);

        string? refused = Ui.Await(WorkspaceEdits.StaleAsync(window.EditorArea, Held, Edit($$$"""{"changes": {"{{{UriOf(circle)}}}": [{{{Insert}}}]}}"""), since: null),
            "the judging of an edit to a deleted file");
        Assert.True(refused?.StartsWith("Circle.cs was deleted, which the language server still held", StringComparison.Ordinal) == true && !File.Exists(circle),
            $"An edit to Circle.cs, deleted, was judged {(refused is null ? "fit to apply" : $"stale saying '{refused}'")}");

        string created = $$$"""{"documentChanges": [{"kind": "create", "uri": "{{{UriOf(circle)}}}"}, {"textDocument": {"uri": "{{{UriOf(circle)}}}", "version": null}, "edits": [{{{Insert}}}]}]}""";
        string moved = $$$"""{"documentChanges": [{"kind": "rename", "oldUri": "{{{UriOf(shape)}}}", "newUri": "{{{UriOf(circle)}}}"}, {"textDocument": {"uri": "{{{UriOf(circle)}}}", "version": null}, "edits": [{{{Insert}}}]}]}""";
        (string What, string Edit)[] puttingThere = [("made", created), ("moved there", moved)];
        foreach ((string what, string edit) in puttingThere)
        {
            string? judged = Ui.Await(WorkspaceEdits.StaleAsync(window.EditorArea, Held, Edit(edit), since: null), $"the judging of an edit to a file it {what}");
            Assert.True(judged is null, $"An edit to Circle.cs, which it {what}, was judged stale: {judged}");
        }
        window.Close();

        // The server holds every file asked of it: a deleted one as it was.
        static Task<IReadOnlyList<TextHeld>> Held(IReadOnlyList<string> paths) => Task.FromResult<IReadOnlyList<TextHeld>>([.. paths.Select(p => new TextHeld(p, "held"))]);
    }

    /// <summary>A server's edit deleting a file and a folder moves both to the Trash as the Explorer's Delete does, its command
    /// keeping where they went, never their bytes: Undo puts each back as it was (a link as a link, a script's mode, an
    /// empty folder), Redo moves them there again; a folder holding anything goes only where the edit says recursively.</summary>
    [AvaloniaFact]
    public void A_servers_edit_deleting_a_file_and_a_folder_moves_both_to_the_Trash_and_Undo_puts_each_back_its_command_holding_none_of_their_bytes()
    {
        const int Large = 8 << 20;
        string folder = PathNames.Canonical(TestFolders.Create("edit-delete-"));
        string trash = TestFolders.Create("trash-");
        string shop = TestFolders.Write(folder, "Shop.cs", "public class Shop\n{\n}\n");
        string data = Path.Combine(folder, "data.bin");
        File.WriteAllBytes(data, new byte[Large]);
        string tools = Path.Combine(folder, "Tools");
        string script = TestFolders.Write(folder, "Tools/run.sh", "#!/bin/sh\necho run\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        }
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(script);
        string empty = Directory.CreateDirectory(Path.Combine(tools, "Empty")).FullName;
        string link = Path.Combine(tools, "Shop.cs");
        string target = Path.Join("..", "Shop.cs");
        bool linked = true;
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            // Windows lets this user make no symlink: the rest is checked without one.
            linked = false;
        }
        string large = Path.Combine(tools, "big.bin");
        File.WriteAllBytes(large, new byte[Large]);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        Trash.Folder = trash;
        try
        {
            WorkspaceEditFailed kept = Assert.Throws<WorkspaceEditFailed>(() => Apply(window, Edit($$$"""{"documentChanges": [{"kind": "delete", "uri": "{{{UriOf(tools)}}}"}]}""")));
            Assert.True(File.Exists(large) && !Directory.EnumerateFileSystemEntries(trash).Any(),
                $"An edit deleting Tools, which holds files, without saying recursively took it: {kept.Message}");

            Apply(window, Edit($$$"""{"documentChanges": [{"kind": "delete", "uri": "{{{UriOf(data)}}}"}, {"kind": "delete", "uri": "{{{UriOf(tools)}}}", "options": {"recursive": true}}]}"""));
            Assert.True(!File.Exists(data) && !Directory.Exists(tools), $"The edit left {(File.Exists(data) ? "data.bin" : "Tools")} where it was");
            Assert.Equal(["Tools", "data.bin"], Directory.EnumerateFileSystemEntries(trash).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            CommandHistory.Command? command = window.EditorArea.Commands.ToUndo(null);
            Assert.True(command is not null, "The edit's deletes left no command for Undo to take back");
            (long held, string most) = HeldBytes.Of(command, o => o is Avalonia.AvaloniaObject || (o is OpenDocument open && window.EditorArea.Documents.Contains(open)));
            Assert.True(held < 256 * 1024, $"The edit's Undo holds {held:N0} bytes for a file and a folder of {Large:N0} each, the most in {most}");

            UndoOrRedo(window, shop, EditorActions.Undo);
            AsItWas("Undo");
            UndoOrRedo(window, shop, EditorActions.Redo);
            Assert.True(!File.Exists(data) && !Directory.Exists(tools) && Directory.EnumerateFileSystemEntries(trash).Count() == 2,
                $"Redo left {(File.Exists(data) ? "data.bin" : "Tools")} where it was, or not in the Trash: {window.Status}");
            UndoOrRedo(window, shop, EditorActions.Undo);
            AsItWas("Undo after Redo");
            Assert.Equal(["Undo", "Redo", "Undo"], asked);
        }
        finally
        {
            Trash.Folder = null;
            window.Close();
        }

        void AsItWas(string put)
        {
            Assert.True(File.Exists(data) && new FileInfo(data).Length == Large, $"{put} did not put data.bin back as it was: {window.Status}");
            Assert.True(Directory.Exists(tools), $"{put} did not put Tools back: {window.Status}");
            Assert.True(!linked || new FileInfo(link).LinkTarget == target,
                $"{put} put Shop.cs back as {(new FileInfo(link).LinkTarget is { } led ? $"a link to {led}" : "a file")}, not as its link to {target}");
            Assert.True(Directory.Exists(empty) && !Directory.EnumerateFileSystemEntries(empty).Any(), $"{put} did not put the empty folder back");
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(File.GetUnixFileMode(script) == mode, $"{put} put run.sh back as {File.GetUnixFileMode(script)}, not {mode}");
            }
            Assert.Equal(Large, new FileInfo(large).Length);
            Assert.True(!Directory.EnumerateFileSystemEntries(trash).Any(), $"{put} left {string.Join(", ", Directory.EnumerateFileSystemEntries(trash).Select(Path.GetFileName))} in the Trash");
        }
    }

    /// <summary>A server's edit applied as a code action's is: one command of Undo, its files written.</summary>
    private static void Apply(MainWindow window, JsonElement edit)
    {
        using CommandHistory.Recording recording = window.EditorArea.Commands.Record("Apply Fix");
        WorkspaceEdits.ApplyAsCommand(window.EditorArea, edit, recording);
        Ui.Await(WorkspaceEdits.LandedAsync(recording, null), "the edit's writes");
    }

    /// <summary>Undo or Redo in the Solution view, a row of it chosen.</summary>
    private static void UndoOrRedo(MainWindow window, string chosen, string action)
    {
        window.ExplorerView.Reveal(chosen);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == chosen));
        Ui.Run(window, action);
    }

    private const string Insert = """{"range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 0}}, "newText": "// "}""";

    private static string UriOf(string path) => DocumentUris.FromPath(path);

    private static JsonElement Edit(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
