using System.Buffers;
using System.Runtime;
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

    /// <summary>Each call is measured apart, so a failure names the calls that allocated, in how many rounds, and how many
    /// methods the runtime compiled on this thread meanwhile: bytes in every round are the scoring's garbage, bytes in one
    /// round alone something done once.</summary>
    [Fact]
    public void Scoring_names_paths_and_labels_allocates_nothing()
    {
        string longPath = string.Join('/', Enumerable.Repeat("folder", 60)) + "/CodeEditor.cs";
        string longQuery = string.Join('\\', Enumerable.Repeat("fold", 70));
        PathLetters letters = PathLetters.Of(longPath);
        ulong wanted = FuzzyMatch.Wanted("ced");
        string[] said =
        [
            .. Files.SelectMany(path => new[] { $"Score(\"ced\", \"{path}\")", $"Score(\"editor/code\", \"{path}\")" }),
            $"Score of a query of {longQuery.Length} characters with slashes against a path of {longPath.Length}",
            "Score(\"ced\") of that path read with its letters",
            "Label(\"sb\", \"StringBuilder\")",
            "Label(\"gTOD\", \"GetTypeOrDefault\")",
        ];
        Allocations seen = new(said);
        int matched = 0;
        void Ask(int round)
        {
            int call = 0;
            foreach (string path in Files)
            {
                long at = seen.Begin();
                matched += FuzzyMatch.Score("ced", path) is null ? 0 : 1;
                seen.End(call++, round, at);
                at = seen.Begin();
                matched += FuzzyMatch.Score("editor/code", path) is null ? 0 : 1;
                seen.End(call++, round, at);
            }
            long since = seen.Begin();
            matched += FuzzyMatch.Score(longQuery, longPath) is null ? 0 : 1;
            seen.End(call++, round, since);
            since = seen.Begin();
            matched += FuzzyMatch.Score("ced", wanted, longPath, letters) is null ? 0 : 1;
            seen.End(call++, round, since);
            since = seen.Begin();
            matched += FuzzyMatch.Label("sb", "StringBuilder") is null ? 0 : 1;
            seen.End(call++, round, since);
            since = seen.Begin();
            matched += FuzzyMatch.Label("gTOD", "GetTypeOrDefault") is null ? 0 : 1;
            seen.End(call, round, since);
        }
        Ask(round: -1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < Allocations.Rounds; round++)
        {
            Ask(round);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"A thousand rounds of scoring allocated {allocated:N0} bytes: Go To File would make garbage on every key for every file. {seen.Said(allocated)}");
        Assert.True(matched > 0);
    }

    /// <summary>A collection under memory pressure can leave the shared pool holding nothing, so the next to rent an array makes one.</summary>
    [Fact]
    public void Scoring_a_query_and_path_too_long_for_the_stack_allocates_nothing_once_the_shared_pool_holds_nothing()
    {
        string longPath = string.Join('\\', Enumerable.Repeat("folder", 60)) + "\\CodeEditor.cs";
        string longQuery = string.Join('/', Enumerable.Repeat("folder", 40));
        Assert.Equal(600 - longPath.Length, FuzzyMatch.Score(longQuery, longPath));
        bool made = false;
        while (!made)
        {
            long renting = GC.GetAllocatedBytesForCurrentThread();
            ArrayPool<char>.Shared.Rent(longPath.Length);
            made = GC.GetAllocatedBytesForCurrentThread() != renting;
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int? score = FuzzyMatch.Score(longQuery, longPath);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"Scoring allocated {allocated:N0} bytes with the pool empty: Go To File would make garbage on a key whenever the machine is short of memory");
        Assert.Equal(600 - longPath.Length, score);
    }

    [Fact]
    public void A_query_and_path_too_long_for_the_stack_match_along_the_path_as_short_ones_do_and_a_query_longer_than_the_path_matches_nothing()
    {
        string path = string.Join('\\', Enumerable.Repeat("folder", 60)) + "\\CodeEditor.cs";
        string query = string.Join('/', Enumerable.Repeat("FOLDER", 59)) + "/code";
        Assert.Equal(600 - path.Length, FuzzyMatch.Score(query, path));
        Assert.Equal(600 - path.Length, FuzzyMatch.Score(query[..300], path));
        int passedOver = "folder\\od".Length;
        Assert.Equal(300 - passedOver - path.Length / 4, FuzzyMatch.Score(query.Replace("/code", "/ce", StringComparison.Ordinal), path));
        Assert.Null(FuzzyMatch.Score(query + "/nothing", path));
        Assert.Null(FuzzyMatch.Score(path + "/", path));
        Assert.Null(FuzzyMatch.Score("a/b/", "a/b"));
    }

    private static IEnumerable<string> Ranked(string query, IEnumerable<string> paths) =>
        paths.Select(p => (Path: p, Score: FuzzyMatch.Score(query, p))).Where(m => m.Score is not null).OrderByDescending(m => m.Score).Select(m => m.Path);

    /// <summary>What each call allocated on this thread across the measured rounds, kept in arrays made before them so that
    /// keeping it allocates nothing, with the methods the runtime compiled on this thread in those calls.</summary>
    private sealed class Allocations
    {
        public const int Rounds = 1_000;

        private readonly string[] said;
        private readonly long[] bytes;
        private readonly int[] rounds;
        private readonly int[] firstRound;
        private readonly long[] compiled;
        private long compiledAt;

        public Allocations(string[] said)
        {
            this.said = said;
            bytes = new long[said.Length];
            rounds = new int[said.Length];
            firstRound = new int[said.Length];
            compiled = new long[said.Length];
        }

        public long Begin()
        {
            compiledAt = JitInfo.GetCompiledMethodCount(currentThread: true);
            return GC.GetAllocatedBytesForCurrentThread();
        }

        /// <summary>Notes what the call begun at <paramref name="at"/> allocated in a measured round; round -1 only warms what
        /// the rounds run.</summary>
        public void End(int call, int round, long at)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread() - at;
            long methods = JitInfo.GetCompiledMethodCount(currentThread: true) - compiledAt;
            if (round < 0 || allocated == 0)
            {
                return;
            }
            if (rounds[call] == 0)
            {
                firstRound[call] = round;
            }
            rounds[call]++;
            bytes[call] += allocated;
            compiled[call] += methods;
        }

        /// <summary>Each call that allocated, and what the rounds allocated outside the calls.</summary>
        public string Said(long allocated)
        {
            List<string> calls = [];
            for (int call = 0; call < said.Length; call++)
            {
                if (rounds[call] > 0)
                {
                    calls.Add($"{said[call]} allocated {bytes[call]:N0} bytes in {rounds[call]:N0} of the {Rounds:N0} rounds, "
                        + $"first in round {firstRound[call] + 1:N0}, as the runtime compiled {compiled[call]:N0} methods on this thread");
                }
            }
            long between = allocated - bytes.Sum();
            if (between != 0)
            {
                calls.Add($"{between:N0} bytes were allocated between the calls");
            }
            return string.Join("; ", calls) + ".";
        }
    }
}
