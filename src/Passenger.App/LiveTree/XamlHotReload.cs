using System.Text.Json;
using System.Xml;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Languages.WinForms;
using Passenger.Roslyn.Extension;
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

    /// <summary>The text each file was last heard written on disk with.</summary>
    private readonly Dictionary<string, string> written = new(PathNames.Comparer);

    /// <summary>The files whose edits the app took since its launch: a .NET MAUI app's go with each update of its code.</summary>
    private readonly HashSet<string> taken = new(PathNames.Comparer);

    /// <summary>How many edits each file had when its text went into a .NET MAUI app's update.</summary>
    private readonly Dictionary<string, int> sent = new(PathNames.Comparer);

    /// <summary>Whether it is a file of the app's framework: an Avalonia app's .axaml (or .xaml), a WPF or .NET MAUI app's .xaml,
    /// a Windows Forms app's designer code (.Designer.cs); none of a WinUI app's, whose XAML is compiled into it.</summary>
    public bool Takes(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xaml" => app.Kind is LiveAppKind.Avalonia or LiveAppKind.Wpf or LiveAppKind.Maui,
        ".axaml" or ".paml" => app.Kind == LiveAppKind.Avalonia,
        ".cs" => app.Kind == LiveAppKind.WinForms && IsDesigner(path),
        _ => false,
    };

    /// <summary>Whether a file's edits may reach an app through its inspector: XAML, or a Windows Forms form's designer code.</summary>
    public static bool Reaches(string path) => Xaml.XamlService.IsXaml(path) || IsDesigner(path);

    private static bool IsDesigner(string path) => path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the files' changes are called where Hot Reload says what they came to: design changes for designer code alone.</summary>
    private static string Changes(IEnumerable<string> files) => files.All(IsDesigner) ? "Design changes" : "XAML changes";

    /// <summary>Whether its XAML reaches the app as code: a .NET MAUI app's, whose code MAUI's source generator writes, taken in
    /// with the program's C# update and patched into its open pages by MAUI's own XAML Incremental Hot Reload.</summary>
    public bool ThroughCode => app.Kind == LiveAppKind.Maui;

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
    /// from the text the app holds, unless it holds that text already, or the same write was heard before (the IDE's own,
    /// then the watcher's word of it), which is one edit. Whether it is one.</summary>
    public bool Written(string path, string? text)
    {
        UiThread.Check();
        if (!Takes(path) || app.State == LiveAppState.Ended || text is null || (held.TryGetValue(path, out string? holds) && holds == text)
            || (edited.ContainsKey(path) && written.GetValueOrDefault(path) == text))
        {
            return false;
        }
        written[path] = text;
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
        string changes = Changes(files);
        if (ThroughCode)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, $"{changes} to {named} reach {app.Name} as code, through the debugger: Run it with Hot Reload on, or Debug it");
        }
        if (paused)
        {
            Waiting = true;
            return new HotReloadOutcome(HotReloadResult.NothingToApply, $"{changes} to {named} apply once the program runs on");
        }
        Waiting = false;
        using IDisposable working = BackgroundWork.Scope();
        if (app.State != LiveAppState.Connected)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, app.State switch
            {
                LiveAppState.Waiting => $"{changes} to {named} wait for {app.Name} to show its UI",
                LiveAppState.Refused => $"{changes} to {named} cannot reach {app.Name}: {app.Problem}",
                _ => $"{changes} to {named} cannot reach {app.Name}: it has ended",
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

    /// <summary>For a .NET MAUI app's update of its code: each XAML file it took edits of or has edits for, as the editor has
    /// it (as the app holds it while the program is <paramref name="paused"/>, whose pages take it once it runs on), with
    /// the text the app holds; the edits each has had are kept, so one made as the update is made is the next's.</summary>
    public IReadOnlyList<HotReloadMarkup> Markup(bool paused)
    {
        UiThread.Check();
        List<HotReloadMarkup> files = [];
        sent.Clear();
        foreach (string file in taken.Union(edited.Keys, PathNames.Comparer).Order(PathNames.Comparer))
        {
            if (texts(file) is { } text && held.GetValueOrDefault(file) is { } before)
            {
                files.Add(new HotReloadMarkup(file, paused ? before : text.ToString(), before));
                sent[file] = edited.GetValueOrDefault(file);
            }
        }
        return files;
    }

    /// <summary>How many updates MAUI's XAML Incremental Hot Reload has heard in the app, asked before one its XAML goes in;
    /// null where its inspector cannot say.</summary>
    public async Task<int?> UpdatesHeardAsync()
    {
        UiThread.Check();
        if (app.State != LiveAppState.Connected)
        {
            return null;
        }
        try
        {
            return await app.UpdatesHeardAsync();
        }
        catch (LiveAppException)
        {
            return null;
        }
    }

    /// <summary>What a .NET MAUI app's XAML edits came to once the update of its code (<paramref name="code"/>) was made of
    /// <paramref name="markup"/>: each file's open pages as MAUI patched them, said by its inspector from the update after the
    /// <paramref name="heard"/> ones; nothing to say where no file was edited or the update was not taken (its errors, the
    /// update's own, keep the edits for the next).</summary>
    public async Task<HotReloadOutcome?> UpdatedAsync(HotReloadOutcome code, IReadOnlyList<HotReloadMarkup> markup, int? heard, bool paused)
    {
        UiThread.Check();
        List<HotReloadMarkup> files = [.. markup.Where(m => edited.ContainsKey(m.Path))];
        if (files.Count == 0 || code.Result is not (HotReloadResult.Applied or HotReloadResult.NothingToApply))
        {
            return null;
        }
        string named = string.Join(", ", files.Select(f => Path.GetFileName(f.Path)));
        Waiting = paused;
        if (paused)
        {
            // The update kept the XAML as the app holds it: the edits go in with the next, once the program runs on.
            return new HotReloadOutcome(HotReloadResult.NothingToApply, $"XAML changes to {named} apply once the program runs on");
        }
        foreach (HotReloadMarkup file in markup)
        {
            Took(file);
        }
        if (code.Result == HotReloadResult.NothingToApply)
        {
            return null;
        }
        if (heard is not int before)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, $"XAML changes to {named} went into {app.Name}'s code, but it cannot say what MAUI made of them: {app.Problem ?? "its inspector is not connected"}");
        }
        // MAUI patches nothing of a change of comments or of the white space between tags alone, which shows as it is.
        List<HotReloadMarkup> patched = [.. files.Where(f => !ReadsSame(f.Before, f.Text))];
        if (patched.Count == 0)
        {
            return new HotReloadOutcome(HotReloadResult.NothingToApply, $"XAML changes to {named} change nothing {app.Name} shows") { Files = [.. files.Select(f => f.Path)] };
        }
        List<(string File, XamlReloaded Reloaded, TextSnapshot Text)> done = [];
        foreach (HotReloadMarkup file in patched)
        {
            TextSnapshot text = new TextDocument(file.Text).Snapshot;
            XamlProject? project = await projects(file.Path);
            // As MAUI's loader reads it, its implicit namespaces declared.
            string declared = Passenger.Xaml.Features.MauiPreviewText.Declared(file.Text);
            // Its path in the project, by which MAUI's build names what it made of the file.
            string relative = project is null ? Path.GetFileName(file.Path) : PathNames.Relative(file.Path, project.Folder) ?? Path.GetFileName(file.Path);
            XamlChange change = new(file.Path, declared, Passenger.Xaml.Features.MauiPreviewText.Declared(file.Before), project?.AssemblyName ?? string.Empty, relative)
            {
                Heard = before,
                Class = XamlFile.ClassAttribute(Passenger.Xaml.Syntax.XmlTree.Parse(declared))?.Value,
                Whole = code.Remade.Contains(file.Path, PathNames.Comparer) || MarkupChanged(file.Before, file.Text) ? true : null,
            };
            try
            {
                done.Add((file.Path, await app.ReloadAsync(change), text));
            }
            catch (LiveAppException failed)
            {
                done.Add((file.Path, new XamlReloaded([], $"{Path.GetFileName(file.Path)} went into {app.Name}'s code, but it could not say what MAUI made of it: {failed.Message}", []), text));
            }
        }
        return Outcome(done);
    }

    /// <summary>Whether two texts of a .NET MAUI XAML file read the same as XML but for comments and the white space between
    /// tags, each element's attributes in any order; false for a text that does not read.</summary>
    private static bool ReadsSame(string before, string now)
    {
        try
        {
            using XmlReader was = MauiReader(before);
            using XmlReader read = MauiReader(now);
            while (true)
            {
                bool more = was.Read();
                if (more != read.Read())
                {
                    return false;
                }
                if (!more)
                {
                    return true;
                }
                if (was.NodeType != read.NodeType || was.Name != read.Name || was.Value != read.Value || !Attributes(was).SequenceEqual(Attributes(read)))
                {
                    return false;
                }
            }
        }
        catch (XmlException)
        {
            return false;
        }

        static XmlReader MauiReader(string text) => XmlReader.Create(new StringReader(Passenger.Xaml.Features.MauiPreviewText.Declared(text)),
            new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });

        static List<(string, string)> Attributes(XmlReader reader)
        {
            List<(string, string)> attributes = [];
            for (bool at = reader.MoveToFirstAttribute(); at; at = reader.MoveToNextAttribute())
            {
                attributes.Add((reader.Name, reader.Value));
            }
            reader.MoveToElement();
            return [.. attributes.Order()];
        }
    }

    /// <summary>Whether a .NET MAUI XAML file's change reaches a markup extension, a C# expression or a property written as
    /// an element, any of which MAUI's patch of an open page may pass over without a word, so the page is made again.</summary>
    private static bool MarkupChanged(string before, string now)
    {
        try
        {
            return !Markup(before).SequenceEqual(Markup(now));
        }
        catch (XmlException)
        {
            return true;
        }

        static List<string> Markup(string text)
        {
            List<string> found = [];
            using XmlReader reader = XmlReader.Create(new StringReader(Passenger.Xaml.Features.MauiPreviewText.Declared(text)),
                new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName.Contains('.', StringComparison.Ordinal))
                {
                    found.Add(reader.ReadOuterXml());
                    continue;
                }
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string element = reader.Name;
                    for (bool at = reader.MoveToFirstAttribute(); at; at = reader.MoveToNextAttribute())
                    {
                        if (reader.Value.TrimStart().StartsWith('{') && !reader.Value.TrimStart().StartsWith("{}", StringComparison.Ordinal))
                        {
                            found.Add($"{element} {reader.Name}={reader.Value}");
                        }
                    }
                    reader.MoveToElement();
                }
                reader.Read();
            }
            found.Sort(StringComparer.Ordinal);
            return found;
        }
    }

    /// <summary>A file's text in a .NET MAUI app's update the app took: what it holds from now, its edits since the next's.</summary>
    private void Took(HotReloadMarkup file)
    {
        held[file.Path] = file.Text;
        taken.Add(file.Path);
        if (sent.TryGetValue(file.Path, out int edits) && edited.GetValueOrDefault(file.Path) == edits)
        {
            edited.Remove(file.Path);
        }
    }

    /// <summary>One file's text applied, or null when it is gone, is no XAML of a project Passenger reads, or is designer code
    /// whose edits changed no statement.</summary>
    private async Task<(string, XamlReloaded, TextSnapshot)?> ReloadAsync(string file)
    {
        int edits = edited[file];
        if (texts(file) is not { } text)
        {
            edited.Remove(file);
            return null;
        }
        if (IsDesigner(file))
        {
            return await DesignedAsync(file, text, edits);
        }
        if (await projects(file) is not { } project || project.UriOf(file) is not { } uri)
        {
            return (file, new XamlReloaded([], $"{Path.GetFileName(file)} is in no project whose XAML Passenger reads", []), text);
        }
        string shown = text.ToString();
        XamlChange change = new(file, shown, held.GetValueOrDefault(file), project.AssemblyName, uri)
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

    /// <summary>A form's designer code applied: what its edits since the app took it changed, run in the app's open forms of
    /// its class; null where they changed no statement.</summary>
    private async Task<(string, XamlReloaded, TextSnapshot)?> DesignedAsync(string file, TextSnapshot text, int edits)
    {
        string shown = text.ToString();
        if (held.GetValueOrDefault(file) is not { } before)
        {
            return (file, new XamlReloaded([], $"{Path.GetFileName(file)} as {app.Name} was built is not known, so what changed since is not: run it again to take the edit", []), text);
        }
        IReadOnlyList<DesignerEdit> changed = await BackgroundWork.Run(() => DesignerCode.Changed(before, shown));
        List<string> objects = [];
        List<XamlProblem> problems = [];
        foreach (DesignerEdit edit in changed)
        {
            DesignedChange change = new(file, edit.Class, [.. edit.Source.Usings, .. WinForms.FormDesign.ImplicitUsings],
                JsonSerializer.Serialize([.. edit.Statements], FormDesignWire.Statements), edit.Lines, edit.Gone);
            try
            {
                XamlReloaded taken = await app.DesignedAsync(change);
                objects.AddRange(taken.Objects);
                problems.AddRange(taken.Problems);
            }
            catch (LiveAppException failed)
            {
                return (file, new XamlReloaded([], $"{Path.GetFileName(file)} could not reach {app.Name}: {failed.Message}", []), text);
            }
        }
        held[file] = shown;
        if (edited.GetValueOrDefault(file) == edits)
        {
            edited.Remove(file);
        }
        return changed.Count == 0 ? null : (file, new XamlReloaded(objects, null, problems), text);
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
            said.Add($"{Changes(done.Select(d => d.File))} applied: {string.Join(", ", applied)}");
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
