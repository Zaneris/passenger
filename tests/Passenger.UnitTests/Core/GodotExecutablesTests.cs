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

    [Theory]
    [InlineData("4.7.2.stable.mono.official.ed1daf0bf", "4.7.2", true)]
    [InlineData("4.3.stable.official.77dcf97d8", "4.3", false)]
    [InlineData("4.5.beta2.mono.custom_build.1a2b3c", "4.5", true)]
    [InlineData("  4.7.2.stable.mono.official.ed1daf0bf\r", "4.7.2", true)]
    public void Godots_answer_to_version_gives_its_version_and_whether_it_runs_CSharp(string answer, string version, bool dotnet)
    {
        Assert.Equal((version, dotnet), GodotExecutables.ReadVersion(answer));
    }

    [Theory]
    [InlineData("Godot Engine v4.7.2.stable.mono.official.ed1daf0bf - https://godotengine.org")]
    [InlineData("WARNING: something")]
    [InlineData("4.7.2")]
    public void A_line_that_is_no_answer_to_version_gives_none(string line)
    {
        Assert.Null(GodotExecutables.ReadVersion(line));
    }

    [Theory]
    [InlineData("4.7.2", "4.7.2", null)]
    [InlineData("4.7.1", "4.7.2", null)]
    [InlineData("4.6", "4.7.2", null)]
    [InlineData("4.8.0", "4.7.2", "it builds with Godot.NET.Sdk 4.8.0, newer than the Godot 4.7.2 that runs it: run it on Godot 4.8.0 or newer, or build it with Godot.NET.Sdk 4.7.2")]
    [InlineData("4.7.2", "4.7", "it builds with Godot.NET.Sdk 4.7.2, newer than the Godot 4.7 that runs it: run it on Godot 4.7.2 or newer, or build it with Godot.NET.Sdk 4.7")]
    [InlineData("3.6.0", "4.7.2", "it builds with Godot.NET.Sdk 3.6.0, for Godot 3, and the Godot that runs it is 4.7.2")]
    [InlineData("4.8.0-beta.1", "4.7.2", "it builds with Godot.NET.Sdk 4.8.0-beta.1, newer than the Godot 4.7.2 that runs it: run it on Godot 4.8.0-beta.1 or newer, or build it with Godot.NET.Sdk 4.7.2")]
    [InlineData("unknown", "4.7.2", null)]
    public void A_projects_Godot_NET_Sdk_newer_than_its_Godot_or_of_another_major_version_says_both(string sdk, string godot, string? said)
    {
        Assert.Equal(said, GodotExecutables.Mismatch(sdk, godot));
    }

    [Theory]
    [InlineData("%GODOT%", false)]
    [InlineData("$GODOT", false)]
    [InlineData("${GODOT}", false)]
    [InlineData("$(GODOT)", false)]
    [InlineData(" \"%GODOT%\" ", false)]
    [InlineData("%godot%", true)]
    [InlineData("/Applications/Godot_mono.app/Contents/MacOS/Godot", false)]
    [InlineData("/Applications/Godot_v4.7.2-stable_mono.app", false)]
    [InlineData("/usr/local/bin/godot4-mono", false)]
    [InlineData("/opt/Godot_v4.7.2-stable_mono_linux.arm64", false)]
    [InlineData(@"C:\Tools\Godot_v4.7.2-stable_mono_win64.exe", true)]
    [InlineData(@"C:\Tools\Godot_v4.7.2-stable_mono_win64_console.exe", true)]
    [InlineData(@"C:\Tools\godot.exe", true)]
    public void A_launch_profiles_program_naming_GODOT_or_a_Godot_executable_is_Godot(string program, bool windows)
    {
        Assert.True(GodotExecutables.NamesGodot(program, windows), $"{program} was not taken for Godot, so its launch profile would run the game as a plain program: no remote debugger, no Live Tree, a window for an agent's game");
    }

    [Theory]
    [InlineData("%GODOT_HOME%", false)]
    [InlineData("%godot%", false)]
    [InlineData("dotnet", false)]
    [InlineData("/Applications/Blender.app/Contents/MacOS/Godot", false)]
    [InlineData("/usr/bin/godotenv", false)]
    [InlineData(@"C:\Tools\MyGame.exe", true)]
    [InlineData("", false)]
    public void A_launch_profiles_other_program_is_not_Godot(string program, bool windows)
    {
        Assert.False(GodotExecutables.NamesGodot(program, windows), $"{program} was taken for Godot, so its launch profile would run the project's game in its place");
    }

    [Fact]
    public void A_launch_profiles_editor_flag_opens_Godots_editor_and_its_game_arguments_do_not()
    {
        Assert.True(GodotExecutables.OpensEditor(["--path", ".", "--editor"]), "--editor was not taken for opening the editor, so the profile would wait for a game that never connects");
        Assert.True(GodotExecutables.OpensEditor(["-e", "--path", "."]), "-e was not taken for opening the editor");
        Assert.False(GodotExecutables.OpensEditor(["--path", ".", "--run-tests", "--quit-on-finish"]), "A game's own arguments were taken for opening the editor");
    }

    [Fact]
    public void A_path_naming_the_project_is_left_to_the_launch_and_one_naming_another_folder_refused()
    {
        string game = Folder("game");
        string other = Folder("other");
        Assert.Equal(["--run-tests", "--quit-on-finish"], GodotExecutables.WithoutPath(["--path", "./", "--run-tests", "--quit-on-finish"], game, game));
        Assert.Equal(["--verbose"], GodotExecutables.WithoutPath(["--verbose", "--path", game + Path.DirectorySeparatorChar], other, game));
        Assert.Equal(["--verbose"], GodotExecutables.WithoutPath(["--path", "game", "--verbose"], root, game));
        Assert.Equal(["--headless"], GodotExecutables.WithoutPath(["--headless"], other, game));
        Assert.Null(GodotExecutables.WithoutPath(["--path", "../other"], game, game));
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
