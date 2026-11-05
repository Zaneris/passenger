using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Run;

/// <summary>What desktop app a program's start runs, read from the start as it stands in a temporary folder: the program a
/// configuration names, the assembly a <c>dotnet</c> is given and the project a <c>dotnet run</c> builds, whatever the
/// project's own build left, so a Passenger nobody sees knows what it would put on the person's screen.</summary>
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

    [Fact]
    public void A_project_built_as_a_windowed_program_says_so() =>
        Assert.Equal("a windowed program (OutputType WinExe)", DesktopPrograms.OfProject(Model("App", "WinExe"), []));

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
