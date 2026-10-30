using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A breakpoint's file matched to a PDB's document that names it through a link to its folder, as the disk names
/// both: the same file only once the disk has it, by a module's symbols as by the paths alone.</summary>
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

    /// <summary>A module whose symbols name its one file through a link renaming the file's folder matches no breakpoint of
    /// the file while it is not written, and binds one once it is: what it worked out without the file is not kept.</summary>
    [Fact]
    public void A_file_a_module_first_missed_binds_in_that_module_once_it_is_written()
    {
        string folder = Path.Join(root, "real", "App");
        Directory.CreateDirectory(folder);
        Link(() => Directory.CreateSymbolicLink(Path.Join(root, "link"), folder));
        string file = Path.Join(folder, "Generated.cs");
        const string Code = "public static class Generated\n{\n    public static int One() => 1;\n}\n";
        using ModuleMetadataReader module = Built(Path.Join(root, "link", "Generated.cs"), Code);
        Assert.True((module.DocumentMatch(file), module.ResolveBreakpoint(file, 3)) == (0, null),
            "Before the file was written, the module matched it to a document sharing no more than its file name with it");

        File.WriteAllText(file, Code);
        Assert.True(module.DocumentMatch(file) == SourcePaths.Same,
            $"A file written after the module first missed it still matched its document {module.DocumentMatch(file)}, as when the file was not there");
        Assert.True(module.ResolveBreakpoint(file, 3) is not null, "A breakpoint on the file's code, written after the module first missed it, binds nowhere in it");
    }

    /// <summary>A library compiled from one file's code, its symbols embedded, naming the file <paramref name="document"/>.</summary>
    private static ModuleMetadataReader Built(string document, string code)
    {
        CSharpCompilation compilation = CSharpCompilation.Create("Linked", [CSharpSyntaxTree.ParseText(SourceText.From(code, Encoding.UTF8), path: document)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream image = new();
        EmitResult emitted = compilation.Emit(image, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        Assert.True(emitted.Success, string.Join('\n', emitted.Diagnostics));
        ModuleMetadataReader module = ModuleMetadataReader.TryLoadFromBytes(image.ToArray()) ?? throw new InvalidOperationException("The library built did not read");
        Assert.True(module.HasSymbols, "The library built was read without the symbols embedded in it");
        return module;
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
