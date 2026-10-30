using Passenger.Core;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A breakpoint's file matched to the documents a PDB names, compared as their folders tell letter case: here a
/// volume mounted at cs tells case apart, as a case-sensitive APFS volume does, every other folder ignores it, and the disk
/// holds only the files a test puts in <see cref="there"/>.</summary>
public class SourcePathsTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\v" : "/v";

    private readonly PathComparer paths = new(folder => !Slashed(folder).StartsWith(Slashed(At("cs")), StringComparison.OrdinalIgnoreCase));

    /// <summary>The files on the disk, with '/' between their names.</summary>
    private readonly HashSet<string> there = new(StringComparer.Ordinal);

    [Fact]
    public void A_breakpoint_in_Foo_cs_binds_in_Foo_cs_and_never_in_foo_cs_beside_it_where_the_folder_tells_case()
    {
        On(At("cs", "App", "Foo.cs"), At("cs", "App", "foo.cs"));
        Assert.Equal(SourcePaths.Same, Match(At("cs", "App", "Foo.cs"), At("cs", "App", "Foo.cs")));
        Assert.True(Match(At("cs", "App", "Foo.cs"), At("cs", "App", "foo.cs")) == 0, "A breakpoint in Foo.cs bound in foo.cs beside it on a case-sensitive volume");
        Assert.True(Match(At("cs", "App", "Foo.cs"), "/_/App/foo.cs") == 0, "A breakpoint in Foo.cs bound in a deterministic build's foo.cs while foo.cs is beside it");
        Assert.True(Match(At("cs", "App", "Foo.cs"), "/_/App/Foo.cs") >= 2, "A breakpoint in Foo.cs did not bind in a deterministic build's Foo.cs");
    }

    [Fact]
    public void A_PDB_naming_the_file_in_another_case_binds_where_its_folder_ignores_case_or_holds_only_the_one_file()
    {
        On(At("plain", "App", "Foo.cs"), At("cs", "App", "Foo.cs"));
        Assert.Equal(SourcePaths.Same, Match(At("plain", "App", "Foo.cs"), At("plain", "App", "foo.cs")));
        Assert.True(Match(At("cs", "App", "Foo.cs"), At("cs", "App", "foo.cs")) > 0, "A PDB built where case was ignored, naming foo.cs, took no breakpoint in Foo.cs, the only one there");
        Assert.True(Match(At("cs", "App", "Foo.cs"), "/_/App/foo.cs") >= 2, "A deterministic build's foo.cs took no breakpoint in Foo.cs, the only one there");
        Assert.True(Match(At("CS", "App", "Foo.cs"), At("cs", "App", "Foo.cs")) == SourcePaths.Same, "The volume's own name, in a folder that ignores case, was told apart by case");
    }

    [Fact]
    public void A_Windows_machines_path_ignores_case_wherever_it_is_read()
    {
        Assert.Equal(SourcePaths.Same, Match(@"C:\src\App\Foo.cs", "c:/src/app/foo.cs"));
    }

    private int Match(string requested, string document) => SourcePaths.Match(requested, document, paths, path => there.Contains(path));

    private void On(params string[] files)
    {
        foreach (string file in files)
        {
            there.Add(Slashed(file));
        }
    }

    private static string At(params string[] names) => Path.Join([Root, .. names]);

    private static string Slashed(string path) => path.Replace('\\', '/').TrimEnd('/');
}
