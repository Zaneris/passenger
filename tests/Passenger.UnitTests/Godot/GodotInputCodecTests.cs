using System.Buffers.Binary;
using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>The input events and state Passenger gives a game on macOS's embedded display, read back as Godot reads them:
/// <see cref="Decoded"/> is core/input/input_event_codec.cpp's decoders (Godot 4.5) and display_server_embedded.mm's
/// state, field by field at the offsets they read, so the bytes say to Godot what Passenger meant.</summary>
public sealed class GodotInputCodecTests
{
    /// <summary>An event's fields as Godot's decoder sets them, by name.</summary>
    private static Dictionary<string, object> Decoded(byte[] data)
    {
        Dictionary<string, object> read = new(StringComparer.Ordinal) { ["type"] = (int)data[0] };
        byte bools = data[1];
        void Modifiers()
        {
            read["shift"] = (bools & (1 << 0)) != 0;
            read["ctrl"] = (bools & (1 << 1)) != 0;
            read["alt"] = (bools & (1 << 2)) != 0;
            read["meta"] = (bools & (1 << 3)) != 0;
        }
        float Float(int at) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at));
        switch (data[0])
        {
            case 0:
                Assert.Equal(19, data.Length);
                Modifiers();
                read["echo"] = (bools & (1 << 4)) != 0;
                read["pressed"] = (bools & (1 << 5)) != 0;
                read["keycode"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(2));
                read["physical_keycode"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6));
                read["key_label"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10));
                read["unicode"] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14));
                read["location"] = (int)data[18];
                break;
            case 1:
                Assert.Equal(12, data.Length);
                Modifiers();
                read["pressed"] = (bools & (1 << 5)) != 0;
                read["double_click"] = (bools & (1 << 6)) != 0;
                read["button_index"] = (int)data[2];
                read["position"] = (Float(3), Float(7));
                read["button_mask"] = (int)data[11];
                break;
            case 2:
                Assert.Equal(31, data.Length);
                Modifiers();
                read["pen_inverted"] = (bools & (1 << 7)) != 0;
                read["position"] = (Float(2), Float(6));
                read["pressure"] = Float(10);
                read["tilt"] = (Float(14), Float(18));
                read["relative"] = (Float(22), Float(26));
                read["button_mask"] = (int)data[30];
                break;
            default:
                throw new Xunit.Sdk.XunitException($"Godot's decoder knows no event of type {data[0]}");
        }
        return read;
    }

    /// <summary>Keys, mouse buttons, moves and the display state read back as Godot's codec reads them.</summary>
    [Fact]
    public void Input_reads_back_as_godots_own_codec_reads_it() => Cases.All(
        (nameof(A_key_reads_back_as_godots_decoder_reads_it_its_codes_character_and_side),
            A_key_reads_back_as_godots_decoder_reads_it_its_codes_character_and_side),
        (nameof(A_mouse_button_reads_back_with_its_index_place_in_pixels_and_the_buttons_held),
            A_mouse_button_reads_back_with_its_index_place_in_pixels_and_the_buttons_held),
        (nameof(The_display_state_reads_back_as_display_server_embedded_deserializes_it),
            The_display_state_reads_back_as_display_server_embedded_deserializes_it));

    private void A_key_reads_back_as_godots_decoder_reads_it_its_codes_character_and_side()
    {
        (uint code, byte location) = GodotInputCodec.KeyOf("A");
        Dictionary<string, object> read = Decoded(GodotInputCodec.Key(GodotModifiers.Shift | GodotModifiers.Meta, pressed: true, echo: false, code, code, code, 'A', location));

        Assert.Equal(0, read["type"]);
        Assert.True((bool)read["shift"] && (bool)read["meta"] && !(bool)read["ctrl"] && !(bool)read["alt"], "The key's modifiers read back otherwise than given");
        Assert.True((bool)read["pressed"] && !(bool)read["echo"], "The key read back as let go, or as an echo");
        Assert.Equal((0x41u, 0x41u, 0x41u, (uint)'A', 0), ((uint)read["keycode"], (uint)read["physical_keycode"], (uint)read["key_label"], (uint)read["unicode"], (int)read["location"]));
    }

    private void A_mouse_button_reads_back_with_its_index_place_in_pixels_and_the_buttons_held()
    {
        byte held = (byte)(GodotInputCodec.MaskOf(1) | GodotInputCodec.MaskOf(2));
        Dictionary<string, object> read = Decoded(GodotInputCodec.MouseButton(GodotModifiers.Ctrl, pressed: true, doubleClick: true, 2, 640.5f, 360.25f, held));

        Assert.Equal(1, read["type"]);
        Assert.True((bool)read["ctrl"] && (bool)read["pressed"] && (bool)read["double_click"], "The button read back without its modifier, press or double click");
        Assert.Equal((2, (640.5f, 360.25f), 3), ((int)read["button_index"], ((float, float))read["position"], (int)read["button_mask"]));
    }

    private void The_display_state_reads_back_as_display_server_embedded_deserializes_it()
    {
        byte[] data = GodotInputCodec.DisplayState(2f, 144f, 2f, 69734662);

        Assert.Equal(32, data.Length);
        Assert.Equal((2f, 144f, 2f, 69734662u), (BinaryPrimitives.ReadSingleLittleEndian(data), BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12))));
        Assert.Equal((1d, 1d), (BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(16)), BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(24))));
    }
}
