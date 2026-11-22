using Passenger.Core;

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
    public void A_breakpoints_file_binds_in_a_PDBs_document_as_its_folder_tells_letter_case() => Cases.All(
        ("a breakpoint in Foo.cs binds in Foo.cs and never in foo.cs beside it where the folder tells case", () =>
        {
            On(At("cs", "App", "Foo.cs"), At("cs", "App", "foo.cs"));
                Assert.True(Match(At("cs", "App", "Foo.cs"), At("cs", "App", "foo.cs")) == 0, "A breakpoint in Foo.cs bound in foo.cs beside it on a case-sensitive volume");
            Assert.True(Match(At("cs", "App", "Foo.cs"), "/_/App/foo.cs") == 0, "A breakpoint in Foo.cs bound in a deterministic build's foo.cs while foo.cs is beside it");
            Assert.True(Match(At("cs", "App", "Foo.cs"), "/_/App/Foo.cs") >= 2, "A breakpoint in Foo.cs did not bind in a deterministic build's Foo.cs");
        }),
        ("a PDB naming the file in another case binds where its folder ignores case or holds only the one file", () =>
        {
            On(At("plain", "App", "Foo.cs"), At("cs", "App", "Foo.cs"));
            Assert.Equal(SourcePaths.Same, Match(At("plain", "App", "Foo.cs"), At("plain", "App", "foo.cs")));
            Assert.True(Match(At("cs", "App", "Foo.cs"), At("cs", "App", "foo.cs")) > 0, "A PDB built where case was ignored, naming foo.cs, took no breakpoint in Foo.cs, the only one there");
            Assert.True(Match(At("CS", "App", "Foo.cs"), At("cs", "App", "Foo.cs")) == SourcePaths.Same, "The volume's own name, in a folder that ignores case, was told apart by case");
        }),
        ("a Windows machine's path ignores case wherever it is read", () =>
        {
            On();
            Assert.Equal(SourcePaths.Same, Match(@"C:\src\App\Foo.cs", "c:/src/app/foo.cs"));
        }),
        ("an image's document names back the one file a breakpoint in it binds to and none where two end alike", () =>
        {
            string api = At("plain", "Api", "Controllers", "Orders.cs");
            string old = At("plain", "old", "Controllers", "Orders.cs");
            On(api, old);
            Assert.Equal(api, FileOf("/src/Api/Controllers/Orders.cs", api, old));
            Assert.Equal(api, FileOf("/_/Api/Controllers/Orders.cs", api, old));
            Assert.True(FileOf("/src/Web/Controllers/Orders.cs", api, old) is null, "Orders.cs of a folder neither file is in was named back as one of them");
        }));

    private int Match(string requested, string document) => SourcePaths.Match(requested, document, paths, path => there.Contains(path));

    private string? FileOf(string document, params string[] files) => SourcePaths.FileOf(document, files, paths, path => there.Contains(path));

    /// <summary>The disk holding these files alone, as each check finds it.</summary>
    private void On(params string[] files)
    {
        there.Clear();
        foreach (string file in files)
        {
            there.Add(Slashed(file));
        }
    }

    private static string At(params string[] names) => Path.Join([Root, .. names]);

    private static string Slashed(string path) => path.Replace('\\', '/').TrimEnd('/');
}
