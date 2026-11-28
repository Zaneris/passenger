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
    private async Task<Words?> PlayInGodotEditorAsync(RunConfiguration chosen, bool debug, CancellationToken cancel)
    {
        UiThread.Check();
        ProjectModel? project = session.RunProjects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project));
        if ((project?.GodotFolder ?? (chosen.RunsScriptGame ? Path.GetDirectoryName(chosen.Project) : null) ?? GodotAtHand()) is not { } folder)
        {
            return LaunchWords.PlaysNoGodotGame(chosen.Name);
        }
        if (AgentsOwn)
        {
            return LaunchWords.PlaysInPersonsEditor(chosen.Name);
        }
        string? scene;
        if (chosen.GodotScene == RunConfiguration.CurrentScene)
        {
            string? open = Editors.Active?.Path is { } at ? await BackgroundWork.Run(() => GodotProject.SceneOf(folder, at, GodotProject.Read(folder)?.MainScene)) : null;
            if (open is null)
            {
                return LaunchWords.NoSceneOpen(chosen.Name);
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
                return LaunchWords.PlayNotTaken(name, Durations.Said(Waits.Of(PlayTaken)));
            }
            status.Text = LaunchWords.PlaysThroughAddon(debug ? "debugged" : "run", name).Shown;
            return null;
        }
        string older = session.GodotEditors.Editors.Any(e => PathNames.Same(e.Project, folder)) ? "older" : "none";
        if (!await session.GodotIde.IsOpenAsync(folder))
        {
            return await OfferGodotEditorAsync(chosen, name);
        }
        if (scene is not null)
        {
            return LaunchWords.PlaysMainSceneOnly(older, name);
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
            return LaunchWords.EditorRefusedPlay(name, refused);
        }
        idePlays.Add(folder);
        status.Text = LaunchWords.PlaysThroughMessaging(debug ? "debugged" : "run", older, name).Shown;
        return null;
    }

    /// <summary>With no Godot editor open on the project: why the game cannot play there, once the person was offered Godot's
    /// editor opened on it (its configuration run), which no agent's run is.</summary>
    private async Task<Words> OfferGodotEditorAsync(RunConfiguration chosen, string name)
    {
        RunConfiguration? editor = session.Configurations.All.FirstOrDefault(c => c.GodotEditor && PathNames.Same(c.Project, chosen.Project));
        Words why = editor is null ? LaunchWords.GodotEditorNotOpen(name, chosen.Name) : LaunchWords.GodotEditorNotOpenOpens(name, editor.Name, chosen.Name);
        if (ActionWork.Heard is not null || editor is null)
        {
            return why;
        }
        if (!await ConfirmAsync(LaunchWords.OpenGodotEditorTitle.Shown, LaunchWords.OpenGodotEditorQuestion(name, chosen.Name).Shown, LaunchWords.OpenInGodotEditor.Shown, null))
        {
            return why;
        }
        string kept = chosen.Name;
        session.Configurations.Select(editor.Name);
        Words? opened = await buildRun.RunAsync();
        session.Configurations.Select(kept);
        return opened is null ? LaunchWords.GodotEditorOpens(name, chosen.Name) : LaunchWords.GodotEditorNotOpened(name, opened);
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
