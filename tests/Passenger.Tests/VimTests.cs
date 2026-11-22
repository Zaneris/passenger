using System.Globalization;
using System.Text;
using System.Text.Json;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Vim mode against Vim itself: seeded random texts and keys, each run through the Vim tools/vim.sh builds (its
/// settings shiftwidth=4 tabstop=4 expandtab autoindent nojoinspaces) and through Passenger's Vim mode as an editor
/// drives it, their texts, cursors, registers and visual stretches compared. A difference is shrunk to the smallest case
/// that still differs and named with its seed, so it replays; the differences Passenger keeps are listed (<see
/// cref="Kept"/>), each with why. Where that Vim is missing, the test skips saying how to build it.</summary>
[Collection(Beside.Name)]
public sealed partial class VimTests
{
    /// <summary>The Vim compared with, where given; else tools/vim.sh's build in artifacts/vim/.</summary>
    [IdentifierHome]
    public const string VimVariable = "PASSENGER_VIM";

    /// <summary>The cases a run makes.</summary>
    private const int Count = 4000;

    /// <summary>How many differences a failed run shrinks and names.</summary>
    private const int Named = 3;

    /// <summary>The seed a run makes its cases from, where given; else a new one, which the run says.</summary>
    [IdentifierHome]
    public const string SeedVariable = "PASSENGER_VIM_SEED";

    private static readonly TimeSpan VimLimit = TimeSpan.FromMinutes(2);

    /// <summary>The registers compared: the unnamed, the yank, the deletes, the small delete, two named, and z, which a
    /// visual stretch the keys leave is yanked into (<see cref="Yanked"/>).</summary>
    private const string Registers = "\"0123456789-abz";

    [Fact]
    public async Task Vim_mode_leaves_the_text_cursor_registers_and_visual_stretch_vim_leaves_after_the_same_keys()
    {
        string vim = await PinnedVimAsync();
        string? given = Environment.GetEnvironmentVariable(SeedVariable);
        int seed = string.IsNullOrEmpty(given) ? Random.Shared.Next() : int.Parse(given, CultureInfo.InvariantCulture);
        TestContext.Current.TestOutputHelper?.WriteLine($"Seed {seed} ({SeedVariable}={seed} makes these cases again)");
        Random random = new(seed);
        List<VimCase> cases = [.. Enumerable.Range(0, Count).Select(_ => Make(random))];
        string folder = TestFolders.Create("vim-differential");
        IReadOnlyList<VimEnd> vims = await RunVimAsync(vim, folder, cases);
        List<(VimCase Case, string Differs)> differing = [];
        int asked = 0;
        for (int i = 0; i < cases.Count; i++)
        {
            VimEnd? passenger = RunPassenger(cases[i]);
            if (passenger is null)
            {
                asked++;
                continue;
            }
            if (Differs(cases[i], vims[i], passenger) is { } differs)
            {
                differing.Add((cases[i], differs));
            }
        }
        // Again in a Vim of its own, so what an earlier case left in Vim is never taken for Passenger's difference.
        List<(VimCase Case, string Differs)> alone = [];
        foreach ((VimCase vimCase, string _) in differing)
        {
            if ((await DiffersAsync(vim, folder, [vimCase]))[0] is { } differs)
            {
                alone.Add((vimCase, differs));
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{cases.Count - asked} cases compared, {asked} asked the host, {alone.Count} differ "
            + $"({differing.Count} among the others)");
        differing = alone;
        if (differing.Count == 0)
        {
            return;
        }
        List<string> shrunk = [];
        foreach ((VimCase first, string _) in differing.Take(Named))
        {
            (VimCase smallest, string differs) = await ShrinkAsync(vim, folder, first);
            shrunk.Add($"{smallest.Shown()}\n  {differs}");
        }
        Assert.Fail($"Seed {seed}: Passenger's Vim mode left what Vim {Version} does not in {differing.Count} of {cases.Count} cases, so a "
            + $"person whose fingers know Vim finds it otherwise. The smallest case of the first {shrunk.Count}:\n" + string.Join("\n", shrunk));
    }

    /// <summary>The version of Vim compared with, as tools/vim.sh builds it: 9.2.1382, Vim 9.2 with patches 1 to 1382.</summary>
    private static string Version { get; } = VimScriptVersion().Match(File.ReadAllText(Path.Combine(Debugging.Helpers.Debuggees.Root, "tools", "vim.sh")))
        .Groups[1].Value;

    [System.Text.RegularExpressions.GeneratedRegex(@"^version=(\d+\.\d+\.\d+)$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex VimScriptVersion();

    /// <summary>The Vim <see cref="VimVariable"/> names, else tools/vim.sh's build, where it is <see cref="Version"/>; the
    /// test skips otherwise, saying how to build it.</summary>
    private static async Task<string> PinnedVimAsync()
    {
        Assert.True(Version.Length > 0, "tools/vim.sh names no version=MAJOR.MINOR.PATCH, so which Vim to compare with is unknown");
        string? named = Environment.GetEnvironmentVariable(VimVariable);
        string vim = string.IsNullOrEmpty(named) ? Path.Combine(Debugging.Helpers.Debuggees.Root, "artifacts", "vim", Version, "bin", "vim") : named;
        string build = $"just vim builds Vim {Version} into artifacts/vim/{Version}, or {VimVariable} names one";
        Assert.SkipUnless(File.Exists(vim), $"There is no {vim}, the Vim this test compares Vim mode with: {build}");
        ChildResult said = await ChildProcesses.RunAsync(ChildProcesses.Command(vim, ["--version"]), limit: TimeSpan.FromMinutes(1).Scaled());
        string[] lines = said.Output.Split('\n');
        string[] parts = Version.Split('.');
        bool pinned = lines.Length > 0 && lines[0].StartsWith($"VIM - Vi IMproved {parts[0]}.{parts[1]} ", StringComparison.Ordinal)
            && lines.Any(line => line.Trim() == $"Included patches: 1-{parts[2]}");
        Assert.SkipUnless(pinned, $"{vim} is not Vim {Version}, the one this test compares Vim mode with ({build}): "
            + string.Join(" ", lines.Take(3).Select(line => line.Trim())));
        return vim;
    }

    /// <summary>A case: the text's lines (a final line break after the last), where the cursor starts, and the commands
    /// pressed.</summary>
    private sealed record VimCase(IReadOnlyList<string> Lines, int Line, int Column, IReadOnlyList<VimCommand> Commands)
    {
        public IEnumerable<string> Keys => Commands.SelectMany(command => command.Keys);

        /// <summary>Whether a command of the case uses what <paramref name="use"/> names.</summary>
        public bool Uses(string use) => Commands.Any(command => command.Uses.Contains(use));

        /// <summary>The keys as Vim reads them typed: each named key its control character.</summary>
        public string Typed => string.Concat(Keys.Select(key => key switch
        {
            VimKeys.Escape => "\u001b",
            VimKeys.Enter => "\r",
            VimKeys.Backspace => "\b",
            VimKeys.CtrlV => "\u0016",
            _ => key,
        }));

        public string Text => string.Join("\n", Lines) + "\n";

        public int Offset => Lines.Take(Line - 1).Sum(line => line.Length + 1) + Column - 1;

        /// <summary>The text with its cursor marked |, and the keys in Vim's notation.</summary>
        public string Shown() => $"keys {string.Concat(Keys)} on {JsonSerializer.Serialize(Text.Insert(Offset, "|"))}";
    }

    /// <summary>A command's keys in Vim mode's names, and what of Vim it uses that <see cref="Kept"/> may name.</summary>
    private sealed record VimCommand(IReadOnlyList<string> Keys, IReadOnlyList<string> Uses);

    /// <summary>What keys left: the text's lines, the cursor's line and column (from 1), the visual mode they left and its
    /// cursor (the stretch itself then yanked into z, <see cref="Yanked"/>), and the registers' texts and kinds.</summary>
    private sealed record VimEnd(IReadOnlyList<string> Lines, (int Line, int Column) Cursor, (string Mode, (int Line, int Column) Cursor)? Visual,
        IReadOnlyDictionary<char, (string Text, string Kind)> Registers)
    {
        /// <summary>What Passenger's side came through on its way (<see cref="Observe"/>); none of Vim's.</summary>
        public IReadOnlySet<string> Seen { get; init; } = new HashSet<string>();
    }

    /// <summary>What a case left in visual mode presses after its keys, so the stretch is compared as Vim takes it.</summary>
    private static readonly string[] Yanked = ["\"", "z", "y"];

    /// <summary>Why the two ends differ, null where they agree or differ only as <see cref="Kept"/> says.</summary>
    private static string? Differs(VimCase vimCase, VimEnd vim, VimEnd passenger)
    {
        List<string> differences = [];
        if (!vim.Lines.SequenceEqual(passenger.Lines))
        {
            differences.Add($"the text: Vim's {JsonSerializer.Serialize(vim.Lines)}, Passenger's {JsonSerializer.Serialize(passenger.Lines)}");
        }
        if (vim.Cursor != passenger.Cursor)
        {
            differences.Add($"the cursor: Vim's at {vim.Cursor}, Passenger's at {passenger.Cursor}");
        }
        if (vim.Visual != passenger.Visual)
        {
            differences.Add($"the visual stretch: Vim's {Said(vim.Visual)}, Passenger's {Said(passenger.Visual)}");
        }
        foreach (char name in Registers)
        {
            (string Text, string Kind) mine = vim.Registers[name];
            (string Text, string Kind) theirs = passenger.Registers[name];
            if (mine.Text != theirs.Text || (mine.Text.Length > 0 && mine.Kind != theirs.Kind))
            {
                differences.Add($"the register {name}: Vim's {JsonSerializer.Serialize(mine.Text)} ({mine.Kind}), Passenger's {JsonSerializer.Serialize(theirs.Text)} ({theirs.Kind})");
            }
        }
        return differences.Count == 0 || Kept.Any(kept => kept.Holds(vimCase, vim, passenger)) ? null : string.Join("; ", differences);
    }

    private static string Said((string Mode, (int Line, int Column) Cursor)? visual) =>
        visual is { } shown ? $"{shown.Mode}, its cursor at {shown.Cursor}" : "none";

    /// <summary>Why a difference is kept until #2096 fixes it.</summary>
    private const string ToFix = "differs from Vim 9.2, to fix: #2096";

    /// <summary>The differences Passenger keeps, each with why: on purpose, or until it is fixed.</summary>
    private static readonly (string Why, Func<VimCase, VimEnd, VimEnd, bool> Holds)[] Kept =
    [
        ("Insert mode's Enter, o and O indent as the editor's Enter does, one more level after an open bracket, the indent kept "
            + "on a line left blank and the blanks after the caret carried down, where Vim's autoindent copies the line's own and "
            + "drops the rest: in insert mode the editor types",
            (vimCase, _, _) => vimCase.Uses(TypedEnter) || vimCase.Uses(OpensLine)),
        ($"A text's empty lines and the place after its final line break: a linewise copy, delete or change of a lone empty line "
            + $"takes nothing, dw and cw on an empty line take its break, and a linewise stretch over the last line takes the place after it; {ToFix}",
            (_, vim, passenger) => passenger.Seen.Contains(LoneEmptyLine) || Unbroken(vim.Lines) == Unbroken(passenger.Lines) && (Registers.All(name => OnlyBreaksApart(vim.Registers[name].Text,
                passenger.Registers[name].Text)) || TookAnEmptyLine(vim, passenger))),
        ($"is and as from an empty line ending the text, from blanks before or after its last sentence, and about a paragraph opened "
            + $"by a sentence's end, which Vim's take each in a way of its own; {ToFix}", (vimCase, _, _) => vimCase.Uses(SentenceObject)),
        ($"( and ) about a paragraph opened by a sentence's end; {ToFix}",
            (vimCase, _, passenger) => vimCase.Keys.Any(key => key is "(" or ")") && passenger.Seen.Contains(OpenedByAnEnd)),
        ($"Visual block mode's corners: a block over a line's last character, its I and A, ~ and J after o; {ToFix}", (vimCase, _, _) => vimCase.Uses(Block)),
        ($"A over a visual stretch by characters or lines after another visual operator, which Vim's places by where that "
            + $"operator's stretch began; {ToFix}",
            (vimCase, _, _) => vimCase.Commands.Where((command, at) => command.Uses.Contains(VisualInsert)
                && vimCase.Commands.Take(at).Any(before => before.Keys[0] is "v" or "V" or VimKeys.CtrlV && !before.Uses.Contains(LeftVisual))).Any()),
    ];

    /// <summary>Lines with the empty ones set aside.</summary>
    private static string Unbroken(IEnumerable<string> lines) => string.Join("\n", lines.Where(line => line.Length > 0));

    private static bool OnlyBreaksApart(string one, string other) =>
        one.Replace("\n", "", StringComparison.Ordinal) == other.Replace("\n", "", StringComparison.Ordinal);

    /// <summary>Whether Vim's registers are Passenger's with an empty line's delete more: line breaks alone in the unnamed
    /// register and 1, the numbered ones shifted down once, the rest alike.</summary>
    private static bool TookAnEmptyLine(VimEnd vim, VimEnd passenger) =>
        vim.Registers['"'].Text.Length > 0 && vim.Registers['"'].Text.All(c => c == '\n') && vim.Registers['1'].Text == vim.Registers['"'].Text
            && Enumerable.Range('2', 8).All(name => vim.Registers[(char)name].Text == passenger.Registers[(char)(name - 1)].Text)
            && "0-abz".All(name => vim.Registers[name].Text == passenger.Registers[name].Text);

    private const string Block = "visual block mode";
    private const string VisualInsert = "I or A in visual mode";
    private const string TypedEnter = "Enter typed in insert mode";
    private const string OpensLine = "o or O";
    private const string LoneEmptyLine = "a text of one empty line";
    private const string SentenceObject = "is or as";
    private const string OpenedByAnEnd = "a paragraph opened by a sentence's end";

    /// <summary>A case of 1 to 6 lines and 1 to 4 commands, the cursor anywhere on a character; a visual stretch the
    /// keys leave is left only by the last command, the others' ended by Escape.</summary>
    private static VimCase Make(Random random)
    {
        string[] lines = [.. Enumerable.Range(0, random.Next(1, 7)).Select(_ => Line(random))];
        int line = random.Next(1, lines.Length + 1);
        int column = random.Next(1, Math.Max(1, lines[line - 1].Length) + 1);
        string text = string.Join("\n", lines);
        VimCommand[] commands = [.. Enumerable.Range(0, random.Next(1, 5)).Select(_ => Command(random, text))];
        for (int i = 0; i < commands.Length - 1; i++)
        {
            commands[i] = commands[i].Uses.Contains(LeftVisual) ? commands[i] with { Keys = [.. commands[i].Keys, VimKeys.Escape] } : commands[i];
        }
        return new VimCase(lines, line, column, commands);
    }

    /// <summary>A command that leaves visual mode on.</summary>
    private const string LeftVisual = "visual left on";

    private static readonly string[] Words = ["one", "two", "foo", "bar", "x", "a1", "if", "end", "the", "b_c", "Zed"];

    private static readonly string[] Marks = [".", ",", ";", "(", ")", "\"", "'", "{", "}", "[", "]", "-", "!", "=", "+"];

    private static string Line(Random random)
    {
        if (random.Next(6) == 0)
        {
            return string.Empty;
        }
        StringBuilder line = new(new string(' ', random.Next(4) switch { 0 => 4, 1 => 2, 2 => 8, _ => 0 }));
        int parts = random.Next(1, 7);
        for (int i = 0; i < parts; i++)
        {
            line.Append(random.Next(3) == 0 ? Pick(random, Marks) : Pick(random, Words));
            if (random.Next(3) > 0 && i < parts - 1)
            {
                line.Append(' ');
            }
        }
        return line.ToString();
    }

    /// <summary>A character to find: one of the text's mostly, so the find lands.</summary>
    private static string Target(Random random, string text) =>
        text.Length > 0 && random.Next(5) > 0 ? text[random.Next(text.Length)] is var c && c != ' ' ? c.ToString() : "x" : Pick(random, ["z", "o", "("]);

    private static string Counted(Random random) => random.Next(10) < 3 ? random.Next(2, 5).ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static IEnumerable<string> Each(string keys) => keys.Select(c => c.ToString());

    /// <summary>Typed text for insert mode, a line break in it now and then, ended by Escape: keys that, where the command
    /// before them failed, are commands both have (no z, which begins Vim's folds and scrolls, no sentence motions, and no
    /// ., which there Vim may repeat for ever).</summary>
    private static IEnumerable<string> Typing(Random random)
    {
        int length = random.Next(0, 5);
        for (int i = 0; i < length; i++)
        {
            yield return random.Next(8) switch
            {
                0 when random.Next(2) == 0 => VimKeys.Enter,
                0 or 1 => " ",
                _ => Pick(random, ["a", "b", "X"]),
            };
        }
        yield return VimKeys.Escape;
    }

    private static readonly string[] Motions = ["h", "j", "k", "l", "w", "b", "e", "W", "B", "E", "ge", "0", "^", "$", "gg", "G", "%", "{", "}", "(", ")", "_", "+", "-"];

    private static readonly string[] Objects = ["iw", "aw", "iW", "aW", "i(", "a(", "i\"", "a\"", "i'", "a'", "i{", "a{", "i[", "a[", "ip", "ap", "is", "as", "ib"];

    /// <summary>Changes for . to make again.</summary>
    private static readonly string[][] Changes = [["x"], ["d", "w"], ["c", "w", "z", VimKeys.Escape], ["A", "!", VimKeys.Escape], ["~"], ["J"], [">", ">"],
        ["d", "d"], ["o", "z", VimKeys.Escape], ["g", "U", "w"]];

    private static T Pick<T>(Random random, T[] from) => from[random.Next(from.Length)];

    /// <summary>A motion and its count before it; no count before 0, which a count takes as its digit.</summary>
    private static IEnumerable<string> Motion(Random random, string text, string count = "")
    {
        if (random.Next(6) == 0)
        {
            string find = Pick(random, ["f", "t", "F", "T"]);
            return [.. Each(count), find, Target(random, text)];
        }
        string motion = Pick(random, Motions);
        return motion == "0" ? Each(motion) : Each(count + motion);
    }

    /// <summary>One command: a motion, an operator over a motion or text object, a change, a put, a visual stretch and
    /// what it does, a search, a mark, an ex command, or . again; with what it uses that <see cref="Kept"/> may name.</summary>
    private static VimCommand Command(Random random, string text)
    {
        List<string> keys = [];
        HashSet<string> uses = [];
        string count = Counted(random);
        string register = random.Next(8) == 0 ? "\"" + Pick(random, ["a", "b", "A"]) : string.Empty;
        switch (random.Next(15))
        {
            case 0:
            case 1:
                keys.AddRange(Motion(random, text, count));
                break;
            case 2:
            case 3:
                string op = Pick(random, ["d", "y", "c", ">", "<", "g~", "gu", "gU"]);
                keys.AddRange(Each(register + count + op));
                if (random.Next(2) == 0)
                {
                    keys.AddRange(Motion(random, text));
                }
                else
                {
                    string textObject = Pick(random, Objects);
                    keys.AddRange(Each(textObject));
                    uses.UnionWith(textObject is "is" or "as" ? [SentenceObject] : []);
                }
                if (op == "c")
                {
                    keys.AddRange(Typing(random));
                }
                break;
            case 4:
                string acts = Pick(random, ["dd", "yy", ">>", "<<", "x", "X", "D", "Y", "J", "gJ", "~", "p", "P", "gp", "gP"]);
                keys.AddRange(Each(register + count + acts));
                break;
            case 5:
                string inserts = Pick(random, ["i", "a", "I", "A", "o", "O", "cc", "C", "s", "S", "cw"]);
                keys.AddRange(Each(count + inserts));
                keys.AddRange(Typing(random));
                uses.UnionWith(inserts is "o" or "O" ? [OpensLine] : []);
                break;
            case 6:
                keys.AddRange(Each(count + "r"));
                keys.Add(Pick(random, ["q", "-", " "]));
                break;
            case 7:
            case 8:
                string visual = Pick(random, ["v", "V", VimKeys.CtrlV]);
                keys.Add(visual);
                uses.UnionWith(visual == VimKeys.CtrlV ? [Block] : []);
                for (int i = random.Next(1, 3); i > 0; i--)
                {
                    if (random.Next(4) == 0)
                    {
                        string inVisual = Pick(random, ["o", "iw", "ip", "a("]);
                        keys.AddRange(Each(inVisual));
                    }
                    else
                    {
                        keys.AddRange(Motion(random, text, Counted(random)));
                    }
                }
                if (random.Next(4) > 0)
                {
                    string does = Pick(random, ["d", "y", "c", ">", "<", "~", "u", "U", "J", "x", "p", "r", "I", "A", "C", "S"]);
                    keys.AddRange(Each(register + does));
                    uses.UnionWith(does is "I" or "A" ? [VisualInsert] : []);
                    if (does is "r")
                    {
                        keys.Add("-");
                    }
                    else if (does is "c" or "I" or "A" or "C" or "S")
                    {
                        keys.AddRange(Typing(random));
                    }
                }
                else
                {
                    uses.Add(LeftVisual);
                }
                break;
            case 9:
                keys.Add(random.Next(2) == 0 ? "/" : "?");
                keys.AddRange(Each(Pick(random, Words)));
                keys.Add(VimKeys.Enter);
                keys.AddRange(Each(random.Next(2) == 0 ? "n" : "N"));
                break;
            case 10:
                keys.AddRange(Each(count + Pick(random, ["*", "#"])));
                break;
            case 11:
                keys.AddRange(Each("ma"));
                keys.AddRange(Motion(random, text));
                string back = Pick(random, ["d'a", "y`a", "`a", "'a", "c`a"]);
                keys.AddRange(Each(back));
                if (back[0] == 'c')
                {
                    keys.AddRange(Typing(random));
                }
                break;
            case 12:
                string command = Pick(random, ["s/o/0/", "s/o/0/g", "%s/a/-/g", "d", ".,+1d", "m0", "t.", "j", ">", "2,3d", "1,$y", "$m-2", "g/o/d"]);
                keys.AddRange(Each(":" + command));
                keys.Add(VimKeys.Enter);
                break;
            default:
                string[] change = Pick(random, Changes);
                keys.AddRange(change);
                keys.AddRange(Each(count + "."));
                uses.UnionWith(change[0] == "o" ? [OpensLine] : []);
                break;
        }
        uses.UnionWith(keys.Contains(VimKeys.Enter) && keys[0] is not ("/" or "?" or ":") ? [TypedEnter] : []);
        return new VimCommand(keys, [.. uses]);
    }

    /// <summary>The cases through Vim at once, one Vim for them all: each case's text set, its registers, search and
    /// last find and marks emptied and its last change Passenger's too (<see cref="Before"/>), its keys fed as typed, a
    /// visual stretch they leave yanked into z, then Escape.</summary>
    private static async Task<IReadOnlyList<VimEnd>> RunVimAsync(string vim, string folder, IReadOnlyList<VimCase> cases)
    {
        string named = Path.Combine(folder, $"cases-{Guid.NewGuid():N}.json");
        string results = Path.ChangeExtension(named, ".results.json");
        string script = Path.ChangeExtension(named, ".vim");
        await File.WriteAllTextAsync(named, JsonSerializer.Serialize(cases.Select(c => new { lines = c.Lines, line = c.Line, col = c.Column, keys = c.Typed })));
        await File.WriteAllTextAsync(script, $$"""
            set nocp sw=4 ts=4 et ai nojoinspaces nofixeol backspace=indent,eol,start nomore shortmess=aoOstTWAIcCqFS cmdheight=20 noswapfile
            let s:registers = split('{{Registers.Replace("'", "''", StringComparison.Ordinal)}}', '\zs')
            let s:results = []
            for c in json_decode(join(readfile('{{named.Replace("'", "''", StringComparison.Ordinal)}}'), "\n"))
              silent %delete _
              call setline(1, ['{{Before.Text}}'])
              execute 'normal! {{Before.Keys}}'
              silent %delete _
              call setline(1, c.lines)
              call cursor(c.line, c.col)
              delmarks a-z
              for r in s:registers
                call setreg(r, [], 'c')
              endfor
              let @/ = ''
              call setcharsearch({'char': ''})
              silent! call feedkeys(c.keys, 'xt')
              let m = mode()
              let visual = []
              if m ==# 'v' || m ==# 'V' || m ==# "\<C-v>"
                let visual = [m, getpos('.')[1:2]]
                silent! call feedkeys('{{string.Concat(Yanked)}}', 'xt')
              endif
              silent! call feedkeys("\<Esc>", 'xt')
              let regs = {}
              for r in s:registers
                let regs[r] = [getreg(r), getregtype(r)]
              endfor
              call add(s:results, {'lines': getline(1, '$'), 'cursor': getpos('.')[1:2], 'visual': visual, 'registers': regs})
            endfor
            call writefile([json_encode(s:results)], '{{results.Replace("'", "''", StringComparison.Ordinal)}}')
            qa!
            """);
        ChildResult ran = await ChildProcesses.RunAsync(ChildProcesses.Command(vim, ["-u", "NONE", "-N", "-i", "NONE", "-n", "-X", "--not-a-term", "-S", script], folder),
            limit: VimLimit.Scaled());
        Assert.True(File.Exists(results), $"Vim wrote no results for the cases, so nothing could be compared (it exited with {ran.ExitCode}): {ran.Error}");
        using JsonDocument read = JsonDocument.Parse(await File.ReadAllTextAsync(results));
        List<VimEnd> ends = [.. read.RootElement.EnumerateArray().Select(FromVim)];
        Assert.True(ends.Count == cases.Count, $"Vim gave {ends.Count} results for {cases.Count} cases");
        return ends;
    }

    private static VimEnd FromVim(JsonElement end)
    {
        IReadOnlyList<JsonElement> visual = JsonMembers.Items(end, "visual");
        Dictionary<char, (string, string)> registers = [];
        foreach (JsonProperty register in JsonMembers.Properties(end, "registers"))
        {
            JsonElement[] pair = [.. register.Value.EnumerateArray()];
            string kind = pair[1].GetString() ?? string.Empty;
            registers[register.Name[0]] = (pair[0].GetString() ?? string.Empty, kind.Length == 0 ? string.Empty : kind[0] == '\u0016' ? "block" : kind);
        }
        return new VimEnd(JsonMembers.Texts(end, "lines"), Place(JsonMembers.Items(end, "cursor")),
            visual.Count == 0 ? null : (ModeOf(visual[0].GetString()), Place([.. visual[1].EnumerateArray()])),
            registers);
    }

    private static (int, int) Place(IReadOnlyList<JsonElement> place) => (place[0].GetInt32(), place[1].GetInt32());

    private static string ModeOf(string? vimMode) => vimMode switch
    {
        "v" => nameof(VimMode.Visual),
        "V" => nameof(VimMode.VisualLine),
        _ => nameof(VimMode.VisualBlock),
    };

    /// <summary>The case through Passenger's Vim mode as an editor drives it; null where a key asks the host for what only
    /// a window does (Vim then has nothing to compare it with).</summary>
    private static VimEnd? RunPassenger(VimCase vimCase)
    {
        Vim vim = new();
        VimTextView view = new(new IndentOptions()) { KeepsFinalLineBreak = true };
        vim.Press(Before.Keys, TextSnapshot.From(Before.Text + "\n"), SelectionSet.Single(0), view);
        vim.Registers = new VimRegisters();
        TextSnapshot text = TextSnapshot.From(vimCase.Text);
        SelectionSet carets = SelectionSet.Single(vimCase.Offset);
        HashSet<string> seen = [];
        (text, carets) = Drive(vim, text, carets, vimCase.Keys, view, seen);
        (string, (int, int))? visual = null;
        if (Vim.IsVisual(vim.Mode))
        {
            IReadOnlyList<int> cursors = vim.VisualCursors(carets);
            visual = (vim.Mode.ToString(), cursors.Count > 0 ? PlaceOf(text, cursors[0]) : (0, 0));
            (text, carets) = Drive(vim, text, carets, Yanked, view, seen);
        }
        if (vim.Mode != VimMode.Normal)
        {
            (text, carets) = Drive(vim, text, carets, [VimKeys.Escape], view, seen);
        }
        if (seen.Contains(Asked))
        {
            return null;
        }
        string all = text.ToString();
        List<string> lines = [.. (all.EndsWith('\n') ? all[..^1] : all).Split('\n')];
        Dictionary<char, (string, string)> registers = Registers.ToDictionary(name => name, name => vim.Registers.Get(name) is { } held
            ? (held.Text, held.Blockwise ? "block" : held.Linewise ? "V" : "v") : (string.Empty, string.Empty));
        return new VimEnd(lines, PlaceOf(text, carets.Primary.Caret), visual, registers) { Seen = seen };
    }

    private static (int Line, int Column) PlaceOf(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(offset);
        return (line + 1, offset - text.GetLineStart(line) + 1);
    }

    /// <summary>A change made before each case on a text of its own, so a . the case presses with no change of its own made
    /// before it makes the same change in both.</summary>
    private static readonly (string Text, string Keys) Before = ("ab", "x");

    /// <summary>The keys through Vim mode as an editor takes them: what Vim mode does done, a command line run command by
    /// command, and in insert mode each key typed as the editor types it, Enter carrying its line's indent; a key that
    /// asks the host for more is marked <paramref name="asked"/>.</summary>
    private static (TextSnapshot Text, SelectionSet Carets) Drive(Vim vim, TextSnapshot text, SelectionSet carets, IEnumerable<string> keys, VimTextView view,
        HashSet<string> seen)
    {
        foreach (string key in keys)
        {
            Observe(text, seen);
            VimResult result = vim.Press(key, text, carets, view);
            seen.UnionWith(result.Action is not null || result.Request is { Kind: not VimRequestKind.Commands } ? [Asked] : []);
            if (result.Replay is { } replay)
            {
                (text, carets) = Drive(vim, text, carets, replay, view, seen);
                continue;
            }
            if (result.Request is { Kind: VimRequestKind.Commands, Path: { } line })
            {
                foreach (string command in Vim.Bars(line))
                {
                    VimResult ran = vim.RunCommand(command, text, carets, view);
                    seen.UnionWith(ran.Action is not null || ran.Request is not null ? [Asked] : []);
                    if (ran.Replay is { } keysOf)
                    {
                        (text, carets) = Drive(vim, text, carets, keysOf, view, seen);
                    }
                    (text, carets) = Applied(ran, text, carets, view);
                    carets = Vim.Settled(text, carets, view.KeepsFinalLineBreak);
                    if (ran.MessageIsError)
                    {
                        break;
                    }
                }
                continue;
            }
            if (!result.Taken && vim.Mode == VimMode.Insert)
            {
                EditPlan? typed = key switch
                {
                    VimKeys.Enter => TextEditing.NewLine(text, carets, "\n", view.Indent),
                    VimKeys.Backspace => TextEditing.Backspace(text, carets, view.Indent),
                    _ => TextEditing.Insert(text, carets, key),
                };
                if (typed is not null)
                {
                    view.Follow(typed.Edits);
                    (text, carets) = (text.Apply(typed.Edits), typed.Selections);
                }
                continue;
            }
            (text, carets) = Applied(result, text, carets, view);
            if (vim.Mode == VimMode.Normal)
            {
                carets = Vim.Settled(text, carets, view.KeepsFinalLineBreak);
            }
        }
        Observe(text, seen);
        return (text, carets);
    }

    /// <summary>A key that asked the host for what only a window does.</summary>
    private const string Asked = "asked the host";

    /// <summary>What Passenger's side came through that <see cref="Kept"/> may name: a text of one empty line, a paragraph
    /// opened by a sentence's end.</summary>
    private static void Observe(TextSnapshot text, HashSet<string> seen)
    {
        seen.UnionWith(Enumerable.Range(0, text.LineCount).Any(line => text.GetLine(line) is { Length: > 0 } span && (line == 0 || text.GetLine(line - 1).Length == 0)
            && text[span.Start] is '.' or '!' or '?' && VimMotions.EndsSentence(text, span.Start, out _)) ? [OpenedByAnEnd] : []);
        seen.UnionWith(text.Length == 0 || (text.Length == 1 && text[0] == '\n') ? [LoneEmptyLine] : []);
    }

    /// <summary>What Vim mode did applied to the text, its marks carried through the edits as an editor's anchors are.</summary>
    private static (TextSnapshot, SelectionSet) Applied(VimResult result, TextSnapshot text, SelectionSet carets, VimTextView view)
    {
        foreach (EditPlan edit in result.Edits ?? [])
        {
            view.Follow(edit.Edits);
            (text, carets) = (text.Apply(edit.Edits), edit.Selections);
        }
        return result.Edits is not { Count: > 0 } && result.Selections is { } moved ? (text, moved) : (text, carets);
    }

    /// <summary>The smallest case that still differs, found by taking out a command, a line or a character, or a count,
    /// while the case still differs; each round's tries through one Vim.</summary>
    private static async Task<(VimCase Case, string Differs)> ShrinkAsync(string vim, string folder, VimCase start)
    {
        VimCase best = start;
        string differs = await DiffersAsync(vim, folder, [start]) is [{ } first] ? first : "differs";
        for (int round = 0; round < 200; round++)
        {
            List<VimCase> tries = [.. Smaller(best)];
            IReadOnlyList<string?> found = await DiffersAsync(vim, folder, tries);
            int at = found.ToList().FindIndex(f => f is not null);
            if (at < 0)
            {
                break;
            }
            (best, differs) = (tries[at], found[at]!);
        }
        return (best, differs);
    }

    private static async Task<IReadOnlyList<string?>> DiffersAsync(string vim, string folder, IReadOnlyList<VimCase> cases)
    {
        if (cases.Count == 0)
        {
            return [];
        }
        IReadOnlyList<VimEnd> vims = await RunVimAsync(vim, folder, cases);
        return [.. cases.Select((c, i) => RunPassenger(c) is { } passenger ? Differs(c, vims[i], passenger) : null)];
    }

    /// <summary>The cases one step smaller than <paramref name="from"/>.</summary>
    private static IEnumerable<VimCase> Smaller(VimCase bigger)
    {
        for (int i = 0; i < bigger.Commands.Count && bigger.Commands.Count > 1; i++)
        {
            yield return bigger with { Commands = [.. bigger.Commands.Where((_, j) => j != i)] };
        }
        for (int i = 0; i < bigger.Commands.Count; i++)
        {
            IReadOnlyList<string> keys = bigger.Commands[i].Keys;
            for (int k = 0; k < keys.Count; k++)
            {
                if (keys[k] is [>= '2' and <= '9'])
                {
                    yield return bigger with { Commands = [.. bigger.Commands.Select((c, j) => j != i ? c : c with { Keys = [.. keys.Where((_, m) => m != k)] })] };
                }
            }
        }
        for (int i = 0; i < bigger.Lines.Count && bigger.Lines.Count > 1; i++)
        {
            if (i + 1 != bigger.Line)
            {
                yield return bigger with { Lines = [.. bigger.Lines.Where((_, j) => j != i)], Line = i + 1 < bigger.Line ? bigger.Line - 1 : bigger.Line };
            }
        }
        for (int i = 0; i < bigger.Lines.Count; i++)
        {
            for (int c = 0; c < bigger.Lines[i].Length; c++)
            {
                if (i + 1 == bigger.Line && c + 1 == bigger.Column && bigger.Lines[i].Length > 1)
                {
                    continue;
                }
                int column = i + 1 == bigger.Line && c + 1 < bigger.Column ? bigger.Column - 1 : bigger.Column;
                string[] lines = [.. bigger.Lines];
                lines[i] = lines[i].Remove(c, 1);
                yield return bigger with { Lines = lines, Column = i + 1 == bigger.Line ? Math.Max(1, Math.Min(column, Math.Max(1, lines[i].Length))) : bigger.Column };
            }
        }
    }
}
