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
    private sealed record Screen(IReadOnlyList<string> Lines, (int Column, int Row) Cursor, bool Alternate, IReadOnlyList<string> Styles)
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
        if (!xterm.Styles.SequenceEqual(passenger.Styles))
        {
            int row = Enumerable.Range(0, Math.Min(xterm.Styles.Count, passenger.Styles.Count)).FirstOrDefault(r => xterm.Styles[r] != passenger.Styles[r]);
            differences.Add($"the colours and flags of row {row}: xterm's {xterm.Styles.ElementAtOrDefault(row)}, Passenger's {passenger.Styles.ElementAtOrDefault(row)}");
        }
        if (xterm.Alternate != passenger.Alternate)
        {
            differences.Add($"the screen: xterm's {(xterm.Alternate ? "alternate" : "main")}, Passenger's {(passenger.Alternate ? "alternate" : "main")}");
        }
        return differences.Count == 0 || Kept.Any(kept => kept.Holds(output, xterm, passenger)) ? null : string.Join("; ", differences);
    }

    /// <summary>The differences kept, each with why: xterm.js's own ways, where Passenger does as xterm does.</summary>
    private static readonly (string Why, Func<Output, Screen, Screen, bool> Holds)[] Kept =
    [
        ("xterm.js measures an emoji with Unicode 6's widths, one cell, where Passenger takes Unicode's present widths, two, as "
            + "other terminals do", (output, _, _) => output.Uses(Emoji)),
        ("xterm.js keeps a pending wrap's cursor past the last column, so a tab, erase or move there acts from beyond the line, where "
            + "xterm and Passenger act on its last column", (_, _, passenger) => passenger.Seen.Contains(PendingWrap)),
        ("xterm.js keeps tab stops for each screen, where xterm and Passenger keep one set", (output, _, _) => output.Uses(TabStops) && output.Uses(Alternate)),
        ("xterm.js fills the lines a scroll or an insert of lines brings in with the default background, where xterm and Passenger erase "
            + "with the background set (BCE)", (output, _, _) => output.Uses(Scrolls) && output.Uses(Background)),
        ("xterm.js clears the alternate screen in the background set each time it comes up, where xterm clears it only as 1049 brings "
            + "it up and 1047 takes it down, so 47 and 1047 bring up what it held",
            (output, _, _) => output.Pieces.Any(piece => piece.Text is $"{Escape}[?47h" or $"{Escape}[?1047h")),
        ("xterm.js keeps the scroll region over ESC # 8 (DECALN), where xterm and the VT510 set the margins to the screen's edges",
            (output, _, _) => output.Uses(Alignment) && output.Uses(ScrollRegion)),
        ("xterm.js counts the scroll region's top twice as CUU, CUD, CUF, CUB, CNL, CPL, CHA, HPA, HPR or VPR moves the cursor in origin "
            + "mode, so CUU there moves it down, where xterm moves it from where it stands",
            (output, _, _) => output.Uses(OriginMode) && output.Uses(ScrollRegion) && output.Uses(Moves)),
        ("xterm.js neither saves origin mode with the cursor nor restores it, where the VT510 and xterm save it with DECSC (and 1049) "
            + "and DECRC restores it, or turns it off with nothing saved", (output, _, _) => output.Uses(OriginMode) && (output.Uses(Restores) || output.Uses(Alternate))),
        ("xterm.js, with autowrap off, writes over a wide character's second half in the last column and leaves its first, so the "
            + "character hides what was written, where xterm and Passenger blank the half not written over",
            (output, _, _) => output.Uses(Wide) && output.Uses(AutoWrap)),
        ("xterm.js keeps line feed/new line mode (LNM) over a full reset (RIS), where xterm and Passenger reset it",
            (output, _, _) => output.Uses(NewLineMode) && output.Uses(FullReset)),
    ];

    private const string Emoji = "an emoji";
    private const string Wide = "a wide character";
    private const string AutoWrap = "DECAWM";
    private const string TabStops = "tab stops set or cleared";
    private const string Alternate = "the alternate screen";
    private const string ScrollRegion = "a scroll region";
    private const string Alignment = "DECALN";
    private const string FullReset = "RIS";
    private const string OriginMode = "origin mode";
    private const string Moves = "CUU, CUD, CUF, CUB, CNL, CPL, CHA, HPA, HPR or VPR";
    private const string Restores = "a restore of the cursor";
    private const string Repeat = "REP";
    private const string NewLineMode = "LNM";
    private const string PendingWrap = "a control after a write to the last column";
    private const string Scrolls = "SU, SD, IL or DL";
    private const string Background = "a background colour";

    /// <summary>A case: a screen of one of a few sizes and 1 to 11 pieces of output.</summary>
    private static Output Make(Random random)
    {
        (int columns, int rows) = Pick(random, [(20, 6), (10, 4), (30, 8)]);
        return new Output(columns, rows, [.. Enumerable.Range(0, random.Next(1, 12)).Select(_ => Make(random, columns, rows))]);
    }

    private const string Escape = "\u001b";

    private static T Pick<T>(Random random, T[] from) => from[random.Next(from.Length)];

    /// <summary>Colours and flags a program sets: each SGR Passenger reads, alone or with others.</summary>
    private static readonly string[] Styles = ["", "0", "1", "2", "3", "4", "7", "8", "9", "22", "23", "24", "27", "28", "29", "31", "42", "39", "49",
        "91", "104", "1;4;31", "38;5;196", "48;5;22", "38;2;255;128;0", "48;2;0;0;255", "7;33;44", "0;1"];

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
                string count = Number(random, 5);
                string move = Pick(random, ["A", "B", "C", "D", "E", "F", "G", "d", "`", "a", "e"]);
                return new($"{Escape}[{count}{move}", move == "d" ? null : Moves);
            case 11:
                return new($"{Escape}[{random.Next(1, rows + 3)};{random.Next(1, columns + 5)}{Pick(random, ["H", "f"])}");
            case 12:
                return new($"{Escape}[{Number(random, 4)}{Pick(random, ["J", "K"])}");
            case 13:
                string final = Pick(random, ["@", "P", "L", "M", "X", "S", "T", "b"]);
                return new($"{Escape}[{Number(random, 5)}{final}", final switch
                {
                    "L" or "M" or "S" or "T" => Scrolls,
                    "b" => Repeat,
                    _ => null,
                });
            case 14:
                return new($"{Escape}[{random.Next(1, rows + 2)};{random.Next(1, rows + 2)}r", ScrollRegion);
            case 15:
                string escaped = Pick(random, ["7", "8", "D", "M", "E", "H", "c", "#8"]);
                return new(Escape + escaped, escaped switch { "#8" => Alignment, "c" => FullReset, "H" => TabStops, "8" => Restores, _ => null });
            case 16:
                string mode = Pick(random, ["7", "6", "1049", "47", "1047", "25"]);
                return new($"{Escape}[?{mode}{Pick(random, ["h", "l"])}", mode switch
                {
                    "1049" or "47" or "1047" => Alternate,
                    "6" => OriginMode,
                    "7" => AutoWrap,
                    _ => null,
                });
            case 17:
                string tabs = Pick(random, ["g", "I", "Z"]);
                return new($"{Escape}[{Number(random, 4)}{tabs}", tabs == "g" ? TabStops : null);
            case 18:
                string style = Pick(random, Styles);
                return new($"{Escape}[{style}m", style is "42" or "104" or "48;5;22" or "48;2;0;0;255" or "7;33;44" ? Background : null);
            case 19:
                string set = Pick(random, ["4", "20"]);
                return new($"{Escape}[{set}{Pick(random, ["h", "l"])}", set == "20" ? NewLineMode : null);
            case 20:
                string kept = Pick(random, ["s", "u", "r"]);
                return new($"{Escape}[{kept}", kept switch { "r" => ScrollRegion, "u" => Restores, _ => null });
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
                const styles = [];
                const colour = (isDefault, isPalette, value) => isDefault ? 'd' : isPalette ? 'p' + value : 'r' + value.toString(16);
                for (let y = 0; y < c.rows; y++) {
                  const line = b.getLine(b.viewportY + y);
                  lines.push(line.translateToString(true));
                  const row = [];
                  for (let x = 0; x < c.cols; x++) {
                    const cell = line.getCell(x);
                    const back = colour(cell.isBgDefault(), cell.isBgPalette(), cell.getBgColor());
                    if (x > 0 && line.getCell(x - 1).getWidth() === 2) {
                      row.push('-');
                      continue;
                    }
                    // A blank, as is a wide character's second half with no first before it, shows its back colour alone.
                    if (cell.getChars() === '') {
                      row.push('/' + back);
                      continue;
                    }
                    const flags = (cell.isBold() ? 1 : 0) | (cell.isDim() ? 2 : 0) | (cell.isItalic() ? 4 : 0) | (cell.isUnderline() ? 8 : 0)
                      | (cell.isInverse() ? 16 : 0) | (cell.isStrikethrough() ? 32 : 0) | (cell.isInvisible() ? 64 : 0);
                    row.push(colour(cell.isFgDefault(), cell.isFgPalette(), cell.getFgColor()) + '/' + back + '/' + flags);
                  }
                  styles.push(row.join(' '));
                }
                out.push({ lines, styles, x: b.cursorX, y: b.cursorY, alternate: b.type === 'alternate' });
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
            JsonMembers.Bool(end, "alternate") ?? false, JsonMembers.Texts(end, "styles")))];
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
            // REP writes too.
            bool printed = !char.IsControl(piece.Text[0]) || piece.Use == Repeat;
            seen.UnionWith(atLastColumn && !printed && piece.Text is not ("\r" or "\n") ? [PendingWrap] : []);
            screen.Feed(Encoding.UTF8.GetBytes(piece.Text));
            atLastColumn = printed && screen.CursorColumn == output.Columns - 1;
        }
        return new Screen([.. screen.Lines.Select(line => line.Text().TrimEnd())], (screen.CursorColumn, screen.CursorRow), screen.IsAlternate,
            [.. screen.Lines.Select(line => string.Join(' ', line.Cells.Select((cell, i) => i > 0 && line.Cells[i - 1].Width == 2 ? "-" : Styled(cell))))])
        {
            Seen = seen,
        };
    }

    /// <summary>A cell's colours and flags as the comparison writes them, xterm.js's way: a blank one's back colour alone. The
    /// cell a wide character covers is written "-" on both sides, its colours its first half's, as the character is drawn.</summary>
    private static string Styled(Cell cell)
    {
        static string Colour(int colour) => colour == CellStyle.Default ? "d"
            : (colour & CellStyle.Rgb) != 0 ? "r" + (colour & 0xFFFFFF).ToString("x", CultureInfo.InvariantCulture)
            : "p" + colour.ToString(CultureInfo.InvariantCulture);
        CellStyle style = cell.Style;
        return cell.IsBlank && cell.Width != 0 ? "/" + Colour(style.Back)
            : $"{Colour(style.Fore)}/{Colour(style.Back)}/{(int)style.Flags}";
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
