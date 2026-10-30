using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A file-based program's <c>#:include</c> globs matched against files as MSBuild's globs match them, each name in
/// the letter case its folder tells: here a volume mounted at cs tells case apart, as a case-sensitive APFS volume does,
/// and every other folder ignores it.</summary>
public class IncludeGlobsTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\v" : "/v";

    /// <summary>Each folder asked whether it ignores case, in order.</summary>
    private readonly List<string> asked = [];

    [Fact]
    public void An_include_takes_a_file_named_in_another_case_only_where_its_folder_ignores_case()
    {
        Assert.True(Globbed(At("plain", "lib", "*.cs"), At("plain", "Lib", "Util.cs")), "Where case is ignored, lib/*.cs did not take Lib/Util.cs");
        Assert.True(Globbed(At("plain", "lib", "util.cs"), At("plain", "lib", "Util.cs")), "Where case is ignored, lib/util.cs did not take lib/Util.cs");
        Assert.False(Globbed(At("cs", "lib", "*.cs"), At("cs", "Lib", "Util.cs")), "On a case-sensitive volume, lib/*.cs took Lib/Util.cs, a folder of its own");
        Assert.False(Globbed(At("cs", "lib", "*.cs"), At("cs", "lib", "Util.CS")), "On a case-sensitive volume, lib/*.cs took Util.CS");
        Assert.False(Globbed(At("cs", "lib", "util.cs"), At("cs", "lib", "Util.cs")), "On a case-sensitive volume, lib/util.cs took Util.cs beside it");
        Assert.True(Globbed(At("CS", "lib", "*.cs"), At("cs", "lib", "Util.cs")), "The volume's own name, in a folder that ignores case, was told apart by case");
        Assert.True(Globbed(At("cs", "**", "*.cs"), At("cs", "a", "B", "Util.cs")), "**/*.cs did not take a file folders down");
    }

    [Fact]
    public void A_file_matching_in_its_own_case_or_in_none_asks_no_folder()
    {
        Assert.True(Globbed(At("cs", "lib", "*.cs"), At("cs", "lib", "Util.cs")));
        Assert.False(Globbed(At("cs", "lib", "*.cs"), At("cs", "lib", "Util.vb")));
        Assert.False(Globbed(At("cs", "lib", "*.cs"), At("cs", "src", "Util.cs")));
        Assert.True(asked.Count == 0, $"Names alike, or differing past case, asked {string.Join(", ", asked)}");
    }

    private bool Globbed(string pattern, string path) => WorkspaceModel.Globbed(pattern, path, CaseIgnoredIn);

    private bool CaseIgnoredIn(string folder)
    {
        asked.Add(folder);
        return !folder.Replace('\\', '/').StartsWith(At("cs").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string At(params string[] names) => Path.Join([Root, .. names]);
}
