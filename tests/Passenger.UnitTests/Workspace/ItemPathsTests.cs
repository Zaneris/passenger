using Passenger.Core;

namespace Passenger.UnitTests.Workspace;

/// <summary>A path written into a project file reads back as that path. Each spelling here is what a real evaluation takes:
/// a plain name reads each %XX once; an Include, Remove or Update glob twice, an Exclude's or Import's once; and a glob with
/// an escaped wildcard is a plain name fitting no file. A value is matched against files as MSBuild's globs match them,
/// each name in the letter case its folder tells: here a volume mounted at cs tells case apart, as a case-sensitive APFS
/// volume does, and every other folder ignores it.</summary>
public class ItemPathsTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\v" : "/v";

    /// <summary>Each folder asked whether it ignores case, in order.</summary>
    private readonly List<string> asked = [];

    [Fact]
    public void A_value_is_read_moved_and_matched_where_msbuilds_own_evaluation_cannot_be_asked() => Cases.All(
        ("a glob written for a folder reads back as that folder and its wildcards", () => Cases.Each<string>(
        [
            "per%41cent",
            "%(x)",
        ], A_glob_written_for_a_folder_reads_back_as_that_folder_and_its_wildcards)),
        ("a quote is escaped only between a conditions quotes", A_quote_is_escaped_only_between_a_conditions_quotes),
        ("a glob read once takes one escape of its percent", A_glob_read_once_takes_one_escape_of_its_percent),
        ("a list or what msbuild expands says no one path", () => Cases.Each<string>(
        [
            "a.cs;b.cs",
            "$(Folder)/a.cs",
            "lib/$(Configuration)/*.dll",
        ], A_list_or_what_msbuild_expands_says_no_one_path)),
        ("a glob with an escaped wildcard is a plain name as msbuild reads it", A_glob_with_an_escaped_wildcard_is_a_plain_name_as_msbuild_reads_it),
        ("a value takes a file named in another case only where its folder ignores case", A_value_takes_a_file_named_in_another_case_only_where_its_folder_ignores_case),
        ("a glob goes only as deep as the path it matches however long it is", A_glob_goes_only_as_deep_as_the_path_it_matches_however_long_it_is),
        ("a glob of many double stars against a deep path is answered at once", A_glob_of_many_double_stars_against_a_deep_path_is_answered_at_once));

    private void A_glob_written_for_a_folder_reads_back_as_that_folder_and_its_wildcards(string folder)
    {
        ItemPath read = Assert.IsType<ItemPath>(ItemPaths.Read(ItemPaths.Under($"{folder}/script_templates")));

        Assert.Equal($"{folder}/script_templates", read.Path);
        Assert.Equal("/**", read.Rest);
        Assert.Equal(ItemPaths.Under("moved/per%cent"), read.At("moved/per%cent"));
    }

    /// <summary>A single quote closes a condition's string, and nothing in an item's value, which reads it as it is.</summary>
    private void A_quote_is_escaped_only_between_a_conditions_quotes()
    {
        Assert.Equal("Bob%27s/a.props", ItemPaths.InCondition(ItemPaths.Named("Bob's/a.props")));
        Assert.Equal("Bob's/a.props", ItemPaths.Read("Bob%27s/a.props")?.Path);
    }

    /// <summary>An Exclude and an Import read a glob once, so what follows one writes its '%' once.</summary>
    private void A_glob_read_once_takes_one_escape_of_its_percent()
    {
        ItemPath read = Assert.IsType<ItemPath>(ItemPaths.Read("per%25cent/*.targets", once: true));

        Assert.Equal("per%cent", read.Path);
        Assert.Equal("was%25here/*.targets", read.At("was%here"));
    }

    private void A_list_or_what_msbuild_expands_says_no_one_path(string written)
    {
        Assert.Null(ItemPaths.Read(written));
    }

    private void A_glob_with_an_escaped_wildcard_is_a_plain_name_as_msbuild_reads_it()
    {
        ItemPath read = Assert.IsType<ItemPath>(ItemPaths.Read("star%2Ax/**"));

        Assert.False(read.Glob, "A value MSBuild takes for one file was read as a glob");
        Assert.Equal("star*x/**", read.Path);
    }

    private void A_value_takes_a_file_named_in_another_case_only_where_its_folder_ignores_case()
    {
        Assert.True(Matches(At("plain", "lib", "*.cs"), At("plain", "Lib", "Util.cs")), "Where case is ignored, lib/*.cs did not take Lib/Util.cs");
        Assert.True(Matches(At("plain", "lib", "util.cs"), At("plain", "lib", "Util.cs")), "Where case is ignored, lib/util.cs did not take lib/Util.cs");
        Assert.False(Matches(At("cs", "lib", "*.cs"), At("cs", "Lib", "Util.cs")), "On a case-sensitive volume, lib/*.cs took Lib/Util.cs, a folder of its own");
        Assert.False(Matches(At("cs", "lib", "*.CS"), At("cs", "lib", "Util.cs")), "On a case-sensitive volume, lib/*.CS took Util.cs");
        Assert.False(Matches(At("cs", "lib", "util.cs"), At("cs", "lib", "Util.cs")), "On a case-sensitive volume, lib/util.cs took Util.cs beside it");
        Assert.True(Matches(At("CS", "lib", "*.cs"), At("cs", "lib", "Util.cs")), "The volume's own name, in a folder that ignores case, was told apart by case");
        Assert.True(Matches(At("cs", "**", "*.cs"), At("cs", "a", "B", "Util.cs")), "**/*.cs did not take a file folders down");
    }

    private void A_glob_goes_only_as_deep_as_the_path_it_matches_however_long_it_is()
    {
        string stars = string.Concat(Enumerable.Repeat($"**{Path.DirectorySeparatorChar}", 100_000));
        SmallStack.Run(() =>
        {
            Assert.True(Matches(At("cs", stars + "*.cs"), At("cs", "a", "b", "Util.cs")), "A long run of ** did not take a file folders down");
            Assert.False(Matches(At("cs", stars + "*.vb"), At("cs", "a", "b", "Util.cs")), "A long run of ** took a file its last part does not fit");
        });
    }

    /// <summary>A shared project's items are its .projitems' text, matched against every file of the folder: twelve
    /// <c>**</c> between names could be laid along a path 40 folders deep in more ways than any machine could try.</summary>
    private void A_glob_of_many_double_stars_against_a_deep_path_is_answered_at_once()
    {
        string glob = string.Concat(Enumerable.Repeat($"**{Path.DirectorySeparatorChar}a{Path.DirectorySeparatorChar}", 12));
        string[] deep = [.. Enumerable.Repeat("a", 40)];
        Assert.False(Matches(At("cs", glob + "b.cs"), At(["cs", .. deep, "c.cs"])), "Twelve **/a/ took a file named c.cs for b.cs");
        Assert.True(Matches(At("cs", glob + "b.cs"), At(["cs", .. deep, "b.cs"])), "Twelve **/a/ did not take b.cs 40 folders a deep");
        Assert.False(Matches(At("cs", glob + "b.cs"), At(["cs", .. deep[..11], "b.cs"])), "Twelve **/a/ took b.cs only 11 folders a deep");
    }

    private bool Matches(string pattern, string path) => ItemPaths.Matches(Assert.IsType<ItemPath>(ItemPaths.Read(pattern)), path, CaseIgnoredIn);

    private bool CaseIgnoredIn(string folder)
    {
        asked.Add(folder);
        return !folder.Replace('\\', '/').StartsWith(At("cs").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string At(params string[] names) => Path.Join([Root, .. names]);
}
