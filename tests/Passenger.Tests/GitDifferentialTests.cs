using System.Globalization;
using System.Text;
using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Passenger's reads and writes of a repository held to git's own over random repositories, seeded: the status
/// of changed, staged, deleted, new and renamed files; a file's diff against its last commit; a partial stage of chosen
/// changes or lines, which git then reads back as staged and unstaged; and whether git stores a file alike with either
/// line break, under each core.autocrlf. A difference is shrunk to its smallest file and said with its seed.</summary>
[Collection(Beside.Name)]
public sealed class GitDifferentialTests
{
    /// <summary>The run's seed, unless PASSENGER_GIT_SEED names one to replay.</summary>
    private const int Seed = 2095;

    private const int Rounds = 24;

    /// <summary>Lines drawn from few words, so a diff meets lines alike in several places.</summary>
    private static readonly string[] Words = ["a", "b", "c", "d", "e"];

    /// <summary>Names a repository's files take, a space and a letter beyond ASCII among them.</summary>
    private static readonly string[] Names = ["one.txt", "two words.txt", "café.txt", "deep/four.txt"];

    // Differences kept on purpose, which the generated texts keep out of what is compared:
    // - a line's ending and the file's last line break are no lines of the editor's diff (FileDiff.SeparatorChanges says
    //   them apart), where git's counts the line they end as changed: a file keeps one ending, and ends with a break,
    //   wherever its lines are counted against git's.

    [Fact]
    public async Task Passengers_status_diffs_partial_stages_and_line_breaks_agree_with_gits_own_over_random_repositories()
    {
        int seed = Environment.GetEnvironmentVariable("PASSENGER_GIT_SEED") is { Length: > 0 } given ? int.Parse(given, CultureInfo.InvariantCulture) : Seed;
        Random rounds = new(seed);
        for (int round = 0; round < Rounds; round++)
        {
            int roundSeed = rounds.Next();
            Repository repository = Repository.Made(new Random(roundSeed));
            if (await DifferenceAsync(repository) is { } difference)
            {
                Repository smallest = await ShrunkAsync(repository);
                Assert.Fail($"Passenger and git differ on round {round} of seed {seed} (its own seed {roundSeed}): {await DifferenceAsync(smallest) ?? difference}\n"
                    + $"The smallest repository found that still differs:\n{smallest}");
            }
        }
    }

    /// <summary>What Passenger reads or writes otherwise than git does in a repository made as <paramref name="made"/> says,
    /// or null where they agree.</summary>
    private static async Task<string?> DifferenceAsync(Repository made)
    {
        string folder = TestFolders.Create("git-differential-");
        try
        {
            await made.WriteAsync(folder);
            Git git = (await Git.OpenAsync(folder)).Tree ?? throw new InvalidOperationException($"No working tree was found in {folder}");
            return await StatusDifferenceAsync(git, made) ?? await StagingDifferenceAsync(git, made) ?? await BreaksDifferenceAsync(git, made);
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Each changed file's path, kind, where it came from and how much the index holds, as Passenger reads the
    /// status and as git's own short format says it.</summary>
    private static async Task<string?> StatusDifferenceAsync(Git git, Repository made)
    {
        GitStatus status = await git.StatusAsync();
        string[] ours = [.. status.Changes.Select(c => Said(Relative(git, c.Path), c.Kind, c.From is null ? null : Relative(git, c.From), c.Staged)).Order(StringComparer.Ordinal)];
        string[] gits = [.. GitsStatus(await GitAsync(git.Root, "status", "--porcelain=v1", "-z", "--untracked-files=all")).Order(StringComparer.Ordinal)];
        return ours.SequenceEqual(gits) ? null
            : $"the status reads [{string.Join("; ", ours)}] where git says [{string.Join("; ", gits)}], after {made.Operations}";
    }

    /// <summary>Each file's changes against its last commit counted as git counts them, then a random choice of them, by
    /// changes or by lines, staged through Passenger, which git must read back as exactly the chosen staged and the rest
    /// not.</summary>
    private static async Task<string?> StagingDifferenceAsync(Git git, Repository made)
    {
        await GitAsync(git.Root, "reset", "-q");
        Random choosing = new(made.ChoiceSeed);
        foreach (FileMade file in made.Files.Where(f => f.Asked is "edited" or "edited and staged whole"))
        {
            string path = Path.Combine(git.Root, file.Name);
            FileText disk = FileText.Load(path) ?? throw new InvalidOperationException($"{file.Name} read as binary");
            byte[] stored = await git.BytesAtAsync("HEAD", path) ?? throw new InvalidOperationException($"HEAD has no {file.Name}");
            FileText head = FileText.FromBytes(stored, disk.Encoding)!;
            FileDiff diff = FileDiff.Compare(head.Text, TextSnapshot.From(disk.Text));
            (int Added, int Deleted) whole = Counted(diff.Hunks);
            (int Added, int Deleted) gits = Numstat(await GitAsync(git.Root, "diff", "--numstat", "--minimal", "HEAD", "--", file.Name));
            if (whole != gits)
            {
                return $"{file.Name}'s diff adds {whole.Added} and deletes {whole.Deleted} lines where git's adds {gits.Added} and deletes {gits.Deleted}";
            }
            if (diff.Hunks.Count == 0)
            {
                continue;
            }
            bool byLines = choosing.Next(2) == 0;
            IReadOnlyList<DiffHunk> chosen = byLines ? LinesOf(diff, choosing) : [.. diff.Hunks.Where(_ => choosing.Next(2) == 0)];
            GitResult staged = await git.StageChangesAsync(path, new PartialCommit(diff, chosen));
            if (!staged.Succeeded)
            {
                return $"staging {(byLines ? "lines" : "changes")} of {file.Name} was refused: {staged.Message}";
            }
            (int Added, int Deleted) meant = Counted(chosen);
            (int Added, int Deleted) cached = Numstat(await GitAsync(git.Root, "diff", "--cached", "--numstat", "--minimal", "--", file.Name));
            (int Added, int Deleted) left = Numstat(await GitAsync(git.Root, "diff", "--numstat", "--minimal", "--", file.Name));
            if (cached != meant || left != (whole.Added - meant.Added, whole.Deleted - meant.Deleted))
            {
                return $"staging {(byLines ? "lines" : "changes")} of {file.Name} adding {meant.Added} and deleting {meant.Deleted} of its {whole.Added} and {whole.Deleted} "
                    + $"left git reading {cached.Added} and {cached.Deleted} staged and {left.Added} and {left.Deleted} not";
            }
            await GitAsync(git.Root, "reset", "-q", "--", file.Name);
        }
        return null;
    }

    /// <summary>Whether git stores a committed file alike with LF or CRLF ending its lines, as Passenger asks git and as
    /// git's own diff of the last commit's text written either way says.</summary>
    private static async Task<string?> BreaksDifferenceAsync(Git git, Repository made)
    {
        foreach (FileMade file in made.Files.Where(f => f.Asked is "edited" or "edited and staged whole" && f.Head.Length > 0).Take(1))
        {
            string path = Path.Combine(git.Root, file.Name);
            byte[] kept = await File.ReadAllBytesAsync(path);
            FileText head = FileText.FromBytes((await git.BytesAtAsync("HEAD", path))!)!;
            bool ours = await git.BreaksStoredAlikeAsync("HEAD", [path], path, head);
            bool lf = await UnchangedWrittenAsync(git.Root, file.Name, LineEndings.In(head.Text, "\n"));
            bool crlf = await UnchangedWrittenAsync(git.Root, file.Name, LineEndings.In(head.Text, "\r\n"));
            await File.WriteAllBytesAsync(path, kept);
            if (ours != (lf && crlf))
            {
                return $"{file.Name} under core.autocrlf {made.AutoCrlf} is said {(ours ? string.Empty : "not ")}stored alike with either break, where git's diff "
                    + $"finds it unchanged written with LF {lf} and with CRLF {crlf}";
            }
        }
        return null;
    }

    /// <summary>Whether git's diff finds the last commit's file unchanged once <paramref name="text"/> is written there.</summary>
    private static async Task<bool> UnchangedWrittenAsync(string root, string name, string text)
    {
        await File.WriteAllBytesAsync(Path.Combine(root, name), Encoding.UTF8.GetBytes(text));
        ChildResult diff = await ChildProcesses.RunAsync(ChildProcesses.Command("git", ["diff", "--quiet", "HEAD", "--", name], root), limit: TimeSpan.FromMinutes(1));
        return diff.ExitCode == 0;
    }

    /// <summary>A few stretches of the file's lines and of the last commit's, chosen at random, as the person picks lines.</summary>
    private static IReadOnlyList<DiffHunk> LinesOf(FileDiff diff, Random random)
    {
        return PartialCommit.LinesChosen(diff, Stretches(LinesIn(diff.Text.ToString())), Stretches(LinesIn(diff.Head)));

        List<(int, int)> Stretches(int count)
        {
            List<(int, int)> chosen = [];
            for (int i = random.Next(3); i > 0 && count > 0; i--)
            {
                int first = random.Next(count);
                chosen.Add((first, Math.Min(count - 1, first + random.Next(3))));
            }
            return chosen;
        }
    }

    /// <summary>How many lines a text holds, its last counted whether or not a break ends it.</summary>
    private static int LinesIn(string text) => text.Length == 0 ? 0 : text.AsSpan().Count('\n') + (text[^1] == '\n' ? 0 : 1);

    private static (int Added, int Deleted) Counted(IEnumerable<DiffHunk> hunks) => (hunks.Sum(h => h.NewCount), hunks.Sum(h => h.OldCount));

    /// <summary>The lines git's --numstat says one file's diff adds and deletes, none where it lists no file.</summary>
    private static (int Added, int Deleted) Numstat(string said) =>
        said.Split('\n', StringSplitOptions.RemoveEmptyEntries) is [{ } line, ..] && line.Split('\t') is [{ } added, { } deleted, ..]
            ? (int.Parse(added, CultureInfo.InvariantCulture), int.Parse(deleted, CultureInfo.InvariantCulture))
            : (0, 0);

    /// <summary>Each entry of git's short status (-z) as <see cref="Said"/> words it: its two letters read as the file's kind
    /// and how much of its change the index holds.</summary>
    private static IEnumerable<string> GitsStatus(string written)
    {
        string[] fields = written.Split('\0');
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Length < 4)
            {
                continue;
            }
            char index = fields[i][0];
            char tree = fields[i][1];
            string name = fields[i][3..];
            string? from = index == 'R' ? fields[++i] : null;
            StagedState staged = index is ' ' or '?' ? StagedState.None : tree == ' ' ? StagedState.Whole : StagedState.Part;
            ChangeKind kind = (index, tree) switch
            {
                ('?', '?') => ChangeKind.Unversioned,
                ('R', _) => ChangeKind.Renamed,
                ('A', _) => ChangeKind.Added,
                ('D', _) or (_, 'D') => ChangeKind.Deleted,
                _ => ChangeKind.Modified,
            };
            yield return Said(name, kind, from, staged);
        }
    }

    private static string Said(string name, ChangeKind kind, string? from, StagedState staged) =>
        $"{name} {kind}{(from is null ? string.Empty : $" from {from}")} staged {staged}";

    private static string Relative(Git git, string path) => PathNames.Relative(path, git.Root) ?? path;

    private static async Task<string> GitAsync(string root, params string[] arguments) => await TestGit.RunAsync(root, arguments);

    /// <summary>The smallest repository found that still differs: files taken out, then each file's lines one at a time,
    /// while git and Passenger still differ.</summary>
    private static async Task<Repository> ShrunkAsync(Repository repository)
    {
        bool smaller = true;
        while (smaller)
        {
            smaller = false;
            foreach (Repository each in repository.Smaller())
            {
                if (await DifferenceAsync(each) is not null)
                {
                    repository = each;
                    smaller = true;
                    break;
                }
            }
        }
        return repository;
    }

    /// <summary>A file as the repository holds it: its lines in the last commit (none where it is new), in the working tree
    /// (null where it was deleted), its line break, and what git was asked of it.</summary>
    private sealed record FileMade(string Name, string[] Head, string[]? Work, string Break, bool Committed, string Asked)
    {
        public string Text(string[] lines) => string.Concat(lines.Select(line => line + Break));
    }

    /// <summary>A repository to make: its files, core.autocrlf, and the seed its stages are chosen by.</summary>
    private sealed record Repository(IReadOnlyList<FileMade> Files, string AutoCrlf, int ChoiceSeed)
    {
        public string Operations => string.Join(", ", Files.Select(f => $"{f.Name} {f.Asked}"));

        public static Repository Made(Random random)
        {
            string[] settings = ["false", "true", "input"];
            string autoCrlf = settings[random.Next(settings.Length)];
            List<FileMade> files = [];
            foreach (string name in Names.Where(_ => random.Next(4) > 0))
            {
                string lineBreak = random.Next(2) == 0 ? "\n" : "\r\n";
                string[] head = Lines(random, random.Next(9));
                (string[]? work, bool committed, string asked) = random.Next(8) switch
                {
                    0 => ((string[]?)null, true, "deleted"),
                    1 => ((string[]?)null, true, "removed by git rm"),
                    2 => (Lines(random, 1 + random.Next(6)), false, "made new"),
                    3 => (Lines(random, 1 + random.Next(6)), false, "made new and added"),
                    4 => (head, true, "moved by git mv"),
                    5 => (Edited(head, random), true, "edited and staged whole"),
                    _ => (Edited(head, random), true, "edited"),
                };
                files.Add(new FileMade(name, committed ? head : [], work, lineBreak, committed, asked));
            }
            return new Repository(files, autoCrlf, random.Next());
        }

        /// <summary>Repositories one step smaller: one file left out, or one line of one file's.</summary>
        public IEnumerable<Repository> Smaller()
        {
            for (int i = 0; i < Files.Count; i++)
            {
                yield return this with { Files = [.. Files.Where((_, at) => at != i)] };
            }
            for (int i = 0; i < Files.Count; i++)
            {
                FileMade file = Files[i];
                for (int line = 0; line < file.Head.Length; line++)
                {
                    yield return With(i, file with { Head = [.. file.Head.Where((_, at) => at != line)] });
                }
                for (int line = 0; line < (file.Work?.Length ?? 0); line++)
                {
                    yield return With(i, file with { Work = [.. file.Work!.Where((_, at) => at != line)] });
                }
            }
        }

        private Repository With(int at, FileMade file) => this with { Files = [.. Files.Select((each, i) => i == at ? file : each)] };

        /// <summary>The repository made in <paramref name="folder"/>: its files committed, then changed as each says.</summary>
        public async Task WriteAsync(string folder)
        {
            await GitAsync(folder, "init", "-q", "-b", "main");
            await GitAsync(folder, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
            await GitAsync(folder, "config", "user.name", "Passenger Tests");
            await GitAsync(folder, "config", "core.autocrlf", AutoCrlf);
            await GitAsync(folder, "commit", "-q", "--allow-empty", "-m", "Empty");
            foreach (FileMade file in Files.Where(f => f.Committed))
            {
                Write(folder, file.Name, file.Text(file.Head));
            }
            await GitAsync(folder, "add", "-A");
            await GitAsync(folder, "commit", "-q", "--allow-empty", "-m", "First");
            foreach (FileMade file in Files)
            {
                string path = Path.Combine(folder, file.Name);
                switch (file.Asked)
                {
                    case "deleted":
                        File.Delete(path);
                        break;
                    case "removed by git rm":
                        await GitAsync(folder, "rm", "-q", "--", file.Name);
                        break;
                    case "moved by git mv":
                        await GitAsync(folder, "mv", "--", file.Name, file.Name + ".moved");
                        break;
                    default:
                        Write(folder, file.Name, file.Text(file.Work!));
                        if (file.Asked is "made new and added" or "edited and staged whole")
                        {
                            await GitAsync(folder, "add", "--", file.Name);
                        }
                        break;
                }
            }
        }

        public override string ToString() => $"core.autocrlf {AutoCrlf}, choices seeded {ChoiceSeed}:\n" + string.Join('\n', Files.Select(f =>
            $"  {f.Name}, {f.Asked}, lines ending {(f.Break == "\n" ? "LF" : "CRLF")}: [{string.Join(", ", f.Head)}] -> [{(f.Work is null ? "gone" : string.Join(", ", f.Work))}]"));

        private static void Write(string folder, string name, string text)
        {
            string path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        }

        private static string[] Lines(Random random, int count) => [.. Enumerable.Range(0, count).Select(_ => Words[random.Next(Words.Length)])];

        /// <summary>The lines with a few replaced, put in or taken out at random.</summary>
        private static string[] Edited(string[] lines, Random random)
        {
            List<string> edited = [.. lines];
            for (int edits = 1 + random.Next(4); edits > 0; edits--)
            {
                int at = random.Next(edited.Count + 1);
                switch (random.Next(3))
                {
                    case 0 when at < edited.Count:
                        edited[at] = Words[random.Next(Words.Length)].ToUpperInvariant();
                        break;
                    case 1 when at < edited.Count:
                        edited.RemoveAt(at);
                        break;
                    default:
                        edited.Insert(at, Words[random.Next(Words.Length)]);
                        break;
                }
            }
            return [.. edited];
        }
    }
}
