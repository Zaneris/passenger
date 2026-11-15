using System.Runtime.InteropServices;
using System.Text.Json;

namespace Passenger.Core;

/// <summary>The .NET a Godot .NET editor Passenger starts runs on, so that its own builds of the game's C# work. On macOS
/// that editor builds with the installer's dotnet where there is one, but runs on DOTNET_ROOT's .NET, else the one the
/// system registers, its newest released major from .NET 8 (its plugins roll forward so). Finding no released SDK of that major
/// where it builds, a Godot carrying Microsoft.Build.Locator before 1.6 takes the PATH's SDK through it, which names a .NET
/// 11 SDK's folders without their separator, and no build finds Microsoft.NET.Sdk. Such a start is given the installer's
/// .NET to run on where the game builds there (and loads, for an editor that runs it), else told why its builds fail.</summary>
public static class GodotEditorDotnet
{
    /// <summary>Where Microsoft's installer puts .NET on macOS, whose dotnet Godot's editor builds with there.</summary>
    public const string InstallerRoot = "/usr/local/share/dotnet";

    /// <summary>The first Microsoft.Build.Locator that names an SDK's folders whatever dotnet ends its base path with.</summary>
    private static readonly Version LocatorFixed = new(1, 6);

    /// <summary>The first SDK whose dotnet --info ends its base path without a separator.</summary>
    private const int UnseparatedSdk = 11;

    /// <summary>The oldest .NET Godot's .NET plugins run on, rolling forward from it to the newest released major.</summary>
    private const int PluginsMajor = 8;

    /// <summary>What a start of Godot's editor is given: variables over its environment, and why its builds of the game's C#
    /// fail where they will; neither where it builds as it stands.</summary>
    public sealed record Given(IReadOnlyDictionary<string, string> Variables, string? WhyNot)
    {
        public static readonly Given AsItStands = new(new Dictionary<string, string>(), null);
    }

    /// <summary>What a start of the Godot found, given <paramref name="variables"/> over this process's environment (the
    /// person's own DOTNET_ROOT among them kept), in <paramref name="folder"/>, needs for its builds of a game on
    /// <paramref name="framework"/>; one that runs the game (<paramref name="runsGame"/>, an editor's, as an export's does
    /// not) needs that game's .NET too. Reads the disk.</summary>
    public static Given For(GodotFound found, IReadOnlyDictionary<string, string> variables, string folder, string? framework, bool runsGame)
    {
        if (!OperatingSystem.IsMacOS() || found.Sharp is null)
        {
            return Given.AsItStands;
        }
        string? Variable(string name) => variables.TryGetValue(name, out string? given) ? given : Environment.GetEnvironmentVariable(name);
        string arch = ProcessorOf(found.Executable);
        string[] path = (Variable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string? onPath = Programs.CommandIn(path, Dotnet.HostName);
        string? builder = Builder(arch, File.Exists) ?? onPath;
        if (builder is null || RunsOn(arch, Variable, Directory.Exists, FirstLine, File.Exists) is not { } runsOn)
        {
            return Given.AsItStands;
        }
        DotnetInstallation? taken = onPath is null ? null : DotnetInstallation.Read(Path.GetDirectoryName(PathNames.Canonical(onPath))!);
        GlobalJsonSdk? asked = GlobalJsonSdk.Read(Dotnet.GlobalJsonIn(folder)?.FullName);
        Machine machine = new(DotnetInstallation.Read(PathNames.Canonical(runsOn)), DotnetInstallation.Read(Path.GetDirectoryName(PathNames.Canonical(builder))!), builder,
            taken?.Sdks.FirstOrDefault(s => asked?.Takes(s) ?? true), LocatorOf(found.Sharp), Variable(RuntimeVariables.RollForwardToPrerelease) == "1");
        string[] roots = variables.ContainsKey(RuntimeVariables.Root) || variables.ContainsKey(RuntimeVariables.RootFor(arch)) ? []
            : [RuntimeVariables.Root, .. Variable(RuntimeVariables.RootFor(arch)) is { Length: > 0 } ? [RuntimeVariables.RootFor(arch)] : Array.Empty<string>()];
        return Decide(machine, runsGame ? FrameworkNames.Major(framework) : null, roots, found.Version is { } version ? $"Godot {version}'s editor" : "Godot's editor");
    }

    /// <summary>The installations a start of Godot's editor meets: the one it runs on, the one it builds with and its dotnet,
    /// the SDK the PATH's dotnet takes in its folder (null for none), the Microsoft.Build.Locator it carries (null where
    /// unsaid), and whether its .NET rolls forward to a preview.</summary>
    internal sealed record Machine(DotnetInstallation RunsOn, DotnetInstallation Builds, string Builder, string? PathSdk, Version? Locator, bool Previews);

    /// <summary>What a start meeting <paramref name="machine"/> is given, its game on .NET <paramref name="game"/> (null
    /// where it runs none): as it stands where its builds work and it runs the game; else the variables
    /// <paramref name="roots"/> (none where the person named the .NET it runs on) naming the installation it builds with,
    /// where they work and run it on that one's .NET; else why not, <paramref name="editor"/> naming it.</summary>
    internal static Given Decide(Machine machine, int? game, IReadOnlyList<string> roots, string editor)
    {
        if (Major(machine.RunsOn, machine.Previews) is not int on)
        {
            return Given.AsItStands;
        }
        bool Builds(int major) => machine.Builds.Sdks.Any(s => !VersionOrder.IsPrerelease(s) && VersionOrder.Major(s) == major)
            || machine.Locator is not { } locator || locator >= LocatorFixed || machine.PathSdk is not { } sdk || VersionOrder.Major(sdk) < UnseparatedSdk;
        bool Runs(int major) => game is not int needs || needs <= major;
        if (Builds(on) && Runs(on))
        {
            return Given.AsItStands;
        }
        bool apart = !PathNames.Same(machine.RunsOn.Root, machine.Builds.Root);
        int? own = apart ? Major(machine.Builds, machine.Previews) : on;
        if (apart && roots.Count > 0 && own is int there && Builds(there) && Runs(there))
        {
            return new Given(roots.ToDictionary(r => r, _ => machine.Builds.Root, StringComparer.Ordinal), null);
        }
        string install = $"install the .NET {Math.Max(on, game ?? 0)} SDK into {machine.Builds.Root} (Microsoft's installer puts it there)";
        string older = (!apart || roots.Count > 0) && own is int fits && Builds(fits) && game > fits ? $", or target the game at net{fits}.0, which it then runs on from there" : string.Empty;
        if (!Builds(on))
        {
            return new Given(new Dictionary<string, string>(), $"{editor} builds no C# here: it runs on .NET {on} of {machine.RunsOn.Root} but builds with {machine.Builder}, "
                + $"which has no released .NET {on} SDK, so it takes the PATH's SDK {machine.PathSdk} through the Microsoft.Build.Locator {machine.Locator} it carries, "
                + $"which names a .NET {UnseparatedSdk} SDK's folders without their separator, and every build finds no Microsoft.NET.Sdk: {install}{older}");
        }
        return new Given(new Dictionary<string, string>(), $"{editor} runs no C# of the game here: it runs on .NET {on} of {machine.RunsOn.Root}, older than the "
            + $".NET {game} the game targets, and builds with {machine.Builder}: {install}{older}");
    }

    /// <summary>The newest major of the runtimes an installation holds that Godot's plugins roll forward to: released ones
    /// alone unless <paramref name="previews"/>, from .NET 8; null for none.</summary>
    internal static int? Major(DotnetInstallation installation, bool previews) => installation.Runtimes
        .Where(r => previews || !VersionOrder.IsPrerelease(r)).Select(VersionOrder.Major).OfType<int>().Where(m => m >= PluginsMajor).Cast<int?>().Max();

    /// <summary>The installer's dotnet Godot's editor on macOS, built for <paramref name="processor"/>, builds with where it
    /// is there, an x64 editor's x64 one first; null for none, where it takes the PATH's.</summary>
    internal static string? Builder(string processor, Func<string, bool> exists) =>
        ((string[])[.. processor == X64 ? [Path.Combine(InstallerRoot, X64, Dotnet.HostName)] : Array.Empty<string>(), Path.Combine(InstallerRoot, Dotnet.HostName)]).FirstOrDefault(exists);

    /// <summary>The x64 processor as .NET names its installations' folders and variables.</summary>
    private const string X64 = "x64";

    /// <summary>The installation Godot's editor on macOS, built for <paramref name="processor"/> ("arm64", "x64"), runs on:
    /// DOTNET_ROOT_ARM64 or _X64's folder, else DOTNET_ROOT's, else the one /etc/dotnet registers for the processor (none
    /// past a file of it saying none), else the installer's; null where that holds no host to load.</summary>
    internal static string? RunsOn(string processor, Func<string, string?> variable, Func<string, bool> isFolder, Func<string, string?> firstLine, Func<string, bool> exists)
    {
        string? root = ((string[])[RuntimeVariables.RootFor(processor), RuntimeVariables.Root]).Select(variable).FirstOrDefault(r => r is { Length: > 0 } && isFolder(r));
        if (root is null && firstLine($"/etc/dotnet/install_location_{processor}") is { } registered)
        {
            root = registered.Length > 0 ? registered : null;
        }
        else if (root is null)
        {
            root = firstLine("/etc/dotnet/install_location") is { Length: > 0 } legacy ? legacy : null;
        }
        root ??= processor == X64 && exists(Path.Combine(InstallerRoot, X64, Dotnet.HostName)) ? Path.Combine(InstallerRoot, X64) : InstallerRoot;
        return isFolder(Path.Combine(root, "host", "fxr")) ? root : null;
    }

    /// <summary>The processor a program runs on here as .NET names its installations' (arm64, x64): this process's where
    /// the program is built for it (a universal one), else the one it is built for.</summary>
    private static string ProcessorOf(string program)
    {
        IReadOnlyList<Architecture> built = ProgramProcessors.Of(program);
        Architecture runs = built.Count == 0 || built.Contains(RuntimeInformation.ProcessArchitecture) ? RuntimeInformation.ProcessArchitecture : built[0];
        return runs.ToString().ToLowerInvariant();
    }

    /// <summary>A file's first line, empty for an empty file; null where there is no file to read.</summary>
    private static string? FirstLine(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadLines(file).FirstOrDefault()?.Trim() ?? string.Empty : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The version of Microsoft.Build.Locator a Godot's GodotSharp folder carries for its editor's tools, as their
    /// dependencies list it; null where they say none.</summary>
    internal static Version? LocatorOf(string sharp)
    {
        const string Locator = "Microsoft.Build.Locator/";
        string deps = Path.Combine(sharp, "Tools", "GodotTools.deps.json");
        try
        {
            using JsonDocument read = JsonDocument.Parse(File.ReadAllText(deps));
            string? carried = JsonMembers.Properties(read.RootElement, "libraries").Select(p => p.Name)
                .FirstOrDefault(n => n.StartsWith(Locator, StringComparison.Ordinal))?[Locator.Length..];
            return Version.TryParse(carried, out Version? version) ? version : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
