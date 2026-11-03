using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Passenger.App.Debugging;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Passenger's addon for Godot's editor (<see cref="GodotAddonFiles"/>): written into the open Godot project and
/// enabled in its project.godot once the person agrees, one command of Undo, an earlier Passenger's copy updated where
/// it is. A person's window on a Godot project listens for it (<see cref="GodotEditorLink"/>); an editor running it then
/// builds through this window as Play begins, the build's errors in Problems and an up-to-date build skipped; each game
/// Play starts is relayed here as a game of the window's (Live Visual Tree, error pins, live values), and while Settings
/// debugs what Godot plays it is held at its runtime's start, taken by the process it says, and attached to in a Debug tab
/// of its own, which Stop lets go of, the game running on in Godot.</summary>
public partial class MainWindow
{
    private void AddGodotAddon()
    {
        Editors.HeldElsewhere = HeldInGodot;
        Editors.WritingOver = WritingOverGodotAsync;
        // Saves come from any window of the process: each takes those of its own folder's scenes.
        Action<string, byte[]?, byte[]> wrote = (path, _, _) => WroteForGodot(path);
        Text.FileText.Wrote += wrote;
        Closed += (_, _) => Text.FileText.Wrote -= wrote;
        actions!.Add(new IdeAction(AppActions.GodotAddon, "Install Passenger's Godot Addon…", "Godot", _ => IdeErrors.Observe(InstallGodotAddonAsync()))
        {
            WhyNot = _ => Folder is null ? Refusals.NoFolder : GodotFolder() is null ? Refusals.NoGodotProject : null,
            Menus = [new MenuPlace("Tools", 0, 7)],
            Words = ["Godot", "Addon", "Plugin", "Play", "Debug", "Editor"],
            Works = AppActions.GodotAddon,
        });
    }

    /// <summary>How many Godot editors on the open project its addon has connected to this window.</summary>
    public int GodotEditorsConnected => session.GodotEditors.Editors.Count;

    /// <summary>Installs the addon once the person agrees, saying what it writes; says what it came to.</summary>
    private async Task InstallGodotAddonAsync()
    {
        UiThread.Check();
        if (work.Begin(AppActions.GodotAddon, "Reading the project's addons", stoppable: false) is not { } working)
        {
            return;
        }
        using (working)
        {
            await InstallGodotAddonAsync(working, ask: true);
        }
    }

    /// <summary>The install's plan, asked about where <paramref name="ask"/> (else agreed to already, beside the script
    /// editor's settings), then written as one command of Undo. Null once installed, else why not.</summary>
    private async Task<string?> InstallGodotAddonAsync(Working working, bool ask)
    {
        if (GodotFolder() is not { } folder)
        {
            return Said(working, false, Refusals.NoGodotProject);
        }
        string file = Path.Combine(folder, GodotProject.FileName);
        if (SaveBeforeWriting([file]) is { } unsaved)
        {
            return Said(working, false, $"{GodotProject.FileName} could not be saved: {unsaved}");
        }
        (GodotAddonInstall? plan, FileText? had) = await BackgroundWork.Run(() =>
        {
            if (FileText.Load(file) is not { } project)
            {
                return ((GodotAddonInstall?)null, (FileText?)null);
            }
            IReadOnlyList<GodotPlugin> plugins = GodotPlugins.Read(folder, GodotAddon.Enabled(project.Text));
            return (GodotAddon.Plan(folder, GodotAddonFiles.Carried, plugins, project.Text, path => File.Exists(path) ? FileText.Load(path)?.Text : null), project);
        });
        if (plan is null || had is null)
        {
            return Said(working, false, $"{PathNames.Shown(file, Folder)} could not be read");
        }
        string shown = PathNames.Shown(plan.Folder, Folder);
        if (plan.Newer is { } newer)
        {
            return Said(working, true, $"{shown} holds a newer Passenger's addon (version {newer}), left as it is");
        }
        if (plan.Done)
        {
            return Said(working, true, $"Passenger's addon is installed in {shown} and enabled in {GodotProject.FileName}");
        }
        string listed = string.Join(Environment.NewLine, [
            .. plan.Files.Select(f => $"{PathNames.Shown(f.Path, Folder)} ({(f.Made ? "new" : "updated")})"),
            .. plan.Project is null ? [] : new[] { $"{PathNames.Shown(file, Folder)} (the addon enabled)" }]);
        if (ask)
        {
            working.Say("Asking to install Passenger's addon");
            if (!await ConfirmAsync("Install Passenger's Godot Addon?", $"{AddonDoes(folder)} Passenger writes these; Godot's editor takes the addon as it next opens the project.",
                plan.Updated ? "Update Addon" : "Install Addon", Copyable(listed)))
            {
                return Said(working, false, "Passenger's addon was not installed");
            }
        }
        List<string> paths = [.. plan.Files.Select(f => f.Path), .. plan.Project is null ? [] : new[] { file }];
        string? refused = await ChangeFileAsync(plan.Updated ? "Update Passenger's Godot Addon" : "Install Passenger's Godot Addon", paths, () =>
        {
            foreach (GodotAddonFile written in plan.Files)
            {
                if (written.Made || FileText.Load(written.Path) is not { } now)
                {
                    _ = FileText.SaveNew(written.Path, written.Text);
                }
                else
                {
                    _ = FileText.SaveOver(written.Path, now, written.Text);
                }
            }
            if (plan.Project is { } enabled)
            {
                _ = FileText.SaveOver(file, had, enabled);
            }
            return null;
        });
        if (refused is not null)
        {
            return Said(working, false, $"Passenger's addon could not be written: {refused}");
        }
        Editors.GodotPages?.Refresh();
        return Said(working, true, $"Passenger's addon is {(plan.Updated ? "updated" : "installed")} in {shown} and enabled in {GodotProject.FileName}: "
            + "once Godot's editor opens the project again, its Play builds here and this Passenger debugs what it plays");

        // Asked beside the script editor's settings, the words are that action's to tell.
        string? Said(Working work, bool succeeded, string said)
        {
            if (ask)
            {
                Told(work, succeeded, said);
            }
            return succeeded ? null : said;
        }
    }

    /// <summary>What the addon gives, said before it is written.</summary>
    private string AddonDoes(string folder) =>
        $"With Passenger's addon in {Path.GetFileName(folder)}, Godot's Play builds the game's C# in this window, its errors in Problems; each game Play starts "
        + "shows in the Live Visual Tree, its errors pinned to their lines, and this Passenger's debugger attaches to it, so a breakpoint set here stops it; "
        + "Stop here lets the game go on. With Passenger closed, Godot plays as it always did.";

    /// <summary>Listens for Godot's editors on the open folder's Godot project where a person's window keeps it.</summary>
    private void ListenForGodotEditors()
    {
        UiThread.Check();
        if (!keepsWorkspace || GodotFolder() is not { } godot)
        {
            return;
        }
        ProjectSession listening = session;
        IdeErrors.Observe(listening.GodotEditors.ListenAsync(PathNames.Canonical(godot), [godot]));
    }

    /// <summary>The handlers of a session's link to Godot's editors, wired once as the session is bound: Play's build, and each
    /// game the editor plays relayed into the session as a game of its own (<see cref="GodotGame.Relay"/>).</summary>
    private void FollowGodotEditors(ProjectSession next)
    {
        GodotEditorLink link = next.GodotEditors;
        link.EditorsChanged += (_, _) =>
        {
            if (session == next)
            {
                foreach (GodotEditorPeer editor in link.Editors)
                {
                    SelectOnConnect(editor);
                }
                status.Text = link.Editors.Count > 0
                    ? $"Godot's editor on {Path.GetFileName(link.Project)} is connected: its Play builds here, and this Passenger shows what it plays"
                    : "Godot's editor let go of this window";
            }
        };
        link.Handle("build", (peer, _) => session == next ? GodotPlayBuildAsync(peer) : throw new GodotEditorRefused(Refusals.SolutionClosed));
        link.Hear("started", (peer, told) =>
        {
            if (session == next && JsonMembers.Number(told, "session") is int id)
            {
                RelayGodotGame(next, peer, id, JsonMembers.Bool(told, "held") == true);
                peer.PlaysForPassenger |= peer.PlayAsked;
                peer.PlayAsked = false;
            }
        });
        link.Hear("game", (peer, told) =>
        {
            if (JsonMembers.Number(told, "session") is int id && peer.Games.GetValueOrDefault(id) is { } game && JsonMembers.Text(told, "message") is { } message)
            {
                game.Take(Convert.FromBase64String(message));
            }
        });
        link.Hear("stopped", (peer, told) =>
        {
            if (JsonMembers.Number(told, "session") is int id && peer.Games.Remove(id, out GodotGame? game))
            {
                game.RelayEnded();
            }
            peer.PlaysForPassenger &= peer.Games.Count > 0 || peer.PlayAsked;
        });
        link.Hear("scenes", (peer, told) =>
        {
            peer.OpenScenes = JsonMembers.Texts(told, "open");
            peer.UnsavedScenes = JsonMembers.Texts(told, "unsaved");
            peer.SaysUnsaved = JsonMembers.Bool(told, "sure") == true;
            peer.Inspected = JsonMembers.Text(told, "inspected") is { Length: > 0 } inspected ? inspected : null;
        });
        link.Hear("selected", (peer, told) =>
        {
            string? scene = JsonMembers.Text(told, "scene");
            string? node = JsonMembers.Text(told, "node");
            // What Passenger asked the editor to select is no selection of the person's to follow here.
            if (peer.SelectionAsked is (string asked, string at) && asked == scene && at == node)
            {
                peer.SelectionAsked = null;
                return;
            }
            if (session == next)
            {
                ShowGodotSelection(scene, node);
            }
        });
        link.Hear("scene", (peer, told) =>
        {
            peer.EditedScene = JsonMembers.Text(told, "path") ?? string.Empty;
            foreach (GodotGame game in peer.Games.Values)
            {
                game.EditorScene = peer.EditedScene;
            }
        });
        link.EditorGone += (_, peer) =>
        {
            foreach (GodotGame game in peer.Games.Values)
            {
                game.RelayEnded();
            }
            peer.Games.Clear();
            peer.Kept?.Dispose();
            peer.Kept = null;
        };
    }

    /// <summary>The name of the open Godot project's game: its C# project's, else its folder's.</summary>
    private string GodotGameName() => GodotGameProject()?.Name ?? Path.GetFileName(GodotFolder() ?? Folder ?? "Godot");

    private ProjectModel? GodotGameProject() =>
        GodotFolder() is { } folder ? Projects.FirstOrDefault(p => p.GodotFolder is { } game && PathNames.Same(game, folder)) : null;

    /// <summary>Godot's Play built here: the game's project built for its chosen framework as a launch builds it (saved first,
    /// skipped while its last build stands), answered with whether it built and, while Settings debugs what Godot plays, the
    /// variables that hold each game at its start until the debugger takes it, the edits since the build kept for it.</summary>
    private async Task<JsonObject> GodotPlayBuildAsync(GodotEditorPeer peer)
    {
        UiThread.Check();
        string name = GodotGameName();
        if (GodotGameProject() is not { } project)
        {
            // A game of GDScript alone runs no .NET to hold.
            return new JsonObject { ["built"] = true, ["said"] = $"{name} has no C# project to build", ["variables"] = new JsonObject() };
        }
        BuiltEdits? edits = null;
        BuildOutcome built;
        bool builds = peer.Builds ?? true;
        peer.Builds = null;
        if (!builds)
        {
            // A configuration Passenger played that builds nothing first: the game runs as it was last built.
            edits = session.Debug.Kept();
            built = new BuildOutcome(BuildStatus.Succeeded, $"{project.Name} was not built: its configuration builds nothing first");
        }
        else
        {
            using Working? working = work.Begin(AppActions.GodotAddon, $"Building {project.Name} for Godot's Play");
            BuildStep step = new(project.Path, session.Frameworks.Of(project));
            void Reading()
            {
                edits?.Dispose();
                edits = session.Debug.Kept();
            }
            built = working is null ? await buildRun.BuildForAsync(step, CancellationToken.None, Reading) : await buildRun.BuildForAsync(step, working, Reading);
            working?.Tell(built.Succeeded, built.Succeeded ? $"{project.Name} is built for Godot's Play" : built.Message);
        }
        peer.Kept?.Dispose();
        peer.Kept = null;
        if (!built.Succeeded)
        {
            edits?.Dispose();
            status.Text = $"Godot's Play stopped: {built.Message}";
            return new JsonObject { ["built"] = false, ["said"] = built.Message };
        }
        JsonObject variables = [];
        bool debugs = peer.Debugs ?? settings.GodotDebugPlays;
        peer.Debugs = null;
        if (debugs)
        {
            peer.Kept = edits;
            foreach ((string variable, string value) in HeldStart.Variables(changeable: true))
            {
                variables[variable] = value;
            }
        }
        else
        {
            edits?.Dispose();
        }
        return new JsonObject { ["built"] = true, ["said"] = built.Message, ["variables"] = variables };
    }

    /// <summary>A game the editor's session began with, relayed into the window's session as a game of its own, named after
    /// the project ("Spinner in Godot", then "Spinner in Godot #2" beside it); one Play started held is attached to once it
    /// says its process (<see cref="TakeHeldGameAsync"/>).</summary>
    private void RelayGodotGame(ProjectSession into, GodotEditorPeer peer, int id, bool held)
    {
        UiThread.Check();
        if (peer.Games.Remove(id, out GodotGame? before))
        {
            before.RelayEnded();
        }
        string named = $"{GodotGameName()} in Godot";
        string name = peer.Games.Count == 0 ? named : $"{named} #{peer.Games.Count + 1}";
        GodotGame game = GodotGame.Relay(name, GodotFolder() ?? into.Folder ?? string.Empty, peer.Godot,
            (body, _) => peer.TellAsync("send", new JsonObject { ["session"] = id, ["message"] = Convert.ToBase64String(body) }));
        game.EditorScene = peer.EditedScene;
        peer.Games[id] = game;
        if (held)
        {
            void Found(object? sender, int process)
            {
                game.ProcessFound -= Found;
                IdeErrors.Observe(TakeHeldGameAsync(into, peer, id, game, process));
            }
            game.ProcessFound += Found;
        }
        into.TakeGame(game);
        game.RelayConnected();
    }

    /// <summary>A game held at its start for the debugger, taken by its process as it said it: attached to in a Debug tab of its
    /// own, or, where its runtime turns the debugger off or the attach is refused, let go to run on undebugged, saying why.
    /// Either way the addon is told it was released, so it stops no Play for it.</summary>
    private async Task TakeHeldGameAsync(ProjectSession into, GodotEditorPeer peer, int id, GodotGame game, int process)
    {
        UiThread.Check();
        string? undebugged = null;
        try
        {
            if (await BackgroundWork.Run(() => HeldStart.DebuggerOffAsync(process)) is { } off)
            {
                undebugged = off;
            }
            else if (await into.Debug.AttachAsync(process, game.Name, hotReload: true, edits: peer.Kept is { } kept ? into.Debug.Copied(kept) : null,
                startedElsewhere: $"{game.Name} is played by Godot's editor, whose Output shows what it prints; Stop here lets it go on in Godot") is { } refused)
            {
                undebugged = refused;
            }
            else
            {
                game.Debugged = into.Debug.SessionNamed(game.Name);
                BuildRunController.EndsWithGame(game);
            }
            if (undebugged is not null)
            {
                game.Undebugged = undebugged;
                // Held at its start, it would wait for good for a debugger that is not coming.
                await HeldStart.LetGoAsync(process);
                if (session == into)
                {
                    status.Text = $"{game.Name} runs undebugged: {undebugged}";
                }
            }
        }
        finally
        {
            await peer.TellAsync("released", new JsonObject { ["session"] = id, ["said"] = undebugged is null ? string.Empty : $"{game.Name} runs undebugged: {undebugged}" });
        }
    }
}
