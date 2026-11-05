using System.Reflection.PortableExecutable;
using System.Text.Json;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A desktop app a start runs: what it is in words, the file it runs from, and what keeps a startup hook given to
/// the start from reaching it, in words; null where the hook goes in with it.</summary>
public sealed record DesktopProgram(string Called, string File, string? PastHooks = null);

/// <summary>What desktop app a program's start runs, read from the start itself: the program it names, the assembly a
/// <c>dotnet</c> is given, the project a <c>dotnet run</c> builds. A program of no framework known here (a native
/// program's, a script's) reads as none, and a project no build has made a program of reads by what the project says.</summary>
public static class DesktopPrograms
{
    /// <summary>An app built with Avalonia, as <see cref="Of"/> names it.</summary>
    public const string Avalonia = "an Avalonia app";

    private const string WindowsDesktop = "a Windows desktop app (WPF or Windows Forms)";

    private const string Windowed = "a windowed program (OutputType WinExe)";

    /// <summary>The switch in a program's runtime configuration that turns its startup hooks off (StartupHookSupport).</summary>
    private const string StartupHooksSwitch = "System.StartupHookProvider.IsSupported";

    /// <summary>The UI frameworks that make a program a desktop app, with what such an app is called: the assembly found beside
    /// it, and the package a project takes the framework by, or a package named under it.</summary>
    private static readonly (string File, string Called, string Package)[] Frameworks =
    [
        ("Avalonia.Base.dll", Avalonia, "Avalonia"), ("Microsoft.WinUI.dll", "a WinUI app", "Microsoft.WindowsAppSDK"), ("Microsoft.Maui.dll", "a .NET MAUI app", "Microsoft.Maui.Controls"),
        ("Uno.UI.dll", "an Uno Platform app", "Uno.WinUI"), ("Eto.dll", "an Eto.Forms app", "Eto.Forms"), ("GtkSharp.dll", "a GTK app", "GtkSharp"),
    ];

    /// <summary>What desktop app a program's file is, in words ("an Avalonia app"): one of a UI framework found beside it, a
    /// WindowsDesktop one (WPF, Windows Forms), or one built as a windowed program (OutputType WinExe); null for any other.</summary>
    public static string? Of(string file)
    {
        string folder = Path.GetDirectoryName(file)!;
        foreach ((string framework, string called, _) in Frameworks)
        {
            if (File.Exists(Path.Combine(folder, framework)))
            {
                return called;
            }
        }
        try
        {
            string config = Path.ChangeExtension(file, ".runtimeconfig.json");
            if (File.Exists(config) && File.ReadAllText(config).Contains(FrameworkNames.WindowsDesktop, StringComparison.Ordinal))
            {
                return WindowsDesktop;
            }
            using FileStream stream = File.OpenRead(file);
            using PEReader reader = new(stream);
            return reader.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsGui ? Windowed : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>What desktop app a project says it is, before any build has made a file to read: by the packages it and the
    /// projects it references take, its Windows desktop framework, or its OutputType; null for any other.</summary>
    public static string? OfProject(ProjectModel project, IReadOnlyList<ProjectModel> projects)
    {
        List<string> packages = [.. project.WithReferenced(projects).SelectMany(p => p.Packages).Select(p => p.Name)];
        foreach ((_, string called, string package) in Frameworks)
        {
            if (packages.Any(taken => taken.Equals(package, StringComparison.OrdinalIgnoreCase) || taken.StartsWith(package + ".", StringComparison.OrdinalIgnoreCase)))
            {
                return called;
            }
        }
        if (project.Evaluated?.Dependencies.Any(d => d.FrameworkReferences.Any(FrameworkNames.IsWindowsDesktop)) == true)
        {
            return WindowsDesktop;
        }
        return string.Equals(project.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase) ? Windowed : null;
    }

    /// <summary>The desktop app a start runs, or null for a start that runs none: <paramref name="program"/> with its
    /// <paramref name="arguments"/> in <paramref name="folder"/>, read before the project's <paramref name="built"/> program
    /// it may wrap; a <c>dotnet run</c> runs what its project builds, <paramref name="target"/>. A file no build has made yet
    /// is what its project says it is, <paramref name="declared"/>.</summary>
    public static DesktopProgram? Started(string program, IReadOnlyList<string> arguments, string folder, string? built, string? target, string? declared)
    {
        if (IsDotnet(program) && arguments is [("run" or "watch") and var command, ..])
        {
            return target is not null && (Of(target) ?? declared) is { } own
                ? new DesktopProgram(own, target, $"dotnet {command} starts it without the inspector that puts it on Avalonia's headless platform, which a configuration naming no program gives it")
                : null;
        }
        if (Runs(program, arguments, folder) is { } file && Of(file) is { } called)
        {
            return new DesktopProgram(called, file, HooksOff(file));
        }
        return built is not null && (Of(built) ?? declared) is { } wrapped ? new DesktopProgram(wrapped, built, HooksOff(built)) : null;
    }

    /// <summary>Whether a program is the <c>dotnet</c> host.</summary>
    public static bool IsDotnet(string program) => string.Equals(Path.GetFileNameWithoutExtension(program), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file a start runs: the assembly a <c>dotnet</c> is given, past <c>exec</c> and its options, each of which
    /// takes a value; else the program, where it is a path; null for any other of the .NET CLI's commands or a name no search found.</summary>
    private static string? Runs(string program, IReadOnlyList<string> arguments, string folder)
    {
        if (!IsDotnet(program))
        {
            return Path.IsPathFullyQualified(program) ? program : null;
        }
        int at = arguments is ["exec", ..] ? 1 : 0;
        while (at < arguments.Count && arguments[at].StartsWith('-'))
        {
            at += 2;
        }
        return at < arguments.Count && arguments[at].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Path.Combine(folder, arguments[at]) : null;
    }

    /// <summary>What keeps startup hooks out of a program: a build that turned them off in its runtime configuration; null
    /// where they go in.</summary>
    private static string? HooksOff(string file)
    {
        try
        {
            string config = Path.ChangeExtension(file, ".runtimeconfig.json");
            if (!File.Exists(config))
            {
                return null;
            }
            using JsonDocument read = JsonDocument.Parse(File.ReadAllText(config));
            JsonElement? properties = JsonMembers.Object(JsonMembers.Object(read.RootElement, "runtimeOptions"), "configProperties");
            return JsonMembers.Bool(properties, StartupHooksSwitch) == false
                ? "Its build turns startup hooks off (StartupHookSupport), so the inspector that puts it on Avalonia's headless platform cannot go in"
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
