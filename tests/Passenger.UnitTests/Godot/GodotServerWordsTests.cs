using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>What Passenger reads of Godot for GDScript's server: the port its editor serves on, from its Editor Settings as
/// Godot writes them, Godot's own where they set none; and a warning's name out of its message, as Godot's server writes one.</summary>
public class GodotServerWordsTests
{
    [Fact]
    public void The_editors_port_is_its_settings_remote_port_and_godots_own_where_they_set_none()
    {
        const string Settings = "[gd_resource type=\"EditorSettings\" format=3]\n\n[resource]\ninterface/editor/code_font_size = 15\n"
            + "network/language_server/remote_host = \"127.0.0.1\"\nnetwork/language_server/remote_port = 6008\n";
        Assert.Equal(6008, GodotServerWords.PortIn(Settings));
        Assert.Equal(GodotServerWords.DefaultPort, GodotServerWords.PortIn("[resource]\ninterface/editor/code_font_size = 15\n"));
        Assert.Equal(GodotServerWords.DefaultPort, GodotServerWords.PortIn("[resource]\nnetwork/language_server/remote_port = \"none\"\n"));
    }

    [Fact]
    public void A_warnings_name_is_read_out_of_its_message_and_an_error_keeps_its_own()
    {
        Assert.Equal(("UNUSED_PARAMETER", "The parameter \"delta\" is never used in the function \"_process()\"."),
            GodotServerWords.Warning("(UNUSED_PARAMETER): The parameter \"delta\" is never used in the function \"_process()\"."));
        Assert.Equal((null, null), GodotServerWords.Warning("Expected expression after \"+\" operator."));
        Assert.Equal((null, null), GodotServerWords.Warning("(not a name): words"));
    }

    /// <summary>Godot 4.8's server gives a warning's name as the diagnostic's code and its message alone.</summary>
    [Fact]
    public void A_warnings_name_given_as_its_code_is_its_name_and_the_message_stays_whole()
    {
        Assert.Equal(("UNUSED_VARIABLE", "The local variable \"a\" is declared but never used in the block."),
            GodotServerWords.Warning("The local variable \"a\" is declared but never used in the block.", "UNUSED_VARIABLE"));
        Assert.Equal((null, null), GodotServerWords.Warning("Expected expression after \"+\" operator.", null));
        Assert.Equal((null, null), GodotServerWords.Warning("Some lint rule.", "no-unused-vars"));
    }
}
