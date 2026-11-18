using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.UnitTests.Run;

/// <summary>A 32-bit x86 program as Windows runs it: known by its PlatformTarget, run on the x86 .NET an x86 apphost finds
/// (its own variables, then the IDE's arch-specific ones, then the registered and the default folders), and what that .NET
/// lacks of the frameworks its build names, by the installer that brings each.</summary>
public sealed class X86ProgramsTests
{
    private static readonly Dictionary<string, string> None = [];

    [Theory]
    [InlineData("x86", true)]
    [InlineData("X86", true)]
    [InlineData("AnyCPU", false)]
    [InlineData("x64", false)]
    public void A_program_runs_as_32_bit_x86_by_its_platform_target(string platform, bool x86) =>
        Assert.Equal(x86, X86Programs.Builds(new Dictionary<string, string> { [MSBuildNames.PlatformTarget] = platform }));

    [Fact]
    public void The_x86_net_is_where_the_x86_apphost_looks_first_its_own_variables_over_the_ides()
    {
        Dictionary<string, string> inherited = new() { ["DOTNET_ROOT_X86"] = @"D:\ide-x86", [RuntimeVariables.Root] = @"C:\Program Files\dotnet" };
        Assert.Equal(@"E:\own", Root(new() { ["dotnet_root_x86"] = @"E:\own" }, inherited));
        Assert.Equal(@"D:\ide-x86", Root(new() { [RuntimeVariables.RootX86] = @"E:\older" }, inherited));
        Assert.Equal(@"E:\older", Root(new() { [RuntimeVariables.RootX86] = @"E:\older" }, []));
        Assert.Equal(@"E:\shared", Root(new() { [RuntimeVariables.Root] = @"E:\shared" }, new() { [RuntimeVariables.Root] = @"C:\Program Files\dotnet" }));
    }

    [Fact]
    public void Without_a_variable_for_x86_the_ides_dotnet_root_is_passed_over_for_the_registered_then_the_default_folder()
    {
        Dictionary<string, string> inherited = new() { [RuntimeVariables.Root] = @"C:\Program Files\dotnet" };
        Assert.Equal(@"F:\registered", X86Programs.Root(None, inherited.GetValueOrDefault, () => @"F:\registered", @"C:\Program Files (x86)"));
        Assert.Equal(Path.Combine(@"C:\Program Files (x86)", "dotnet"), X86Programs.Root(None, inherited.GetValueOrDefault, () => null, @"C:\Program Files (x86)"));
    }

    [Fact]
    public void A_wpf_app_lacking_the_desktop_runtime_names_it_alone_as_it_carries_nets_own()
    {
        RuntimeConfig wpf = Config(("Microsoft.NETCore.App", "8.0.0"), (FrameworkNames.WindowsDesktop, "8.0.0"));
        Assert.Equal([(".NET Desktop Runtime", "8.0")], Lacking(wpf, [], null));
        Assert.Equal([(".NET Desktop Runtime", "8.0")], Lacking(wpf, [("Microsoft.NETCore.App", "8.0.11")], null));
        Assert.Empty(Lacking(wpf, [("Microsoft.NETCore.App", "8.0.11"), (FrameworkNames.WindowsDesktop, "8.0.11")], null));
    }

    [Fact]
    public void A_later_major_serves_only_where_the_program_rolls_forward_by_major()
    {
        RuntimeConfig console = Config(("Microsoft.NETCore.App", "8.0.0"));
        Assert.Equal([(".NET Runtime", "8.0")], Lacking(console, [("Microsoft.NETCore.App", "10.0.1")], null));
        Assert.Empty(Lacking(console, [("Microsoft.NETCore.App", "10.0.1")], "LatestMajor"));
        Assert.Equal([(".NET Runtime", "8.0")], Lacking(console, [("Microsoft.NETCore.App", "7.0.20")], "Major"));
    }

    [Fact]
    public void A_web_app_lacking_both_runtimes_names_each_installer()
    {
        RuntimeConfig web = Config(("Microsoft.NETCore.App", "9.0.0"), ("Microsoft.AspNetCore.App", "9.0.0"));
        Assert.Equal([(".NET Runtime", "9.0"), ("ASP.NET Core Runtime", "9.0")], Lacking(web, [], null));
    }

    private static string Root(Dictionary<string, string> given, Dictionary<string, string> inherited) =>
        X86Programs.Root(given, inherited.GetValueOrDefault, () => @"F:\registered", @"C:\Program Files (x86)");

    private static RuntimeConfig Config(params (string Name, string? Version)[] frameworks) => new("net8.0", frameworks, new Dictionary<string, string>());

    private static IReadOnlyList<(string Installer, string Version)> Lacking(RuntimeConfig config, (string Name, string Version)[] installed, string? rollForward) =>
        X86Programs.Installers(X86Programs.Lacking(config, name => [.. installed.Where(i => i.Name == name).Select(i => i.Version)], rollForward));
}
