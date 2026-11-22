using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Which files a value of a project file names, as Passenger reads it without MSBuild (<see cref="ItemPaths"/>,
/// <see cref="ProjectItems"/>), held to MSBuild's own evaluation over seeded random folders: names of MSBuild's own
/// characters, values written as Passenger writes them or as a person might, each %XX or %25XX escape, an escaped
/// wildcard, <c>*</c>, <c>?</c> and <c>**</c>, read as an Include (a glob unescaped twice) or an Exclude (once). A
/// difference is shrunk to its smallest value and said with its seed and the files it differs on.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed class MSBuildItemsTests
{
    /// <summary>The differences kept on purpose, each with why: none.</summary>
    private const string Kept = "none";

    /// <summary>The folders a run makes, one evaluation each, and the values asked of each.</summary>
    private const int Seeds = 60;

    private const int Values = 60;

    /// <summary>What a name is made of: letters and a digit that make %XX escapes of no letter, and MSBuild's own
    /// characters with those any file system takes.</summary>
    private const string NameCharacters = "ab1%;$@' (#&";

    /// <summary>The characters a person may write escaped, each as %XX or, in a glob, %25XX.</summary>
    private const string Escapable = "%;$@' (#&";

    private const string Project = "Items.proj";

    [Fact]
    public async Task The_files_a_value_names_are_those_msbuilds_own_evaluation_takes()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        List<string> differences = [];
        for (int seed = 1; seed <= Seeds && differences.Count == 0; seed++)
        {
            Random random = new(seed);
            string folder = PathNames.Canonical(TestFolders.Create("msbuild-items-"));
            string[] files = [.. Tree(random).Append(Project)];
            foreach (string file in files.SkipLast(1))
            {
                TestFolders.Write(folder, file, string.Empty);
            }
            List<Value> values = [.. Enumerable.Range(0, Values).Select(_ => Drawn(random, files)).Where(v => Read(folder, v) is not null)];
            Dictionary<Value, Difference> found = await DifferencesAsync(folder, files, values, cancel);
            foreach ((Value value, Difference difference) in found.Take(3))
            {
                (Value smallest, Difference left) = await ShrunkAsync(folder, files, value, difference, cancel);
                differences.Add($"Seed {seed}: {smallest} (drawn as {value}) {left}, of the files {string.Join(", ", files.Select(f => $"\"{f}\""))}");
            }
        }
        Assert.True(differences.Count == 0, $"Passenger names other files than MSBuild's evaluation does, so a move, a shared project's items or a "
            + $"build's check follows the wrong files; kept apart on purpose: {Kept}.\n{string.Join("\n", differences)}");
    }

    /// <summary>A value of a project file: what is written, and whether it is an Exclude beside an Include of every file.</summary>
    private sealed record Value(string Written, bool Exclude)
    {
        public override string ToString() => Exclude ? $"Include=\"**\" Exclude=\"{Written}\"" : $"Include=\"{Written}\"";
    }

    /// <summary>The files only MSBuild named and only Passenger did.</summary>
    private sealed record Difference(IReadOnlyList<string> MSBuildAlone, IReadOnlyList<string> PassengerAlone)
    {
        public override string ToString() =>
            $"names {Listed(MSBuildAlone)} by MSBuild's evaluation alone and {Listed(PassengerAlone)} by Passenger's reading alone";

        private static string Listed(IReadOnlyList<string> files) => files.Count == 0 ? "nothing" : string.Join(", ", files.Select(f => $"\"{f}\""));
    }

    /// <summary>Folders two deep, each with a file or two, every name drawn from <see cref="NameCharacters"/>.</summary>
    private static List<string> Tree(Random random)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        for (int top = random.Next(2, 4); top > 0; top--)
        {
            string folder = Name(random);
            foreach (string under in Enumerable.Range(0, random.Next(0, 3)).Select(_ => $"{folder}/{Name(random)}").Prepend(folder))
            {
                for (int file = random.Next(1, 3); file > 0; file--)
                {
                    files.Add($"{under}/{Name(random)}{(random.Next(2) == 0 ? ".cs" : ".txt")}");
                }
            }
        }
        return [.. files.Where(f => !files.Any(other => other.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>A name no system trims or refuses: never empty, never beginning or ending with a space.</summary>
    private static string Name(Random random)
    {
        StringBuilder name = new();
        for (int length = random.Next(1, 5); length > 0; length--)
        {
            name.Append(NameCharacters[random.Next(NameCharacters.Length)]);
        }
        string trimmed = name.ToString().Trim();
        return trimmed.Length == 0 ? "a" : trimmed;
    }

    /// <summary>A value naming one of the files or folders: as Passenger writes a file or what lies under a folder, or as a
    /// person might, each special character written as it is or escaped, a name or two made wildcards.</summary>
    private static Value Drawn(Random random, string[] files)
    {
        string file = files[random.Next(files.Length - 1)];
        string[] parts = file.Split('/');
        string folder = string.Join('/', parts[..random.Next(1, parts.Length)]);
        string written = random.Next(5) switch
        {
            0 => ItemPaths.Named(file),
            1 => ItemPaths.Under(folder),
            2 => string.Join('/', parts.Select(part => Escaped(random, part, glob: false))),
            _ => Glob(random, parts),
        };
        return new Value(written, Exclude: random.Next(3) == 0);
    }

    /// <summary>A glob as a person might write one for a file: some names a wildcard of its own, the folders before the
    /// first escaped as a glob is, now and then a wildcard escaped as a plain name.</summary>
    private static string Glob(Random random, string[] parts)
    {
        string[] written = new string[parts.Length];
        for (int at = 0; at < parts.Length; at++)
        {
            bool last = at == parts.Length - 1;
            written[at] = random.Next(4) switch
            {
                0 => last ? "*" : "**",
                1 when last => "*" + Path.GetExtension(parts[at]),
                1 => "*",
                2 when parts[at].Length > 1 => "?" + Escaped(random, parts[at][1..], glob: true),
                _ => Escaped(random, parts[at], glob: true),
            };
        }
        string glob = string.Join('/', written);
        return random.Next(8) == 0 ? glob.Replace("*", "%2A", StringComparison.Ordinal) : glob;
    }

    /// <summary>A name with each special character written as it is or escaped, as %XX or in a glob %25XX too.</summary>
    private static string Escaped(Random random, string name, bool glob)
    {
        StringBuilder written = new();
        foreach (char c in name)
        {
            int way = Escapable.Contains(c) ? random.Next(glob ? 3 : 2) : 0;
            written.Append(way switch
            {
                0 => c.ToString(),
                1 => $"%{(int)c:X2}",
                _ => $"%25{(int)c:X2}",
            });
        }
        return written.ToString();
    }

    /// <summary>What Passenger reads the value to name from the folder, or null where it names no one path.</summary>
    private static ItemPath? Read(string folder, Value value) => ProjectItems.PathOf(folder, value.Written, exclude: value.Exclude);

    /// <summary>The files Passenger reads the value to name, of those the folder holds.</summary>
    private static HashSet<string> PassengerNames(string folder, string[] files, Value value)
    {
        HashSet<string> named = new(StringComparer.Ordinal);
        if (Read(folder, value) is not { } path)
        {
            return named;
        }
        foreach (string file in files)
        {
            if (ItemPaths.Matches(path, Path.GetFullPath(Path.Combine(folder, file)), _ => false) != value.Exclude)
            {
                named.Add(file);
            }
        }
        return named;
    }

    /// <summary>Each value whose files MSBuild's evaluation names otherwise than Passenger reads it to, asked in one
    /// evaluation.</summary>
    private static async Task<Dictionary<Value, Difference>> DifferencesAsync(string folder, string[] files, IReadOnlyList<Value> values, CancellationToken cancel)
    {
        XElement group = new("ItemGroup");
        for (int at = 0; at < values.Count; at++)
        {
            XElement item = new($"V{at}");
            item.SetAttributeValue("Include", values[at].Exclude ? "**" : values[at].Written);
            if (values[at].Exclude)
            {
                item.SetAttributeValue("Exclude", values[at].Written);
            }
            group.Add(item);
        }
        string project = TestFolders.Write(folder, Project, new XElement("Project", group).ToString());
        MSBuildQuestion question = new([]) { Items = [.. values.Select((_, at) => $"V{at}")] };
        MSBuildAnswer answer = await Waiting.Apart(() => MSBuildQuestions.AskAsync(project, null, new Dictionary<string, string>(), question, cancel), cancel);
        Assert.True(answer.Answered, $"MSBuild gave no answer: {answer.Said.Error}{answer.Said.Output}");
        Dictionary<string, string> byPath = files.ToDictionary(f => Path.GetFullPath(Path.Combine(folder, f)), f => f, StringComparer.Ordinal);
        Dictionary<Value, Difference> differing = [];
        for (int at = 0; at < values.Count; at++)
        {
            HashSet<string> msbuild = [.. answer.Items($"V{at}").Select(item => JsonMembers.Text(item, "FullPath"))
                .OfType<string>().Select(Path.GetFullPath).Where(byPath.ContainsKey).Select(path => byPath[path])];
            HashSet<string> passenger = PassengerNames(folder, files, values[at]);
            if (!msbuild.SetEquals(passenger))
            {
                differing[values[at]] = new Difference([.. msbuild.Except(passenger).Order(StringComparer.Ordinal)], [.. passenger.Except(msbuild).Order(StringComparer.Ordinal)]);
            }
        }
        return differing;
    }

    /// <summary>The smallest value still differing that taking out one character or escape reaches, each round's every
    /// smaller value asked in one evaluation.</summary>
    private static async Task<(Value Value, Difference Difference)> ShrunkAsync(string folder, string[] files, Value value, Difference difference, CancellationToken cancel)
    {
        for (int round = 0; round < 60; round++)
        {
            List<Value> smaller = [.. Smaller(value.Written).Distinct().Select(w => value with { Written = w }).Where(v => Read(folder, v) is not null)];
            if (smaller.Count == 0)
            {
                break;
            }
            Dictionary<Value, Difference> found = await DifferencesAsync(folder, files, smaller, cancel);
            if (smaller.FirstOrDefault(found.ContainsKey) is not { } next)
            {
                break;
            }
            (value, difference) = (next, found[next]);
        }
        return (value, difference);
    }

    /// <summary>The value without one of its characters, an escape taken out whole.</summary>
    private static IEnumerable<string> Smaller(string written)
    {
        for (int at = 0; at < written.Length; at++)
        {
            int length = written[at] == '%' && at + 2 < written.Length && char.IsAsciiHexDigit(written[at + 1]) && char.IsAsciiHexDigit(written[at + 2]) ? 3 : 1;
            string shorter = written.Remove(at, length);
            if (shorter.Length > 0)
            {
                yield return shorter;
            }
        }
    }
}
