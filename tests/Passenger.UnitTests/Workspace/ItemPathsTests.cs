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

    [Theory]
    [InlineData(@"..\semi;colon\App.csproj", @"..\semi%3Bcolon\App.csproj")]
    [InlineData("per%cent/a.cs", "per%25cent/a.cs")]
    [InlineData("per%41cent/a.cs", "per%2541cent/a.cs")]
    [InlineData("star*x/a.cs", "star%2Ax/a.cs")]
    [InlineData("what?/a.cs", "what%3F/a.cs")]
    [InlineData("$(x)/a.cs", "%24(x)/a.cs")]
    [InlineData("@(x)/a.cs", "%40(x)/a.cs")]
    [InlineData("%(x)/a.cs", "%25(x)/a.cs")]
    [InlineData("Bob's/a.cs", "Bob's/a.cs")]
    [InlineData("plain (copy)/a.cs", "plain (copy)/a.cs")]
    public void A_name_of_any_legal_characters_is_written_so_msbuild_reads_it_back(string path, string written)
    {
        Assert.Equal(written, ItemPaths.Named(path));
        Assert.True(ItemPaths.Read(written) is { Glob: false } read && read.Path == path, $"{written} did not read back as {path}, so the project names another file");
    }

    /// <summary>Each of a glob's characters read twice, its '%' is written twice over; its wildcards stay, as one escaped
    /// would make the glob fit no file at all.</summary>
    [Theory]
    [InlineData("semi;colon/script_templates", "semi%3Bcolon/script_templates/**")]
    [InlineData("per%cent/script_templates", "per%2525cent/script_templates/**")]
    [InlineData("per%41cent/script_templates", "per%252541cent/script_templates/**")]
    [InlineData("$(x)/script_templates", "%24(x)/script_templates/**")]
    [InlineData("@(x)/script_templates", "%40(x)/script_templates/**")]
    [InlineData("%(x)/script_templates", "%2525(x)/script_templates/**")]
    [InlineData("Bob's/script_templates", "Bob's/script_templates/**")]
    [InlineData("star*x/script_templates", "star*x/script_templates/**")]
    [InlineData("what?/script_templates", "what?/script_templates/**")]
    public void What_lies_under_a_folder_of_any_legal_characters_is_written_as_a_glob_msbuild_reads(string folder, string written)
    {
        Assert.Equal(written, ItemPaths.Under(folder));
    }

    [Theory]
    [InlineData("semi;colon")]
    [InlineData("per%cent")]
    [InlineData("per%41cent")]
    [InlineData("$(x)")]
    [InlineData("@(x)")]
    [InlineData("%(x)")]
    public void A_glob_written_for_a_folder_reads_back_as_that_folder_and_its_wildcards(string folder)
    {
        ItemPath read = Assert.IsType<ItemPath>(ItemPaths.Read(ItemPaths.Under($"{folder}/script_templates")));

        Assert.Equal($"{folder}/script_templates", read.Path);
        Assert.Equal("/**", read.Rest);
        Assert.Equal(ItemPaths.Under("moved/per%cent"), read.At("moved/per%cent"));
    }

    /// <summary>A single quote closes a condition's string, and nothing in an item's value, which reads it as it is.</summary>
    [Fact]
    public void A_quote_is_escaped_only_between_a_conditions_quotes()
    {
        Assert.Equal("Bob%27s/a.props", ItemPaths.InCondition(ItemPaths.Named("Bob's/a.props")));
        Assert.Equal("Bob's/a.props", ItemPaths.Read("Bob%27s/a.props")?.Path);
    }

    /// <summary>An Exclude and an Import read a glob once, so what follows one writes its '%' once.</summary>
    [Fact]
    public void A_glob_read_once_takes_one_escape_of_its_percent()
    {
        ItemPath read = Assert.IsType<ItemPath>(ItemPaths.Read("per%25cent/*.targets", once: true));

        Assert.Equal("per%cent", read.Path);
        Assert.Equal("was%25here/*.targets", read.At("was%here"));
    }

    [Theory]
    [InlineData("a.cs;b.cs")]
    [InlineData("$(Folder)/a.cs")]
    [InlineData("@(Compile)")]
    [InlineData("%(Identity)")]
    [InlineData("lib/$(Configuration)/*.dll")]
    public void A_list_or_what_msbuild_expands_says_no_one_path(string written)
    {
        Assert.Null(ItemPaths.Read(written));
    }

    /// <summary>A '$', '@' or '%' opening no expression is the character itself, in a name a person wrote by hand.</summary>
    [Theory]
    [InlineData("cost$/a.cs")]
    [InlineData("me@work/a.cs")]
    [InlineData("100%/a.cs")]
    public void A_character_opening_no_expression_is_part_of_the_name(string written)
    {
        Assert.Equal(written, ItemPaths.Read(written)?.Path);
    }

    [Fact]
    public void A_glob_with_an_escaped_wildcard_is_a_plain_name_as_msbuild_reads_it()
    {
        ItemPath read = Assert.IsType<ItemPath>(ItemPaths.Read("star%2Ax/**"));

        Assert.False(read.Glob, "A value MSBuild takes for one file was read as a glob");
        Assert.Equal("star*x/**", read.Path);
    }

    [Fact]
    public void A_folders_last_slash_stays_where_it_was_written()
    {
        ItemPath read = Assert.IsType<ItemPath>(ItemPaths.Read(@"old%3Bone\"));

        Assert.Equal("old;one", read.Path);
        Assert.Equal(@"new%3Btwo\", read.At("new;two"));
    }

    [Theory]
    [InlineData("per%25cent/a%3Bb.png", "per%cent/a;b.png")]
    [InlineData("per%2525cent/*%253Bb.png", "per%cent/*;b.png")]
    [InlineData("**/*.png", "**/*.png")]
    public void All_a_value_says_is_read_as_msbuild_reads_it_its_wildcards_kept(string written, string whole)
    {
        Assert.Equal(whole, ItemPaths.Read(written)?.Whole);
    }

    [Fact]
    public void A_value_takes_a_file_named_in_another_case_only_where_its_folder_ignores_case()
    {
        Assert.True(Matches(At("plain", "lib", "*.cs"), At("plain", "Lib", "Util.cs")), "Where case is ignored, lib/*.cs did not take Lib/Util.cs");
        Assert.True(Matches(At("plain", "lib", "util.cs"), At("plain", "lib", "Util.cs")), "Where case is ignored, lib/util.cs did not take lib/Util.cs");
        Assert.False(Matches(At("cs", "lib", "*.cs"), At("cs", "Lib", "Util.cs")), "On a case-sensitive volume, lib/*.cs took Lib/Util.cs, a folder of its own");
        Assert.False(Matches(At("cs", "lib", "*.CS"), At("cs", "lib", "Util.cs")), "On a case-sensitive volume, lib/*.CS took Util.cs");
        Assert.False(Matches(At("cs", "lib", "util.cs"), At("cs", "lib", "Util.cs")), "On a case-sensitive volume, lib/util.cs took Util.cs beside it");
        Assert.True(Matches(At("CS", "lib", "*.cs"), At("cs", "lib", "Util.cs")), "The volume's own name, in a folder that ignores case, was told apart by case");
        Assert.True(Matches(At("cs", "**", "*.cs"), At("cs", "a", "B", "Util.cs")), "**/*.cs did not take a file folders down");
    }

    [Fact]
    public void A_glob_goes_only_as_deep_as_the_path_it_matches_however_long_it_is()
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
    [Fact]
    public void A_glob_of_many_double_stars_against_a_deep_path_is_answered_at_once()
    {
        string glob = string.Concat(Enumerable.Repeat($"**{Path.DirectorySeparatorChar}a{Path.DirectorySeparatorChar}", 12));
        string[] deep = [.. Enumerable.Repeat("a", 40)];
        Assert.False(Matches(At("cs", glob + "b.cs"), At(["cs", .. deep, "c.cs"])), "Twelve **/a/ took a file named c.cs for b.cs");
        Assert.True(Matches(At("cs", glob + "b.cs"), At(["cs", .. deep, "b.cs"])), "Twelve **/a/ did not take b.cs 40 folders a deep");
        Assert.False(Matches(At("cs", glob + "b.cs"), At(["cs", .. deep[..11], "b.cs"])), "Twelve **/a/ took b.cs only 11 folders a deep");
    }

    [Fact]
    public void A_file_matching_in_its_own_case_or_in_none_asks_no_folder()
    {
        Assert.True(Matches(At("cs", "lib", "*.cs"), At("cs", "lib", "Util.cs")));
        Assert.False(Matches(At("cs", "lib", "*.cs"), At("cs", "lib", "Util.vb")));
        Assert.False(Matches(At("cs", "lib", "*.cs"), At("cs", "src", "Util.cs")));
        Assert.True(asked.Count == 0, $"Names alike, or differing past case, asked {string.Join(", ", asked)}");
    }

    [Theory]
    [InlineData("per%2525cent/*%253Bb.png", "per%cent/a;b.png")]
    [InlineData("per%25cent/a%3Bb.png", "per%cent/a;b.png")]
    [InlineData("per%2525cent/**", "per%cent/x/a;b.png")]
    public void A_value_takes_the_files_msbuild_reads_it_to_name(string written, string file)
    {
        ItemPath read = Assert.IsType<ItemPath>(ItemPaths.Read(written));

        Assert.True(ItemPaths.Matches(read.FromFolder(Root), Path.Join(Root, file), CaseIgnoredIn), $"{written} did not take {file}");
    }

    private bool Matches(string pattern, string path) => ItemPaths.Matches(Assert.IsType<ItemPath>(ItemPaths.Read(pattern)), path, CaseIgnoredIn);

    private bool CaseIgnoredIn(string folder)
    {
        asked.Add(folder);
        return !folder.Replace('\\', '/').StartsWith(At("cs").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string At(params string[] names) => Path.Join([Root, .. names]);
}
