using System.Text.Json;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;

namespace Passenger.Tests;

/// <summary>A language server's edit judged against the files as they are on disk before any is written.</summary>
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

    private const string Insert = """{"range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 0}}, "newText": "// "}""";

    private static string UriOf(string path) => DocumentUris.FromPath(path);

    private static JsonElement Edit(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
