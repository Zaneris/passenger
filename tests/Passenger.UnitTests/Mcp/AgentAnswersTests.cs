using System.Globalization;
using Passenger.App.Mcp;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.UnitTests.Mcp;

/// <summary>What an agent's answer holds of what runs long: a debugged value cut at a few thousand characters, saying its
/// length and the debug_evaluate that reads on, a string's literal never cut inside an escape; a list of thousands of files
/// held to its first ones, or to a size, saying how many more and how to read them; groups held to a size, every one counted
/// and the first whole; a long line shown around its column; and a folder of untracked files one entry, as git's status
/// shows it, from the repository's root or a folder in it.</summary>
public sealed class AgentAnswersTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void An_agents_answer_holds_long_lists_to_their_first_entries_saying_how_many_more_and_how_to_read_them() => Cases.All(
        ("lists of findings held to a total keep every lists errors before any warning and count what was left out", Lists_of_findings_held_to_a_total_keep_every_lists_errors_before_any_warning_and_count_what_was_left_out),
        ("findings held to a size keep the severest first that fit", Findings_held_to_a_size_keep_the_severest_first_that_fit),
        ("five thousand files in a folder git shows whole are one entry saying how many beside the new file of a tracked folder", Five_thousand_files_in_a_folder_git_shows_whole_are_one_entry_saying_how_many_beside_the_new_file_of_a_tracked_folder),
        ("a folder inside one git shows whole lists its own folders as one entry each and its own files", A_folder_inside_one_git_shows_whole_lists_its_own_folders_as_one_entry_each_and_its_own_files),
        ("a folders changes are those under it and a rename out of it", A_folders_changes_are_those_under_it_and_a_rename_out_of_it),
        ("five thousand changes are held to the first ones saying how many more and how to read them", Five_thousand_changes_are_held_to_the_first_ones_saying_how_many_more_and_how_to_read_them),
        ("five thousand files named in a sentence are held to the first ones saying how many more and how to read them", Five_thousand_files_named_in_a_sentence_are_held_to_the_first_ones_saying_how_many_more_and_how_to_read_them),
        ("a list held to a size keeps its first entries that fit and says how many more", A_list_held_to_a_size_keeps_its_first_entries_that_fit_and_says_how_many_more),
        ("groups held to a size name every group that fits and give the first whole", Groups_held_to_a_size_name_every_group_that_fits_and_give_the_first_whole),
        ("room shared among lists gives the small ones all they need and splits the rest among those needing more", Room_shared_among_lists_gives_the_small_ones_all_they_need_and_splits_the_rest_among_those_needing_more));

    /// <summary>Lists of findings held to a total keep every list's errors before any list's warnings, each list in its own
    /// order, and count what each severity left out: two errors of the second list come before the first list's warnings.</summary>
    private void Lists_of_findings_held_to_a_total_keep_every_lists_errors_before_any_warning_and_count_what_was_left_out()
    {
        string[] build = ["error a", "warning b", "warning c"];
        string[] solution = ["error d", "error e", "warning f", "message g"];
        (IReadOnlyList<string>[] kept, int[] left) = AgentAnswers.Severest<string>([build, solution], Rank, 3, 4);
        Assert.Equal(["error a", "warning b"], kept[0]);
        Assert.Equal(["error d", "error e"], kept[1]);
        Assert.Equal([0, 2, 1], left);

        (kept, left) = AgentAnswers.Severest<string>([build, solution], Rank, 3, 100);
        Assert.Equal([build, solution], kept);
        Assert.Equal([0, 0, 0], left);

        static int Rank(string finding) => finding.Split(' ')[0] switch { "error" => 0, "warning" => 1, _ => 2 };
    }

    [Fact]
    public void An_agents_answer_shows_a_long_value_whole_or_cut_where_it_reads_on_never_inside_an_escape_or_a_pair() => Cases.All(
        ("a short value is shown whole and says no cut", A_short_value_is_shown_whole_and_says_no_cut),
        ("a million character string shows its first characters and says its length and the substring that reads on", A_million_character_string_shows_its_first_characters_and_says_its_length_and_the_substring_that_reads_on),
        ("a strings escapes are never cut and its length and places are the strings own", A_strings_escapes_are_never_cut_and_its_length_and_places_are_the_strings_own),
        ("a cut never parts a surrogate pair", A_cut_never_parts_a_surrogate_pair),
        ("a value no expression names or one that is no variable is read on through its own substring or in parentheses", A_value_no_expression_names_or_one_that_is_no_variable_is_read_on_through_its_own_substring_or_in_parentheses),
        ("a long line is shown around its column and a short one trimmed", A_long_line_is_shown_around_its_column_and_a_short_one_trimmed));

    private void A_short_value_is_shown_whole_and_says_no_cut()
    {
        Assert.Equal(("\"short\"", (string?)null), AgentAnswers.Value("\"short\"", "text"));
        string fits = new('x', AgentAnswers.ValueLength);
        Assert.Equal((fits, (string?)null), AgentAnswers.Value(fits, "text"));
    }

    private void A_million_character_string_shows_its_first_characters_and_says_its_length_and_the_substring_that_reads_on()
    {
        (string shown, string? cut) = AgentAnswers.Value($"\"{new string('x', 1_000_000)}\"", "big");
        Assert.True(shown.Length <= AgentAnswers.ValueLength && shown == $"\"{new string('x', AgentAnswers.ValueLength - 2)}…",
            $"A string of a million characters was shown as {shown.Length:N0} characters, an answer an agent's client may refuse: {shown[..40]}…");
        Assert.Equal("1,000,000 characters, the first 3,998 shown: debug_evaluate big.Substring(3998, 3998) reads on", cut);
    }

    private void A_strings_escapes_are_never_cut_and_its_length_and_places_are_the_strings_own()
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

    private void A_cut_never_parts_a_surrogate_pair()
    {
        string pairs = string.Concat(Enumerable.Repeat("\U0001F600", 5_000));
        (string shown, _) = AgentAnswers.Value($"\"{pairs}\"", "faces");
        Assert.False(char.IsHighSurrogate(shown[^2]), $"The literal's cut parted a surrogate pair: its shown text ends {(int)shown[^2]:X4}");
        (string plain, string? said) = AgentAnswers.Value(pairs, "faces");
        Assert.False(char.IsHighSurrogate(plain[^2]), $"The value's cut parted a surrogate pair: its shown text ends {(int)plain[^2]:X4}");
        Assert.StartsWith("10,000 characters, the first 3,998 shown: debug_evaluate faces.ToString().Substring(3998, 3998)", said, StringComparison.Ordinal);
    }

    private void A_value_no_expression_names_or_one_that_is_no_variable_is_read_on_through_its_own_substring_or_in_parentheses()
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

    private void Five_thousand_files_in_a_folder_git_shows_whole_are_one_entry_saying_how_many_beside_the_new_file_of_a_tracked_folder()
    {
        string modules = At("node_modules");
        List<FileChange> changes = [new(At("src", "App.cs"), ChangeKind.Modified), new(At("src", "New.cs"), ChangeKind.Unversioned)];
        changes.AddRange(Enumerable.Range(0, 5_000).Select(i => new FileChange(At("node_modules", $"package{i % 50}", $"file{i}.js"), ChangeKind.Unversioned)));
        IReadOnlyList<ListedChange> listed = AgentAnswers.Gathered(changes, [modules], root);
        Assert.Equal([(At("src", "App.cs"), (int?)null), (At("src", "New.cs"), null), (modules, 5_000)], listed.Select(c => (c.Change.Path, c.Files)));
    }

    private void A_folder_inside_one_git_shows_whole_lists_its_own_folders_as_one_entry_each_and_its_own_files()
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

    private void A_folders_changes_are_those_under_it_and_a_rename_out_of_it()
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

    private void Five_thousand_changes_are_held_to_the_first_ones_saying_how_many_more_and_how_to_read_them()
    {
        IReadOnlyList<ListedChange> gathered = AgentAnswers.Gathered(
            [.. Enumerable.Range(0, 5_000).Select(i => new FileChange(At($"folder{i % 20}", $"file{i}.cs"), ChangeKind.Modified))], [], root);
        (IReadOnlyList<ListedChange> kept, string? more) = AgentAnswers.Listed(gathered, AgentAnswers.MostFiles, "change", "vcs_status with a folder's path lists its own");
        Assert.Equal(gathered.Take(AgentAnswers.MostFiles), kept);
        Assert.Equal("4,800 more changes left out: vcs_status with a folder's path lists its own", more);
        (IReadOnlyList<ListedChange> all, string? none) = AgentAnswers.Listed(gathered.Take(AgentAnswers.MostFiles).ToList(), AgentAnswers.MostFiles, "change", "never said");
        Assert.True(all.Count == AgentAnswers.MostFiles && none is null, $"A list that fits was cut, saying: {none}");
    }

    private void Five_thousand_files_named_in_a_sentence_are_held_to_the_first_ones_saying_how_many_more_and_how_to_read_them()
    {
        string[] names = [.. Enumerable.Range(0, 5_000).Select(i => $"many/file-with-a-longish-name-{i}.txt")];
        string said = AgentAnswers.Named(names, "file", "local_history of a folder within names those there");
        Assert.True(said.Length < 5_000 && said.StartsWith("many/file-with-a-longish-name-0.txt, many/file-with-a-longish-name-1.txt, ", StringComparison.Ordinal)
            && said.EndsWith($"many/file-with-a-longish-name-{AgentAnswers.MostNamed - 1}.txt; 4,900 more files left out: local_history of a folder within names those there", StringComparison.Ordinal),
            $"5,000 files were named in {said.Length:N0} characters: {said[..Math.Min(said.Length, 300)]}…{said[^Math.Min(said.Length, 200)..]}");
        Assert.Equal("a.txt, b.txt", AgentAnswers.Named(["a.txt", "b.txt"], "file", "never said"));
    }

    /// <summary>A list held to a size keeps its first entries while they fit, each with its separator, and says how many
    /// more it left out; one that fits is kept whole, saying nothing.</summary>
    private void A_list_held_to_a_size_keeps_its_first_entries_that_fit_and_says_how_many_more()
    {
        string[] entries = [.. Enumerable.Range(0, 1_000).Select(i => $"row {i:D6}")];
        (IReadOnlyList<string> kept, string? more) = AgentAnswers.Within(entries, e => e.Length, 100, "row", "a filter narrows them");
        Assert.Equal(entries.Take(9), kept);
        Assert.Equal("991 more rows left out: a filter narrows them", more);
        Assert.Equal(((IReadOnlyList<string>)entries, (string?)null), AgentAnswers.Within(entries, e => e.Length, 11_000, "row", "never said"));
    }

    /// <summary>Groups held to a size are whole where they all fit; else every group that fits is named with its count, the
    /// first of them whole while the room left takes them, and the rest left out.</summary>
    private void Groups_held_to_a_size_name_every_group_that_fits_and_give_the_first_whole()
    {
        int[] groups = [50, 50, 50, 50, 50];
        Assert.Equal((5, 0), AgentAnswers.Grouped(groups, g => g, _ => 10, 1_000));
        Assert.Equal((2, 3), AgentAnswers.Grouped(groups, g => g, _ => 10, 150));
        Assert.Equal((0, 3), AgentAnswers.Grouped(groups, g => g, _ => 10, 33));
    }

    /// <summary>Room shared among lists gives each that needs no more than an equal share of what the smaller ones leave all
    /// it needs, and splits the rest equally among those needing more; two lists needing more than half take half each.</summary>
    private void Room_shared_among_lists_gives_the_small_ones_all_they_need_and_splits_the_rest_among_those_needing_more()
    {
        Assert.Equal([40_000, 50, 300, 10_000], AgentAnswers.Shares([70_000, 50, 300, 10_000], 50_350));
        Assert.Equal([10_000, 50, 300, 10_000], AgentAnswers.Shares([70_000, 50, 300, 60_000], 20_350));
        Assert.Equal([500, 500], AgentAnswers.Shares([2_000, 3_000], 1_000));
        Assert.Equal([200, 800], AgentAnswers.Shares([200, 3_000], 1_000));
        Assert.Equal([0, 0, 0], AgentAnswers.Shares([10, 20, 30], -5));
        Assert.Equal([333, 333, 334], AgentAnswers.Shares([1_000, 1_000, 1_000], 1_000));
    }

    /// <summary>A place's line past the length an answer shows is cut around its column, "…" where cut, never between a
    /// pair's surrogates; a short one is shown trimmed.</summary>
    private void A_long_line_is_shown_around_its_column_and_a_short_one_trimmed()
    {
        Assert.Equal("int x = 1;", AgentAnswers.Line("    int x = 1;   ", 5));
        string minified = $"    {new string('a', 5_000)}needle{new string('b', 5_000)}";
        string shown = AgentAnswers.Line(minified, minified.IndexOf("needle", StringComparison.Ordinal) + 1);
        Assert.True(shown.Length <= AgentAnswers.LineLength + 2 && shown.StartsWith('…') && shown.EndsWith('…') && shown.Contains("needle", StringComparison.Ordinal),
            $"A minified line's match was shown as {shown.Length} characters: {shown}");
        Assert.Equal("ab…", AgentAnswers.Cut("ab\U0001F600cd", 3));
    }

    /// <summary>Findings held to a size as well as a count keep the severest first while they fit, every one after the
    /// first that did not fit left out.</summary>
    private void Findings_held_to_a_size_keep_the_severest_first_that_fit()
    {
        string[] found = ["warning one", "error two", "warning three", "error four"];
        (IReadOnlyList<string>[] kept, int[] left) = AgentAnswers.Severest<string>([found], f => f.StartsWith("error", StringComparison.Ordinal) ? 0 : 1, 2, 100,
            f => f.Length, 34);
        Assert.Equal(["error two", "error four", "warning one"], kept[0]);
        Assert.Equal([0, 1], left);
    }

    private string At(params string[] names) => Path.Combine([root, .. names]);
}
