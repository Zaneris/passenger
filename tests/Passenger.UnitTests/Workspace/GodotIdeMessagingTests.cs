using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's IDE messaging protocol as Passenger speaks it to the person's Godot editor: the meta file's port, the
/// handshake each side checks, a message framed as the editor's own peer frames it and read back line by line however its
/// lines end, a request for a script's names and the editor's answer read without its quotes.</summary>
public class GodotIdeMessagingTests
{
    [Fact]
    public void A_meta_file_names_its_port_and_executable_and_a_text_naming_no_port_is_none()
    {
        Assert.Equal((52431, "/Applications/Godot_mono.app/Contents/MacOS/Godot"), GodotIdeMessaging.MetaOf("52431\n/Applications/Godot_mono.app/Contents/MacOS/Godot\n"));
        Assert.Equal((52431, @"C:\Godot\Godot.exe"), GodotIdeMessaging.MetaOf("52431\r\nC:\\Godot\\Godot.exe\r\n"));
        Assert.Null(GodotIdeMessaging.MetaOf(string.Empty));
        Assert.Null(GodotIdeMessaging.MetaOf("port\n/godot\n"));
        Assert.Null(GodotIdeMessaging.MetaOf("70000\n/godot\n"));
        Assert.EndsWith(Path.Combine(".godot", "mono", "metadata", "ide_messaging_meta.txt"), GodotIdeMessaging.MetaPath("/game"), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_godot_editor_of_the_protocol_s_major_and_no_later_minor_is_talked_to()
    {
        Assert.Equal("GodotIdeClient,Version=1.1.2,Passenger", GodotIdeMessaging.ClientHandshake);
        Assert.True(GodotIdeMessaging.IsServerHandshake("GodotIdeServer,Version=1.1.0,server"));
        Assert.True(GodotIdeMessaging.IsServerHandshake("GodotIdeServer,Version=1.0.7,server"));
        Assert.False(GodotIdeMessaging.IsServerHandshake("GodotIdeServer,Version=1.2.0,server"), "a later minor version refuses a client of an earlier one");
        Assert.False(GodotIdeMessaging.IsServerHandshake("GodotIdeServer,Version=2.0.0,server"), "another major version is another protocol");
        Assert.False(GodotIdeMessaging.IsServerHandshake("GodotIdeClient,Version=1.1.0,server"), "another client is no editor");
        Assert.False(GodotIdeMessaging.IsServerHandshake("HTTP/1.1 400 Bad Request"));
    }

    [Fact]
    public void A_request_is_framed_as_the_editor_reads_it_and_its_answer_is_read_back_line_by_line()
    {
        string written = GodotIdeMessaging.Written(new GodotIdeMessage(true, GodotIdeMessaging.CodeCompletion, GodotIdeMessaging.Ok,
            GodotIdeMessaging.CompletionAsked(GodotIdeKind.ThemeColors, "res://Hud.cs")));
        Assert.Equal("Request\nCodeCompletion\nOk\n1\n{\"Kind\":6,\"ScriptFile\":\"res://Hud.cs\"}\n\n", written);

        // As the editor's peer writes an answer on Windows: each line ended by "\r\n", one empty line after the body.
        string answer = "Response\r\nCodeCompletion\r\nOk\r\n1\r\n{\"Kind\":6,\"ScriptFile\":\"res://Hud.cs\",\"Suggestions\":[\"\\\"font_color\\\"\",\"\\\"font_shadow_color\\\"\"]}\r\n\r\n";
        GodotIdeMessaging.Reader reader = new();
        List<GodotIdeMessage> read = [];
        foreach (string line in Lines("Garbage\n" + answer + written))
        {
            if (reader.Read(line) is { } message)
            {
                read.Add(message);
            }
        }
        Assert.Equal(2, read.Count);
        Assert.False(read[0].Request);
        Assert.Equal((GodotIdeMessaging.CodeCompletion, GodotIdeMessaging.Ok), (read[0].Id, read[0].Status));
        Assert.Equal(["font_color", "font_shadow_color"], GodotIdeMessaging.CompletionAnswered(read[0].Body));
        Assert.True(read[1].Request);
        Assert.Null(GodotIdeMessaging.CompletionAnswered("null"));
    }

    [Fact]
    public void Font_sizes_and_styleboxes_are_asked_by_the_numbers_the_editor_s_engine_reads()
    {
        Assert.Equal(9, (int)GodotIdeKind.ThemeFontSizes);
        Assert.Equal(10, (int)GodotIdeKind.ThemeStyles);
        Assert.Contains("\"Kind\":10", GodotIdeMessaging.CompletionAsked(GodotIdeKind.ThemeStyles, "res://a.cs"), StringComparison.Ordinal);
    }

    /// <summary>A text's lines as the editor's reader takes them, each ended by "\n", "\r\n" or "\r".</summary>
    private static IEnumerable<string> Lines(string text)
    {
        using StringReader reader = new(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
