using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A build's runtimeconfig.json, read one way by the XAML previews, Hot Reload, the Live Visual Tree, a run's
/// desktop app and the SDKs found: the framework it was built for in the letter case its project wrote, the shared
/// frameworks it runs on, its switches written as JSON's or as text, and each property as .NET's host gives it to the runtime,
/// which Passenger's helper host gives a helper.</summary>
public sealed class RuntimeConfigTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_programs_runtime_configuration_reads_as_the_host_reads_it() => Cases.AllApart<RuntimeConfigTests>(
        ("a wpf apps configuration written with comments reads its framework as dotnet 8 its desktop framework and its switches", test => test.A_wpf_apps_configuration_written_with_comments_reads_its_framework_as_dotnet_8_its_desktop_framework_and_its_switches()),
        ("each property reads as the host gives it an object or a list as its json written whole on one line", test => test.Each_property_reads_as_the_host_gives_it_an_object_or_a_list_as_its_json_written_whole_on_one_line()),
        ("a self contained build runs on the frameworks it carries and one with no configuration reads as none", test => test.A_self_contained_build_runs_on_the_frameworks_it_carries_and_one_with_no_configuration_reads_as_none()));

    private void A_wpf_apps_configuration_written_with_comments_reads_its_framework_as_dotnet_8_its_desktop_framework_and_its_switches()
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

    /// <summary>What .NET 10's host gave a program for each, read back through AppContext.GetData.</summary>
    private void Each_property_reads_as_the_host_gives_it_an_object_or_a_list_as_its_json_written_whole_on_one_line()
    {
        string assembly = Path.Combine(root, "Helper.dll");
        File.WriteAllText(Path.ChangeExtension(assembly, ".runtimeconfig.json"), """
            { "runtimeOptions": { "configProperties": {
                "Text": "caf\u00e9 \"q\"", "On": true, "Off": false, "Count": 3,
                "Thing": { "a" : "é \"x\"",
                           "b": [ 3 , null ] },
                "Nothing": null
            } } }
            """);

        IReadOnlyDictionary<string, string>? read = RuntimeConfig.Read(assembly)?.Properties;

        Dictionary<string, string> given = new(StringComparer.Ordinal)
        {
            ["Text"] = "café \"q\"", ["On"] = "true", ["Off"] = "false", ["Count"] = "3", ["Thing"] = """{"a":"é \"x\"","b":[3,null]}""", ["Nothing"] = "null",
        };
        Assert.True(read is not null && read.Count == given.Count && given.All(p => read.TryGetValue(p.Key, out string? value) && value == p.Value),
            $"The properties read as {(read is null ? "nothing" : string.Join(", ", read.Select(p => $"{p.Key}={p.Value}")))}, where the host gives {string.Join(", ", given.Select(p => $"{p.Key}={p.Value}"))}: "
            + "a helper Passenger runs is given its configuration otherwise than its own host would give it");
    }

    private void A_self_contained_build_runs_on_the_frameworks_it_carries_and_one_with_no_configuration_reads_as_none()
    {
        string assembly = Path.Combine(root, "Tool");
        File.WriteAllText(Path.ChangeExtension(assembly, ".runtimeconfig.json"),
            """{ "runtimeOptions": { "tfm": "net10.0", "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.1" } ] } }""");

        Assert.Equal("10.0.1", RuntimeConfig.Read(assembly)?.VersionOf("Microsoft.NETCore.App"));
        Assert.Null(RuntimeConfig.Read(Path.Combine(root, "Library.dll")));
    }
}
