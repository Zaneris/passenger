using System.Text;

namespace Passenger.Terminal;

/// <summary>What a reader of a program's text does with each thing <see cref="SequenceReader"/> finds in it: a character
/// to show, a C0 control, and each escape, CSI and OSC sequence as it ends (DCS, SOS, PM and APC strings are passed over).
/// A struct, so the reader's calls go straight to it.</summary>
public interface ISequenceActions
{
    void Print(int rune);

    void Control(int rune);

    /// <summary>An escape sequence ended: its intermediates are the reader's.</summary>
    void Escape(SequenceReader sequence, char final);

    /// <summary>A CSI ended: its private marker, intermediates and parameters are the reader's.</summary>
    void Csi(SequenceReader sequence, char final);

    /// <summary>An OSC ended, its text up to the reader's limit.</summary>
    void Osc(ReadOnlySpan<char> text);
}

/// <summary>The one reader of a program's control sequences, as xterm reads them, after the state machine in vt100.net's DEC
/// parser: C0 controls, ESC, CSI and OSC sequences to their ends, DCS, SOS, PM and APC strings passed over, CAN and SUB
/// abandoning a sequence and ESC beginning another. The terminal's screen and the consoles read through it, each acting on
/// what it finds (<see cref="ISequenceActions"/>). It keeps a sequence split between two reads.</summary>
public sealed class SequenceReader
{
    /// <summary>The longest text an OSC keeps, in UTF-16 units: what a program writes past it is dropped.</summary>
    private const int MostOsc = 8192;

    private char[] osc = new char[256];
    private int oscLength;
    private State state;

    /// <summary>The sequence's first intermediate byte (ESC ( and CSI SP q's).</summary>
    private int intermediate;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        Text,
        TextEscape,
    }

    /// <summary>Whether no sequence is under way: what comes is text or a control.</summary>
    public bool InText => state == State.Ground;

    /// <summary>Whether an OSC or a DCS, SOS, PM or APC string is under way, which only BEL or ST ends.</summary>
    public bool InString => state is State.Osc or State.OscEscape or State.Text or State.TextEscape;

    /// <summary>The CSI's parameters, read as they come.</summary>
    public SequenceParameters Parameters { get; } = new();

    /// <summary>The CSI's private marker (?, &lt;, = or &gt;), or '\0'.</summary>
    public char PrivateMarker { get; private set; }

    /// <summary>How many intermediate bytes the sequence has.</summary>
    public int Intermediates { get; private set; }

    /// <summary>Whether the sequence's intermediates are just this one byte.</summary>
    public bool Only(char middle) => Intermediates == 1 && intermediate == middle;

    /// <summary>Gives up the sequence under way, if any: what comes next is text.</summary>
    public void Abandon() => state = State.Ground;

    /// <summary>Takes a character (a whole code point), telling <paramref name="actions"/> what it is or what it ends.</summary>
    public void Take<T>(int rune, T actions) where T : ISequenceActions
    {
        switch (state)
        {
            case State.Ground:
                if (rune == 0x1b)
                {
                    Begin(State.Escape);
                }
                else if (rune < 0x20 || rune == 0x7f)
                {
                    actions.Control(rune);
                }
                else
                {
                    actions.Print(rune);
                }
                break;
            case State.Escape:
                if (Interrupts(rune, actions))
                {
                    break;
                }
                switch (rune)
                {
                    case '[':
                        state = State.Csi;
                        break;
                    case ']':
                        oscLength = 0;
                        state = State.Osc;
                        break;
                    case 'P' or 'X' or '^' or '_':
                        state = State.Text;
                        break;
                    case >= 0x20 and <= 0x2f:
                        Intermediate(rune);
                        state = State.EscapeIntermediate;
                        break;
                    default:
                        state = State.Ground;
                        actions.Escape(this, (char)rune);
                        break;
                }
                break;
            case State.EscapeIntermediate:
                if (Interrupts(rune, actions))
                {
                    break;
                }
                if (rune is >= 0x20 and <= 0x2f)
                {
                    Intermediate(rune);
                }
                else
                {
                    state = State.Ground;
                    actions.Escape(this, (char)rune);
                }
                break;
            case State.Csi:
                if (Interrupts(rune, actions))
                {
                    break;
                }
                if (rune is '<' or '=' or '>' or '?' && Parameters.IsEmpty && PrivateMarker == '\0')
                {
                    PrivateMarker = (char)rune;
                }
                else if (rune is >= 0x30 and <= 0x3f)
                {
                    Parameters.Take(rune);
                }
                else if (rune is >= 0x20 and <= 0x2f)
                {
                    Intermediate(rune);
                }
                else if (rune is >= 0x40 and <= 0x7e)
                {
                    Parameters.End();
                    state = State.Ground;
                    actions.Csi(this, (char)rune);
                }
                break;
            case State.Osc:
                if (rune == 0x07)
                {
                    state = State.Ground;
                    actions.Osc(osc.AsSpan(0, oscLength));
                }
                else if (rune == 0x1b)
                {
                    state = State.OscEscape;
                }
                else if (oscLength < MostOsc)
                {
                    if (osc.Length < oscLength + 2)
                    {
                        Array.Resize(ref osc, Math.Min(osc.Length * 2, MostOsc + 1));
                    }
                    oscLength += new Rune(rune).EncodeToUtf16(osc.AsSpan(oscLength));
                }
                break;
            case State.OscEscape:
                state = State.Ground;
                actions.Osc(osc.AsSpan(0, oscLength));
                if (rune != '\\')
                {
                    Begin(State.Escape);
                    Take(rune, actions);
                }
                break;
            case State.Text:
                // DCS, SOS, PM and APC: nothing here needs their contents, so they are passed over to their end.
                if (rune == 0x1b)
                {
                    state = State.TextEscape;
                }
                else if (rune is 0x07 or 0x18 or 0x1a)
                {
                    state = State.Ground;
                }
                break;
            case State.TextEscape:
                if (rune == '\\')
                {
                    state = State.Ground;
                }
                else
                {
                    Begin(State.Escape);
                    Take(rune, actions);
                }
                break;
        }
    }

    /// <summary>A control inside a sequence: CAN and SUB abandon it, ESC starts another, the rest act as they would
    /// outside it.</summary>
    private bool Interrupts<T>(int rune, T actions) where T : ISequenceActions
    {
        if (rune is 0x18 or 0x1a)
        {
            state = State.Ground;
            return true;
        }
        if (rune == 0x1b)
        {
            Begin(State.Escape);
            return true;
        }
        if (rune < 0x20 || rune == 0x7f)
        {
            actions.Control(rune);
            return true;
        }
        return false;
    }

    private void Begin(State next)
    {
        Parameters.Clear();
        intermediate = 0;
        Intermediates = 0;
        PrivateMarker = '\0';
        state = next;
    }

    private void Intermediate(int rune)
    {
        if (Intermediates++ == 0)
        {
            intermediate = rune;
        }
    }
}

/// <summary>A CSI's parameters, read as their bytes arrive: groups apart at semicolons, each group's parts apart at colons
/// (SGR's 38:2:R:G:B), each part a number up to 65535, or -1 when it is empty, holds a byte other than a digit or is past an
/// int's range. A sequence keeps its first <see cref="Most"/> parts and drops the rest, so millions of separators (a binary
/// file written out) take no more memory or work than those.</summary>
public sealed class SequenceParameters
{
    /// <summary>The most parts a sequence keeps, its groups' together.</summary>
    private const int Most = 32;

    private readonly int[] values = new int[Most];

    /// <summary>Where each group's parts start among <see cref="values"/>.</summary>
    private readonly int[] starts = new int[Most];

    private int count;
    private long number;
    private bool digits;
    private bool bad;

    /// <summary>Whether the sequence has <see cref="Most"/> parts, so what comes after them is dropped.</summary>
    private bool full;

    /// <summary>How many groups the sequence has: none until a parameter byte comes.</summary>
    public int Count { get; private set; }

    /// <summary>Whether no parameter byte has come yet.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>A group's part; every group has its first.</summary>
    public int this[int group, int part] => values[starts[group] + part];

    public int Parts(int group) => (group + 1 < Count ? starts[group + 1] : count) - starts[group];

    internal void Clear()
    {
        Count = 0;
        count = 0;
        number = 0;
        digits = false;
        bad = false;
        full = false;
    }

    /// <summary>Takes a parameter byte, 0x30 to 0x3F.</summary>
    internal void Take(int c)
    {
        if (Count == 0)
        {
            Open();
        }
        if (full)
        {
            return;
        }
        switch (c)
        {
            case >= '0' and <= '9':
                digits = true;
                if (!bad)
                {
                    number = (number * 10) + (c - '0');
                    bad = number > int.MaxValue;
                }
                break;
            case ';':
                EndPart();
                if (!full)
                {
                    Open();
                }
                break;
            case ':':
                EndPart();
                break;
            default:
                bad = true;
                break;
        }
    }

    /// <summary>The sequence's final byte has come: its last part ends, unless it was dropped.</summary>
    internal void End()
    {
        if (Count > 0 && !full)
        {
            EndPart();
        }
    }

    private void Open() => starts[Count++] = count;

    private void EndPart()
    {
        values[count++] = digits && !bad ? (int)Math.Min(number, 65535) : -1;
        number = 0;
        digits = false;
        bad = false;
        full = count == Most;
    }
}
