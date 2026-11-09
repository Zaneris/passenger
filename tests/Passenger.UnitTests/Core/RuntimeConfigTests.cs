using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A build's runtimeconfig.json, read one way by the XAML previews, Hot Reload, the Live Visual Tree, a run's
/// desktop app and the SDKs found: the framework it was built for in the letter case its project wrote, the shared
/// frameworks it runs on, and its switches written as JSON's or as text.</summary>
public sealed class RuntimeConfigTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_wpf_apps_configuration_written_with_comments_reads_its_framework_as_dotnet_8_its_desktop_framework_and_its_switches()
    {
        string assembly = Path.Combine(root, "App.dll");
        File.WriteAllText(Path.ChangeExtension(assembly, ".runtimeconfig.json"), """
            {
              // The SDK writes the framework as the project names it.
              "runtimeOptions": {
                "tfm": "NET8.0-windows",
                "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "8.0.0" },
                  { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.0" },
                ],
                "configProperties": { "System.StartupHookProvider.IsSupported": "False", "System.GC.Server": true, "Count": 3 }
              }
            }
            """);

        RuntimeConfig? read = RuntimeConfig.Read(assembly);

        Assert.True(read is { TargetFramework: "NET8.0-windows", OnWindowsDesktop: true }, $"The configuration reads as {read}");
        Assert.True(FrameworkNames.Major(read.TargetFramework) == 8, $"{read.TargetFramework} reads as .NET {FrameworkNames.Major(read.TargetFramework)}, so a preview of it runs as for a .NET before 8");
        Assert.Equal("8.0.0", read.VersionOf("microsoft.netcore.app"));
        Assert.False(read.Switch("System.StartupHookProvider.IsSupported"), "A switch written as the text False is not read as off");
        Assert.True(read.Switch("System.GC.Server"), "A switch written as JSON's true is not read as on");
        Assert.Null(read.Switch("Count"));
        Assert.Null(read.Switch("System.Globalization.Invariant"));
    }

    [Fact]
    public void A_self_contained_build_runs_on_the_frameworks_it_carries_and_one_with_no_configuration_reads_as_none()
    {
        string assembly = Path.Combine(root, "Tool");
        File.WriteAllText(Path.ChangeExtension(assembly, ".runtimeconfig.json"),
            """{ "runtimeOptions": { "tfm": "net10.0", "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.1" } ] } }""");

        Assert.Equal("10.0.1", RuntimeConfig.Read(assembly)?.VersionOf("Microsoft.NETCore.App"));
        Assert.Null(RuntimeConfig.Read(Path.Combine(root, "Library.dll")));
    }
}
