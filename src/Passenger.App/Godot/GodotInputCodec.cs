using System.Buffers.Binary;

namespace Passenger.App.Godot;

/// <summary>The modifiers an input event of Godot's carries, as its codec packs them in an event's second byte.</summary>
[Flags]
public enum GodotModifiers : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Meta = 8,
}

/// <summary>Input events in the bytes Godot's embedded display takes them in (<c>embed:event</c>), as its own codec writes
/// them (core/input/input_event_codec.cpp, Godot 4.5): the event's type, a byte of its modifiers and flags, then its fields,
/// whole numbers and floats little-endian; and the display's state (<c>embed:ds_state</c>) as display_server_embedded.mm
/// serializes it. A key is named by Avalonia's name for it (<see cref="KeyOf"/>) and given Godot's code.</summary>
public static class GodotInputCodec
{
    private const byte KeyType = 0;
    private const byte MouseButtonType = 1;
    private const byte MouseMotionType = 2;

    private const int Echo = 4;
    private const int Pressed = 5;
    private const int DoubleClick = 6;

    /// <summary>The bit of a key's code for a key that writes no character, Godot's Key::SPECIAL.</summary>
    private const uint Special = 1u << 22;

    /// <summary>A key pressed or let go (19 bytes): its code, its code by its place, its label, the character it writes (0 for
    /// none) and which of a pair it is (0 either, 1 the left, 2 the right).</summary>
    public static byte[] Key(GodotModifiers modifiers, bool pressed, bool echo, uint keycode, uint physical, uint label, uint unicode, byte location)
    {
        byte[] data = new byte[19];
        data[0] = KeyType;
        data[1] = (byte)((byte)modifiers | (echo ? 1 << Echo : 0) | (pressed ? 1 << Pressed : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), keycode);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6), physical);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), label);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), unicode);
        data[18] = location;
        return data;
    }

    /// <summary>A mouse button pressed or let go (12 bytes): its index (1 left, 2 right, 3 middle, 4 to 7 the wheel's ways,
    /// 8 and 9 the side buttons), where in the game's window in its pixels, and the buttons held after it as Godot's mask.</summary>
    public static byte[] MouseButton(GodotModifiers modifiers, bool pressed, bool doubleClick, byte button, float x, float y, byte mask)
    {
        byte[] data = new byte[12];
        data[0] = MouseButtonType;
        data[1] = (byte)((byte)modifiers | (pressed ? 1 << Pressed : 0) | (doubleClick ? 1 << DoubleClick : 0));
        data[2] = button;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(3), x);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(7), y);
        data[11] = mask;
        return data;
    }

    /// <summary>The mouse moved (31 bytes): where it is in the game's window in its pixels, its pressure (no pen's, none),
    /// no tilt, how far it moved, and the buttons held as Godot's mask.</summary>
    public static byte[] MouseMotion(GodotModifiers modifiers, float x, float y, float relativeX, float relativeY, byte mask)
    {
        byte[] data = new byte[31];
        data[0] = MouseMotionType;
        data[1] = (byte)modifiers;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(2), x);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(6), y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(10), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(14), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(18), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(22), relativeX);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(26), relativeY);
        data[30] = mask;
        return data;
    }

    /// <summary>The embedded display's state (32 bytes, of which Godot before 4.7 reads the first 16): the scale sizes it is
    /// told are in, the screen's dots per inch, the scale of the window it is shown in, that window's display's id, and the
    /// display's brightest values, those of a display of no extended range.</summary>
    public static byte[] DisplayState(float maxScale, float dpi, float windowScale, uint display)
    {
        byte[] data = new byte[32];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0), maxScale);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), dpi);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), windowScale);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), display);
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(16), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(24), 1);
        return data;
    }

    /// <summary>A mouse button's bit in Godot's mask of the buttons held (MouseButtonMask, a byte as its codec keeps it): the
    /// button's index less one, so the side buttons' fall outside it, as in Godot's own.</summary>
    public static byte MaskOf(int button) => button is >= 1 and <= 8 ? (byte)(1 << (button - 1)) : (byte)0;

    /// <summary>Godot's code of a key by Avalonia's name for it (its Key or its PhysicalKey: "A", "D1", "Digit1", "Return",
    /// "Enter", "ShiftLeft", "NumPad5"), with which of a pair it is; Godot's UNKNOWN for a name it has no code for.</summary>
    public static (uint Code, byte Location) KeyOf(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterUpper(name[0]))
        {
            return (name[0], 0);
        }
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1]))
        {
            return (name[1], 0);
        }
        if (name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6 && char.IsAsciiDigit(name[5]))
        {
            return (name[5], 0);
        }
        if ((name.StartsWith("NumPad", StringComparison.Ordinal) || name.StartsWith("Numpad", StringComparison.Ordinal)) && name.Length == 7 && char.IsAsciiDigit(name[6]))
        {
            return (Special | (uint)(0x86 + (name[6] - '0')), 0);
        }
        if (name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 35)
        {
            return (Special | (uint)(0x1B + f), 0);
        }
        return name switch
        {
            "Escape" => (Special | 0x01, 0),
            "Tab" => (Special | 0x02, 0),
            "Back" or "Backspace" => (Special | 0x04, 0),
            "Return" or "Enter" => (Special | 0x05, 0),
            "NumPadEnter" or "NumpadEnter" => (Special | 0x06, 0),
            "Insert" => (Special | 0x07, 0),
            "Delete" => (Special | 0x08, 0),
            "Pause" => (Special | 0x09, 0),
            "PrintScreen" or "Snapshot" => (Special | 0x0A, 0),
            "Clear" or "NumPadClear" => (Special | 0x0C, 0),
            "Home" => (Special | 0x0D, 0),
            "End" => (Special | 0x0E, 0),
            "Left" or "ArrowLeft" => (Special | 0x0F, 0),
            "Up" or "ArrowUp" => (Special | 0x10, 0),
            "Right" or "ArrowRight" => (Special | 0x11, 0),
            "Down" or "ArrowDown" => (Special | 0x12, 0),
            "PageUp" or "Prior" => (Special | 0x13, 0),
            "PageDown" or "Next" => (Special | 0x14, 0),
            "LeftShift" or "ShiftLeft" => (Special | 0x15, 1),
            "RightShift" or "ShiftRight" => (Special | 0x15, 2),
            "LeftCtrl" or "ControlLeft" => (Special | 0x16, 1),
            "RightCtrl" or "ControlRight" => (Special | 0x16, 2),
            "LWin" or "MetaLeft" => (Special | 0x17, 1),
            "RWin" or "MetaRight" => (Special | 0x17, 2),
            "LeftAlt" or "AltLeft" => (Special | 0x18, 1),
            "RightAlt" or "AltRight" => (Special | 0x18, 2),
            "CapsLock" or "Capital" => (Special | 0x19, 0),
            "NumLock" => (Special | 0x1A, 0),
            "Scroll" or "ScrollLock" => (Special | 0x1B, 0),
            "Multiply" or "NumPadMultiply" => (Special | 0x81, 0),
            "Divide" or "NumPadDivide" => (Special | 0x82, 0),
            "Subtract" or "NumPadSubtract" => (Special | 0x83, 0),
            "Decimal" or "NumPadDecimal" => (Special | 0x84, 0),
            "Add" or "NumPadAdd" => (Special | 0x85, 0),
            "Apps" or "ContextMenu" => (Special | 0x42, 0),
            "Help" => (Special | 0x45, 0),
            "Space" => (0x20, 0),
            "OemQuotes" or "Quote" => (0x27, 0),
            "OemComma" or "Comma" => (0x2C, 0),
            "OemMinus" or "Minus" => (0x2D, 0),
            "OemPeriod" or "Period" => (0x2E, 0),
            "OemQuestion" or "Slash" => (0x2F, 0),
            "OemSemicolon" or "Semicolon" => (0x3B, 0),
            "OemPlus" or "Equal" => (0x3D, 0),
            "OemOpenBrackets" or "BracketLeft" => (0x5B, 0),
            "OemPipe" or "Backslash" => (0x5C, 0),
            "OemCloseBrackets" or "BracketRight" => (0x5D, 0),
            "OemTilde" or "Backquote" => (0x60, 0),
            _ => (Special | 0x7FFFFF, 0),
        };
    }
}
