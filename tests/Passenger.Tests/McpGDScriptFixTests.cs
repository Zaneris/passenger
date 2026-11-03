using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>An agent's code_actions in a GDScript lists the fixes Show Context Actions offers there, in its order and words,
/// and apply_code_action makes one as the window does, saved, the person's one Undo taking it back. Godot's server for the
/// script starts where a Godot is found (its user folders the test's), so run alone; the fixes need none.</summary>
[Collection(Alone.Name)]
public sealed class McpGDScriptFixTests : McpRig
{
    private const string Script = "extends Node\n\n\nfunc _ready() -> void:\n\tvar unused := 1\n";

    [AvaloniaFact]
    public void An_agent_lists_and_applies_a_gdscripts_fixes_as_show_context_actions_offers_them_the_persons_undo_taking_each_back()
    {
        string folder = TestFolders.Create("mcp-gdscript-fix-");
        DeleteAtEnd(folder);
        DeleteAtEnd(folder + "-home");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Fixes\"\n\n[debug]\n\n"
            + "gdscript/warnings/inferred_declaration=1\n");
        TestFolders.Write(folder, "main.gd", Script);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        window.ApplySettings(window.Settings with { McpServer = true });
        Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
        window.Confirm = (_, _, _) => Task.FromResult(true);
        string script = Path.Combine(window.Folder!, "main.gd");
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Dictionary<string, object?> place = new() { ["path"] = "main.gd", ["line"] = 5, ["column"] = 7 };
            string[] listed = [.. Await(Waiting.Apart(() => Call(client, McpTools.CodeActions, place)), "the agent's code_actions").EnumerateArray().Select(a => a.GetString()!)];
            OpenDocument document = Assert.Single(window.EditorArea.Documents, d => d.Name == "main.gd");
            string[] offered = [.. Ui.Await(window.Assist.OwnFixTitlesAsync(document, Script.IndexOf("unused", StringComparison.Ordinal)), "Show Context Actions' fixes there")
                .Select(f => $"{f} [quickfix]")];
            Assert.Equal(["Add type annotation ': int' [quickfix]", "Prefix 'unused' with an underscore [quickfix]"], listed);
            Assert.Equal(offered, listed);

            Task<JsonElement> prefixing = Waiting.Apart(() => Call(client, McpTools.ApplyCodeAction, new(place) { ["title"] = "Prefix 'unused' with an underscore" }));
            Press(Question(window, "the question for the agent's first fix"), "Allow");
            Assert.Equal("Applied 'Prefix 'unused' with an underscore' to main.gd", Await(prefixing, "the underscore's fix").GetString());
            Assert.Equal(Script.Replace("var unused", "var _unused", StringComparison.Ordinal), File.ReadAllText(script));
            Undo(window, document);
            Assert.Equal(Script, document.Text.Snapshot.ToString());
            window.EditorArea.SaveAll();
            Ui.Settle();

            Assert.Equal("Applied 'Add type annotation ': int'' to main.gd",
                Await(Waiting.Apart(() => Call(client, McpTools.ApplyCodeAction, new(place) { ["title"] = "Add type annotation ': int'" })), "the type's fix").GetString());
            Assert.Equal(Script.Replace("var unused := 1", "var unused: int = 1", StringComparison.Ordinal), File.ReadAllText(script));
            Undo(window, document);
            Assert.Equal(Script, document.Text.Snapshot.ToString());
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>The person's one Undo in the editor of the file the agent fixed.</summary>
    private static void Undo(MainWindow window, OpenDocument document)
    {
        window.OpenFile(document.Path);
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
    }
}
