using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A new Godot 4 game's files, as Godot's editor writes a new project's: its project.godot (the main scene its game
/// starts in, the features of its Godot's version), a main scene whose root runs a script that prints as the game starts,
/// that script (C# or GDScript), and for C# its project on the Godot.NET.Sdk of the Godot found with a nuget.config that
/// takes Godot's own packages from that Godot's folder, so a new game restores with no network. Pure: each file a path
/// under the game's folder and its text.</summary>
public static class GodotGames
{
    /// <summary>The main scene's res:// path.</summary>
    public const string MainScene = "res://main.tscn";

    /// <summary>What the script prints as the game starts.</summary>
    public const string Greeting = "Hello from Godot!";

    /// <summary>Godot's download page, where both its builds come from.</summary>
    private const string Downloads = "godotengine.org/download";

    /// <summary>Why a game cannot be made on the Godot found, in words that say how to get Godot: none found
    /// (<paramref name="none"/> saying so), one not there, one that cannot run a game of GDScript alone
    /// (<paramref name="scripts"/>) or of C#, or for C# one naming no version for its project's Godot.NET.Sdk; null where it can.</summary>
    public static string? WhyNot(GodotFound? found, bool scripts, string none)
    {
        string how = $"Godot 4 comes from {Downloads}";
        if (found is not { Exists: true })
        {
            return found is null ? $"A Godot game needs Godot, and {none}. {how}" : $"A Godot game needs Godot: {found.WhyNot}. {how}";
        }
        if (scripts)
        {
            return found.WhyNotScripts is { } old ? $"A Godot game needs Godot 4: {old}. {how}" : null;
        }
        if (found.WhyNot is { } why)
        {
            return $"A Godot game in C# needs Godot's .NET build: {why}. {how}";
        }
        return found.Version is null ? $"{found.Executable} says no version, which its game's Godot.NET.Sdk takes" : null;
    }

    /// <summary>A game's files: <paramref name="version"/> is the Godot found's ("4.7.2"), <paramref name="framework"/> the
    /// project's target framework and <paramref name="packages"/> the folder of Godot's own packages, for C#.</summary>
    public static IReadOnlyList<(string Path, string Text)> Files(string name, bool csharp, string version, string? framework = null, string? packages = null)
    {
        List<(string, string)> files =
        [
            (GodotProject.FileName, Project(name, csharp, version)),
            ("main.tscn", Scene(csharp)),
            (csharp ? "Main.cs" : "main.gd", csharp ? Script : GDScript),
            (".gitignore", ".godot/\n/android/\n"),
        ];
        if (csharp)
        {
            files.Add(($"{name}.csproj", ProjectFile(version, framework ?? "net8.0")));
            if (packages is not null)
            {
                files.Add(("nuget.config", NuGetConfig(packages)));
            }
        }
        return files;
    }

    /// <summary>The frameworks a new game's project can target so Godot's .NET host runs it, newest first: those offered and
    /// those of the runtimes installed, from .NET 8, which Godot's own plugins target, to the newest runtime installed that is
    /// no preview, as the host rolls forward to a released runtime alone; all offered where no runtime is known.</summary>
    public static IReadOnlyList<string> Runnable(IReadOnlyList<string> offered, IReadOnlyList<string> runtimes)
    {
        List<int> released = [.. runtimes.Where(r => !VersionOrder.IsPrerelease(r)).Select(VersionOrder.Major).OfType<int>()];
        if (released.Count == 0)
        {
            return offered;
        }
        int newest = released.Max();
        return [.. offered.Select(FrameworkNames.Major).OfType<int>()
            .Concat(released).Where(major => major >= 8 && major <= newest).Distinct().OrderDescending()
            .Select(major => $"net{major.ToString(System.Globalization.CultureInfo.InvariantCulture)}.0")];
    }

    private static string Project(string name, bool csharp, string version)
    {
        string features = string.Join('.', version.Split('.').Take(2));
        string dotnet = csharp ? $"\n[dotnet]\n\nproject/assembly_name={GodotWrites.Quoted(name)}\n" : string.Empty;
        return "; Engine configuration file.\n; It's best edited using the editor UI and not directly,\n; since the parameters that go here are not all obvious.\n"
            + ";\n; Format:\n;   [section] ; section goes between []\n;   param=value ; assign values to parameters\n\nconfig_version=5\n\n[application]\n\n"
            + $"config/name={GodotWrites.Quoted(name)}\nrun/main_scene=\"{MainScene}\"\n"
            + $"config/features=PackedStringArray(\"{features}\"{(csharp ? ", \"C#\"" : string.Empty)}, \"Forward Plus\")\n{dotnet}";
    }

    private static string Scene(bool csharp) =>
        $"[gd_scene format=3]\n\n[ext_resource type=\"Script\" path=\"res://{(csharp ? "Main.cs" : "main.gd")}\" id=\"1_main\"]\n\n"
        + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1_main\")\n";

    private const string Script = $$"""
        using Godot;

        public partial class Main : Node2D
        {
            public override void _Ready()
            {
                GD.Print("{{Greeting}}");
            }
        }

        """;

    private const string GDScript = $"extends Node2D\n\n\nfunc _ready() -> void:\n\tprint(\"{Greeting}\")\n";

    private static string ProjectFile(string version, string framework) =>
        $"<Project Sdk=\"Godot.NET.Sdk/{version}\">\n  <PropertyGroup>\n    <TargetFramework>{framework}</TargetFramework>\n"
        + "    <EnableDynamicLoading>true</EnableDynamicLoading>\n  </PropertyGroup>\n</Project>\n";

    /// <summary>Godot's packages from its own folder and every other from nuget.org, so a restore asks no network for Godot's;
    /// a comment says how a feed of the person's is added, as a mapping takes each package only from the feeds it names.</summary>
    private static string NuGetConfig(string packages) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <add key="Godot" value="{Passenger.Text.XmlEntities.Encode(packages, '"')}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
          </packageSources>
          <!-- Each package comes only from the feeds whose patterns below match it, so a feed added above gives nothing
               until it is mapped too. To add one of your own:
                 under packageSources:       <add key="MyFeed" value="https://example.com/nuget/v3/index.json" />
                 under packageSourceMapping: <packageSource key="MyFeed"><package pattern="MyCompany.*" /></packageSource>
               The most specific pattern wins, so MyCompany.* comes from MyFeed while * still takes nuget.org. -->
          <packageSourceMapping>
            <packageSource key="Godot">
              <package pattern="Godot*" />
            </packageSource>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>

        """;
}
