using System.Globalization;
using Passenger.App.Mcp;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.UnitTests.Mcp;

/// <summary>What an agent's answer holds of what runs long: a debugged value cut at a few thousand characters, saying its
/// length and the debug_evaluate that reads on, a string's literal never cut inside an escape; a list of thousands of files
/// held to its first ones, saying how many more and how to read them; and a folder of untracked files one entry, as git's
/// status shows it, from the repository's root or a folder in it.</summary>
public sealed class AgentAnswersTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_short_value_is_shown_whole_and_says_no_cut()
    {
        Assert.Equal(("\"short\"", (string?)null), AgentAnswers.Value("\"short\"", "text"));
        string fits = new('x', AgentAnswers.ValueLength);
        Assert.Equal((fits, (string?)null), AgentAnswers.Value(fits, "text"));
    }

    [Fact]
    public void A_million_character_string_shows_its_first_characters_and_says_its_length_and_the_substring_that_reads_on()
    {
        (string shown, string? cut) = AgentAnswers.Value($"\"{new string('x', 1_000_000)}\"", "big");
        Assert.True(shown.Length <= AgentAnswers.ValueLength && shown == $"\"{new string('x', AgentAnswers.ValueLength - 2)}…",
            $"A string of a million characters was shown as {shown.Length:N0} characters, an answer an agent's client may refuse: {shown[..40]}…");
        Assert.Equal("1,000,000 characters, the first 3,998 shown: debug_evaluate big.Substring(3998, 3998) reads on", cut);
    }

    [Fact]
    public void A_strings_escapes_are_never_cut_and_its_length_and_places_are_the_strings_own()
    {
        // Each \n is two characters of the literal and one of the string; each \u0001 six and one.
        string literal = "\"" + string.Concat(Enumerable.Repeat("ab\\n\\u0001", 2_000)) + "\"";
        (string shown, string? cut) = AgentAnswers.Value(literal, "text");
        char next = literal[shown.Length - 1];
        Assert.True(shown.Length <= AgentAnswers.ValueLength && next is 'a' or 'b' or '\\',
            $"A string's literal was cut inside an escape, which an agent reads as other text: …{shown[^20..]} before {literal.Substring(shown.Length - 1, 8)}");
        int shownChars = Unescaped(shown[1..^1]);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture,
            $"8,000 characters, the first {shownChars:N0} shown: debug_evaluate text.Substring({shownChars}, {Math.Min(AgentAnswers.ValueLength - 2, 8_000 - shownChars)}) reads on"), cut);
    }

    [Fact]
    public void A_cut_never_parts_a_surrogate_pair()
    {
        string pairs = string.Concat(Enumerable.Repeat("\U0001F600", 5_000));
        (string shown, _) = AgentAnswers.Value($"\"{pairs}\"", "faces");
        Assert.False(char.IsHighSurrogate(shown[^2]), $"The literal's cut parted a surrogate pair: its shown text ends {(int)shown[^2]:X4}");
        (string plain, string? said) = AgentAnswers.Value(pairs, "faces");
        Assert.False(char.IsHighSurrogate(plain[^2]), $"The value's cut parted a surrogate pair: its shown text ends {(int)plain[^2]:X4}");
        Assert.StartsWith("10,000 characters, the first 3,998 shown: debug_evaluate faces.ToString().Substring(3998, 3998)", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_no_expression_names_or_one_that_is_no_variable_is_read_on_through_its_own_substring_or_in_parentheses()
    {
        string big = $"\"{new string('y', 5_000)}\"";
        Assert.Equal("5,000 characters, the first 3,998 shown: debug_evaluate of its .Substring(3998, 1002) reads on", AgentAnswers.Value(big, null).Cut);
        Assert.Equal("5,000 characters, the first 3,998 shown: debug_evaluate (new string('y', 5000)).Substring(3998, 1002) reads on",
            AgentAnswers.Value(big, "new string('y', 5000)").Cut);
        Assert.Equal("5,000 characters, the first 3,998 shown: debug_evaluate order.Lines[2].Note.Substring(3998, 1002) reads on",
            AgentAnswers.Value(big, "order.Lines[2].Note").Cut);
    }

    /// <summary>How many characters a literal's text (its quotes left off) writes.</summary>
    private static int Unescaped(string text)
    {
        int count = 0;
        for (int at = 0; at < text.Length; count++)
        {
            at += text[at] != '\\' ? 1 : text[at + 1] == 'u' ? 6 : 2;
        }
        return count;
    }

    [Fact]
    public void Five_thousand_files_in_a_folder_git_shows_whole_are_one_entry_saying_how_many_beside_the_new_file_of_a_tracked_folder()
    {
        string modules = At("node_modules");
        List<FileChange> changes = [new(At("src", "App.cs"), ChangeKind.Modified), new(At("src", "New.cs"), ChangeKind.Unversioned)];
        changes.AddRange(Enumerable.Range(0, 5_000).Select(i => new FileChange(At("node_modules", $"package{i % 50}", $"file{i}.js"), ChangeKind.Unversioned)));
        IReadOnlyList<ListedChange> listed = AgentAnswers.Gathered(changes, [modules], root);
        Assert.Equal([(At("src", "App.cs"), (int?)null), (At("src", "New.cs"), null), (modules, 5_000)], listed.Select(c => (c.Change.Path, c.Files)));
    }

    [Fact]
    public void A_folder_inside_one_git_shows_whole_lists_its_own_folders_as_one_entry_each_and_its_own_files()
    {
        List<FileChange> changes = [new(At("README.md"), ChangeKind.Modified), new(At("node_modules", "index.js"), ChangeKind.Unversioned)];
        changes.AddRange(Enumerable.Range(0, 5_000).Select(i => new FileChange(At("node_modules", $"package{i % 50}", "lib", $"file{i}.js"), ChangeKind.Unversioned)));
        IReadOnlyList<ListedChange> listed = AgentAnswers.Gathered(changes, [At("node_modules")], At("node_modules"));
        Assert.Equal(51, listed.Count);
        Assert.Equal((At("node_modules", "index.js"), (int?)null), (listed[0].Change.Path, listed[0].Files));
        Assert.All(listed.Skip(1), c => Assert.True(c.Files == 100 && Path.GetDirectoryName(c.Change.Path) == At("node_modules"),
            $"{c.Change.Path} was listed with {c.Files} files, not as one of node_modules' own folders of 100"));
        IReadOnlyList<ListedChange> package = AgentAnswers.Gathered(changes, [At("node_modules")], At("node_modules", "package7"));
        Assert.Equal([(At("node_modules", "package7", "lib"), (int?)100)], package.Select(c => (c.Change.Path, c.Files)));
    }

    [Fact]
    public void A_folders_changes_are_those_under_it_and_a_rename_out_of_it()
    {
        IReadOnlyList<FileChange> changes =
        [
            new(At("src", "App.cs"), ChangeKind.Modified),
            new(At("docs", "Moved.md"), ChangeKind.Renamed, At("src", "Moved.md")),
            new(At("srcs", "Other.cs"), ChangeKind.Modified),
            new(At("tests", "AppTests.cs"), ChangeKind.Deleted),
        ];
        Assert.Equal([At("src", "App.cs"), At("docs", "Moved.md")], AgentAnswers.Gathered(changes, [], At("src")).Select(c => c.Change.Path));
    }

    [Fact]
    public void Five_thousand_changes_are_held_to_the_first_ones_saying_how_many_more_and_how_to_read_them()
    {
        IReadOnlyList<ListedChange> gathered = AgentAnswers.Gathered(
            [.. Enumerable.Range(0, 5_000).Select(i => new FileChange(At($"folder{i % 20}", $"file{i}.cs"), ChangeKind.Modified))], [], root);
        (IReadOnlyList<ListedChange> kept, string? more) = AgentAnswers.Listed(gathered, AgentAnswers.MostFiles, "change", "vcs_status with a folder's path lists its own");
        Assert.Equal(gathered.Take(AgentAnswers.MostFiles), kept);
        Assert.Equal("4,800 more changes left out: vcs_status with a folder's path lists its own", more);
        (IReadOnlyList<ListedChange> all, string? none) = AgentAnswers.Listed(gathered.Take(AgentAnswers.MostFiles).ToList(), AgentAnswers.MostFiles, "change", "never said");
        Assert.True(all.Count == AgentAnswers.MostFiles && none is null, $"A list that fits was cut, saying: {none}");
    }

    [Fact]
    public void Five_thousand_files_named_in_a_sentence_are_held_to_the_first_ones_saying_how_many_more_and_how_to_read_them()
    {
        string[] names = [.. Enumerable.Range(0, 5_000).Select(i => $"many/file-with-a-longish-name-{i}.txt")];
        string said = AgentAnswers.Named(names, "file", "local_history of a folder within names those there");
        Assert.True(said.Length < 5_000 && said.StartsWith("many/file-with-a-longish-name-0.txt, many/file-with-a-longish-name-1.txt, ", StringComparison.Ordinal)
            && said.EndsWith($"many/file-with-a-longish-name-{AgentAnswers.MostNamed - 1}.txt; 4,900 more files left out: local_history of a folder within names those there", StringComparison.Ordinal),
            $"5,000 files were named in {said.Length:N0} characters: {said[..Math.Min(said.Length, 300)]}…{said[^Math.Min(said.Length, 200)..]}");
        Assert.Equal("a.txt, b.txt", AgentAnswers.Named(["a.txt", "b.txt"], "file", "never said"));
    }

    private string At(params string[] names) => Path.Combine([root, .. names]);
}
