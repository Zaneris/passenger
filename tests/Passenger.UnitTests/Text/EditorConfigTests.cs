using Passenger.Core;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>An .editorconfig's sections: which files a glob matches, brackets taking each character as itself; a section
/// whose glob cannot be used matching no file, named where it is, while the file's other sections still apply; and a
/// value read by its first word, an inline comment after it left out.</summary>
public sealed class EditorConfigTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void An_editorconfigs_sections_and_values_read_as_the_format_says_comments_left_out() => Cases.AllApart<EditorConfigTests>(
        ("A section whose range runs backwards matches no file and the sections around it still apply", test => test.A_section_whose_range_runs_backwards_matches_no_file_and_the_sections_around_it_still_apply()),
        ("Each section that matches no file is named by where its glob is in the text and why", test => test.Each_section_that_matches_no_file_is_named_by_where_its_glob_is_in_the_text_and_why()),
        ("A values word is its first in lower case without the comment after it", test => test.A_values_word_is_its_first_in_lower_case_without_the_comment_after_it()),
        ("A flag and a number are read from a values word whatever comment follows", test => test.A_flag_and_a_number_are_read_from_a_values_word_whatever_comment_follows()),
        ("A files values followed by a comment say what their word says", test => test.A_files_values_followed_by_a_comment_say_what_their_word_says()),
        ("Braces nested as deep as a glob may still match", test => test.Braces_nested_as_deep_as_a_glob_may_still_match()));

    private void A_section_whose_range_runs_backwards_matches_no_file_and_the_sections_around_it_still_apply()
    {
        File.WriteAllText(Path.Join(root, EditorConfig.FileName),
            "root = true\n\n[*]\nindent_size = 2\n\n[[z-a]*.txt]\nindent_size = 8\ncharset = latin1\n\n[*.txt]\ntab_width = 6\n");

        EditorConfig notes = EditorConfig.For(Path.Join(root, "notes.txt"));
        Assert.True(notes["indent_size"] == "2", $"The section [[z-a]*.txt] gave notes.txt its indent_size {notes["indent_size"]}, not [*]'s 2");
        Assert.True(notes["tab_width"] == "6", "The section [*.txt] after one that matches no file did not apply");
        Assert.True(notes["charset"] is null, "The section [[z-a]*.txt] gave notes.txt its charset");
        Assert.Equal("2", EditorConfig.For(Path.Join(root, "z.txt"))["indent_size"]);
        Assert.False(EditorConfig.Matches("[z-a]*.txt", "/z.txt"), "A glob whose range runs backwards matched a file");
    }

    private void Each_section_that_matches_no_file_is_named_by_where_its_glob_is_in_the_text_and_why()
    {
        string deep = string.Concat(Enumerable.Repeat("{a,", 65)) + "b" + new string('}', 65);
        string deepest = string.Concat(Enumerable.Repeat("{a,", 100_000)) + "b" + new string('}', 100_000);
        string text = $"root = true\r\n\r\n[[a-\\d]*.cs]\r\n  [[z-a]*.txt]\r\nindent_size = 8\r\n[*.{{cs,md}}]\r\n[{deep}]\r\n\t[{deepest}]\r\n[[a-\\]].x]\r\n";

        IReadOnlyList<EditorConfig.RefusedSection> refused = EditorConfig.Refused(text);

        Assert.Equal(["[z-a]*.txt", deep, deepest, "[a-\\]].x"], refused.Select(r => text.Substring(r.Glob.Start, r.Glob.Length)));
        Assert.Equal("its range z-a is in reverse order", refused[0].Why);
        Assert.Equal("its braces nest more than 64 deep", refused[1].Why);
        Assert.Equal("its range a-] is in reverse order", refused[3].Why);
        Assert.False(EditorConfig.Matches(deepest, "/b"), "A glob nested too deep matched a file");
    }

    private void A_values_word_is_its_first_in_lower_case_without_the_comment_after_it() => Cases.Each<(string? value, string? word)>(
    [
        ("off", "off"),
        ("  Off  # no wrap", "off"),
        ("Tab;tabs", "tab"),
        ("file_scoped:warning", "file_scoped:warning"),
        ("  ", null),
        (null, null),
    ], row =>
    {
        (string? value, string? word) = row;
        
                Assert.Equal(word, EditorConfig.Word(value));
    });

    private void A_flag_and_a_number_are_read_from_a_values_word_whatever_comment_follows()
    {
        Assert.True(EditorConfig.Flag("TRUE  # always"));
        Assert.False(EditorConfig.Flag("false;never"));
        Assert.Null(EditorConfig.Flag("yes"));
        Assert.Equal(80, EditorConfig.Number("80  # columns"));
        Assert.Null(EditorConfig.Number("+80"));
        Assert.Null(EditorConfig.Number("off"));
        Assert.Null(EditorConfig.Number("99999999999"));
    }

    private void A_files_values_followed_by_a_comment_say_what_their_word_says()
    {
        File.WriteAllText(Path.Join(root, EditorConfig.FileName),
            "root = true # top\n\n[*]\nindent_style = space # no tabs\nindent_size = 2;two\nend_of_line = lf  # unix\ninsert_final_newline = true # always\n");
        string inner = Directory.CreateDirectory(Path.Join(root, "inner")).FullName;
        File.WriteAllText(Path.Join(inner, EditorConfig.FileName), "[*]\nindent_size = unset # the outer's\n");

        EditorConfig notes = EditorConfig.For(Path.Join(root, "notes.txt"));
        Assert.Equal(new IndentOptions(2, false, 2), notes.Indent(new IndentOptions(4, true, 4)));
        Assert.Equal(LineSeparator.LF, notes.EndOfLine);
        Assert.True(notes.InsertFinalNewline, "insert_final_newline = true # always asked for no line break at the end");
        Assert.True(EditorConfig.For(Path.Join(inner, "notes.txt"))["indent_size"] is null, "indent_size = unset # the outer's left the outer's size in force");
    }

    private void Braces_nested_as_deep_as_a_glob_may_still_match()
    {
        string deep = string.Concat(Enumerable.Repeat("{a,", 64)) + "b" + new string('}', 64);
        Assert.True(EditorConfig.Matches(deep, "/b"));
        Assert.True(EditorConfig.Matches(deep, "/src/a"));
        Assert.False(EditorConfig.Matches(deep, "/c"));
    }

    [Fact]
    public void An_editorconfig_glob_matches_braces_ranges_and_brackets_as_the_format_says_at_once() => Cases.AllApart<EditorConfigTests>(
        ("A glob of many stars against a long name is answered at once", test => test.A_glob_of_many_stars_against_a_long_name_is_answered_at_once()),
        ("A number range matches a whole number between its ends", test => test.A_number_range_matches_a_whole_number_between_its_ends()),
        ("A bracket matches one of its characters as the format says", test => test.A_bracket_matches_one_of_its_characters_as_the_format_says()),
        ("A backslash in a bracket takes the next character as itself", test => test.A_backslash_in_a_bracket_takes_the_next_character_as_itself()),
        ("A dash first in a bracket is itself and a range across a slash never matches one", test => test.A_dash_first_in_a_bracket_is_itself_and_a_range_across_a_slash_never_matches_one()),
        ("Matching ten thousand paths against a glob makes nothing as matching ten does", test => test.Matching_ten_thousand_paths_against_a_glob_makes_nothing_as_matching_ten_does()));

    /// <summary>A section's glob is the repository's text, matched on the window's thread as a file opens: one whose stars
    /// could be laid along a name in more ways than any machine could try (21 of them over 80 letters) is answered at once.</summary>
    private void A_glob_of_many_stars_against_a_long_name_is_answered_at_once()
    {
        string glob = string.Concat(Enumerable.Repeat("*a", 21));
        Assert.False(EditorConfig.Matches(glob, "/" + new string('a', 80) + "b"), $"[{glob}] matched a name ending in b");
        Assert.True(EditorConfig.Matches(glob, "/src/" + new string('a', 80)), $"[{glob}] did not match a name of 80 a's");
    }

    private void A_number_range_matches_a_whole_number_between_its_ends() => Cases.Each<(string glob, string relative, bool matches)>(
    [
        ("a{1..3}.txt", "/a2.txt", true),
        ("a{1..3}.txt", "/a4.txt", false),
        ("a{1..3}.txt", "/a02.txt", false),
        ("v{-2..2}/*.cs", "/v-1/A.cs", true),
        ("v{-2..2}/*.cs", "/v3/A.cs", false),
    ], row =>
    {
        (string glob, string relative, bool matches) = row;
        
                Assert.True(EditorConfig.Matches(glob, relative) == matches, $"[{glob}] {(matches ? "did not match" : "matched")} {relative}");
    });

    private void A_bracket_matches_one_of_its_characters_as_the_format_says() => Cases.Each<(string glob, string relative, bool matches)>(
    [
        ("[ab].a", "/b.a", true),
        ("[d-g].c", "/f.c", true),
        ("[abd-g].e", "/e.e", true),
        ("[!\\]ab].g", "/].g", false),
        ("ab[/c", "/ab[/c", true),
        ("[ab*c{1..2}].j", "/3.j", false),
    ], row =>
    {
        (string glob, string relative, bool matches) = row;
        
                Assert.True(EditorConfig.Matches(glob, relative) == matches, $"[{glob}] {(matches ? "did not match" : "matched")} {relative}");
    });

    private void A_backslash_in_a_bracket_takes_the_next_character_as_itself() => Cases.Each<(string glob, string relative, bool matches)>(
    [
        ("[\\p].txt", "/p.txt", true),
        ("[a-\\d].cs", "/c.cs", true),
        ("[\\b].md", "/b.md", true),
        ("[\\b].md", "/\b.md", false),
        ("[a\\-z].x", "/-.x", true),
        ("[\\é].x", "/é.x", true),
    ], row =>
    {
        (string glob, string relative, bool matches) = row;
        
                Assert.True(EditorConfig.Matches(glob, relative) == matches, $"[{glob}] {(matches ? "did not match" : "matched")} {relative}");
    });

    private void A_dash_first_in_a_bracket_is_itself_and_a_range_across_a_slash_never_matches_one() => Cases.Each<(string glob, string relative, bool matches)>(
    [
        ("[!-a].x", "/-.x", false),
        ("[!-a].x", "/a.x", false),
        ("[!-a].x", "/0.x", true),
        ("a[+-0]b", "/a.b", true),
        ("a[+-0]b", "/a0b", true),
        ("a[+-0]b", "/a/b", false),
    ], row =>
    {
        (string glob, string relative, bool matches) = row;
        
                Assert.True(EditorConfig.Matches(glob, relative) == matches, $"[{glob}] {(matches ? "did not match" : "matched")} {relative}");
    });

    /// <summary>A glob is matched against every path a watch hears and every file an .editorconfig is asked of, so what it
    /// keeps, and what a match makes, may not grow with the paths it has seen: stars, a "/**/", brackets and braces of choices,
    /// in either letter case.</summary>
    private void Matching_ten_thousand_paths_against_a_glob_makes_nothing_as_matching_ten_does()
    {
        string[] globs = ["*.cs", "{**/*.cs,**/*.csproj}", "src/**/[a-z]?.{md,txt}", "[!.]*.json"];
        string[] paths = [.. Enumerable.Range(0, 10_000).Select(i => $"/src/Folder{i % 37}/sub/{(char)('a' + i % 26)}{i % 10}.{(i % 3 == 0 ? "cs" : "md")}")];
        PathGlob lowered = PathGlob.Of("/**/*.CS", lowered: true);
        long Made(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                foreach (string glob in globs)
                {
                    EditorConfig.Matches(glob, paths[i]);
                }
                lowered.Matches(paths[i]);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.True(globs.Count(glob => EditorConfig.Matches(glob, paths[0])) == 2 && lowered.Matches("/A.Cs") && !lowered.Matches("/A.Csx"),
            "The globs did not match what they should, so their matching was not held");
        long few = Made(10);
        long many = Made(paths.Length);
        Assert.True(few == 0 && many == 0, $"Matching ten paths against the globs made {few:N0} bytes, ten thousand {many:N0}: what a glob keeps grows with every path a watch hears");
    }
}
