using Passenger.Core;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A breakpoint's file matched to a PDB's document that names it through a link to its folder, as the disk names
/// both: the same file only once the disk has it.</summary>
public sealed class SourcePathsOnDiskTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_file_written_after_its_first_match_then_matches_its_document_by_its_canonical_name()
    {
        string folder = Path.Join(root, "real", "App");
        Directory.CreateDirectory(folder);
        Link(() => Directory.CreateSymbolicLink(Path.Join(root, "link"), Path.Join(root, "real")));
        string file = Path.Join(folder, "Generated.cs");
        string document = Path.Join(root, "link", "App", "Generated.cs");
        Assert.True(SourcePaths.Match(file, document) is > 0 and < SourcePaths.Same, "A file not yet written matched as the same file, or not by the names it shares with its document");

        File.WriteAllText(file, "class Generated { }\n");
        Assert.True(SourcePaths.Match(file, document) == SourcePaths.Same,
            "A file written after its first match still matched its document only by the names they share, as a same-named file elsewhere would");
    }

    /// <summary>Makes a link, the test skipped where the system lets this user make none (Windows without Developer Mode).</summary>
    private static void Link(Func<FileSystemInfo> make)
    {
        try
        {
            make();
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows lets this user make no symlink: {refused.Message}");
        }
    }
}
