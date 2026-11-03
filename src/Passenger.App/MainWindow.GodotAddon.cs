using System.Diagnostics;
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
/// builds through this window as Play begins, the build's errors in Problems and an up-to-date build skipped, and each
/// game Play starts is held at its runtime's start by the variables the build's answer gives, found by its command line
/// naming the editor, and attached to in a Debug tab of its own, which Stop lets go of, the game running on in Godot.</summary>
public partial class MainWindow
{
    /// <summary>How long after a build Play is given to start its games.</summary>
    private static readonly TimeSpan PlayStarts = TimeSpan.FromSeconds(30);

    /// <summary>How long after a game is found its siblings (Godot's several instances) are looked for.</summary>
    private static readonly TimeSpan SiblingsStart = TimeSpan.FromSeconds(2);

    /// <summary>The games of Godot's Play attached to, by process, so none is taken twice.</summary>
    private readonly HashSet<int> godotPlayed = [];

    private void AddGodotAddon()
    {
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
        $"With Passenger's addon in {Path.GetFileName(folder)}, Godot's Play builds the game's C# in this window, its errors in Problems, and this Passenger's "
        + "debugger attaches to each game Play starts, so a breakpoint set here stops it; Stop here lets the game go on. With Passenger closed, Godot plays as it always did.";

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

    /// <summary>The handlers of a session's link to Godot's editors, wired once as the session is bound.</summary>
    private void FollowGodotEditors(ProjectSession next)
    {
        GodotEditorLink link = next.GodotEditors;
        link.EditorsChanged += (_, _) =>
        {
            if (session == next)
            {
                status.Text = link.Editors.Count > 0
                    ? $"Godot's editor on {Path.GetFileName(link.Project)} is connected: its Play builds here, and this Passenger debugs what it plays"
                    : "Godot's editor let go of this window";
            }
        };
        link.Handle("build", (peer, _) => session == next ? GodotPlayBuildAsync(peer) : throw new GodotEditorRefused(Refusals.SolutionClosed));
    }

    /// <summary>The editors whose games are being looked for now.</summary>
    private readonly HashSet<int> godotLooking = [];

    /// <summary>The name of the open Godot project's game: its C# project's, else its folder's.</summary>
    private string GodotGameName() => GodotGameProject()?.Name ?? Path.GetFileName(GodotFolder() ?? Folder ?? "Godot");

    private ProjectModel? GodotGameProject() =>
        GodotFolder() is { } folder ? Projects.FirstOrDefault(p => p.GodotFolder is { } game && PathNames.Same(game, folder)) : null;

    /// <summary>Godot's Play built here: the game's project built as a launch builds it (saved first, skipped while its last
    /// build stands), answered with whether it built and the variables that hold each game at its start for the debugger,
    /// whose games are then looked for and attached to.</summary>
    private async Task<JsonObject> GodotPlayBuildAsync(GodotEditorPeer peer)
    {
        UiThread.Check();
        string name = GodotGameName();
        if (GodotGameProject() is not { } project)
        {
            return new JsonObject { ["built"] = true, ["said"] = $"{name} has no C# project to build", ["variables"] = Held() };
        }
        HashSet<int> known = await BackgroundWork.Run(() => Process.GetProcesses().Select(p =>
        {
            using (p)
            {
                return p.Id;
            }
        }).ToHashSet());
        BuiltEdits? edits = null;
        BuildOutcome built;
        using (Working? working = work.Begin(AppActions.GodotAddon, $"Building {project.Name} for Godot's Play"))
        {
            BuildStep step = new(project.Path, null);
            void Reading()
            {
                edits?.Dispose();
                edits = session.Debug.Kept();
            }
            built = working is null ? await buildRun.BuildForAsync(step, CancellationToken.None, Reading) : await buildRun.BuildForAsync(step, working, Reading);
            working?.Tell(built.Succeeded, built.Succeeded ? $"{project.Name} is built for Godot's Play" : built.Message);
        }
        if (!built.Succeeded)
        {
            edits?.Dispose();
            status.Text = $"Godot's Play stopped: {built.Message}";
            return new JsonObject { ["built"] = false, ["said"] = built.Message };
        }
        IdeErrors.Observe(AttachGodotPlaysAsync(peer.Editor, edits, project.Name, known));
        return new JsonObject { ["built"] = true, ["said"] = built.Message, ["variables"] = Held() };

        static JsonObject Held()
        {
            JsonObject variables = [];
            foreach ((string variable, string value) in HeldStart.Variables(changeable: true))
            {
                variables[variable] = value;
            }
            return variables;
        }
    }

    /// <summary>The games an editor's Play starts, looked for until one is found and its siblings have had their moment, or
    /// Play's time is up, each attached to in a Debug tab of its own, held at its start until then; one the debugger cannot
    /// reach is let go, running on, saying why. <paramref name="kept"/> holds the edits since the build, each game's own copy.</summary>
    private async Task AttachGodotPlaysAsync(int editor, BuiltEdits? kept, string name, HashSet<int> known)
    {
        UiThread.Check();
        if (!godotLooking.Add(editor))
        {
            kept?.Dispose();
            return;
        }
        ProjectSession looking = session;
        HashSet<int> looked = known;
        try
        {
            long began = Stopwatch.GetTimestamp();
            long? lastFound = null;
            int found = 0;
            while (session == looking && !looking.IsEnded && Stopwatch.GetElapsedTime(began) < Waits.Of(PlayStarts)
                && (lastFound is not long last || Stopwatch.GetElapsedTime(last) < SiblingsStart))
            {
                IReadOnlyList<int> games = await BackgroundWork.Run(() => GodotEditorLink.GamesOf(editor, looked));
                foreach (int game in games.Where(godotPlayed.Add))
                {
                    found++;
                    lastFound = Stopwatch.GetTimestamp();
                    string tab = found == 1 ? $"{name} in Godot" : $"{name} in Godot #{found}";
                    string? refused = await looking.Debug.AttachAsync(game, tab, hotReload: true, edits: kept is null ? null : looking.Debug.Copied(kept),
                        startedElsewhere: $"{name} is played by Godot's editor, whose Output shows what it prints; Stop here lets it go on in Godot");
                    if (refused is not null)
                    {
                        // Held at its start, it would wait for good for a debugger that is not coming.
                        await HeldStart.LetGoAsync(game);
                        status.Text = $"{tab} runs undebugged: {refused}";
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }
        finally
        {
            godotLooking.Remove(editor);
            kept?.Dispose();
        }
    }
}
