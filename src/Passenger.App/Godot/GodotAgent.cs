namespace Passenger.App.Godot;

/// <summary>Passenger's helper in a Godot game it debugs (<c>PassengerAgent.gd</c>, carried in the App): a node of
/// GDScript under the game's root, for what Godot's remote debug protocol does not carry, input and where nodes are
/// drawn. <see cref="GodotGame.AskAsync"/> brings it in through Godot's own live editing messages when an agent first
/// needs it (a node made under the root, its script set from the file <c>Written</c> writes); it says where each node is in the
/// window and what it shows, gives clicks, keys, typing and the wheel as a person does, through Godot's Input, and counts
/// the frames the game runs, which Passenger's hold on its loop steps one at a time.</summary>
public static partial class GodotAgent
{
    /// <summary>Its node's name under the game's root.</summary>
    public const string NodeName = "PassengerAgent";

    /// <summary>The message each of its answers comes in: the ask it answers, whether it was done, then what it came to,
    /// or why not.</summary>
    public const string Said = "passenger:said";

    /// <summary>The name of its script's file.</summary>
    private const string FileName = "passenger_agent.gd";

    /// <summary>Its modifier flags, as its calls take them.</summary>
    public const int Shift = 1;
    public const int Control = 2;
    public const int Alt = 4;
    public const int Meta = 8;

    /// <summary>Whether a node of a game's tree is the helper, whose type is its script's path.</summary>
    public static bool Is(GodotNode node) => node.Name == NodeName && node.Type.EndsWith(FileName, StringComparison.Ordinal);

    /// <summary>Whether a place an error was raised at is the helper's script.</summary>
    public static bool Raised(GodotError error) =>
        error.File.EndsWith(FileName, StringComparison.Ordinal) || error.Frames.Any(f => f.File.EndsWith(FileName, StringComparison.Ordinal));

    /// <summary>A key as Godot names it (<c>OS.find_keycode_from_string</c>), from Avalonia's name for it as app_key reads one.</summary>
    public static string KeyName(string avalonia) => avalonia switch
    {
        "Back" => "Backspace",
        "Return" => "Enter",
        "OemPlus" => "Plus",
        "OemMinus" => "Minus",
        "OemComma" => "Comma",
        "OemPeriod" => "Period",
        "Next" => "PageDown",
        "Prior" => "PageUp",
        ['D', >= '0' and <= '9'] digit => digit[1..],
        _ => avalonia,
    };

    /// <summary>A node path from its text: "/root", "..", "../PassengerAgent".</summary>
    public static GodotNodePath PathOf(string text) =>
        new([.. text.Split('/', StringSplitOptions.RemoveEmptyEntries)], [], text.StartsWith('/'));
}
