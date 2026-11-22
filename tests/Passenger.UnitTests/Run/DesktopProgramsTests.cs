using System.Text;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Packages;
using Passenger.UnitTests.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Run;

/// <summary>What desktop app a program's start runs, read from the start as it stands in a temporary folder: the program a
/// configuration names, native or managed, the assembly a <c>dotnet</c> is given and the project a <c>dotnet run</c> builds
/// or names, whatever the project's own build left, so a Passenger nobody sees knows what it would put on the person's
/// screen.</summary>
public sealed class DesktopProgramsTests : IDisposable
{
    private const string Dotnet = "/usr/local/share/dotnet/dotnet";

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_start_is_the_desktop_app_its_program_is_native_or_managed() => Cases.AllApart<DesktopProgramsTests>(
        ("a named program beside its ui framework is a desktop app though no project built it", test => test.A_named_program_beside_its_ui_framework_is_a_desktop_app_though_no_project_built_it()),
        ("a program with no ui framework is no desktop app", test => test.A_program_with_no_ui_framework_is_no_desktop_app()),
        ("a program taking avalonia base alone is no desktop app and one beside avalonias controls is an avalonia app", test => test.A_program_taking_avalonia_base_alone_is_no_desktop_app_and_one_beside_avalonias_controls_is_an_avalonia_app()),
        ("a mac program linking what opens windows is a desktop app", _ => Cases.EachApart<DesktopProgramsTests, string>(
        [
            "/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit",
            "@rpath/libSDL2-2.0.0.dylib",
        ], (test, row) => test.A_mac_program_linking_what_opens_windows_is_a_desktop_app(row))),
        ("a program in a macs app bundle is a desktop app whatever it links", test => test.A_program_in_a_macs_app_bundle_is_a_desktop_app_whatever_it_links()),
        ("a single file publish on a mac is known by the ui framework inside it and goes past the inspector", test => test.A_single_file_publish_on_a_mac_is_known_by_the_ui_framework_inside_it_and_goes_past_the_inspector()),
        ("a ui framework named across two reads of a large program is found all the same", test => test.A_ui_framework_named_across_two_reads_of_a_large_program_is_found_all_the_same()));

    private void A_named_program_beside_its_ui_framework_is_a_desktop_app_though_no_project_built_it()
    {
        string program = Write("gtk/Viewer");
        Write("gtk/GtkSharp.dll");
        DesktopProgram? found = DesktopPrograms.Started(program, [], root, built: null, target: null, declared: null);
        Assert.True(found is { Called: "a GTK app" } && found.File == program, $"A named GTK program read as {found}, so a Passenger nobody sees would start it on the person's screen");
    }

    [Fact]
    public void A_dotnet_start_is_what_its_command_runs_or_its_project_builds() => Cases.AllApart<DesktopProgramsTests>(
        ("the assembly a named dotnet is given is what runs past exec and the hosts options", test => test.The_assembly_a_named_dotnet_is_given_is_what_runs_past_exec_and_the_hosts_options()),
        ("a project s run command is read before the project s own program", test => test.A_project_s_run_command_is_read_before_the_project_s_own_program()),
        ("a script a project s run command names starts the project s app a hook goes through", test => test.A_script_a_project_s_run_command_names_starts_the_project_s_app_a_hook_goes_through()),
        ("a dotnet program a project s run command names is what runs never the project s own app", test => test.A_dotnet_program_a_project_s_run_command_names_is_what_runs_never_the_project_s_own_app()),
        ("a named dotnet run starts what its project builds past any hook given it", _ => Cases.EachApart<DesktopProgramsTests, string>(
        [
            "run",
            "watch",
        ], (test, row) => test.A_named_dotnet_run_starts_what_its_project_builds_past_any_hook_given_it(row))),
        ("a build turning startup hooks off keeps the inspector out", test => test.A_build_turning_startup_hooks_off_keeps_the_inspector_out()),
        ("a dotnet run naming a project names it past the folder it starts in", _ => Cases.EachApart<DesktopProgramsTests, (string, string, string)>(
        [
            ("run", "-p", "../Other/Other.csproj"),
            ("watch", "--project", "../Other/Other.csproj"),
        ], (test, row) => test.A_dotnet_run_naming_a_project_names_it_past_the_folder_it_starts_in(row.Item1, row.Item2, row.Item3))),
        ("a dotnet run naming another project is what that project says never what the plans built", test => test.A_dotnet_run_naming_another_project_is_what_that_project_says_never_what_the_plans_built()),
        ("a dotnet run of a project no window loaded is refused for what cannot be read but on linux which has no display", test => test.A_dotnet_run_of_a_project_no_window_loaded_is_refused_for_what_cannot_be_read_but_on_linux_which_has_no_display()));

    private void The_assembly_a_named_dotnet_is_given_is_what_runs_past_exec_and_the_hosts_options()
    {
        string assembly = Write("app/App.dll");
        Write("app/Avalonia.Controls.dll");
        Assert.Equal(assembly, DesktopPrograms.Started(Dotnet, ["app/App.dll", "--port", "1"], root, null, null, null)?.File);
        Assert.Equal(assembly, DesktopPrograms.Started(Dotnet, ["exec", "--runtimeconfig", "other.json", assembly], root, null, null, null)?.File);
        Assert.Equal(assembly, DesktopPrograms.Started(Dotnet, ["--roll-forward", "Major", assembly], root, null, null, null)?.File);
    }

    private void A_program_with_no_ui_framework_is_no_desktop_app()
    {
        string tool = Write("tools/tool");
        Write("tools/tool.dll");
        Assert.Null(DesktopPrograms.Started(tool, [], root, null, null, null));
        Assert.Null(DesktopPrograms.Started("not-on-any-path", [], root, null, null, null));
        Assert.Null(DesktopPrograms.Started(Dotnet, ["ef", "database", "update"], root, null, target: Write("app/App.dll"), declared: null));
    }

    /// <summary>Avalonia.Base holds Avalonia's geometry, colours and values, which a library's tool or a test harness takes
    /// alone; an app's Application and windows are in Avalonia.Controls.</summary>
    private void A_program_taking_avalonia_base_alone_is_no_desktop_app_and_one_beside_avalonias_controls_is_an_avalonia_app()
    {
        string tool = Write("tool/Tool.dll");
        Write("tool/Avalonia.Base.dll");
        Assert.True(DesktopPrograms.Started(Dotnet, [tool], root, tool, tool, null) is null && !DesktopPrograms.BesideAvalonia(tool),
            "A program taking Avalonia.Base alone read as an Avalonia app, so the inspector would go into a program with no windows for it");
        Write("tool/Avalonia.Controls.dll");
        Assert.True(DesktopPrograms.Started(Dotnet, [tool], root, tool, tool, null) is { Called: DesktopPrograms.Avalonia } && DesktopPrograms.BesideAvalonia(tool),
            "A program beside Avalonia's controls did not read as an Avalonia app");
    }

    private void A_project_s_run_command_is_read_before_the_project_s_own_program()
    {
        string built = Write("app/App.dll");
        Write("app/Avalonia.Controls.dll");
        string other = Write("other/Other");
        Write("other/GtkSharp.dll");
        Assert.Equal("a GTK app", DesktopPrograms.Started(other, [], root, built, built, null)?.Called);
    }

    private void A_script_a_project_s_run_command_names_starts_the_project_s_app_a_hook_goes_through()
    {
        string built = Write("app/App.dll");
        Write("app/Avalonia.Controls.dll");
        DesktopProgram? found = DesktopPrograms.Started(Write("run.sh"), [], root, built, built, null);
        Assert.True(found is { Called: DesktopPrograms.Avalonia, PastHooks: null } && found.File == built, $"A script starting the project's app read as {found}");
    }

    /// <summary>A .NET program takes a hook given to the start out of whatever it starts in turn, so the project's own app
    /// never has it.</summary>
    private void A_dotnet_program_a_project_s_run_command_names_is_what_runs_never_the_project_s_own_app()
    {
        string built = Write("app/App.dll");
        Write("app/Avalonia.Controls.dll");
        string tool = Write("tool/Tool.dll");
        Assert.True(DesktopPrograms.Started(Dotnet, [tool, "--size", "2"], root, built, built, null) is null,
            "The assembly a run command's dotnet runs, of no UI framework, read as the project's own Avalonia app, so the inspector would go into it");
        string host = Write("tool/Tool");
        Write("tool/Tool.runtimeconfig.json", """{ "runtimeOptions": { "tfm": "net11.0", "framework": { "name": "Microsoft.NETCore.App", "version": "11.0.0" } } }""");
        Assert.True(DesktopPrograms.Started(host, [], root, built, built, null) is null,
            "The apphost a run command names, of no UI framework, read as the project's own Avalonia app, so the inspector would go into it");
        Assert.Equal(built, DesktopPrograms.Started(Dotnet, [built], root, built, built, null)?.File);
    }

    private void A_named_dotnet_run_starts_what_its_project_builds_past_any_hook_given_it(string command)
    {
        string target = Write("app/App.dll");
        Write("app/Avalonia.Controls.dll");
        DesktopProgram? found = DesktopPrograms.Started(Dotnet, [command, "--no-build"], root, built: null, target, declared: null);
        Assert.True(found is { Called: DesktopPrograms.Avalonia, PastHooks: { } past } && past.StartsWith($"dotnet {command} ", StringComparison.Ordinal),
            $"dotnet {command} of an Avalonia project read as {found}: the inspector it would be given stops in the .NET CLI, and the app opens on the person's screen");
    }

    private void A_build_turning_startup_hooks_off_keeps_the_inspector_out()
    {
        string built = Write("app/App.dll");
        Write("app/Avalonia.Controls.dll");
        Assert.Null(DesktopPrograms.Started(Dotnet, [built], root, built, built, null)?.PastHooks);
        Write("app/App.runtimeconfig.json", """{ "runtimeOptions": { "configProperties": { "System.StartupHookProvider.IsSupported": false } } }""");
        Assert.NotNull(DesktopPrograms.Started(Dotnet, [built], root, built, built, null)?.PastHooks);
        Write("app/App.runtimeconfig.json", """{ "runtimeOptions": { "configProperties": { "System.StartupHookProvider.IsSupported": true } } }""");
        Assert.Null(DesktopPrograms.Started(Dotnet, [built], root, built, built, null)?.PastHooks);
    }

    [Fact]
    public void A_project_says_what_it_is_by_its_packages_and_the_projects_it_references() => Cases.AllApart<DesktopProgramsTests>(
        ("a project says what it is by the packages it takes", _ => Cases.EachApart<DesktopProgramsTests, (string, string?)>(
        [
            ("Avalonia.Desktop", DesktopPrograms.Avalonia),
            ("GtkSharp", "a GTK app"),
            ("Eto.Parse", null),
            ("Serilog", null),
        ], (test, row) => test.A_project_says_what_it_is_by_the_packages_it_takes(row.Item1, row.Item2))),
        ("a project takes its ui framework from the projects it references", test => test.A_project_takes_its_ui_framework_from_the_projects_it_references()),
        ("a project takes xaml by any package of avalonias of its own or of the projects it references", _ => Cases.EachApart<DesktopProgramsTests, (string, bool)>(
        [
            ("Avalonia.Themes.Fluent", true),
            ("Eto.Forms", false),
        ], (test, row) => test.A_project_takes_xaml_by_any_package_of_avalonias_of_its_own_or_of_the_projects_it_references(row.Item1, row.Item2))),
        ("a restore says a project takes xaml where a package of another name brings avalonia", test => test.A_restore_says_a_project_takes_xaml_where_a_package_of_another_name_brings_avalonia()));

    private void A_project_says_what_it_is_by_the_packages_it_takes(string package, string? called) =>
        Assert.Equal(called, DesktopPrograms.OfProject(Model("App", "Exe", package), []));

    private void A_project_takes_its_ui_framework_from_the_projects_it_references()
    {
        ProjectModel shared = Model("Shared", "Library", "Avalonia");
        ProjectModel head = Model("App", "Exe") with { ProjectReferences = [shared.Path] };
        Assert.Equal(DesktopPrograms.Avalonia, DesktopPrograms.OfProject(head, [head, shared]));
    }

    private void A_mac_program_linking_what_opens_windows_is_a_desktop_app(string library)
    {
        string program = WriteBytes("native/Viewer", MachOBytes.Universal(MachOBytes.Thin("/usr/lib/libSystem.B.dylib", library)));
        DesktopProgram? found = DesktopPrograms.Started(program, [], root, null, null, null);
        Assert.True(found is { Called: "a native macOS app", PastHooks: null } && found.File == program, $"A native program linking {library} read as {found}, so a Passenger nobody sees would start it on the person's screen");
    }

    /// <summary>A bundle's program links what it likes, or is a script: an Electron app's links only the framework beside it.</summary>
    private void A_program_in_a_macs_app_bundle_is_a_desktop_app_whatever_it_links()
    {
        Assert.Equal("a native macOS app", DesktopPrograms.Of(WriteBytes(Path.Join("Viewer.app", "Contents", "MacOS", "Viewer"), MachOBytes.Thin("@rpath/Electron Framework.framework/Electron Framework"))));
        Assert.Equal("a native macOS app", DesktopPrograms.Of(Write(Path.Join("Script.app", "Contents", "MacOS", "Script"), "#!/bin/sh\n")));
    }

    /// <summary>A single-file publish lists what it holds by name, with nothing beside it to read. The inspector reads an app's
    /// assemblies from its folder, so such an app is no Avalonia app to it, and no start goes through it.</summary>
    private void A_single_file_publish_on_a_mac_is_known_by_the_ui_framework_inside_it_and_goes_past_the_inspector()
    {
        string program = WriteBytes("single/App", [.. MachOBytes.Thin("/usr/lib/libSystem.B.dylib"), .. new byte[100], .. Encoding.UTF8.GetBytes("Avalonia.Controls.dll"), .. new byte[100]]);
        DesktopProgram? found = DesktopPrograms.Started(program, [], root, null, null, null);
        Assert.True(found is { Called: "an Avalonia app, published as a single file" }, $"A single-file Avalonia app read as {found}");
        Assert.NotEqual(DesktopPrograms.Avalonia, found!.Called);
    }

    private void A_ui_framework_named_across_two_reads_of_a_large_program_is_found_all_the_same()
    {
        const int Read = 1024 * 1024;
        byte[] header = MachOBytes.Thin("/usr/lib/libSystem.B.dylib");
        byte[] name = Encoding.UTF8.GetBytes("Eto.dll");
        byte[] program = new byte[(2 * Read) + 100];
        header.CopyTo(program, 0);
        name.CopyTo(program, Read - 3);
        Assert.Equal("an Eto.Forms app, published as a single file", DesktopPrograms.Of(WriteBytes("single/Large", program)));
    }

    private void A_dotnet_run_naming_a_project_names_it_past_the_folder_it_starts_in(string command, string option, string project)
    {
        string folder = Path.Combine(root, "app");
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, project)), DesktopPrograms.NamedProject(Dotnet, [command, "--no-build", option, project], folder));
    }

    /// <summary>The plan's own project says nothing of what such a run starts, in either direction.</summary>
    private void A_dotnet_run_naming_another_project_is_what_that_project_says_never_what_the_plans_built()
    {
        string target = Write("console/Tool.dll");
        string other = Path.Combine(root, "gui", "Gui.csproj");
        ProjectModel gui = Model("Gui", "Exe", "GtkSharp") with { Path = other };
        Assert.Equal("a GTK app", DesktopPrograms.OfNamed(other, [gui]));
        Assert.Equal("a GTK app", DesktopPrograms.OfNamed(Path.GetDirectoryName(other)!, [gui]));

        DesktopProgram? found = DesktopPrograms.Started(Dotnet, ["run", "--project", other], root, null, target, "a GTK app", other);
        Assert.True(found is { Called: "a GTK app", PastHooks: not null } && found.File == other, $"dotnet run of a GTK project read as {found}");

        string avalonia = Write("avalonia/App.dll");
        Write("avalonia/Avalonia.Controls.dll");
        Assert.Null(DesktopPrograms.Started(Dotnet, ["run", "--project", other], root, null, avalonia, null, other));
    }

    private void A_dotnet_run_of_a_project_no_window_loaded_is_refused_for_what_cannot_be_read_but_on_linux_which_has_no_display()
    {
        Assert.Equal(OperatingSystem.IsLinux() ? null : DesktopPrograms.UnreadProject, DesktopPrograms.OfNamed(Path.Combine(root, "elsewhere", "Far.csproj"), []));
    }

    /// <summary>A desktop head names Avalonia.Desktop, or only the library holding its XAML: each is an Avalonia app.</summary>
    private void A_project_takes_xaml_by_any_package_of_avalonias_of_its_own_or_of_the_projects_it_references(string package, bool takes)
    {
        ProjectModel own = Model("App", "Exe", package);
        ProjectModel shared = Model("Shared", "Library", package);
        ProjectModel head = Model("Head", "Exe") with { ProjectReferences = [shared.Path] };
        Assert.Equal(takes, DesktopPrograms.TakesXaml(own, [own]));
        Assert.Equal(takes, DesktopPrograms.TakesXaml(head, [head, shared]));
    }

    private void A_restore_says_a_project_takes_xaml_where_a_package_of_another_name_brings_avalonia()
    {
        Assert.True(DesktopPrograms.RestoredXaml(Restored("Vendor.Controls", "Avalonia")));
        Assert.False(DesktopPrograms.RestoredXaml(Restored("Serilog")));
        Assert.False(DesktopPrograms.RestoredXaml(RestoredVersions.None));
    }

    private static RestoredVersions Restored(params string[] packages)
    {
        string libraries = string.Join(", ", packages.Select(package => $"\"{package}/1.0.0\": {{ \"type\": \"package\" }}"));
        return RestoredVersions.Read(new MemoryStream(Encoding.UTF8.GetBytes($"{{ \"targets\": {{ \"net11.0\": {{ {libraries} }} }} }}")));
    }

    private string WriteBytes(string relative, byte[] bytes)
    {
        string file = Write(relative);
        File.WriteAllBytes(file, bytes);
        return file;
    }

    private string Write(string relative, string text = "")
    {
        string file = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    private ProjectModel Model(string name, string outputType, params string[] packages) =>
        new(Path.Combine(root, name, $"{name}.csproj"), name, outputType, ["net11.0"], [.. packages.Select(p => (p, (string?)"1.0.0"))], [], Exists: true);
}
