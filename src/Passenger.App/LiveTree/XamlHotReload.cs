using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;
using Passenger.Xaml;

namespace Passenger.App.LiveTree;

/// <summary>XAML Hot Reload for one inspected app: the XAML files edited since its launch, in the editor or written on disk
/// while not open, each with its text as the app holds it (the build's, read as it launched, else from before its first
/// edit; then the text last applied), applied through the app's inspector (<see cref="LiveApp.ReloadAsync"/>) as a save or
/// Apply Hot Reload Changes asks, and what that came to in Hot Reload's words, each problem at the editor's line. A UI-thread
/// object.</summary>
public sealed class XamlHotReload(LiveApp app, Func<string, TextSnapshot?> texts, Func<string, Task<XamlProject?>> projects)
{
    /// <summary>The text the app holds of each file edited since its launch.</summary>
    private readonly Dictionary<string, string> held = new(PathNames.Comparer);

    /// <summary>The files edited since the app last took them, each with how many edits it has had.</summary>
    private readonly Dictionary<string, int> edited = new(PathNames.Comparer);

    /// <summary>Whether it is a file of the app's framework: an Avalonia app's .axaml (or .xaml), a WPF app's .xaml.</summary>
    public bool Takes(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xaml" => true,
        ".axaml" or ".paml" => app.Kind == LiveAppKind.Avalonia,
        _ => false,
    };

    /// <summary>Whether a XAML file has been edited since the app last took it.</summary>
    public bool Changed => edited.Count > 0;

    /// <summary>Whether this file has.</summary>
    public bool Has(string path) => edited.ContainsKey(path);

    /// <summary>Whether its edits wait for the program to run on: asked while it was paused, whose UI answers nothing.</summary>
    public bool Waiting { get; private set; }

    /// <summary>The texts the app's build read of the folder's XAML files of its framework, read off the window's thread as it
    /// launches, which it holds until it takes another: a file's first edit takes it from there, a file written on disk too.</summary>
    public async Task KeepBuiltAsync(IReadOnlyList<string> files)
    {
        UiThread.Check();
        List<string> taken = [.. files.Where(Takes)];
        List<(string Path, string? Text)> read = await BackgroundWork.Run(() => taken.Select(f => (f, EditorArea.Read(f)?.ToString())).ToList());
        foreach ((string path, string? text) in read)
        {
            if (text is not null)
            {
                held.TryAdd(path, text);
            }
        }
    }

    /// <summary>A XAML file of its framework written on disk while it is not open (another editor's save, a checkout): an edit,
    /// from the text the app holds, unless it holds that text already. Whether it is one.</summary>
    public bool Written(string path, string? text)
    {
        UiThread.Check();
        if (!Takes(path) || app.State == LiveAppState.Ended || text is null || (held.TryGetValue(path, out string? holds) && holds == text))
        {
            return false;
        }
        edited[path] = edited.GetValueOrDefault(path) + 1;
        return true;
    }

    /// <summary>A XAML file of its framework edited: its text from before is the one the app holds, if it took none since.</summary>
    public void Edited(string path, TextSnapshot before)
    {
        UiThread.Check();
        if (!Takes(path) || app.State == LiveAppState.Ended)
        {
            return;
        }
        held.TryAdd(path, before.ToString());
        edited[path] = edited.GetValueOrDefault(path) + 1;
    }

    /// <summary>Applies the files edited (those of <paramref name="only"/>, when named) to the app, each as the editor has it
    /// now; null when none has been. A program paused under the debugger takes none until it runs on (<see cref="Waiting"/>).</summary>
    public async Task<HotReloadOutcome?> ApplyAsync(bool paused, IReadOnlyCollection<string>? only = null)
    {
        UiThread.Check();
        List<string> files = [.. edited.Keys.Where(f => only is null || only.Contains(f, PathNames.Comparer))];
        if (files.Count == 0)
        {
            return null;
        }
        string named = string.Join(", ", files.Select(Path.GetFileName));
        if (paused)
        {
            Waiting = true;
            return new HotReloadOutcome(HotReloadResult.NothingToApply, $"XAML changes to {named} apply once the program runs on");
        }
        Waiting = false;
        using IDisposable working = BackgroundWork.Scope();
        if (app.State != LiveAppState.Connected)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, app.State switch
            {
                LiveAppState.Waiting => $"XAML changes to {named} wait for {app.Name} to show its UI",
                LiveAppState.Refused => $"XAML changes to {named} cannot reach {app.Name}: {app.Problem}",
                _ => $"XAML changes to {named} cannot reach {app.Name}: it has ended",
            });
        }
        List<(string File, XamlReloaded Reloaded, TextSnapshot Text)> done = [];
        foreach (string file in files)
        {
            if (await ReloadAsync(file) is { } reloaded)
            {
                done.Add(reloaded);
            }
        }
        return Outcome(done);
    }

    /// <summary>One file's text applied, or null when it is gone or is no XAML of a project Passenger reads.</summary>
    private async Task<(string, XamlReloaded, TextSnapshot)?> ReloadAsync(string file)
    {
        int edits = edited[file];
        if (texts(file) is not { } text)
        {
            edited.Remove(file);
            return null;
        }
        if (await projects(file) is not { } project)
        {
            return (file, new XamlReloaded([], $"{Path.GetFileName(file)} is in no project whose XAML Passenger reads", []), text);
        }
        string shown = text.ToString();
        XamlChange change = new(file, shown, held.GetValueOrDefault(file), project.AssemblyName, UriOf(project, file))
        {
            CompiledBindings = app.Kind == LiveAppKind.Avalonia ? project.CompilesBindings : null,
        };
        XamlReloaded reloaded;
        try
        {
            reloaded = await app.ReloadAsync(change);
        }
        catch (LiveAppException failed)
        {
            return (file, new XamlReloaded([], $"{Path.GetFileName(file)} could not reach {app.Name}: {failed.Message}", []), text);
        }
        if (reloaded.Refused is null)
        {
            held[file] = shown;
            // An edit made while the app took it is the next one's.
            if (edited.GetValueOrDefault(file) == edits)
            {
                edited.Remove(file);
            }
        }
        return (file, reloaded, text);
    }

    /// <summary>The URI the build gave a file in its assembly: avares:// for Avalonia's, a pack URI for WPF's.</summary>
    private string UriOf(XamlProject project, string file)
    {
        string inside = Path.GetRelativePath(project.Folder, file).Replace('\\', '/');
        return app.Kind == LiveAppKind.Wpf
            ? $"pack://application:,,,/{project.AssemblyName};component/{inside}"
            : $"avares://{project.AssemblyName}/{inside}";
    }

    /// <summary>What the files came to, said as one: refused (with an error the XAML's own, else a change only a build makes),
    /// applied, or made into nothing open.</summary>
    private HotReloadOutcome Outcome(List<(string File, XamlReloaded Reloaded, TextSnapshot Text)> done)
    {
        List<Problem> problems = [.. done.SelectMany(d => d.Reloaded.Problems.Select(p => ProblemOf(d.File, d.Text, p)))];
        List<string> refused = [.. done.Where(d => d.Reloaded.Refused is not null).Select(Placed)];
        List<string> applied = [.. done.Where(d => d.Reloaded.Refused is null && d.Reloaded.Objects.Count > 0)
            .Select(d => $"{Path.GetFileName(d.File)} ({Plural.Listed([.. d.Reloaded.Objects.Distinct()])})")];
        List<string> unmade = [.. done.Where(d => d.Reloaded.Refused is null && d.Reloaded.Objects.Count == 0).Select(d => Path.GetFileName(d.File))];
        List<string> said = [];
        if (applied.Count > 0)
        {
            said.Add($"XAML changes applied: {string.Join(", ", applied)}");
        }
        if (unmade.Count > 0)
        {
            said.Add($"Nothing open in {app.Name} was made from {Plural.Listed(unmade)}: what it makes from {Plural.Agreeing(unmade.Count, "it", "them")} from now takes the edit");
        }
        said.AddRange(refused);
        HotReloadResult result = refused.Count == 0 ? (applied.Count > 0 ? HotReloadResult.Applied : HotReloadResult.NothingToApply)
            : problems.Any(p => p.Severity == ProblemSeverity.Error) ? HotReloadResult.Errors
            : HotReloadResult.RestartNeeded;
        return new HotReloadOutcome(result, string.Join("; ", said))
        {
            Problems = problems,
            Files = [.. done.Where(d => d.Reloaded.Refused is null).Select(d => d.File)],
        };
    }

    /// <summary>Why a file did not apply, with the editor's line of its first error that names one.</summary>
    private static string Placed((string File, XamlReloaded Reloaded, TextSnapshot Text) done) =>
        done.Reloaded.Problems.FirstOrDefault(p => p.Error && p.Line is not null) is { Line: int line } first
            ? $"{done.Reloaded.Refused} (line {AppPlaces.PositionOf(done.File, line, first.Column ?? 1, done.Text).Line + 1})"
            : done.Reloaded.Refused!;

    /// <summary>A problem the app's XAML compiler named, at the editor's line of the text it was given.</summary>
    private static Problem ProblemOf(string file, TextSnapshot text, XamlProblem problem) => new(
        problem.Error ? ProblemSeverity.Error : ProblemSeverity.Warning,
        problem.Message,
        null,
        file,
        problem.Line is int line ? AppPlaces.PositionOf(file, line, problem.Column ?? 1, text) : new TextPosition(0, 0),
        null);
}
