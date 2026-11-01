using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Which Godot runs a Godot project's C#, found as the SDK is: the person's choice, then GODOT, then the PATH, then
/// the app bundles in Applications, a .NET build of Godot 4 taken over one without .NET and the newest over the rest; a
/// console wrapper taken for the Godot it starts, and each refusal saying why.</summary>
public sealed class GodotExecutablesTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void The_newest_dotnet_bundle_in_Applications_is_taken_over_a_newer_one_without_dotnet()
    {
        string applications = Folder("Applications");
        Bundle(applications, "Godot.app", dotnet: null);
        Bundle(applications, "Godot_mono.app", dotnet: "4.6.1");
        string newest = Bundle(applications, "Godot_v4.7.2-stable_mono.app", dotnet: "4.7.2");
        GodotFound? found = GodotExecutables.Find(null, null, [], [applications, Folder("missing")], windows: false);
        Assert.True(found is { WhyNot: null, Version: "4.7.2" } && found.Executable == newest,
            $"Applications holding Godot without .NET, 4.6.1 and 4.7.2 gave {found}, not 4.7.2's .NET build, so a C# game would run on an older Godot");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(newest))!, "Resources", "GodotSharp", "Tools", "nupkgs"), found!.Packages);
    }

    [Fact]
    public void A_Godot_without_dotnet_is_found_saying_it_cannot_run_CSharp()
    {
        string applications = Folder("Applications");
        string plain = Bundle(applications, "Godot.app", dotnet: null);
        GodotFound? found = GodotExecutables.Find(null, null, [], [applications], windows: false);
        Assert.True(found?.Executable == plain, $"Godot without .NET alone gave {found}");
        Assert.Equal($"{plain} is a build of Godot without .NET, which cannot run C#: Godot's .NET build can", found!.WhyNot);
    }

    [Fact]
    public void The_choice_comes_before_GODOT_and_GODOT_before_the_PATH_and_a_choice_not_there_says_so()
    {
        string onPath = Download(Folder("bin"), "4.7.2");
        string named = Download(Folder("opt"), "4.6.1");
        string chosen = Download(Folder("chosen"), "4.5");
        Assert.Equal(chosen, GodotExecutables.Find((chosen, "Settings | Godot"), named, [Path.GetDirectoryName(onPath)!], [], windows: false)?.Executable);
        Assert.Equal(named, GodotExecutables.Find(null, named, [Path.GetDirectoryName(onPath)!], [], windows: false)?.Executable);
        GodotFound? path = GodotExecutables.Find(null, null, [Folder("empty"), Path.GetDirectoryName(onPath)!], [], windows: false);
        Assert.True(path is { WhyNot: null, Version: "4.7.2", From: GodotExecutables.PathWords } && path.Executable == onPath, $"The PATH's Godot was found as {path}");
        string gone = Path.Combine(root, "gone", "godot");
        Assert.Equal($"Settings | Godot names {gone}, which is not there", GodotExecutables.Find((gone, "Settings | Godot"), named, [], [], windows: false)?.WhyNot);
    }

    [Fact]
    public void A_link_on_the_PATH_is_the_Godot_it_links_to_with_its_GodotSharp()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Making a symbolic link takes a privilege a Windows user may not have");
        string godot = Download(Folder("opt"), "4.7.2");
        string bin = Folder("bin");
        File.CreateSymbolicLink(Path.Combine(bin, "godot"), godot);
        GodotFound? found = GodotExecutables.Find(null, null, [bin], [], windows: false);
        Assert.True(found is { WhyNot: null } && found.Executable == godot, $"A godot linking to {godot} was found as {found}, so its GodotSharp beside it would be missed");
    }

    [Fact]
    public void The_Windows_console_wrapper_is_taken_for_the_Godot_it_starts()
    {
        string folder = Folder("Godot_v4.7.2-stable_mono_windows_arm64");
        string started = Touch(Path.Combine(folder, "Godot_v4.7.2-stable_mono_windows_arm64.exe"));
        string wrapper = Touch(Path.Combine(folder, "Godot_v4.7.2-stable_mono_windows_arm64_console.exe"));
        Sharp(Path.Combine(folder, "GodotSharp"), "4.7.2");
        GodotFound found = GodotExecutables.At(wrapper, GodotExecutables.Variable, windows: true);
        Assert.True(found.Executable == started && found.WhyNot is null,
            $"GODOT naming the console wrapper gave {found}, whose child Godot no debugger or profiler of the wrapper would reach");
    }

    [Fact]
    public void Godot_3_is_refused_for_running_its_CSharp_on_Mono()
    {
        string folder = Folder("Godot_v3.6-stable_mono_x11_64");
        string godot = Touch(Path.Combine(folder, "Godot_v3.6-stable_mono_x11.64"));
        Directory.CreateDirectory(Path.Combine(folder, "GodotSharp", "Api"));
        Directory.CreateDirectory(Path.Combine(folder, "GodotSharp", "Mono"));
        GodotFound found = GodotExecutables.At(godot, GodotExecutables.Variable, windows: false);
        Assert.Equal($"{godot} is Godot 3, whose C# runs on Mono: Passenger runs C# on Godot 4's .NET build", found.WhyNot);
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    private static string Touch(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Empty);
        return file;
    }

    /// <summary>A GodotSharp folder as a .NET build carries it, its packages of the version given.</summary>
    private static void Sharp(string sharp, string version)
    {
        Directory.CreateDirectory(Path.Combine(sharp, "Api", "Debug"));
        Touch(Path.Combine(sharp, "Tools", "nupkgs", $"GodotSharp.{version}.nupkg"));
        Touch(Path.Combine(sharp, "Tools", "nupkgs", $"Godot.NET.Sdk.{version}.nupkg"));
    }

    /// <summary>An app bundle's executable, with the GodotSharp of a .NET build of the version given, or none.</summary>
    private static string Bundle(string folder, string name, string? dotnet)
    {
        string executable = Touch(Path.Combine(folder, name, "Contents", "MacOS", "Godot"));
        if (dotnet is not null)
        {
            Sharp(Path.Combine(folder, name, "Contents", "Resources", "GodotSharp"), dotnet);
        }
        return executable;
    }

    /// <summary>An official Linux download of a .NET build, its executable beside its GodotSharp.</summary>
    private static string Download(string folder, string version)
    {
        string godot = Touch(Path.Combine(folder, $"Godot_v{version}-stable_mono_linux.arm64"));
        Sharp(Path.Combine(folder, "GodotSharp"), version);
        return godot;
    }
}
