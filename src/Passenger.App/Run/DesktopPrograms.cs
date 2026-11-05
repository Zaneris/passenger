using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A desktop app a start runs: what it is in words, the file it runs from, and what keeps a startup hook given to
/// the start from reaching it, in words; null where the hook goes in with it.</summary>
public sealed record DesktopProgram(string Called, string File, string? PastHooks = null);

/// <summary>What desktop app a program's start runs, read from the start itself: the program it names, the assembly a
/// <c>dotnet</c> is given, the project a <c>dotnet run</c> builds or its arguments name. A native program reads by a Mac's
/// bundle or what it links; one of no framework known here (a native Linux program's, a script's) reads as none, and a
/// project no build has made a program of reads by what the project says.</summary>
public static class DesktopPrograms
{
    /// <summary>An app built with Avalonia, as <see cref="Of"/> names it.</summary>
    public const string Avalonia = "an Avalonia app";

    private const string WindowsDesktop = "a Windows desktop app (WPF or Windows Forms)";

    private const string Windowed = "a windowed program (OutputType WinExe)";

    private const string MacApp = "a native macOS app";

    /// <summary>What a <c>dotnet run</c> of a project no window has loaded is, nothing having been read of it.</summary>
    public const string UnreadProject = "a project not loaded here, so of no known kind";

    /// <summary>The switch in a program's runtime configuration that turns its startup hooks off (StartupHookSupport).</summary>
    private const string StartupHooksSwitch = "System.StartupHookProvider.IsSupported";

    /// <summary>The UI frameworks that make a program a desktop app, with what such an app is called: the assembly found beside
    /// it, and the package a project takes the framework by, or a package named under it.</summary>
    private static readonly (string File, string Called, string Package)[] Frameworks =
    [
        ("Avalonia.Base.dll", Avalonia, "Avalonia"), ("Microsoft.WinUI.dll", "a WinUI app", "Microsoft.WindowsAppSDK"), ("Microsoft.Maui.dll", "a .NET MAUI app", "Microsoft.Maui.Controls"),
        ("Uno.UI.dll", "an Uno Platform app", "Uno.WinUI"), ("Eto.dll", "an Eto.Forms app", "Eto.Forms"), ("GtkSharp.dll", "a GTK app", "GtkSharp"),
    ];

    /// <summary>The libraries that open a macOS program's windows, as each is named or begins, for a program that links one.</summary>
    private static readonly string[] MacWindows = ["AppKit", "Cocoa", "SwiftUI", "UIKit", "QtGui", "QtWidgets", "libSDL2", "libSDL3", "libglfw", "libgtk-", "libwx_osx"];

    /// <summary>How much of a program is read at a time for what it carries inside it.</summary>
    private const int Chunk = 1024 * 1024;

    /// <summary>What desktop app a program's file is, in words ("an Avalonia app"): one of a UI framework found beside it, a
    /// WindowsDesktop one (WPF, Windows Forms), one in a Mac's app bundle, a Mac's program that links what opens windows or
    /// carries a UI framework inside it, or one built as a windowed program (OutputType WinExe); null for any other.</summary>
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
            if (folder.EndsWith(MacBundleFolder, StringComparison.Ordinal))
            {
                return MacApp;
            }
            using FileStream stream = File.OpenRead(file);
            if (MachOFile.Libraries(stream) is { } libraries)
            {
                return libraries.Any(OpensWindows) ? MacApp : Carried(stream);
            }
            stream.Position = 0;
            using PEReader reader = new(stream);
            return reader.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsGui ? Windowed : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Where a Mac's app bundle keeps its program.</summary>
    private static readonly string MacBundleFolder = Path.Join(".app", "Contents", "MacOS");

    private static bool OpensWindows(string library)
    {
        string name = library[(library.LastIndexOf('/') + 1)..];
        return MacWindows.Any(windows => name == windows || (windows.StartsWith("lib", StringComparison.Ordinal) && name.StartsWith(windows, StringComparison.Ordinal)));
    }

    /// <summary>What a program carries inside it, as a single-file publish does, whose list of what it holds names each
    /// assembly: the UI framework found, in words, or null. No hook of Passenger's reads such a program's assemblies, so it
    /// is never <see cref="Avalonia"/>.</summary>
    private static string? Carried(Stream program)
    {
        byte[][] names = [.. Frameworks.Select(f => Encoding.UTF8.GetBytes(f.File))];
        int overlap = names.Max(name => name.Length);
        byte[] read = new byte[Chunk + overlap];
        int kept = 0;
        program.Position = 0;
        while (program.Read(read, kept, Chunk) is var count and > 0)
        {
            ReadOnlySpan<byte> held = read.AsSpan(0, kept + count);
            for (int name = 0; name < names.Length; name++)
            {
                if (held.IndexOf(names[name]) >= 0)
                {
                    return $"{Frameworks[name].Called}, published as a single file";
                }
            }
            kept = Math.Min(overlap, held.Length);
            held[^kept..].CopyTo(read);
        }
        return null;
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

    /// <summary>What desktop app a project named in a plan's project's place is by what the window has loaded: on Linux, where
    /// what it starts finds no display, nothing need be known of one it has not.</summary>
    public static string? OfNamed(string project, IReadOnlyList<ProjectModel> projects) =>
        projects.FirstOrDefault(p => PathNames.Same(p.Path, project) || PathNames.Same(p.Folder, project)) is { } loaded ? OfProject(loaded, projects)
            : OperatingSystem.IsLinux() ? null
            : UnreadProject;

    /// <summary>The project or file-based program a <c>dotnet run</c> or <c>dotnet watch</c> names, in
    /// <paramref name="folder"/>; null where it names none, and runs the project of the folder it starts in.</summary>
    public static string? NamedProject(string program, IReadOnlyList<string> arguments, string folder)
    {
        if (!IsDotnet(program) || arguments is not [("run" or "watch"), ..])
        {
            return null;
        }
        for (int at = 1; at < arguments.Count && arguments[at] != "--"; at++)
        {
            string argument = arguments[at];
            string? named = argument is "--project" or "-p" or "--file" ? arguments.ElementAtOrDefault(at + 1)
                : argument.StartsWith("--project=", StringComparison.Ordinal) || argument.StartsWith("--file=", StringComparison.Ordinal) ? argument[(argument.IndexOf('=', StringComparison.Ordinal) + 1)..]
                : !argument.StartsWith('-') && argument.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? argument
                : null;
            if (named is not null)
            {
                return PathNames.Canonical(Path.Combine(folder, named));
            }
        }
        return null;
    }

    /// <summary>The desktop app a start runs, or null for a start that runs none: <paramref name="program"/> with its
    /// <paramref name="arguments"/> in <paramref name="folder"/>, read before the project's <paramref name="built"/> program
    /// it may wrap; a <c>dotnet run</c> runs what its project builds, <paramref name="target"/>, or <paramref name="other"/>,
    /// a project it names in that one's place. A file no build has made yet, and a project named, is what the project says
    /// it is, <paramref name="declared"/>.</summary>
    public static DesktopProgram? Started(string program, IReadOnlyList<string> arguments, string folder, string? built, string? target, string? declared, string? other = null)
    {
        if (IsDotnet(program) && arguments is [("run" or "watch") and var command, ..])
        {
            return (other ?? target) is { } runs && ((other is null ? Of(runs) : null) ?? declared) is { } own
                ? new DesktopProgram(own, runs, $"dotnet {command} starts it without the inspector that puts it on Avalonia's headless platform, which a configuration naming no program gives it")
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
