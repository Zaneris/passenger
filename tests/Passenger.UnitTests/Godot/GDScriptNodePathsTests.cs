using Passenger.Text;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>The node path a GDScript names at the caret, read by Passenger's GDScript parser: a $ path, a % unique name, and
/// a string get_node is given; none away from them.</summary>
public class GDScriptNodePathsTests
{
    private const string Script = """
        extends Control

        func _ready() -> void:
        	var button := $Panel/Button
        	var unique := %Button
        	var named = get_node("Panel/Button")
        	var quoted := $"Panel/Button"
        	print(button, unique, named, quoted)

        """;

    private static string? At(string after) => GDScriptNodePaths.At(GDScriptTree.Parse(Script), Script.IndexOf(after, StringComparison.Ordinal) + after.Length);

    /// <summary>A script's node paths are read at the caret and renamed as what holds them writes them.</summary>
    [Fact]
    public void A_scripts_node_paths_are_read_and_renamed_as_written() => Cases.All(
        (nameof(A_dollar_path_a_unique_name_a_quoted_path_and_a_get_node_string_are_each_read_at_the_caret),
            A_dollar_path_a_unique_name_a_quoted_path_and_a_get_node_string_are_each_read_at_the_caret),
        (nameof(A_new_name_is_written_as_what_holds_the_path_writes_it), A_new_name_is_written_as_what_holds_the_path_writes_it));

    private void A_dollar_path_a_unique_name_a_quoted_path_and_a_get_node_string_are_each_read_at_the_caret()
    {
        Assert.Equal("Panel/Button", At("$Panel/But"));
        Assert.Equal("%Button", At("%But"));
        Assert.Equal("Panel/Button", At("get_node(\"Pan"));
        Assert.Equal("Panel/Button", At("$\"Panel/Bu"));
    }

    /// <summary>A new name is written as what holds the path writes it: a bare path takes an identifier as Godot reads one (marks
    /// and a joiner in it) and becomes a quoted one for any other, a string escapes for its quote, and a raw string that
    /// can't say it says so.</summary>
    private void A_new_name_is_written_as_what_holds_the_path_writes_it()
    {
        string Renamed(string written, string name, string old = "Score")
        {
            string script = $"extends Node\n\n\nfunc _ready():\n\tprint({written})\n";
            GDScriptNodePath path = GDScriptNodePaths.In(GDScriptTree.Parse(script)).Single();
            int at = path.Path.IndexOf(old, StringComparison.Ordinal);
            IReadOnlyList<TextEdit> edits = path.Renamed([(at, at + old.Length)], name)!;
            Assert.Single(edits);
            return script[(script.IndexOf("print(", StringComparison.Ordinal) + 6)..edits[0].Start] + edits[0].Text + script[edits[0].End..^2];
        }
        Assert.Equal("$Hud/Points", Renamed("$Hud/Score", "Points"));
        Assert.Equal("%Points", Renamed("%Score", "Points"));
        Assert.Equal("$Hud/नमस्ते", Renamed("$Hud/Score", "नमस्ते"));
        Assert.Equal("%a\u200Cb", Renamed("%Score", "a\u200Cb"));
        Assert.Equal("$\"Hud/My Points\"", Renamed("$Hud/Score", "My Points"));
        Assert.Equal("$\"%My Points\"", Renamed("%Score", "My Points"));
        Assert.Equal("get_node(\"Hud/A\\\\B\")", Renamed("get_node(\"Hud/Score\")", "A\\B"));
        Assert.Equal("get_node('Hud/It\\'s')", Renamed("get_node('Hud/Score')", "It's"));
        Assert.Equal("get_node(\"Hud/It's\")", Renamed("get_node(\"Hud/Score\")", "It's"));

        GDScriptNodePath raw = GDScriptNodePaths.In(GDScriptTree.Parse("extends Node\n\n\nfunc _ready():\n\tget_node(r'Score')\n")).Single();
        Assert.Null(raw.Renamed([(0, 5)], "It's"));
    }

}
