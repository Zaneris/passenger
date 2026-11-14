namespace Passenger.Editor;

/// <summary>Mappings in force: keys typed in a mode a mapping takes are held while more may make one, then pressed as what
/// it maps them to (mapped again unless it is a noremap's), or as they were where they make none; a key an f, t, r, m, a
/// mark or a register waits for is never mapped, nor is a key . or :normal presses again.</summary>
public sealed partial class Vim
{
    /// <summary>The keys typed toward a mapping, held until they make one or cannot.</summary>
    private readonly List<string> typedAhead = [];

    /// <summary>How many of the keys pressed next are taken as they are, never mapped: what a noremap maps to.</summary>
    private int unmappedLeft;

    /// <summary>A key through the mappings: null to take it as it is; held while more keys may make a mapping; else the
    /// keys it came to, for the editor to press in turn.</summary>
    private VimResult? Mapped(string key)
    {
        VimMapModes mode = MapMode();
        if (typedAhead.Count == 0 && (mode == VimMapModes.None || !Registers.Mappings.Maps(mode)))
        {
            return null;
        }
        typedAhead.Add(key);
        (VimMapping? exact, bool longer) = Registers.Mappings.Find(mode, typedAhead);
        if (longer)
        {
            return VimResult.Held;
        }
        List<string> held = [.. typedAhead];
        typedAhead.Clear();
        if (exact is not null)
        {
            return Expanded(exact, []);
        }
        // Keys that went past every mapping: the longest one they began with, then the rest; else the first as it is.
        for (int n = held.Count - 1; n >= 1; n--)
        {
            if (Registers.Mappings.Find(mode, held[..n]).Exact is { } shorter)
            {
                return Expanded(shorter, held[n..]);
            }
        }
        if (held.Count == 1)
        {
            return null;
        }
        unmappedLeft = 1;
        return new VimResult(true) { Replay = held };
    }

    /// <summary>Whether keys are held toward a mapping, waiting for more that may make one (for timeoutlen to end).</summary>
    public bool AwaitsMapping => typedAhead.Count > 0;

    /// <summary>The wait for more keys toward a mapping ended (timeoutlen passed): the longest mapping the keys held begin
    /// with, then the rest; else the first key as it is, then the rest, as they were typed. For the editor to press in turn.</summary>
    public VimResult TimedOut()
    {
        if (typedAhead.Count == 0)
        {
            return VimResult.Left;
        }
        VimMapModes mode = MapMode();
        List<string> held = [.. typedAhead];
        typedAhead.Clear();
        for (int n = held.Count; n >= 1; n--)
        {
            if (Registers.Mappings.Find(mode, held[..n]).Exact is { } exact)
            {
                return Expanded(exact, held[n..]);
            }
        }
        unmappedLeft = 1;
        return new VimResult(true) { Replay = held };
    }

    /// <summary>A mapping's keys to press, then <paramref name="after"/>: those it maps to taken as they are for a noremap,
    /// and its own keys where what it maps to begins with them, so it never maps itself without end.</summary>
    private VimResult Expanded(VimMapping mapping, List<string> after)
    {
        unmappedLeft = !mapping.Remaps ? mapping.To.Count
            : mapping.To.Count >= mapping.Keys.Count && mapping.To.Take(mapping.Keys.Count).SequenceEqual(mapping.Keys) ? mapping.Keys.Count : 0;
        List<string> keys = [.. mapping.To, .. after];
        return keys.Count == 0 ? VimResult.Held : new VimResult(true) { Replay = keys };
    }

    /// <summary>The mode mappings take the next key in: none on the command line, nor where a command waits for a
    /// character of its own (f, t, r, m, a mark, a register).</summary>
    private VimMapModes MapMode()
    {
        bool named = pending.Count >= 2 && pending[^2] == "\"";
        if (commandLine is not null || !named && pending.Count > 0 && pending[^1] is "f" or "F" or "t" or "T" or "r" or "m" or "'" or "`" or "q" or "@" or "\"" or "g" or "z" or "i" or "a")
        {
            return VimMapModes.None;
        }
        return Mode switch
        {
            VimMode.Insert or VimMode.Replace => VimMapModes.Insert,
            VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock => VimMapModes.Visual,
            _ when pending.Any(k => k is "d" or "c" or "y" or "<" or ">" or "=") => VimMapModes.Operator,
            _ => VimMapModes.Normal,
        };
    }

    /// <summary>A mappings file's set lines done in turn as :set does them, over the options of <paramref name="registers"/>;
    /// what each one refused, said as :set says it.</summary>
    public static IReadOnlyList<string> SetAll(VimRegisters registers, IEnumerable<string> lines)
    {
        Vim vim = new(registers);
        List<string> refused = [];
        foreach (string line in lines)
        {
            if (vim.Set(line.Trim()) is { MessageIsError: true, Message: { } why })
            {
                refused.Add(why);
            }
        }
        return refused;
    }
}
