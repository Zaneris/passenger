using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.UnitTests.Run;

/// <summary>Whether a plan starts a project through its apphost, read from the evaluation's properties as they name a
/// project's output in a temporary folder: the SDK's own runtime identifier's apphost starts, naming the .NET it takes its
/// runtime from by the variable it reads first; another's runs under dotnet, saying why.</summary>
public sealed class AppHostsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_framework_dependent_apphost_for_the_sdks_own_machine_starts_naming_the_net_of_its_processor()
    {
        (AppHost? host, string? why) = AppHosts.Of(Evaluated("Hello", runtime: null, defaultHost: "osx-arm64"), Target("Hello"));
        Assert.True(host == new AppHost(Path.Combine(root, "Hello"), "DOTNET_ROOT_ARM64") && why is null,
            $"The apphost of the SDK's own osx-arm64 read as {host?.ToString() ?? "none"} ({why}), so Run would lose the program's manifest, icon and name to dotnet");
    }

    [Fact]
    public void A_self_contained_apphost_and_one_for_the_portable_identifier_of_a_distributions_sdk_start_too()
    {
        (AppHost? contained, _) = AppHosts.Of(Evaluated("Hello.exe", runtime: "win-x64", defaultHost: null, sdk: "win-x64"), Target("Hello"));
        (AppHost? portable, _) = AppHosts.Of(Evaluated("Hello", runtime: "linux-x64", defaultHost: null, sdk: "fedora.40-x64", portable: "linux-x64"), Target("Hello"));
        Assert.True(contained == new AppHost(Path.Combine(root, "Hello.exe"), "DOTNET_ROOT_X64") && portable == new AppHost(Path.Combine(root, "Hello"), "DOTNET_ROOT_X64"),
            $"An apphost for the SDK's own machine did not start: {contained?.ToString() ?? "none"} for win-x64, {portable?.ToString() ?? "none"} for linux-x64 under a fedora.40-x64 SDK");
    }

    [Fact]
    public void An_apphost_for_another_processor_or_system_leaves_its_assembly_to_dotnet_saying_which_it_is_built_for()
    {
        (AppHost? emulated, string? why) = AppHosts.Of(Evaluated("Hello", runtime: "osx-x64", defaultHost: null), Target("Hello"));
        (AppHost? linux, string? elsewhere) = AppHosts.Of(Evaluated("Hello", runtime: "linux-arm64", defaultHost: null), Target("Hello"));
        (AppHost? x86, string? older) = AppHosts.Of(Evaluated("Hello.exe", runtime: null, defaultHost: "win-x86", sdk: "win-x64"), Target("Hello"));
        Assert.True(emulated is null && linux is null && x86 is null, "An apphost for another machine started, which the .NET Passenger builds with does not run");
        Assert.True(why?.Contains("osx-x64", StringComparison.Ordinal) == true && elsewhere?.Contains("linux-arm64", StringComparison.Ordinal) == true
            && older?.Contains("win-x86", StringComparison.Ordinal) == true, $"The run says nothing of why it is no apphost's: {why} / {elsewhere} / {older}");
    }

    [Fact]
    public void An_assembly_named_with_dots_has_its_apphost_beside_it_and_another_program_or_dotnet_is_none()
    {
        (AppHost? dotted, _) = AppHosts.Of(Evaluated("My.Game", runtime: null, defaultHost: "osx-arm64"), Target("My.Game"));
        Assert.True(dotted is { } host && host.Program == Path.Combine(root, "My.Game"), $"My.Game's apphost read as {dotted?.ToString() ?? "none"}");
        Assert.True(AppHosts.Is(Path.Combine(root, "My.Game.exe"), Target("My.Game")), "My.Game.exe was not read as My.Game.dll's apphost on Windows");
        Assert.False(AppHosts.Is(Path.Combine(root, "My"), Target("My.Game")), "A program named as the assembly's first word was read as its apphost");
        Assert.False(AppHosts.Is(Path.Combine(root, "elsewhere", "My.Game"), Target("My.Game")), "A program of another folder was read as the assembly's apphost");
        Dictionary<string, string> underDotnet = Evaluated("My.Game", runtime: null, defaultHost: "osx-arm64");
        underDotnet[MSBuildNames.RunCommand] = "dotnet";
        Assert.Equal((null, null), AppHosts.Of(underDotnet, Target("My.Game")));
    }

    [Theory]
    [InlineData("osx-arm64", "v11.0", "DOTNET_ROOT_ARM64")]
    [InlineData("linux-musl-x64", "v8.0", "DOTNET_ROOT_X64")]
    [InlineData("win-x86", "v6.0", "DOTNET_ROOT_X86")]
    [InlineData("osx-x64", "v5.0", RuntimeVariables.Root)]
    [InlineData("win-x86", "v3.1", RuntimeVariables.RootX86)]
    [InlineData("linux-arm", "v3.1", RuntimeVariables.Root)]
    public void The_variable_an_apphost_reads_first_is_its_processors_from_net_6_and_the_shared_one_before(string runtime, string framework, string variable) =>
        Assert.Equal(variable, AppHosts.RootVariable(runtime, framework));

    /// <summary>An evaluation of a project building into the test's folder, its RunCommand <paramref name="command"/> there,
    /// for <paramref name="runtime"/> or of none (its apphost for <paramref name="defaultHost"/>), under an SDK of
    /// <paramref name="sdk"/> (<paramref name="portable"/> its portable identifier where another).</summary>
    private Dictionary<string, string> Evaluated(string command, string? runtime, string? defaultHost, string sdk = "osx-arm64", string? portable = null)
    {
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase)
        {
            [MSBuildNames.RunCommand] = Path.Combine(root, command),
            [MSBuildNames.NETCoreSdkRuntimeIdentifier] = sdk,
            [MSBuildNames.NETCoreSdkPortableRuntimeIdentifier] = portable ?? sdk,
            [MSBuildNames.TargetFrameworkVersion] = "v11.0",
        };
        if (runtime is not null)
        {
            properties[MSBuildNames.RuntimeIdentifier] = runtime;
        }
        if (defaultHost is not null)
        {
            properties[MSBuildNames.DefaultAppHostRuntimeIdentifier] = defaultHost;
        }
        return properties;
    }

    private string Target(string assembly) => Path.Combine(root, assembly + ".dll");
}
