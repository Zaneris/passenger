using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Run in Godot Editor (<see cref="RunConfiguration.GodotInEditor"/>): the person's Godot editor open on the
/// project plays the game, as its own Play does. Through Passenger's addon there where it is connected, which plays the
/// scene chosen, builds through this window and, for Debug, holds the game at its start for this Passenger's debugger;
/// else through the editor's IDE messaging, which plays only its main scene and gives no debugger a way in, so the game
/// is built here first and played undebugged, the addon offered. Stop here stops it in the editor. With no editor open,
/// it says so and offers to open one.</summary>
public partial class MainWindow
{
    /// <summary>The first version of Passenger's addon that plays and stops as Passenger asks.</summary>
    private const int AddonPlays = 3;

    /// <summary>How long the addon has to say it took the play.</summary>
    private static readonly TimeSpan PlayTaken = TimeSpan.FromSeconds(10);

    /// <summary>The Godot projects whose editor plays through its IDE messaging because Passenger asked, which Stop stops.</summary>
    private readonly HashSet<string> idePlays = new(PathNames.Comparer);

    /// <summary>Whether Godot's editor plays a game because Passenger asked it to.</summary>
    private bool PlaysInGodotEditor => idePlays.Count > 0 || session.GodotEditors.Editors.Any(e => e.PlayAsked || e.PlaysForPassenger);

    /// <summary>Plays a configuration's game in Godot's editor open on its project, under the debugger where <paramref name="debug"/>
    /// and the addon lets it: null once the editor plays it, else why not.</summary>
    private async Task<string?> PlayInGodotEditorAsync(RunConfiguration chosen, bool debug, CancellationToken cancel)
    {
        UiThread.Check();
        ProjectModel? project = session.RunProjects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project));
        if ((project?.GodotFolder ?? (chosen.RunsScriptGame ? Path.GetDirectoryName(chosen.Project) : null) ?? GodotAtHand()) is not { } folder)
        {
            return $"{chosen.Name} plays no Godot game";
        }
        if (AgentsOwn)
        {
            return $"{chosen.Name} plays in the person's own Godot editor, which an agent's own Passenger does not ask: choose a configuration that runs the game here";
        }
        string? scene;
        if (chosen.GodotScene == RunConfiguration.CurrentScene)
        {
            string? open = Editors.Active?.Path is { } at ? await BackgroundWork.Run(() => GodotProject.SceneOf(folder, at, GodotProject.Read(folder)?.MainScene)) : null;
            if (open is null)
            {
                return $"{chosen.Name} cannot run: it starts in the scene open in the editor, and none is (a .tscn, or a script a scene's node runs)";
            }
            scene = GodotResPaths.Of(folder, open);
        }
        else
        {
            scene = chosen.GodotScene;
        }
        string name = project?.Name ?? Path.GetFileName(folder);
        if (session.GodotEditors.Editors.FirstOrDefault(e => PathNames.Same(e.Project, folder) && e.Addon >= AddonPlays) is { } peer)
        {
            peer.Debugs = debug;
            peer.Builds = chosen.BuildBeforeLaunch;
            peer.PlayAsked = true;
            if (await peer.AskAsync("play", new JsonObject { ["scene"] = scene ?? string.Empty }, Waits.Of(PlayTaken), cancel) is null)
            {
                peer.Debugs = null;
                peer.Builds = null;
                peer.PlayAsked = false;
                return $"Godot's editor on {name} did not take the play within {Waits.Of(PlayTaken).TotalSeconds:0} s";
            }
            status.Text = debug ? $"{name} plays in Godot's editor, built here and debugged by this Passenger" : $"{name} plays in Godot's editor, built here";
            return null;
        }
        string? older = session.GodotEditors.Editors.Any(e => PathNames.Same(e.Project, folder))
            ? $"; Passenger's addon there is too old to play as asked, so Install Passenger's Godot Addon… updates it" : null;
        if (!await session.GodotIde.IsOpenAsync(folder))
        {
            return await OfferGodotEditorAsync(chosen, name);
        }
        if (scene is not null)
        {
            return $"Godot's editor on {name} plays only its main scene when asked without Passenger's addon: Install Passenger's Godot Addon… to play the scene chosen{older}";
        }
        if (project is { IsScriptGame: false } && chosen.BuildBeforeLaunch)
        {
            BuildOutcome built = await buildRun.BuildForAsync(new BuildStep(project.Path, session.Frameworks.Of(project)), cancel);
            if (!built.Succeeded)
            {
                return built.Message;
            }
        }
        // Built here already: the editor plays without building again.
        if (await session.GodotIde.RequestAsync(folder, GodotIdeMessaging.DebugPlay, GodotIdeMessaging.PlayBuilt) is { } refused)
        {
            return $"Godot's editor on {name} {refused}";
        }
        idePlays.Add(folder);
        status.Text = debug
            ? $"{name} plays in Godot's editor undebugged: Godot's IDE messaging gives no debugger a way in, and Passenger's addon does (Install Passenger's Godot Addon…){older}"
            : $"{name} plays in Godot's editor, built here{older}";
        return null;
    }

    /// <summary>With no Godot editor open on the project: why the game cannot play there, once the person was offered Godot's
    /// editor opened on it (its configuration run), which no agent's run is.</summary>
    private async Task<string> OfferGodotEditorAsync(RunConfiguration chosen, string name)
    {
        RunConfiguration? editor = session.Configurations.All.FirstOrDefault(c => c.GodotEditor && PathNames.Same(c.Project, chosen.Project));
        string why = editor is null ? $"Godot's editor is not open on {name}: open it, then run {chosen.Name} again"
            : $"Godot's editor is not open on {name}: {editor.Name} opens it, then run {chosen.Name} again";
        if (ActionWork.Heard is not null || editor is null)
        {
            return why;
        }
        if (!await ConfirmAsync("Open Godot's Editor?", $"Godot's editor is not open on {name}, so it cannot play {chosen.Name}. Open it now, then run {chosen.Name} again once it is up.",
            "Open in Godot Editor", null))
        {
            return why;
        }
        string kept = chosen.Name;
        session.Configurations.Select(editor.Name);
        string? opened = await buildRun.RunAsync();
        session.Configurations.Select(kept);
        return opened is null ? $"Godot's editor opens on {name}: run {chosen.Name} again once it is up" : $"Godot's editor could not open on {name}: {opened}";
    }

    /// <summary>Stop's end of what Godot's editor plays because Passenger asked: the addon or IDE messaging told to stop, each
    /// game's own session ending as its process does.</summary>
    private async Task StopInGodotEditorAsync()
    {
        UiThread.Check();
        List<Task> stopping = [];
        foreach (GodotEditorPeer peer in session.GodotEditors.Editors.Where(e => e.PlayAsked || e.PlaysForPassenger))
        {
            peer.PlayAsked = false;
            peer.PlaysForPassenger = false;
            stopping.Add(peer.AskAsync("stop", [], Waits.Of(PlayTaken)));
        }
        foreach (string folder in idePlays.ToList())
        {
            idePlays.Remove(folder);
            stopping.Add(session.GodotIde.RequestAsync(folder, GodotIdeMessaging.StopPlay, GodotIdeMessaging.NoBody));
        }
        await Task.WhenAll(stopping);
    }
}
