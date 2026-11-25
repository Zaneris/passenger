using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.UnitTests.Run;

/// <summary>A 32-bit x86 program as Windows runs it: known by its PlatformTarget, a built one by its header, run on the x86
/// .NET an x86 apphost finds (its own variables, then the IDE's arch-specific ones, then the registered and the default
/// folders), and what that .NET lacks of the frameworks its build names, by the installer that brings each.</summary>
public sealed class X86ProgramsTests
{
    private static readonly Dictionary<string, string> None = [];

    [Fact]
    public void A_32_bit_program_runs_on_an_x86_dotnet_and_names_the_runtime_it_lacks() => Cases.All(
        ("a program runs as 32 bit x86 by its platform target", () => Cases.Each<(string, bool)>(
        [
            ("X86", true),
            ("AnyCPU", false),
        ], row => A_program_runs_as_32_bit_x86_by_its_platform_target(row.Item1, row.Item2))),
        ("a built assembly loads only into a 32 bit process where its header requires 32 bits without preferring them", () => Cases.Each<(CorFlags?, bool)>(
        [
            (CorFlags.ILOnly | CorFlags.Requires32Bit, true),
            (CorFlags.ILOnly, false),
            (CorFlags.ILOnly | CorFlags.Requires32Bit | CorFlags.Prefers32Bit, false),
            (null, false),
        ], row => A_built_assembly_loads_only_into_a_32_bit_process_where_its_header_requires_32_bits(row.Item1, row.Item2))),
        ("the x86 net is where the x86 apphost looks first its own variables over the ides", The_x86_net_is_where_the_x86_apphost_looks_first_its_own_variables_over_the_ides),
        ("without a variable for x86 the ides dotnet root is passed over for the registered then the default folder", Without_a_variable_for_x86_the_ides_dotnet_root_is_passed_over_for_the_registered_then_the_default_folder),
        ("a wpf app lacking the desktop runtime names it alone as it carries nets own", A_wpf_app_lacking_the_desktop_runtime_names_it_alone_as_it_carries_nets_own),
        ("a later major serves only where the program rolls forward by major", A_later_major_serves_only_where_the_program_rolls_forward_by_major));

    private void A_program_runs_as_32_bit_x86_by_its_platform_target(string platform, bool x86) =>
        Assert.Equal(x86, X86Programs.Builds(new Dictionary<string, string> { [MSBuildNames.PlatformTarget] = platform }));

    private void A_built_assembly_loads_only_into_a_32_bit_process_where_its_header_requires_32_bits(CorFlags? flags, bool x86)
    {
        using MemoryStream assembly = flags is { } built ? Built(built) : new MemoryStream("not an assembly"u8.ToArray());
        Assert.True(X86Programs.Requires(assembly) == x86,
            $"An assembly whose header says {flags?.ToString() ?? "nothing, being none"} was {(x86 ? "not " : string.Empty)}taken for an x86 build, so its preview "
            + (x86 ? "would go to a 64-bit .NET, which cannot load it" : "would go to the x86 .NET, which it does not need"));
    }

    /// <summary>A library of nothing but its module, its CLI header's flags as given.</summary>
    private static MemoryStream Built(CorFlags flags)
    {
        MetadataBuilder metadata = new();
        metadata.AddModule(0, metadata.GetOrAddString("Plate.dll"), metadata.GetOrAddGuid(Guid.Empty), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("Plate"), new Version(1, 0), default, default, default, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        BlobBuilder image = new();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder(), flags: flags).Serialize(image);
        MemoryStream stream = new();
        image.WriteContentTo(stream);
        stream.Position = 0;
        return stream;
    }

    private void The_x86_net_is_where_the_x86_apphost_looks_first_its_own_variables_over_the_ides()
    {
        Dictionary<string, string> inherited = new() { ["DOTNET_ROOT_X86"] = @"D:\ide-x86", [RuntimeVariables.Root] = @"C:\Program Files\dotnet" };
        Assert.Equal(@"E:\own", Root(new() { ["dotnet_root_x86"] = @"E:\own" }, inherited));
        Assert.Equal(@"D:\ide-x86", Root(new() { [RuntimeVariables.RootX86] = @"E:\older" }, inherited));
        Assert.Equal(@"E:\older", Root(new() { [RuntimeVariables.RootX86] = @"E:\older" }, []));
        Assert.Equal(@"E:\shared", Root(new() { [RuntimeVariables.Root] = @"E:\shared" }, new() { [RuntimeVariables.Root] = @"C:\Program Files\dotnet" }));
    }

    private void Without_a_variable_for_x86_the_ides_dotnet_root_is_passed_over_for_the_registered_then_the_default_folder()
    {
        Dictionary<string, string> inherited = new() { [RuntimeVariables.Root] = @"C:\Program Files\dotnet" };
        Assert.Equal(@"F:\registered", X86Programs.Root(None, inherited.GetValueOrDefault, () => @"F:\registered", @"C:\Program Files (x86)"));
        Assert.Equal(Path.Combine(@"C:\Program Files (x86)", "dotnet"), X86Programs.Root(None, inherited.GetValueOrDefault, () => null, @"C:\Program Files (x86)"));
    }

    private void A_wpf_app_lacking_the_desktop_runtime_names_it_alone_as_it_carries_nets_own()
    {
        RuntimeConfig wpf = Config(("Microsoft.NETCore.App", "8.0.0"), (FrameworkNames.WindowsDesktop, "8.0.0"));
        Assert.Equal([(".NET Desktop Runtime", "8.0")], Lacking(wpf, [], null));
        Assert.Equal([(".NET Desktop Runtime", "8.0")], Lacking(wpf, [("Microsoft.NETCore.App", "8.0.11")], null));
        Assert.Empty(Lacking(wpf, [("Microsoft.NETCore.App", "8.0.11"), (FrameworkNames.WindowsDesktop, "8.0.11")], null));
    }

    private void A_later_major_serves_only_where_the_program_rolls_forward_by_major()
    {
        RuntimeConfig console = Config(("Microsoft.NETCore.App", "8.0.0"));
        Assert.Equal([(".NET Runtime", "8.0")], Lacking(console, [("Microsoft.NETCore.App", "10.0.1")], null));
        Assert.Empty(Lacking(console, [("Microsoft.NETCore.App", "10.0.1")], "LatestMajor"));
        Assert.Equal([(".NET Runtime", "8.0")], Lacking(console, [("Microsoft.NETCore.App", "7.0.20")], "Major"));
    }

    private static string Root(Dictionary<string, string> given, Dictionary<string, string> inherited) =>
        X86Programs.Root(given, inherited.GetValueOrDefault, () => @"F:\registered", @"C:\Program Files (x86)");

    private static RuntimeConfig Config(params (string Name, string? Version)[] frameworks) => new("net8.0", frameworks, new Dictionary<string, string>());

    private static IReadOnlyList<(string Installer, string Version)> Lacking(RuntimeConfig config, (string Name, string Version)[] installed, string? rollForward) =>
        X86Programs.Installers(X86Programs.Lacking(config, name => [.. installed.Where(i => i.Name == name).Select(i => i.Version)], rollForward));
}
