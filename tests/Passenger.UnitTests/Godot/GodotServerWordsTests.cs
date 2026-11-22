using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>What Passenger reads of Godot for GDScript's server: the port its editor serves on, from its Editor Settings as
/// Godot writes them, Godot's own where they set none; and a warning's name out of its message, as Godot's server writes one.</summary>
public class GodotServerWordsTests
{
    /// <summary>The editor's port and a warning's name are read as Godot's server words them.</summary>
    [Fact]
    public void The_servers_port_and_warning_names_are_read_from_its_words() => Cases.All(
        (nameof(The_editors_port_is_its_settings_remote_port_and_godots_own_where_they_set_none),
            The_editors_port_is_its_settings_remote_port_and_godots_own_where_they_set_none),
        (nameof(A_warnings_name_is_read_out_of_its_message_and_an_error_keeps_its_own),
            A_warnings_name_is_read_out_of_its_message_and_an_error_keeps_its_own));

    private void The_editors_port_is_its_settings_remote_port_and_godots_own_where_they_set_none()
    {
        const string Settings = "[gd_resource type=\"EditorSettings\" format=3]\n\n[resource]\ninterface/editor/code_font_size = 15\n"
            + "network/language_server/remote_host = \"127.0.0.1\"\nnetwork/language_server/remote_port = 6008\n";
        Assert.Equal(6008, GodotServerWords.PortIn(Settings));
        Assert.Equal(GodotServerWords.DefaultPort, GodotServerWords.PortIn("[resource]\ninterface/editor/code_font_size = 15\n"));
        Assert.Equal(GodotServerWords.DefaultPort, GodotServerWords.PortIn("[resource]\nnetwork/language_server/remote_port = \"none\"\n"));
    }

    private void A_warnings_name_is_read_out_of_its_message_and_an_error_keeps_its_own()
    {
        Assert.Equal(("UNUSED_PARAMETER", "The parameter \"delta\" is never used in the function \"_process()\"."),
            GodotServerWords.Warning("(UNUSED_PARAMETER): The parameter \"delta\" is never used in the function \"_process()\"."));
        Assert.Equal((null, null), GodotServerWords.Warning("Expected expression after \"+\" operator."));
        Assert.Equal((null, null), GodotServerWords.Warning("(not a name): words"));
    }

}
