using System.Globalization;
using System.Text;
using System.Text.Json;
using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>The terminal's emulator against xterm.js with no screen (@xterm/headless, the tests' Node packages): seeded
/// random output (text, wide and composed characters, controls, cursor moves, erases, inserts and deletes, scroll regions,
/// tab stops, modes, the alternate screen, colours), fed to both on a screen of the same size, their screens' text and
/// cursors compared. A difference is shrunk to the smallest output that still differs and named with its seed, so it
/// replays; the differences kept are listed (<see cref="Kept"/>), each with why. Where Node.js is missing, it skips.</summary>
[Collection(Beside.Name)]
public sealed class TerminalEmulationTests
{
    /// <summary>The cases a run makes.</summary>
    private const int Count = 3000;

    /// <summary>How many differences a failed run shrinks and names.</summary>
    private const int Named = 3;

    /// <summary>The seed a run makes its cases from, where given; else a new one, which the run says.</summary>
    [IdentifierHome]
    public const string SeedVariable = "PASSENGER_TERMINAL_SEED";

    private static readonly TimeSpan NodeLimit = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task The_terminal_leaves_the_screen_and_cursor_xterm_leaves_after_the_same_output()
    {
        string node = Programs.Node() ?? string.Empty;
        Assert.SkipWhen(node.Length == 0, "Node.js is not on the PATH, and xterm.js, which this test holds the terminal to, runs on it");
        string? given = Environment.GetEnvironmentVariable(SeedVariable);
        int seed = string.IsNullOrEmpty(given) ? Random.Shared.Next() : int.Parse(given, CultureInfo.InvariantCulture);
        TestContext.Current.TestOutputHelper?.WriteLine($"Seed {seed} ({SeedVariable}={seed} makes these cases again)");
        Random random = new(seed);
        List<Output> cases = [.. Enumerable.Range(0, Count).Select(_ => Make(random))];
        string folder = TestFolders.Create("terminal-differential");
        IReadOnlyList<Screen> xterms = await RunXtermAsync(node, folder, cases);
        List<Output> differing = [.. cases.Where((output, i) => Differs(output, xterms[i], RunPassenger(output)) is not null)];
        TestContext.Current.TestOutputHelper?.WriteLine($"{cases.Count} cases compared, {differing.Count} differ");
        if (differing.Count == 0)
        {
            return;
        }
        List<string> shrunk = [];
        foreach (Output first in differing.Take(Named))
        {
            (Output smallest, string differs) = await ShrinkAsync(node, folder, first);
            shrunk.Add($"{smallest.Shown()}\n  {differs}");
        }
        Assert.Fail($"Seed {seed}: Passenger's terminal left a screen xterm.js does not in {differing.Count} of {cases.Count} cases, so a program "
            + $"drawn for every terminal draws otherwise in Passenger's. The smallest case of the first {shrunk.Count}:\n" + string.Join("\n", shrunk));
    }

    /// <summary>Output a program writes, piece by piece, to a screen of a size; each piece with what it uses that <see
    /// cref="Kept"/> may name.</summary>
    private sealed record Output(int Columns, int Rows, IReadOnlyList<Piece> Pieces)
    {
        public string Text => string.Concat(Pieces.Select(piece => piece.Text));

        public bool Uses(string use) => Pieces.Any(piece => piece.Use == use);

        public string Shown() => $"{JsonSerializer.Serialize(Text)} on {Columns}x{Rows}";
    }

    private sealed record Piece(string Text, string? Use = null);

    /// <summary>A screen's rows of text, their trailing blanks left out, and its cursor's column and row from 0.</summary>
    private sealed record Screen(IReadOnlyList<string> Lines, (int Column, int Row) Cursor, bool Alternate)
    {
        /// <summary>What Passenger's side came through on its way (<see cref="RunPassenger"/>); none of xterm's.</summary>
        public IReadOnlySet<string> Seen { get; init; } = new HashSet<string>();
    }

    /// <summary>Why the two screens differ, null where they agree or differ only as <see cref="Kept"/> says.</summary>
    private static string? Differs(Output output, Screen xterm, Screen passenger)
    {
        List<string> differences = [];
        if (!xterm.Lines.SequenceEqual(passenger.Lines))
        {
            differences.Add($"the text: xterm's {JsonSerializer.Serialize(xterm.Lines)}, Passenger's {JsonSerializer.Serialize(passenger.Lines)}");
        }
        if (xterm.Cursor != passenger.Cursor)
        {
            differences.Add($"the cursor: xterm's at {xterm.Cursor}, Passenger's at {passenger.Cursor}");
        }
        if (xterm.Alternate != passenger.Alternate)
        {
            differences.Add($"the screen: xterm's {(xterm.Alternate ? "alternate" : "main")}, Passenger's {(passenger.Alternate ? "alternate" : "main")}");
        }
        return differences.Count == 0 || Kept.Any(kept => kept.Holds(output, xterm, passenger)) ? null : string.Join("; ", differences);
    }

    /// <summary>Why a difference is kept until #2098 fixes it.</summary>
    private const string ToFix = "differs from xterm, to fix: #2098";

    /// <summary>The differences kept, each with why: on purpose, or until it is fixed.</summary>
    private static readonly (string Why, Func<Output, Screen, Screen, bool> Holds)[] Kept =
    [
        ("xterm.js measures an emoji with Unicode 6's widths, one cell, where Passenger takes Unicode's present widths, two, as "
            + "other terminals do", (output, _, _) => output.Uses(Emoji)),
        ("xterm.js keeps a pending wrap's cursor past the last column, so a tab, erase or move there acts from beyond the line, where "
            + "xterm and Passenger act on its last column", (_, _, passenger) => passenger.Seen.Contains(PendingWrap)),
        ("xterm.js keeps tab stops for each screen, where xterm and Passenger keep one set", (output, _, _) => output.Uses(TabStops) && output.Uses(Alternate)),
        ($"Setting a scroll region leaves the cursor where it was, where it goes home; {ToFix}", (output, _, _) => output.Uses(ScrollRegion)),
        ($"ESC # 8 (DECALN) leaves the cursor where it was, where it goes home; {ToFix}", (output, _, _) => output.Uses(Alignment)),
        ($"REP repeats, and moves the cursor, with no character before it to repeat; {ToFix}", (output, _, _) => output.Uses(Repeat)),
        ($"Line feed/new line mode (LNM, CSI 20 h) is not followed: a line feed leaves the column as it was; {ToFix}", (output, _, _) => output.Uses(NewLineMode)),
        ($"An insert, delete or erase of characters (ICH, DCH, EL, ED) at a wide character's second half leaves it whole; {ToFix}",
            (output, _, _) => output.Uses(Wide) && output.Uses(EditsCharacters)),
        ($"Insert mode (IRM) over a wide character splits it; {ToFix}", (output, _, _) => output.Uses(Wide) && output.Uses(InsertMode)),
        ($"The alternate screen's switches: 1049 l off it restores a cursor, 1047 h on it clears it; {ToFix}", (output, _, _) => output.Uses(Alternate)),
    ];

    private const string Emoji = "an emoji";
    private const string Wide = "a wide character";
    private const string TabStops = "tab stops cleared";
    private const string Alternate = "the alternate screen";
    private const string ScrollRegion = "a scroll region";
    private const string Alignment = "DECALN";
    private const string Repeat = "REP";
    private const string NewLineMode = "LNM";
    private const string EditsCharacters = "ICH, DCH, ECH, EL or ED";
    private const string InsertMode = "IRM";
    private const string PendingWrap = "a control after a write to the last column";

    /// <summary>A case: a screen of one of a few sizes and 1 to 11 pieces of output.</summary>
    private static Output Make(Random random)
    {
        (int columns, int rows) = Pick(random, [(20, 6), (10, 4), (30, 8)]);
        return new Output(columns, rows, [.. Enumerable.Range(0, random.Next(1, 12)).Select(_ => Make(random, columns, rows))]);
    }

    private const string Escape = "\u001b";

    private static T Pick<T>(Random random, T[] from) => from[random.Next(from.Length)];

    private static string Number(Random random, int below) => random.Next(4) == 0 ? string.Empty : random.Next(below).ToString(CultureInfo.InvariantCulture);

    /// <summary>One piece: text, a control, or an escape sequence, with what it uses that <see cref="Kept"/> may name.</summary>
    private static Piece Make(Random random, int columns, int rows)
    {
        switch (random.Next(24))
        {
            case < 6:
                return new(new string([.. Enumerable.Range(0, random.Next(1, 30)).Select(_ => "abcdefghij XYZ.,"[random.Next(16)])]));
            case 6:
                return new("\r");
            case 7:
                return new("\n");
            case 8:
                return new("\b");
            case 9:
                return new("\t");
            case 10:
                return new($"{Escape}[{Number(random, 5)}{Pick(random, ["A", "B", "C", "D", "E", "F", "G", "d", "`", "a", "e"])}");
            case 11:
                return new($"{Escape}[{random.Next(1, rows + 3)};{random.Next(1, columns + 5)}{Pick(random, ["H", "f"])}");
            case 12:
                return new($"{Escape}[{Number(random, 4)}{Pick(random, ["J", "K"])}", EditsCharacters);
            case 13:
                string final = Pick(random, ["@", "P", "L", "M", "X", "S", "T", "b"]);
                return new($"{Escape}[{Number(random, 5)}{final}", final switch { "P" or "@" or "X" => EditsCharacters, "b" => Repeat, _ => null });
            case 14:
                return new($"{Escape}[{random.Next(1, rows + 2)};{random.Next(1, rows + 2)}r", ScrollRegion);
            case 15:
                string escaped = Pick(random, ["7", "8", "D", "M", "E", "H", "c", "#8"]);
                return new(Escape + escaped, escaped == "#8" ? Alignment : null);
            case 16:
                string mode = Pick(random, ["7", "6", "1049", "47", "1047", "25"]);
                return new($"{Escape}[?{mode}{Pick(random, ["h", "l"])}", mode is "1049" or "47" or "1047" ? Alternate : null);
            case 17:
                string tabs = Pick(random, ["g", "I", "Z"]);
                return new($"{Escape}[{Number(random, 4)}{tabs}", tabs == "g" ? TabStops : null);
            case 18:
                return new($"{Escape}[{random.Next(50)}m");
            case 19:
                string set = Pick(random, ["4", "20"]);
                return new($"{Escape}[{set}{Pick(random, ["h", "l"])}", set == "4" ? InsertMode : NewLineMode);
            case 20:
                string kept = Pick(random, ["s", "u", "r"]);
                return new($"{Escape}[{kept}", kept == "r" ? ScrollRegion : null);
            default:
                string text = Pick(random, ["\u00e9", "e\u0301", "\u4e2d\u6587", "\U0001F600", "\u2588", "a\u0308b"]);
                return new(text, text switch { "\U0001F600" => Emoji, "\u4e2d\u6587" => Wide, _ => null });
        }
    }

    /// <summary>The cases through xterm.js at once, one Node for them all, each on a terminal of its own.</summary>
    private static async Task<IReadOnlyList<Screen>> RunXtermAsync(string node, string folder, IReadOnlyList<Output> cases)
    {
        string named = Path.Combine(folder, $"cases-{Guid.NewGuid():N}.json");
        string results = Path.ChangeExtension(named, ".results.json");
        string script = Path.ChangeExtension(named, ".js");
        string xterm = Path.Combine(AppContext.BaseDirectory, "node-packages", "node_modules", "@xterm", "headless");
        await File.WriteAllTextAsync(named, JsonSerializer.Serialize(cases.Select(c => new { cols = c.Columns, rows = c.Rows, data = c.Text })));
        await File.WriteAllTextAsync(script, """
            const { Terminal } = require(process.argv[2]);
            const fs = require('fs');
            const cases = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
            (async () => {
              const out = [];
              for (const c of cases) {
                const t = new Terminal({ cols: c.cols, rows: c.rows, scrollback: 0, allowProposedApi: true });
                await new Promise(done => t.write(c.data, done));
                const b = t.buffer.active;
                const lines = [];
                for (let y = 0; y < c.rows; y++) {
                  lines.push(b.getLine(b.viewportY + y).translateToString(true));
                }
                out.push({ lines, x: b.cursorX, y: b.cursorY, alternate: b.type === 'alternate' });
                t.dispose();
              }
              fs.writeFileSync(process.argv[4], JSON.stringify(out));
            })();
            """);
        ChildResult ran = await ChildProcesses.RunAsync(ChildProcesses.Command(node, [script, xterm, named, results], folder), limit: NodeLimit.Scaled());
        Assert.True(File.Exists(results), $"xterm.js wrote no results for the cases, so nothing could be compared (Node exited with {ran.ExitCode}): {ran.Error}");
        using JsonDocument read = JsonDocument.Parse(await File.ReadAllTextAsync(results));
        List<Screen> screens = [.. read.RootElement.EnumerateArray().Select((end, i) => new Screen(
            [.. JsonMembers.Texts(end, "lines").Select(line => line.TrimEnd())],
            // A cursor past the last column is xterm's own way of saying a write there wraps first.
            (Math.Min(JsonMembers.Number(end, "x") ?? 0, cases[i].Columns - 1), JsonMembers.Number(end, "y") ?? 0),
            JsonMembers.Bool(end, "alternate") ?? false))];
        Assert.True(screens.Count == cases.Count, $"xterm.js gave {screens.Count} results for {cases.Count} cases");
        return screens;
    }

    /// <summary>The case through Passenger's terminal emulator.</summary>
    private static Screen RunPassenger(Output output)
    {
#pragma warning disable RS0030 // The one test holding the emulator to xterm.js, named in BannedSymbols.txt.
        TerminalScreen screen = new(output.Columns, output.Rows);
#pragma warning restore RS0030
        HashSet<string> seen = [];
        bool atLastColumn = false;
        foreach (Piece piece in output.Pieces)
        {
            bool printed = !char.IsControl(piece.Text[0]);
            seen.UnionWith(atLastColumn && !printed && piece.Text is not ("\r" or "\n") ? [PendingWrap] : []);
            screen.Feed(Encoding.UTF8.GetBytes(piece.Text));
            atLastColumn = printed && screen.CursorColumn == output.Columns - 1;
        }
        return new Screen([.. screen.Lines.Select(line => line.Text().TrimEnd())], (screen.CursorColumn, screen.CursorRow), screen.IsAlternate) { Seen = seen };
    }

    /// <summary>The smallest case that still differs, found by taking out a piece, or half a text piece, while the case
    /// still differs; each round's tries through one Node.</summary>
    private static async Task<(Output Case, string Differs)> ShrinkAsync(string node, string folder, Output start)
    {
        Output best = start;
        string differs = (await DiffersAsync(node, folder, [start]))[0] ?? "differs";
        for (int round = 0; round < 200; round++)
        {
            List<Output> tries = [.. Smaller(best)];
            IReadOnlyList<string?> found = await DiffersAsync(node, folder, tries);
            int at = found.ToList().FindIndex(f => f is not null);
            if (at < 0)
            {
                break;
            }
            (best, differs) = (tries[at], found[at]!);
        }
        return (best, differs);
    }

    private static async Task<IReadOnlyList<string?>> DiffersAsync(string node, string folder, IReadOnlyList<Output> cases)
    {
        if (cases.Count == 0)
        {
            return [];
        }
        IReadOnlyList<Screen> xterms = await RunXtermAsync(node, folder, cases);
        return [.. cases.Select((c, i) => Differs(c, xterms[i], RunPassenger(c)))];
    }

    private static IEnumerable<Output> Smaller(Output bigger)
    {
        for (int i = 0; i < bigger.Pieces.Count; i++)
        {
            yield return bigger with { Pieces = [.. bigger.Pieces.Where((_, j) => j != i)] };
        }
        for (int i = 0; i < bigger.Pieces.Count; i++)
        {
            string text = bigger.Pieces[i].Text;
            if (text.Length > 1 && !text.StartsWith(Escape, StringComparison.Ordinal) && text.All(char.IsAscii))
            {
                yield return bigger with { Pieces = [.. bigger.Pieces.Select((p, j) => j != i ? p : p with { Text = text[..(text.Length / 2)] })] };
                yield return bigger with { Pieces = [.. bigger.Pieces.Select((p, j) => j != i ? p : p with { Text = text[1..] })] };
            }
        }
    }
}
