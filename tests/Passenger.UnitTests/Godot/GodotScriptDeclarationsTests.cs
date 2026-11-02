using Passenger.App.Godot;
using Passenger.Text;

namespace Passenger.UnitTests.Godot;

/// <summary>Where a GDScript file declares a function or a signal, which a scene's connection leads to.</summary>
public class GodotScriptDeclarationsTests
{
    [Fact]
    public void A_gdscript_functions_and_signals_are_found_where_their_lines_declare_them_outside_inner_classes()
    {
        TextSnapshot text = TextSnapshot.From("extends Node\n\nsignal scored(points: int)\n\n@rpc(\"any_peer\") func _on_scored_more() -> void:\n\tpass\n\n"
            + "static func make() -> Node:\n\treturn null\n\nclass Inner:\n\tfunc _on_scored() -> void:\n\t\tpass\n\nfunc _on_scored(points: int) -> void:\n\tpass\n");
        Assert.Equal(new TextPosition(14, 5), GodotMembers.DeclaredIn(text, "_on_scored", signal: false));
        Assert.Equal(new TextPosition(4, 22), GodotMembers.DeclaredIn(text, "_on_scored_more", signal: false));
        Assert.Equal(new TextPosition(7, 12), GodotMembers.DeclaredIn(text, "make", signal: false));
        Assert.Equal(new TextPosition(2, 7), GodotMembers.DeclaredIn(text, "scored", signal: true));
        Assert.Null(GodotMembers.DeclaredIn(text, "scored", signal: false));
        Assert.Null(GodotMembers.DeclaredIn(text, "missing", signal: false));
    }
}
