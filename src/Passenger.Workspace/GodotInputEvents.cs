using System.Globalization;

namespace Passenger.Workspace;

/// <summary>An input event an action of project.godot's <c>[input]</c> takes, as Godot's variant text writes it
/// (<c>Object(InputEventKey,"keycode":0,"physical_keycode":32,…)</c>), said as a person names it: a key with its modifiers
/// ("Ctrl+Space (physical)"), a mouse button, a joypad's button or axis. Its numbers are Godot's: a key's code its Unicode
/// below <see cref="Special"/>, a named key's above it.</summary>
public static class GodotInputEvents
{
    /// <summary>The bit Godot sets in the code of a key that writes no character (Escape, F1, the arrows).</summary>
    private const long Special = 1L << 22;

    private static readonly string[] SpecialKeys =
    [
        string.Empty, "Escape", "Tab", "Backtab", "Backspace", "Enter", "Kp Enter", "Insert", "Delete", "Pause", "Print", "SysReq", "Clear", "Home", "End",
        "Left", "Up", "Right", "Down", "PageUp", "PageDown", "Shift", "Ctrl", "Meta", "Alt", "CapsLock", "NumLock", "ScrollLock",
    ];

    private static readonly string[] MouseButtons =
    [
        string.Empty, "Left Mouse Button", "Right Mouse Button", "Middle Mouse Button", "Mouse Wheel Up", "Mouse Wheel Down", "Mouse Wheel Left",
        "Mouse Wheel Right", "Mouse Thumb Button 1", "Mouse Thumb Button 2",
    ];

    private static readonly string[] JoypadButtons =
    [
        "Bottom Action", "Right Action", "Left Action", "Top Action", "Back", "Guide", "Start", "Left Stick", "Right Stick", "Left Shoulder",
        "Right Shoulder", "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right", "Misc 1", "Paddle 1", "Paddle 2", "Paddle 3", "Paddle 4", "Touchpad",
    ];

    private static readonly (string Name, long Code, long Unicode)[] GivenKeys = MadeKeys();

    /// <summary>The events a person gives an action from the project page, by the names they show: a key pressed by its place
    /// (the letters, the digits, Space, the named keys a game takes most and F1 to F12), then each of the mouse's buttons.</summary>
    public static IReadOnlyList<string> Givable { get; } = [.. GivenKeys.Select(k => k.Name), .. MouseButtons.Skip(1)];

    /// <summary>An event of <see cref="Givable"/> as Godot 4's editor writes it among an action's events; null for a name
    /// it does not hold.</summary>
    public static string? Written(string name)
    {
        foreach ((string key, long code, long unicode) in GivenKeys)
        {
            if (key == name)
            {
                return GodotWrites.KeyEvent(code, unicode);
            }
        }
        int button = Array.IndexOf(MouseButtons, name);
        return button > 0 ? GodotWrites.MouseButtonEvent(button) : null;
    }

    /// <summary>The keys of <see cref="Givable"/>, each with Godot's code and the character a press of it writes (none for a
    /// named key).</summary>
    private static (string, long, long)[] MadeKeys()
    {
        List<(string, long, long)> keys = [];
        for (char c = 'A'; c <= 'Z'; c++)
        {
            keys.Add((c.ToString(), c, char.ToLowerInvariant(c)));
        }
        for (char c = '0'; c <= '9'; c++)
        {
            keys.Add((c.ToString(), c, c));
        }
        keys.Add(("Space", 32, 32));
        foreach (string named in (string[])["Escape", "Enter", "Tab", "Backspace", "Left", "Up", "Right", "Down", "Shift", "Ctrl", "Alt"])
        {
            keys.Add((named, Special + Array.IndexOf(SpecialKeys, named), 0));
        }
        for (int f = 1; f <= 12; f++)
        {
            keys.Add(($"F{f.ToString(CultureInfo.InvariantCulture)}", Special + 0x1B + f, 0));
        }
        return [.. keys];
    }

    /// <summary>An event said by its class and the fields its object gives, each the text of its value as written.</summary>
    public static string Shown(string type, IReadOnlyDictionary<string, string> fields)
    {
        long Number(string key) => fields.TryGetValue(key, out string? value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) ? n : 0;
        switch (type)
        {
            case "InputEventKey":
                long physical = Number("physical_keycode");
                long code = physical != 0 ? physical : Number("keycode") is var key and not 0 ? key : Number("key_label");
                return Modifiers(fields) + KeyName(code) + (physical != 0 ? " (physical)" : string.Empty);
            case "InputEventMouseButton":
                long button = Number("button_index");
                string named = button > 0 && button < MouseButtons.Length ? MouseButtons[button] : $"Mouse Button {button}";
                return Modifiers(fields) + named + (fields.TryGetValue("double_click", out string? twice) && twice == "true" ? " (double-click)" : string.Empty);
            case "InputEventJoypadButton":
                long index = Number("button_index");
                return index >= 0 && index < JoypadButtons.Length ? $"Joypad {JoypadButtons[index]}" : $"Joypad Button {index}";
            case "InputEventJoypadMotion":
                long axis = Number("axis");
                bool negative = fields.TryGetValue("axis_value", out string? value) && value.TrimStart().StartsWith('-');
                return axis switch
                {
                    0 => negative ? "Joypad Left Stick Left" : "Joypad Left Stick Right",
                    1 => negative ? "Joypad Left Stick Up" : "Joypad Left Stick Down",
                    2 => negative ? "Joypad Right Stick Left" : "Joypad Right Stick Right",
                    3 => negative ? "Joypad Right Stick Up" : "Joypad Right Stick Down",
                    4 => "Joypad Left Trigger",
                    5 => "Joypad Right Trigger",
                    _ => $"Joypad Axis {axis} {(negative ? "-" : "+")}",
                };
            default:
                return type.StartsWith("InputEvent", StringComparison.Ordinal) ? type["InputEvent".Length..] : type;
        }
    }

    /// <summary>A key's name as Godot's editor shows it: a character's in capitals, a named key's by its name.</summary>
    public static string KeyName(long code)
    {
        if (code >= Special)
        {
            long n = code - Special;
            return n switch
            {
                > 0 when n < SpecialKeys.Length => SpecialKeys[n],
                >= 0x1C and <= 0x3E => $"F{n - 0x1B}",
                0x42 => "Menu",
                0x81 => "Kp Multiply",
                0x82 => "Kp Divide",
                0x83 => "Kp Subtract",
                0x84 => "Kp Period",
                0x85 => "Kp Add",
                >= 0x86 and <= 0x8F => $"Kp {n - 0x86}",
                _ => $"Key {code.ToString(CultureInfo.InvariantCulture)}",
            };
        }
        return code switch
        {
            0 => "(unset)",
            32 => "Space",
            > 32 and < 0x110000 and not (>= 0xD800 and <= 0xDFFF) => char.ConvertFromUtf32((int)code).ToUpperInvariant(),
            _ => $"Key {code.ToString(CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>The modifiers an event holds, each followed by '+': Ctrl or Cmd for one Godot maps to the system's.</summary>
    private static string Modifiers(IReadOnlyDictionary<string, string> fields)
    {
        bool On(string key) => fields.TryGetValue(key, out string? value) && value == "true";
        string held = On("command_or_control_autoremap") ? "Ctrl/Cmd+" : (On("ctrl_pressed") ? "Ctrl+" : string.Empty) + (On("meta_pressed") ? "Meta+" : string.Empty);
        return held + (On("alt_pressed") ? "Alt+" : string.Empty) + (On("shift_pressed") ? "Shift+" : string.Empty);
    }
}
