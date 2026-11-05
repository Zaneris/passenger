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
    public void A_named_program_beside_its_ui_framework_is_a_desktop_app_though_no_project_built_it()
    {
        string program = Write("gtk/Viewer");
        Write("gtk/GtkSharp.dll");
        DesktopProgram? found = DesktopPrograms.Started(program, [], root, built: null, target: null, declared: null);
        Assert.True(found is { Called: "a GTK app" } && found.File == program, $"A named GTK program read as {found}, so a Passenger nobody sees would start it on the person's screen");
    }

    [Fact]
    public void The_assembly_a_named_dotnet_is_given_is_what_runs_past_exec_and_the_hosts_options()
    {
        string assembly = Write("app/App.dll");
        Write("app/Avalonia.Base.dll");
        Assert.Equal(assembly, DesktopPrograms.Started(Dotnet, ["app/App.dll", "--port", "1"], root, null, null, null)?.File);
        Assert.Equal(assembly, DesktopPrograms.Started(Dotnet, ["exec", "--runtimeconfig", "other.json", assembly], root, null, null, null)?.File);
        Assert.Equal(assembly, DesktopPrograms.Started(Dotnet, ["--roll-forward", "Major", assembly], root, null, null, null)?.File);
    }

    [Fact]
    public void A_program_with_no_ui_framework_is_no_desktop_app()
    {
        string tool = Write("tools/tool");
        Write("tools/tool.dll");
        Assert.Null(DesktopPrograms.Started(tool, [], root, null, null, null));
        Assert.Null(DesktopPrograms.Started("not-on-any-path", [], root, null, null, null));
        Assert.Null(DesktopPrograms.Started(Dotnet, ["ef", "database", "update"], root, null, target: Write("app/App.dll"), declared: null));
    }

    [Fact]
    public void A_project_s_run_command_is_read_before_the_project_s_own_program()
    {
        string built = Write("app/App.dll");
        Write("app/Avalonia.Base.dll");
        string other = Write("other/Other");
        Write("other/GtkSharp.dll");
        Assert.Equal("a GTK app", DesktopPrograms.Started(other, [], root, built, built, null)?.Called);
    }

    [Fact]
    public void A_script_a_project_s_run_command_names_starts_the_project_s_app_a_hook_goes_through()
    {
        string built = Write("app/App.dll");
        Write("app/Avalonia.Base.dll");
        DesktopProgram? found = DesktopPrograms.Started(Write("run.sh"), [], root, built, built, null);
        Assert.True(found is { Called: DesktopPrograms.Avalonia, PastHooks: null } && found.File == built, $"A script starting the project's app read as {found}");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("watch")]
    public void A_named_dotnet_run_starts_what_its_project_builds_past_any_hook_given_it(string command)
    {
        string target = Write("app/App.dll");
        Write("app/Avalonia.Base.dll");
        DesktopProgram? found = DesktopPrograms.Started(Dotnet, [command, "--no-build"], root, built: null, target, declared: null);
        Assert.True(found is { Called: DesktopPrograms.Avalonia, PastHooks: { } past } && past.StartsWith($"dotnet {command} ", StringComparison.Ordinal),
            $"dotnet {command} of an Avalonia project read as {found}: the inspector it would be given stops in the .NET CLI, and the app opens on the person's screen");
    }

    [Fact]
    public void A_build_turning_startup_hooks_off_keeps_the_inspector_out()
    {
        string built = Write("app/App.dll");
        Write("app/Avalonia.Base.dll");
        Assert.Null(DesktopPrograms.Started(Dotnet, [built], root, built, built, null)?.PastHooks);
        Write("app/App.runtimeconfig.json", """{ "runtimeOptions": { "configProperties": { "System.StartupHookProvider.IsSupported": false } } }""");
        Assert.NotNull(DesktopPrograms.Started(Dotnet, [built], root, built, built, null)?.PastHooks);
        Write("app/App.runtimeconfig.json", """{ "runtimeOptions": { "configProperties": { "System.StartupHookProvider.IsSupported": true } } }""");
        Assert.Null(DesktopPrograms.Started(Dotnet, [built], root, built, built, null)?.PastHooks);
    }

    [Fact]
    public void A_program_no_build_has_made_is_what_its_project_says_it_is()
    {
        string built = Path.Combine(root, "bin", "App.dll");
        Assert.Equal("a GTK app", DesktopPrograms.Started(Dotnet, [built], root, built, built, "a GTK app")?.Called);
        Assert.Null(DesktopPrograms.Started(Dotnet, [built], root, built, built, null));
    }

    [Fact]
    public void A_windows_desktop_runtime_makes_a_windows_desktop_app()
    {
        string built = Write("app/App.dll");
        Write("app/App.runtimeconfig.json", $$"""{ "runtimeOptions": { "framework": { "name": "{{FrameworkNames.WindowsDesktop}}" } } }""");
        Assert.Equal("a Windows desktop app (WPF or Windows Forms)", DesktopPrograms.Of(built));
    }

    [Theory]
    [InlineData("Avalonia", DesktopPrograms.Avalonia)]
    [InlineData("Avalonia.Desktop", DesktopPrograms.Avalonia)]
    [InlineData("GtkSharp", "a GTK app")]
    [InlineData("Eto.Forms", "an Eto.Forms app")]
    [InlineData("Eto.Parse", null)]
    [InlineData("Microsoft.Maui.Graphics", null)]
    [InlineData("Serilog", null)]
    public void A_project_says_what_it_is_by_the_packages_it_takes(string package, string? called) =>
        Assert.Equal(called, DesktopPrograms.OfProject(Model("App", "Exe", package), []));

    [Fact]
    public void A_project_takes_its_ui_framework_from_the_projects_it_references()
    {
        ProjectModel shared = Model("Shared", "Library", "Avalonia");
        ProjectModel head = Model("App", "Exe") with { ProjectReferences = [shared.Path] };
        Assert.Equal(DesktopPrograms.Avalonia, DesktopPrograms.OfProject(head, [head, shared]));
    }

    [Theory]
    [InlineData("/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit")]
    [InlineData("/System/Library/Frameworks/Cocoa.framework/Versions/A/Cocoa")]
    [InlineData("/System/Library/Frameworks/SwiftUI.framework/Versions/A/SwiftUI")]
    [InlineData("@rpath/QtWidgets.framework/Versions/A/QtWidgets")]
    [InlineData("@rpath/libSDL2-2.0.0.dylib")]
    [InlineData("/opt/homebrew/lib/libglfw.3.dylib")]
    public void A_mac_program_linking_what_opens_windows_is_a_desktop_app(string library)
    {
        string program = WriteBytes("native/Viewer", MachOBytes.Universal(MachOBytes.Thin("/usr/lib/libSystem.B.dylib", library)));
        DesktopProgram? found = DesktopPrograms.Started(program, [], root, null, null, null);
        Assert.True(found is { Called: "a native macOS app", PastHooks: null } && found.File == program, $"A native program linking {library} read as {found}, so a Passenger nobody sees would start it on the person's screen");
    }

    [Fact]
    public void A_mac_program_linking_nothing_that_opens_windows_is_no_desktop_app()
    {
        Assert.Null(DesktopPrograms.Of(WriteBytes("native/tool", MachOBytes.Thin("/usr/lib/libSystem.B.dylib", "/System/Library/Frameworks/CoreFoundation.framework/Versions/A/CoreFoundation"))));
    }

    /// <summary>A bundle's program links what it likes, or is a script: an Electron app's links only the framework beside it.</summary>
    [Fact]
    public void A_program_in_a_macs_app_bundle_is_a_desktop_app_whatever_it_links()
    {
        Assert.Equal("a native macOS app", DesktopPrograms.Of(WriteBytes(Path.Join("Viewer.app", "Contents", "MacOS", "Viewer"), MachOBytes.Thin("@rpath/Electron Framework.framework/Electron Framework"))));
        Assert.Equal("a native macOS app", DesktopPrograms.Of(Write(Path.Join("Script.app", "Contents", "MacOS", "Script"), "#!/bin/sh\n")));
    }

    [Fact]
    public void An_avalonia_app_in_a_macs_app_bundle_stays_an_avalonia_app_the_inspector_goes_into()
    {
        string program = Write(Path.Join("App.app", "Contents", "MacOS", "App"));
        Write(Path.Join("App.app", "Contents", "MacOS", "Avalonia.Base.dll"));
        Assert.Equal(DesktopPrograms.Avalonia, DesktopPrograms.Of(program));
    }

    /// <summary>A single-file publish lists what it holds by name, with nothing beside it to read. The inspector reads an app's
    /// assemblies from its folder, so such an app is no Avalonia app to it, and no start goes through it.</summary>
    [Fact]
    public void A_single_file_publish_on_a_mac_is_known_by_the_ui_framework_inside_it_and_goes_past_the_inspector()
    {
        string program = WriteBytes("single/App", [.. MachOBytes.Thin("/usr/lib/libSystem.B.dylib"), .. new byte[100], .. Encoding.UTF8.GetBytes("Avalonia.Base.dll"), .. new byte[100]]);
        DesktopProgram? found = DesktopPrograms.Started(program, [], root, null, null, null);
        Assert.True(found is { Called: "an Avalonia app, published as a single file" }, $"A single-file Avalonia app read as {found}");
        Assert.NotEqual(DesktopPrograms.Avalonia, found!.Called);
    }

    [Fact]
    public void A_ui_framework_named_across_two_reads_of_a_large_program_is_found_all_the_same()
    {
        const int Read = 1024 * 1024;
        byte[] header = MachOBytes.Thin("/usr/lib/libSystem.B.dylib");
        byte[] name = Encoding.UTF8.GetBytes("Eto.dll");
        byte[] program = new byte[(2 * Read) + 100];
        header.CopyTo(program, 0);
        name.CopyTo(program, Read - 3);
        Assert.Equal("an Eto.Forms app, published as a single file", DesktopPrograms.Of(WriteBytes("single/Large", program)));
    }

    [Theory]
    [InlineData("run", "--project", "../Other/Other.csproj")]
    [InlineData("run", "-p", "../Other/Other.csproj")]
    [InlineData("watch", "--project", "../Other/Other.csproj")]
    public void A_dotnet_run_naming_a_project_names_it_past_the_folder_it_starts_in(string command, string option, string project)
    {
        string folder = Path.Combine(root, "app");
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, project)), DesktopPrograms.NamedProject(Dotnet, [command, "--no-build", option, project], folder));
    }

    [Fact]
    public void A_dotnet_run_names_a_project_or_a_program_file_however_it_is_written_and_no_argument_of_the_program()
    {
        Assert.Equal(PathNames.Canonical(Path.Combine(root, "o.csproj")), DesktopPrograms.NamedProject(Dotnet, ["run", "--project=o.csproj"], root));
        Assert.Equal(PathNames.Canonical(Path.Combine(root, "app.cs")), DesktopPrograms.NamedProject(Dotnet, ["run", "app.cs"], root));
        Assert.Equal(PathNames.Canonical(Path.Combine(root, "app.cs")), DesktopPrograms.NamedProject(Dotnet, ["run", "--file", "app.cs"], root));
        Assert.Null(DesktopPrograms.NamedProject(Dotnet, ["run", "--", "--project", "o.csproj"], root));
        Assert.Null(DesktopPrograms.NamedProject(Dotnet, ["run", "--no-build"], root));
        Assert.Null(DesktopPrograms.NamedProject(Dotnet, ["run", "--property:Extra=a.cs"], root));
        Assert.Null(DesktopPrograms.NamedProject(Dotnet, ["build", "--project", "o.csproj"], root));
        Assert.Null(DesktopPrograms.NamedProject("/bin/other", ["run", "--project", "o.csproj"], root));
    }

    /// <summary>The plan's own project says nothing of what such a run starts, in either direction.</summary>
    [Fact]
    public void A_dotnet_run_naming_another_project_is_what_that_project_says_never_what_the_plans_built()
    {
        string target = Write("console/Tool.dll");
        string other = Path.Combine(root, "gui", "Gui.csproj");
        ProjectModel gui = Model("Gui", "Exe", "GtkSharp") with { Path = other };
        Assert.Equal("a GTK app", DesktopPrograms.OfNamed(other, [gui]));
        Assert.Equal("a GTK app", DesktopPrograms.OfNamed(Path.GetDirectoryName(other)!, [gui]));

        DesktopProgram? found = DesktopPrograms.Started(Dotnet, ["run", "--project", other], root, null, target, "a GTK app", other);
        Assert.True(found is { Called: "a GTK app", PastHooks: not null } && found.File == other, $"dotnet run of a GTK project read as {found}");

        string avalonia = Write("avalonia/App.dll");
        Write("avalonia/Avalonia.Base.dll");
        Assert.Null(DesktopPrograms.Started(Dotnet, ["run", "--project", other], root, null, avalonia, null, other));
    }

    [Fact]
    public void A_dotnet_run_of_a_project_no_window_loaded_is_refused_for_what_cannot_be_read_but_on_linux_which_has_no_display()
    {
        Assert.Equal(OperatingSystem.IsLinux() ? null : DesktopPrograms.UnreadProject, DesktopPrograms.OfNamed(Path.Combine(root, "elsewhere", "Far.csproj"), []));
    }

    /// <summary>A desktop head names Avalonia.Desktop, or only the library holding its XAML: each is an Avalonia app.</summary>
    [Theory]
    [InlineData("Avalonia", true)]
    [InlineData("Avalonia.Desktop", true)]
    [InlineData("Avalonia.Themes.Fluent", true)]
    [InlineData("Eto.Forms", false)]
    [InlineData("Serilog", false)]
    public void A_project_takes_xaml_by_any_package_of_avalonias_of_its_own_or_of_the_projects_it_references(string package, bool takes)
    {
        ProjectModel own = Model("App", "Exe", package);
        ProjectModel shared = Model("Shared", "Library", package);
        ProjectModel head = Model("Head", "Exe") with { ProjectReferences = [shared.Path] };
        Assert.Equal(takes, DesktopPrograms.TakesXaml(own, [own]));
        Assert.Equal(takes, DesktopPrograms.TakesXaml(head, [head, shared]));
    }

    [Fact]
    public void A_restore_says_a_project_takes_xaml_where_a_package_of_another_name_brings_avalonia()
    {
        Assert.True(DesktopPrograms.RestoredXaml(Restored("Vendor.Controls", "Avalonia")));
        Assert.False(DesktopPrograms.RestoredXaml(Restored("Serilog")));
        Assert.False(DesktopPrograms.RestoredXaml(RestoredVersions.None));
    }

    /// <summary>Such a head runs where no inspector goes, and its Hot Reload is dotnet watch's, its Avalonia never mind.</summary>
    [Theory]
    [InlineData("Avalonia.Browser")]
    [InlineData("Avalonia.Android")]
    [InlineData("Avalonia.iOS")]
    public void A_head_of_a_browser_or_a_device_is_no_xaml_app_of_this_desktop(string package)
    {
        ProjectModel shared = Model("Shared", "Library", "Avalonia");
        ProjectModel head = Model("Head", "Exe", package) with { ProjectReferences = [shared.Path] };
        Assert.False(DesktopPrograms.TakesXaml(head, [head, shared]));
        Assert.False(DesktopPrograms.RestoredXaml(Restored(package, "Avalonia")));
    }

    private static RestoredVersions Restored(params string[] packages)
    {
        string libraries = string.Join(", ", packages.Select(package => $"\"{package}/1.0.0\": {{ \"type\": \"package\" }}"));
        return RestoredVersions.Read(new MemoryStream(Encoding.UTF8.GetBytes($"{{ \"targets\": {{ \"net11.0\": {{ {libraries} }} }} }}")));
    }

    [Fact]
    public void A_project_built_as_a_windowed_program_says_so() =>
        Assert.Equal("a windowed program (OutputType WinExe)", DesktopPrograms.OfProject(Model("App", "WinExe"), []));

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
