using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Go To File's and completion's matching: which names a query matches, in what order, and that asking costs no
/// allocation, as it is asked of every file or item on every key.</summary>
public class FuzzyMatchTests
{
    private static readonly string[] Files =
    [
        "src/Editor/Editor.cs",
        "src/Editor/EditorArea.cs",
        "src/Editor/CodeEditor.cs",
        "tests/Editor/MyEditorTests.cs",
        "src/Reditor.cs",
        "src/EdXitor.cs",
        "src/Other.cs",
        "src/deep/er/still/Editor.cs",
    ];

    [Fact]
    public void A_files_name_ranks_by_exact_then_prefix_then_humps_then_inside_then_letters_close_in_order()
    {
        Assert.Equal(
            ["src/Editor/Editor.cs", "src/deep/er/still/Editor.cs", "src/Editor/EditorArea.cs", "src/Editor/CodeEditor.cs", "tests/Editor/MyEditorTests.cs",
                "src/Reditor.cs", "src/EdXitor.cs"],
            Ranked("editor", Files));
    }

    [Theory]
    [InlineData("CE", "src/Editor/CodeEditor.cs")]
    [InlineData("coed", "src/Editor/CodeEditor.cs")]
    [InlineData("fm", "src/fuzzy_match.py")]
    [InlineData("ipa", "src/IPAddress.cs")]
    [InlineData("ad", "src/IPAddress.cs")]
    [InlineData("kel", "src/Kelvin.cs")]
    public void Initials_of_a_names_words_match_it_across_case_separators_and_runs_of_capitals(string query, string path)
    {
        Assert.NotNull(FuzzyMatch.Score(query, path));
    }

    [Theory]
    [InlineData("xyz", "src/Editor/CodeEditor.cs")]
    [InlineData("ce", "src/Comment/Plain.cs")]
    [InlineData("ec", "src/Editor/CodeEditor.cs")]
    [InlineData("eoz", "src/abcdefghijklmnopqrstuvwxyz.cs")]
    [InlineData("src", "src/Editor/CodeEditor.cs")]
    public void A_query_whose_letters_are_not_in_the_name_in_order_or_are_spread_through_it_matches_nothing(string query, string path)
    {
        Assert.Null(FuzzyMatch.Score(query, path));
    }

    [Fact]
    public void A_query_with_a_slash_matches_along_the_path_either_slash_alike_inside_first_then_letters_in_order()
    {
        int? inside = FuzzyMatch.Score("editor/code", "src/Editor/CodeEditor.cs");
        Assert.Equal(inside, FuzzyMatch.Score("editor\\code", "src\\Editor\\CodeEditor.cs"));
        int? spread = FuzzyMatch.Score("edit/code", "src/Editor/CodeEditor.cs");
        Assert.NotNull(spread);
        Assert.True(inside > spread, $"A path holding the query ({inside}) ranks below one holding only its letters in order ({spread})");
        Assert.Null(FuzzyMatch.Score("tests/code", "src/Editor/CodeEditor.cs"));
    }

    [Fact]
    public void A_completion_label_ranks_by_prefix_in_its_case_then_any_case_then_humps_from_its_start_then_later_then_inside()
    {
        string[] labels = ["Asbestos", "ToStringBuilder", "StringBuilder", "SByte", "sbyte", "Absolute"];
        Assert.Equal(["sbyte", "SByte", "StringBuilder", "ToStringBuilder", "Asbestos"],
            labels.Select(l => (Label: l, Score: FuzzyMatch.Label("sb", l))).Where(m => m.Score is not null).OrderByDescending(m => m.Score).Select(m => m.Label));
        Assert.Equal(0, FuzzyMatch.Label(string.Empty, "Anything"));
        Assert.Null(FuzzyMatch.Label("s", "Absolute"));
    }

    /// <summary>The letters a file is indexed with only ever pass over a file the query cannot match, so the quick score is the
    /// full one for every query and file, a letter matched in another case (the Kelvin sign's k) included.</summary>
    [Fact]
    public void The_score_of_a_file_read_with_its_letters_is_the_score_of_its_path_for_every_query()
    {
        string[] paths = [.. Files, "src/Kelvin.cs", "src/IPAddress.cs", "docs\\read_me.md", "src/Straße.cs", "src/a1b2.cs"];
        string[] queries = ["e", "ed", "editor", "CE", "kel", "KEL", "ipa", "read/me", "docs\\", "strasse", "straß", "12", "a1", "x", "/", "e/c"];
        foreach (string path in paths)
        {
            PathLetters letters = PathLetters.Of(path);
            foreach (string query in queries)
            {
                Assert.True(FuzzyMatch.Score(query, path) == FuzzyMatch.Score(query, FuzzyMatch.Wanted(query), path, letters),
                    $"'{query}' scores {FuzzyMatch.Score(query, path)} against {path} but {FuzzyMatch.Score(query, FuzzyMatch.Wanted(query), path, letters)} with its letters");
            }
        }
    }

    [Fact]
    public void Scoring_names_paths_and_labels_allocates_nothing()
    {
        string longPath = string.Join('/', Enumerable.Repeat("folder", 60)) + "/CodeEditor.cs";
        string longQuery = string.Join('\\', Enumerable.Repeat("fold", 70));
        PathLetters letters = PathLetters.Of(longPath);
        ulong wanted = FuzzyMatch.Wanted("ced");
        int matched = 0;
        void Ask()
        {
            foreach (string path in Files)
            {
                matched += FuzzyMatch.Score("ced", path) is null ? 0 : 1;
                matched += FuzzyMatch.Score("editor/code", path) is null ? 0 : 1;
            }
            matched += FuzzyMatch.Score(longQuery, longPath) is null ? 0 : 1;
            matched += FuzzyMatch.Score("ced", wanted, longPath, letters) is null ? 0 : 1;
            matched += FuzzyMatch.Label("sb", "StringBuilder") is null ? 0 : 1;
            matched += FuzzyMatch.Label("gTOD", "GetTypeOrDefault") is null ? 0 : 1;
        }
        Ask();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            Ask();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"A thousand rounds of scoring allocated {allocated:N0} bytes: Go To File would make garbage on every key for every file");
        Assert.True(matched > 0);
    }

    private static IEnumerable<string> Ranked(string query, IEnumerable<string> paths) =>
        paths.Select(p => (Path: p, Score: FuzzyMatch.Score(query, p))).Where(m => m.Score is not null).OrderByDescending(m => m.Score).Select(m => m.Path);
}
