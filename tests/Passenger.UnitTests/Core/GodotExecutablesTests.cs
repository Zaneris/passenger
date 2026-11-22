using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Which Godot runs a Godot project's C#, found as the SDK is: the person's choice, then GODOT, else GODOT4, then the PATH, then
/// the app bundles in Applications, a .NET build of Godot 4 taken over one without .NET and the newest over the rest; a
/// console wrapper taken for the Godot it starts, and each refusal saying why.</summary>
public sealed class GodotExecutablesTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Godot is found by the choice, GODOT, GODOT4 and the PATH in turn, and its version read from its
    /// answer.</summary>
    [Fact]
    public void Godot_is_found_and_its_version_read_as_the_choice_and_variables_say() => Cases.AllApart<GodotExecutablesTests>(
        (nameof(The_newest_dotnet_bundle_in_Applications_is_taken_over_a_newer_one_without_dotnet),
            test => test.The_newest_dotnet_bundle_in_Applications_is_taken_over_a_newer_one_without_dotnet()),
        (nameof(A_Godot_without_dotnet_is_found_saying_it_cannot_run_CSharp),
            test => test.A_Godot_without_dotnet_is_found_saying_it_cannot_run_CSharp()),
        (nameof(The_choice_comes_before_GODOT_and_GODOT_before_the_PATH_and_a_choice_not_there_says_so),
            test => test.The_choice_comes_before_GODOT_and_GODOT_before_the_PATH_and_a_choice_not_there_says_so()),
        (nameof(GODOT_names_Godot_over_GODOT4_and_neither_set_names_none), test => test.GODOT_names_Godot_over_GODOT4_and_neither_set_names_none()),
        (nameof(The_Windows_console_wrapper_is_taken_for_the_Godot_it_starts),
            test => test.The_Windows_console_wrapper_is_taken_for_the_Godot_it_starts()),
        (nameof(Godot_3_is_refused_for_running_its_CSharp_on_Mono), test => test.Godot_3_is_refused_for_running_its_CSharp_on_Mono()),
        (nameof(Godots_answer_to_version_gives_its_version_and_whether_it_runs_CSharp),
            _ => Godots_answer_to_version_gives_its_version_and_whether_it_runs_CSharp()),
        (nameof(A_projects_Godot_NET_Sdk_newer_than_its_Godot_or_of_another_major_version_says_both),
            _ => A_projects_Godot_NET_Sdk_newer_than_its_Godot_or_of_another_major_version_says_both()));

    /// <summary>A launch profile runs Godot where it names it, on the project its path names, its game's arguments its
    /// own.</summary>
    [Fact]
    public void A_launch_profile_names_godot_and_its_project_as_godot_reads_them() => Cases.AllApart<GodotExecutablesTests>(
        (nameof(A_link_on_the_PATH_is_the_Godot_it_links_to_with_its_GodotSharp),
            test => test.A_link_on_the_PATH_is_the_Godot_it_links_to_with_its_GodotSharp()),
        (nameof(A_launch_profiles_program_naming_GODOT_or_a_Godot_executable_is_Godot),
            _ => A_launch_profiles_program_naming_GODOT_or_a_Godot_executable_is_Godot()),
        (nameof(A_launch_profiles_other_program_is_not_Godot), _ => A_launch_profiles_other_program_is_not_Godot()),
        (nameof(A_path_naming_the_project_is_left_to_the_launch_and_one_naming_another_folder_refused),
            test => test.A_path_naming_the_project_is_left_to_the_launch_and_one_naming_another_folder_refused()),
        (nameof(An_editor_is_on_the_project_its_path_names_whole_never_one_whose_path_begins_with_it),
            test => test.An_editor_is_on_the_project_its_path_names_whole_never_one_whose_path_begins_with_it()),
        (nameof(A_games_arguments_after_godots_separator_are_never_read_as_godots_flags),
            _ => A_games_arguments_after_godots_separator_are_never_read_as_godots_flags()));

    private void The_newest_dotnet_bundle_in_Applications_is_taken_over_a_newer_one_without_dotnet()
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

    private void A_Godot_without_dotnet_is_found_saying_it_cannot_run_CSharp()
    {
        string applications = Folder("Applications");
        string plain = Bundle(applications, "Godot.app", dotnet: null);
        GodotFound? found = GodotExecutables.Find(null, null, [], [applications], windows: false);
        Assert.True(found?.Executable == plain, $"Godot without .NET alone gave {found}");
        Assert.Equal($"{plain} is a build of Godot without .NET, which cannot run C#: Godot's .NET build can", found!.WhyNot);
    }

    private void The_choice_comes_before_GODOT_and_GODOT_before_the_PATH_and_a_choice_not_there_says_so()
    {
        string onPath = Download(Folder("bin"), "4.7.2");
        string named = Download(Folder("opt"), "4.6.1");
        string chosen = Download(Folder("chosen"), "4.5");
        Assert.Equal(chosen, GodotExecutables.Find((chosen, "Settings | Godot"), (named, GodotExecutables.Variable), [Path.GetDirectoryName(onPath)!], [], windows: false)?.Executable);
        Assert.Equal(named, GodotExecutables.Find(null, (named, GodotExecutables.Variable), [Path.GetDirectoryName(onPath)!], [], windows: false)?.Executable);
        GodotFound? path = GodotExecutables.Find(null, null, [Folder("empty"), Path.GetDirectoryName(onPath)!], [], windows: false);
        Assert.True(path is { WhyNot: null, Version: "4.7.2", From: GodotExecutables.PathWords } && path.Executable == onPath, $"The PATH's Godot was found as {path}");
        string gone = Path.Combine(root, "gone", "godot");
        Assert.Equal($"Settings | Godot names {gone}, which is not there", GodotExecutables.Find((gone, "Settings | Godot"), (named, GodotExecutables.Variable), [], [], windows: false)?.WhyNot);
    }

    private void GODOT_names_Godot_over_GODOT4_and_neither_set_names_none()
    {
        Assert.Equal(("/opt/godot", GodotExecutables.Variable), GodotExecutables.Named("/opt/godot", "/opt/godot4"));
        Assert.Equal(("/opt/godot4", GodotExecutables.Variable4), GodotExecutables.Named(string.Empty, "/opt/godot4"));
        Assert.Null(GodotExecutables.Named(null, string.Empty));
        Assert.Null(GodotExecutables.Named(string.Empty, null));
    }

    private void A_link_on_the_PATH_is_the_Godot_it_links_to_with_its_GodotSharp()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Making a symbolic link takes a privilege a Windows user may not have");
        string godot = Download(Folder("opt"), "4.7.2");
        string bin = Folder("bin");
        File.CreateSymbolicLink(Path.Combine(bin, "godot"), godot);
        GodotFound? found = GodotExecutables.Find(null, null, [bin], [], windows: false);
        Assert.True(found is { WhyNot: null } && found.Executable == godot, $"A godot linking to {godot} was found as {found}, so its GodotSharp beside it would be missed");
    }

    private void The_Windows_console_wrapper_is_taken_for_the_Godot_it_starts()
    {
        string folder = Folder("Godot_v4.7.2-stable_mono_windows_arm64");
        string started = Touch(Path.Combine(folder, "Godot_v4.7.2-stable_mono_windows_arm64.exe"));
        string wrapper = Touch(Path.Combine(folder, "Godot_v4.7.2-stable_mono_windows_arm64_console.exe"));
        Sharp(Path.Combine(folder, "GodotSharp"), "4.7.2");
        GodotFound found = GodotExecutables.At(wrapper, GodotExecutables.Variable, windows: true);
        Assert.True(found.Executable == started && found.WhyNot is null,
            $"GODOT naming the console wrapper gave {found}, whose child Godot no debugger or profiler of the wrapper would reach");
    }

    private void Godot_3_is_refused_for_running_its_CSharp_on_Mono()
    {
        string folder = Folder("Godot_v3.6-stable_mono_x11_64");
        string godot = Touch(Path.Combine(folder, "Godot_v3.6-stable_mono_x11.64"));
        Directory.CreateDirectory(Path.Combine(folder, "GodotSharp", "Api"));
        Directory.CreateDirectory(Path.Combine(folder, "GodotSharp", "Mono"));
        GodotFound found = GodotExecutables.At(godot, GodotExecutables.Variable, windows: false);
        Assert.Equal($"{godot} is Godot 3, whose C# runs on Mono: Passenger runs C# on Godot 4's .NET build", found.WhyNot);
    }

    private static void Godots_answer_to_version_gives_its_version_and_whether_it_runs_CSharp() => Cases.EachApart<GodotExecutablesTests, (string Answer, string Version, bool Dotnet)>(
    [
        ("4.7.2.stable.mono.official.ed1daf0bf", "4.7.2", true),
        ("4.3.stable.official.77dcf97d8", "4.3", false),
        ("4.5.beta2.mono.custom_build.1a2b3c", "4.5", true),
        ("  4.7.2.stable.mono.official.ed1daf0bf\r", "4.7.2", true),
    ], (test, row) => test.Godots_answer_to_version_gives_its_version_and_whether_it_runs_CSharp(row.Answer, row.Version, row.Dotnet));

    private void Godots_answer_to_version_gives_its_version_and_whether_it_runs_CSharp(string answer, string version, bool dotnet)
    {
        Assert.Equal((version, dotnet), GodotExecutables.ReadVersion(answer));
    }

    private static void A_projects_Godot_NET_Sdk_newer_than_its_Godot_or_of_another_major_version_says_both() => Cases.EachApart<GodotExecutablesTests, (string Sdk, string Godot, string? Said)>(
    [
        ("4.7.2", "4.7.2", null),
        ("4.6", "4.7.2", null),
        ("3.6.0", "4.7.2", "it builds with Godot.NET.Sdk 3.6.0, for Godot 3, and the Godot that runs it is 4.7.2"),
        ("unknown", "4.7.2", null),
    ], (test, row) => test.A_projects_Godot_NET_Sdk_newer_than_its_Godot_or_of_another_major_version_says_both(row.Sdk, row.Godot, row.Said));

    private void A_projects_Godot_NET_Sdk_newer_than_its_Godot_or_of_another_major_version_says_both(string sdk, string godot, string? said)
    {
        Assert.Equal(said, GodotExecutables.Mismatch(sdk, godot));
    }

    private static void A_launch_profiles_program_naming_GODOT_or_a_Godot_executable_is_Godot() => Cases.EachApart<GodotExecutablesTests, (string Program, bool Windows)>(
    [
        ("%GODOT%", false),
        (" \"%GODOT%\" ", false),
        ("${GODOT4}", false),
        ("/Applications/Godot_v4.7.2-stable_mono.app", false),
        (@"C:\Tools\godot.exe", true),
    ], (test, row) => test.A_launch_profiles_program_naming_GODOT_or_a_Godot_executable_is_Godot(row.Program, row.Windows));

    private void A_launch_profiles_program_naming_GODOT_or_a_Godot_executable_is_Godot(string program, bool windows)
    {
        Assert.True(GodotExecutables.NamesGodot(program, windows), $"{program} was not taken for Godot, so its launch profile would run the game as a plain program: no remote debugger, no Live Tree, a window for an agent's game");
    }

    private static void A_launch_profiles_other_program_is_not_Godot() => Cases.EachApart<GodotExecutablesTests, (string Program, bool Windows)>(
    [
        ("%GODOT_HOME%", false),
        ("$GODOT45", false),
        ("/Applications/Blender.app/Contents/MacOS/Godot", false),
        ("", false),
    ], (test, row) => test.A_launch_profiles_other_program_is_not_Godot(row.Program, row.Windows));

    private void A_launch_profiles_other_program_is_not_Godot(string program, bool windows)
    {
        Assert.False(GodotExecutables.NamesGodot(program, windows), $"{program} was taken for Godot, so its launch profile would run the project's game in its place");
    }

    private void A_path_naming_the_project_is_left_to_the_launch_and_one_naming_another_folder_refused()
    {
        string game = Folder("game");
        string other = Folder("other");
        Assert.Equal(["--run-tests", "--quit-on-finish"], GodotExecutables.WithoutPath(["--path", "./", "--run-tests", "--quit-on-finish"], game, game));
        Assert.Equal(["--verbose"], GodotExecutables.WithoutPath(["--verbose", "--path", game + Path.DirectorySeparatorChar], other, game));
        Assert.Equal(["--verbose"], GodotExecutables.WithoutPath(["--path", "game", "--verbose"], root, game));
        Assert.Equal(["--headless"], GodotExecutables.WithoutPath(["--headless"], other, game));
        Assert.Null(GodotExecutables.WithoutPath(["--path", "../other"], game, game));
    }

    private void An_editor_is_on_the_project_its_path_names_whole_never_one_whose_path_begins_with_it()
    {
        string shooter = Folder("shooter");
        string old = Folder("shooter-old");
        Assert.True(GodotExecutables.OpensEditorOn(["godot", "--path", shooter, "--editor"], shooter), "An editor the project manager started on the project was not taken for one on it");
        Assert.True(GodotExecutables.OpensEditorOn(["godot", "-e", "--path", shooter + Path.DirectorySeparatorChar], shooter), "An editor whose path ends in a separator was not taken for one on the project");
        Assert.True(GodotExecutables.OpensEditorOn(["godot", Path.Combine(shooter, "project.godot")], shooter), "Godot opened on the project's project.godot, which opens its editor, was not taken for one on it");
        Assert.False(GodotExecutables.OpensEditorOn(["godot", "--path", old, "--editor"], shooter),
            "An editor on shooter-old was taken for one on shooter, so what a headless editor writes in shooter would be kept as the person's");
        Assert.False(GodotExecutables.OpensEditorOn(["godot", Path.Combine(old, "project.godot")], shooter), "Godot opened on shooter-old's project.godot was taken for an editor on shooter");
        Assert.False(GodotExecutables.OpensEditorOn(["godot", "--path", Path.Combine(shooter, "levels"), "--editor"], shooter), "An editor on a folder inside the project was taken for one on it");
        Assert.False(GodotExecutables.OpensEditorOn(["godot", "--path", shooter, "res://main.tscn"], shooter), "A game played on the project was taken for its editor");
        Assert.False(GodotExecutables.OpensEditorOn(["godot", "--path", ".", "--editor"], shooter), "A relative path, read from a working folder unknown, was taken for the project");
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, shooter);
            Assert.True(GodotExecutables.OpensEditorOn(["godot", "--path", link, "--editor"], shooter), "An editor on the project through a link to its folder was not taken for one on it");
        }
    }

    private static void A_games_arguments_after_godots_separator_are_never_read_as_godots_flags() => Cases.EachApart<GodotExecutablesTests, string>(
    [
        "--",
        "++",
    ], (test, row) => test.A_games_arguments_after_godots_separator_are_never_read_as_godots_flags(row));

    private void A_games_arguments_after_godots_separator_are_never_read_as_godots_flags(string separator)
    {
        string game = Folder("game");
        string[] words = ["godot", "--path", game, separator, "--editor", "-e", "--headless", "--path", "save", "--lsp-port", Path.Combine(game, "project.godot")];
        Assert.False(GodotExecutables.RunsHeadless(["--path", game, separator, "--headless"]),
            "A game's own --headless was taken for Godot's, so a launch nobody sees left out its own and the game could open a window");
        Assert.False(GodotExecutables.RunsHeadless(["--path", game, separator, "--display-driver", "headless"]), "A game's own --display-driver was taken for Godot's");
        Assert.True(GodotExecutables.RunsHeadless(["--headless", separator, "--anything"]), "Godot's own --headless before the separator went unread");
        Assert.False(GodotExecutables.OpensEditor(["--path", ".", separator, "--editor"]), "A game's own --editor made its launch profile the editor's, which waits for a game that never connects");
        Assert.False(GodotExecutables.OpensEditor([separator, "-e"]), "A game's own -e was taken for opening the editor");
        Assert.True(GodotExecutables.OpensEditor(["--editor", separator, "--level"]), "Godot's own --editor before the separator went unread");
        Assert.False(GodotExecutables.OpensEditorOn(words, game), "A game given --editor and its project's file was taken for the person's editor on the project");
        Assert.Equal(["--verbose", separator, "--path", "save"], GodotExecutables.WithoutPath(["--verbose", separator, "--path", "save"], game, game));
        Assert.Equal(["--verbose", separator, "--path", "save"], GodotExecutables.WithoutPath(["--path", game, "--verbose", separator, "--path", "save"], game, game));
        Assert.Null(GodotExecutables.WithoutPath(["--path", "../other", separator, "--level"], game, game));
        Assert.Equal(["--verbose", separator, "--editor", "-e"], GodotExecutables.WithoutEditorFlags(["-e", "--verbose", "--editor", separator, "--editor", "-e"]));
        Assert.Equal(["--path", game], GodotExecutables.EngineArguments(["--path", game, separator, "--headless", separator, "x"]));
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
