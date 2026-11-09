using Passenger.App.Godot;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>A GDScript stop as a running Godot 4.7 game tells it over its remote debug connection, each message's bytes taken
/// from one (after their length): a stop at a breakpoint, at a pause outside every script and at a script error, the stack,
/// a frame's variables (a local, a member, <c>self</c> as an object's id), an evaluation's answer and the error a bad one
/// raises; then what the debugger shows of a value, what is inside one, and the breakpoints a game is started with.</summary>
public class GodotScriptStopsTests
{
    private const string AtBreakpoint = "1c00000003000000040000000b00000064656275675f656e7465720002000000010000001c00000004000000010000000100000004000000"
        + "0a000000427265616b706f696e74000001000000010000000200000001000000";

    private const string AtPause = "1c00000003000000040000000b00000064656275675f656e7465720002000000010000001c0000000400000001000000010000000400000000"
        + "00000001000000000000000200000001000000";

    private const string AtError = "1c00000003000000040000000b00000064656275675f656e7465720002000000010000001c00000004000000010000000000000004000000"
        + "2e00000043616e6e6f742063616c6c206d6574686f6420276765745f6e616d6527206f6e2061206e756c6c2076616c75652e000001000000010000000200000001000000";

    private const string Stack = "1c00000003000000040000000a000000737461636b5f64756d70000002000000010000001c00000007000000020000000600000004000000"
        + "0d0000007265733a2f2f6d61696e2e6764000000020000001800000004000000050000007477696365000000040000000d0000007265733a2f2f6d61696e2e676400000002"
        + "0000000e00000004000000080000005f70726f63657373";

    private const string NoStack = "1c00000003000000040000000a000000737461636b5f64756d70000002000000010000001c000000010000000200000000000000";

    private const string FiveVariables = "1c000000030000000400000010000000737461636b5f6672616d655f7661727302000000010000001c000000010000000200000005000000";

    private const string Delta = "1c00000003000000040000000f000000737461636b5f6672616d655f7661720002000000010000001c0000000500000004000000050000"
        + "0064656c746100000002000000000000000200000003000000030001004f1be8b4814e7b3f0400000000000000";

    private const string Self = "1c00000003000000040000000f000000737461636b5f6672616d655f7661720002000000010000001c00000005000000040000000400000073"
        + "656c660200000001000000020000001800000018000100d90500760600000004000000060000004e6f646532440000";

    private const string Counter = "1c00000003000000040000000f000000737461636b5f6672616d655f7661720002000000010000001c00000005000000040000000700000063"
        + "6f756e7465720002000000010000000200000002000000020000005d0000000400000000000000";

    private const string Evaluated = "1c0000000300000004000000110000006576616c756174696f6e5f72657475726e00000002000000010000001c000000050000000400000008"
        + "0000006b657074202b203102000000030000000200000002000000020000002a0000000400000000000000";

    private const string BadEvaluation = "1c0000000300000004000000110000006576616c756174696f6e5f72657475726e00000002000000010000001c00000005000000040000"
        + "00050000006e6f70652800000002000000030000000200000000000000000000000400000000000000";

    private const string EvaluationError = "1c0000000300000004000000050000006572726f7200000002000000010000001c0000000e000000020000000000000002000000000000"
        + "00020000000000000002000000fc0000000400000018000000636f72652f6d6174682f65787072657373696f6e2e6370700400000007000000657865637574650002000000"
        + "d70500000400000033000000436f6e646974696f6e20226572726f725f7365742220697320747275652e2052657475726e696e673a2056617269616e742829000400000039"
        + "0000005468657265207761732070726576696f75736c792061207061727365206572726f723a2045787065637465642065787072657373696f6e2e2e000000010000000000"
        + "00000200000003000000040000000d0000007265733a2f2f6d61696e2e676400000004000000060000005f726561647900000200000008000000";

    private static GodotMessage Message(string hex) => GodotMessage.Read(Convert.FromHexString(hex));

    [Fact]
    public void A_stop_reads_as_a_breakpoints_a_pauses_outside_every_script_or_a_script_errors_that_takes_no_step()
    {
        Assert.Equal(new GodotBreak(true, GodotBreak.AtBreakpoint, true, 1), GodotBreak.Read(Message(AtBreakpoint).Data));
        GodotBreak paused = GodotBreak.Read(Message(AtPause).Data)!;
        Assert.False(paused.HasStack, "A pause outside every script read as having a script's stack");
        Assert.False(paused.IsError, "A pause read as a script error");
        GodotBreak error = GodotBreak.Read(Message(AtError).Data)!;
        Assert.True(error.IsError, "A script error's stop read as a breakpoint's");
        Assert.False(error.CanStep, "A script error's stop read as one the game steps on from");
        Assert.Equal("Cannot call method 'get_name' on a null value.", error.Error);
        Assert.Null(GodotBreak.Read([true, "Breakpoint"]));
    }

    [Fact]
    public void A_stack_reads_innermost_first_and_a_pause_outside_every_script_has_none()
    {
        Assert.Equal([new GodotScriptFrame("res://main.gd", 24, "twice"), new GodotScriptFrame("res://main.gd", 14, "_process")],
            GodotScriptFrame.Stack(Message(Stack).Data));
        Assert.Empty(GodotScriptFrame.Stack(Message(NoStack).Data)!);
        Assert.Null(GodotScriptFrame.Stack([6L, "res://main.gd", 24L]));
    }

    [Fact]
    public void A_frames_variables_read_as_locals_and_members_self_an_object_by_its_id_and_class()
    {
        Assert.Equal(5, GodotScriptVariable.Count(Message(FiveVariables).Data));
        GodotScriptVariable delta = GodotScriptVariable.Read(Message(Delta).Data)!;
        Assert.Equal((GodotScope.Local, GodotType.Float, "float"), (delta.Scope, delta.Type, delta.TypeName));
        Assert.Equal(0.006666666666666667, Assert.IsType<double>(delta.Value), 15);
        GodotScriptVariable self = GodotScriptVariable.Read(Message(Self).Data)!;
        Assert.Equal((GodotScope.Member, "Node2D", "<Node2D#27749516761>"), (self.Scope, self.TypeName, self.Shown));
        GodotScriptVariable counter = GodotScriptVariable.Read(Message(Counter).Data)!;
        Assert.Equal(("counter", GodotScope.Member, "int", "93"), (counter.Name, counter.Scope, counter.TypeName, counter.Shown));
    }

    [Fact]
    public void An_evaluations_answer_reads_as_its_value_and_a_bad_ones_as_null_beside_the_error_it_raised()
    {
        GodotScriptVariable answer = GodotScriptVariable.Read(Message(Evaluated).Data)!;
        Assert.Equal(("kept + 1", GodotScope.Evaluated, "42"), (answer.Name, answer.Scope, answer.Shown));
        GodotScriptVariable bad = GodotScriptVariable.Read(Message(BadEvaluation).Data)!;
        Assert.Null(bad.Value);
        GodotError raised = GodotError.Read(Message(EvaluationError).Data)!;
        Assert.Equal("core/math/expression.cpp", raised.File);
        Assert.Equal("Expected expression", GodotScriptValues.Failure(raised));
    }

    [Fact]
    public void A_value_shows_as_GDScript_writes_it_and_what_is_inside_one_is_read_by_its_own_expression()
    {
        Assert.Equal("\"hi\"", GodotScriptValues.Shown("hi"));
        Assert.Equal("Vector2i", GodotScriptValues.TypeName(GodotType.Vector2I));
        Assert.Equal("null", GodotScriptValues.TypeName(GodotType.Nil));
        Assert.Equal([("[0]", "items[0]", (object?)1L), ("[1]", "items[1]", "two")], GodotScriptValues.Inside(new List<object?> { 1L, "two" }, "items"));
        Assert.Equal([("x", "at.x", (object?)3L), ("y", "at.y", 4L)], GodotScriptValues.Inside(new GodotMath(GodotType.Vector2I, [3, 4]), "at"));
        Assert.Equal([("\"a\"", "seen[\"a\"]", (object?)true)],
            GodotScriptValues.Inside(new GodotDictionary([new KeyValuePair<object?, object?>("a", true)]), "seen"));
        Assert.Empty(GodotScriptValues.Inside(5L, "n"));
        Assert.True(GodotScriptValues.HasInside(new GodotObjectId(7)), "An object read as holding nothing");
    }

    /// <summary>Godot sends a vector's numbers as singles, which Variables writes as the numbers they read back as, as the
    /// scene's inspector does, never the double each widens to, its components as singles too; a float is a double, written
    /// with a double's digits.</summary>
    [Fact]
    public void A_vectors_singles_show_in_Variables_as_written_not_as_the_doubles_they_widen_to()
    {
        object? sent = GodotVariant.Read(GodotVariant.Write(new GodotMath(GodotType.Vector2, [(double)0.3f, (double)0.1f])));
        GodotScriptVariable position = GodotScriptVariable.Read(["position", (long)GodotScope.Member, (long)GodotType.Vector2, sent, string.Empty])!;
        Assert.Equal(("Vector2", "(0.3, 0.1)"), (position.TypeName, position.Shown));
        Assert.Equal("Vector2(0.3, 0.1)", GodotText.Scene(sent));
        IReadOnlyList<(string Name, string? Expression, object? Value)> parts = GodotScriptValues.Inside(sent, "position");
        Assert.Equal([("x", "position.x", (object?)0.3f), ("y", "position.y", 0.1f)], parts);
        Assert.Equal(["0.3", "0.1"], parts.Select(p => GodotScriptValues.Shown(p.Value)));
        Assert.Equal(["0.3", "1234567.25"], new[] { 0.3, 1234567.25 }.Select(real =>
            GodotScriptVariable.Read(["far", (long)GodotScope.Local, (long)GodotType.Float, GodotVariant.Read(GodotVariant.Write(real)), string.Empty])!.Shown));
        Assert.Equal("[0.3, nan, inf_neg]", GodotScriptValues.Shown(new[] { 0.3f, float.NaN, float.NegativeInfinity }));
        Assert.Equal("[\"a\\nb\", \"tab\\there\"]", GodotScriptValues.Shown(new[] { "a\nb", "tab\there" }));
    }

    [Fact]
    public void A_game_starts_with_its_breakpoints_a_space_in_a_path_escaped_and_stops_only_at_a_line_with_code()
    {
        Assert.Equal("res://main.gd:8,res://scripts/my%20enemy.gd:12", GodotScriptBreakpoints.Argument([("res://main.gd", 8), ("res://scripts/my enemy.gd", 12)]));
        Assert.Null(GodotScriptBreakpoints.Argument([]));
        const string script = "extends Node\n\n# says hello\nfunc _ready() -> void:\n\tprint(\"hi\")\n";
        Assert.Equal([true, false, false, true, true], [.. Enumerable.Range(1, 5).Select(l => GodotScriptBreakpoints.IsCodeLine(script, l))]);
        Assert.False(GodotScriptBreakpoints.IsCodeLine(script, 40), "A line past the script's end read as one with code");
    }

    [Fact]
    public void A_condition_is_asked_of_the_game_as_the_truth_of_its_value_whatever_it_is_written_with()
    {
        Assert.Equal("not not (items)", GodotScriptBreakpoints.Truth("items"));
        Assert.Equal("not not (a or b)", GodotScriptBreakpoints.Truth("a or b"));
    }

    [Fact]
    public void A_tracepoints_message_parts_its_words_from_the_expressions_read_where_it_stops()
    {
        Assert.Equal([("count is ", false), ("counter", true), (" of {all}", false)], GodotScriptBreakpoints.Pieces("count is {counter} of {{all}}"));
        Assert.Equal([("name", true)], GodotScriptBreakpoints.Pieces("{name,nq}"));
        Assert.Equal([("open { never closed", false)], GodotScriptBreakpoints.Pieces("open { never closed"));
    }
}
