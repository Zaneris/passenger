using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The person's Godot editor as an agent's acts reach it (<see cref="Mcp.PassengerTools.GodotEditor"/>): the
/// editor on the folder's Godot project connected through Passenger's addon, or why none is (an agent's own Passenger, the
/// addon not in the project, older than one that takes them, or no editor open); and a scene played there, debugged here.</summary>
public partial class MainWindow
{
    /// <summary>The first version of Passenger's addon that takes an agent's acts on the scenes its editor edits.</summary>
    internal const int AddonActs = 6;

    /// <summary>The editor an agent's act goes to; throws saying why there is none.</summary>
    internal async Task<GodotEditorPeer> GodotEditorForAgentAsync()
    {
        UiThread.Check();
        if (AgentsOwn)
        {
            throw new InvalidOperationException(Refusals.GodotEditorOfPerson);
        }
        if (GodotFolder() is not { } folder)
        {
            throw new InvalidOperationException(Refusals.NoGodotProject);
        }
        if (session.GodotEditors.Editors.FirstOrDefault(e => e.Addon >= AddonActs) is { } editor)
        {
            return editor;
        }
        string name = Path.GetFileName(folder);
        if (session.GodotEditors.Editors.FirstOrDefault() is { } older)
        {
            throw new InvalidOperationException(Refusals.GodotAddonOlder(name, older.Addon));
        }
        bool addon = await BackgroundWork.Run(() => FileText.Load(Path.Combine(folder, GodotProject.FileName)) is { } project
            && GodotPlugins.Read(folder, GodotAddon.Enabled(project.Text)).Any(p => GodotAddon.Is(p) && p.Enabled));
        string? opens = session.Configurations.All.FirstOrDefault(c => c.GodotEditor)?.Name;
        throw new InvalidOperationException(Refusals.NoGodotEditor(name, addon, opens));
    }

    /// <summary>The folder's Godot project's scene (res://) by the path an agent gave: res:// or uid:// as given, else a path
    /// of the open folder's; null for none given. Throws where it is outside the project.</summary>
    internal string? GodotSceneOf(string? given)
    {
        if (given is null or "")
        {
            return null;
        }
        if (given.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal) || given.StartsWith(GodotUids.Scheme, StringComparison.Ordinal))
        {
            return given;
        }
        string folder = GodotFolder() ?? throw new InvalidOperationException(Refusals.NoGodotProject);
        string full = PathNames.Canonical(Path.GetFullPath(given, Folder ?? folder));
        return GodotResPaths.Of(folder, full)
            ?? throw new ArgumentException($"{given} is outside the Godot project at {PathNames.Shown(folder, Folder)}");
    }

    /// <summary>Plays a scene of the folder's Godot game (res://; null for its main scene) in the person's Godot editor,
    /// debugged here, as Run in Godot Editor plays it: null once the editor took it, else why not.</summary>
    internal Task<string?> PlayInGodotEditorForAgentAsync(string? scene, CancellationToken cancel)
    {
        UiThread.Check();
        if (AgentsOwn)
        {
            return Task.FromResult<string?>(Refusals.GodotEditorOfPerson);
        }
        string? folder = GodotFolder();
        ProjectModel? project = folder is null ? null : session.RunProjects.FirstOrDefault(p => p.GodotFolder is { } at && PathNames.Same(at, folder));
        if (project is null)
        {
            return Task.FromResult<string?>(Refusals.NoGodotProject);
        }
        RunConfiguration? game = session.Configurations.Chosen is { GodotInEditor: true } chosen && PathNames.Same(chosen.Project, project.Path) ? chosen
            : session.Configurations.All.FirstOrDefault(c => c.Generated && !c.GodotEditor && PathNames.Same(c.Project, project.Path));
        return game is null ? Task.FromResult<string?>($"{project.Name} has no configuration that runs its game")
            : PlayInGodotEditorAsync(game with { GodotInEditor = true, GodotScene = scene }, debug: true, cancel);
    }
}
