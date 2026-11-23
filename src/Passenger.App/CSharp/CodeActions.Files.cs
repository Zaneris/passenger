using Passenger.App.Bdd;
using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.History;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>What a run over many files does to each: Reformat Code, Code Cleanup or Optimize Imports.</summary>
public enum Tidying
{
    Reformat,
    Cleanup,
    OptimizeImports,
}

/// <summary>Why a run over many files passed a file over.</summary>
public enum PassedOver
{
    ReadOnly,
    Generated,
    NotTakenIn,
    Unread,
    Unsaved,
}

/// <summary>What the Explorer has chosen for a run over many files: what the words call it ("of src/App"), the files of
/// it a run takes, walked off the window's thread, and why a run cannot take it (null where it can).</summary>
public sealed record ChosenFiles(string Of, Func<Tidying, IReadOnlyList<string>> Files, Func<Tidying, string?> WhyNot);

/// <summary>A file a run over many files passed over, with why in the words an agent is told it.</summary>
public sealed record FileLeft(string Path, PassedOver Why, string Said);

/// <summary>A file a run over many files changed, with what it did there in the status bar's words.</summary>
public sealed record FileTidied(string Path, string Done);

/// <summary>What a run over many files came to: how many it was asked of, those it changed, those it passed over, and
/// whether it was stopped partway, what it did by then kept.</summary>
public sealed record FilesTidied(int Files, IReadOnlyList<FileTidied> Changed, IReadOnlyList<FileLeft> Left, bool Stopped)
{
    /// <summary>What the run came to in the status bar's words, <paramref name="of"/> naming what it ran over ("of src/App").</summary>
    public string Said(Tidying kind, string of)
    {
        string said = $"{CodeActions.TitleOf(kind)} changed {Numbers.Said(Changed.Count)} of the {Plural.Of(Files, "file")} {of}";
        List<string> passed = [];
        foreach (IGrouping<PassedOver, FileLeft> why in Left.GroupBy(l => l.Why).OrderBy(g => g.Key))
        {
            passed.Add(why.Key switch
            {
                PassedOver.ReadOnly => $"{Numbers.Said(why.Count())} read-only",
                PassedOver.Generated => $"{Numbers.Said(why.Count())} generated",
                PassedOver.NotTakenIn => $"{Numbers.Said(why.Count())} the C# language server has not loaded yet",
                PassedOver.Unread => $"{Numbers.Said(why.Count())} no language server reads",
                _ => $"{Numbers.Said(why.Count())} that could not be saved",
            });
        }
        if (passed.Count > 0)
        {
            said += $", passing over {Plural.Listed(passed)}";
        }
        return Stopped ? $"Stopped: {said}" : said;
    }
}

/// <summary>Reformat Code, Code Cleanup and Optimize Imports over many files, the Explorer's and an agent's alike: each
/// file as its own command makes it, those open in their editors and the rest read without a tab and saved, as one
/// command that Undo takes back whole and one change set of Local History; one read-only, generated or held in no
/// project by the language server yet passed over, saying why.</summary>
internal sealed partial class CodeActions
{
    /// <summary>A run's title, as its action is named.</summary>
    public static string TitleOf(Tidying kind) => kind switch
    {
        Tidying.Reformat => "Reformat Code",
        Tidying.Cleanup => "Code Cleanup",
        _ => "Optimize Imports",
    };

    /// <summary>The action a run's work is held under.</summary>
    public static string ActionOf(Tidying kind) => kind switch
    {
        Tidying.Reformat => AppActions.Reformat,
        Tidying.Cleanup => AppActions.CodeCleanup,
        _ => AppActions.OptimizeUsings,
    };

    /// <summary>Whether a run takes a file, by its name: Optimize Imports a C# file, Code Cleanup a C# or XAML file, and
    /// Reformat Code any file of a language Reformat Code lays out.</summary>
    public static bool Takes(Tidying kind, string path)
    {
        bool csharp = path.EndsWith(".cs", PathNames.Comparison);
        return kind switch
        {
            Tidying.OptimizeImports => csharp,
            Tidying.Cleanup => csharp || XamlService.IsXaml(path),
            _ => csharp || XamlService.IsXaml(path) || GodotScriptTypes.IsScript(path) || FeatureChecks.IsFeature(path)
                || Language.ForPath(path).Service is LanguageService.CSharp or LanguageService.TypeScript or LanguageService.Html or LanguageService.Css
                    or LanguageService.Json or LanguageService.Yaml,
        };
    }

    /// <summary>A run over <paramref name="files"/> (canonical paths of files a run takes, <see cref="Takes"/>). The C#
    /// language server is waited for first where a C# file is among them (<paramref name="settled"/>, which gives why a file
    /// it holds in no project yet is left, null for one it holds); <paramref name="relative"/> names a file as the words
    /// say it, <paramref name="stage"/> hears each file as it begins, and <paramref name="stop"/> ends the run between files.</summary>
    internal async Task<FilesTidied> TidyFilesAsync(Tidying kind, IReadOnlyList<string> files, Func<CancellationToken, Task<Func<string, string?>>> settled,
        Func<string, string> relative, LocalHistory? history, Action<string>? stage, CancellationToken stop)
    {
        string name = TitleOf(kind);
        List<FileTidied> changed = [];
        List<FileLeft> left = [];
        List<string> code = [.. files.Where(f => f.EndsWith(".cs", PathNames.Comparison))];
        Func<string, string?> untaken = _ => null;
        IReadOnlySet<string> generated = new HashSet<string>();
        if (code.Count > 0)
        {
            stage?.Invoke("Waiting for the C# language server");
            untaken = await settled(stop);
            CSharpService service = context.CSharp ?? throw new InvalidOperationException(Refusals.NoCSharp);
            generated = await service.Generated.GeneratedAmongAsync(code, stop);
        }
        bool stopped = false;
        EditorArea editors = context.Editors;
        using (CommandHistory.Recording recording = editors.Commands.RecordWrites(name))
        using (LocalHistory.Activity? named = history?.Over(name, files))
        using (named?.Own())
        {
            if (await recording.ClearAsync(files) is { } held)
            {
                throw new InvalidOperationException($"{name} was not applied: {held}");
            }
            HashSet<string> open = new(editors.Documents.Select(d => d.Path), PathNames.Comparer);
            // Those open first, each in its editor; then the rest, read without a tab and saved.
            List<string> ordered = [.. files.Where(open.Contains), .. files.Where(f => !open.Contains(f))];
            for (int i = 0; i < ordered.Count; i++)
            {
                if (stop.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }
                string file = ordered[i];
                stage?.Invoke($"{name}: {Numbers.Said(i + 1)} of {Plural.Of(ordered.Count, "file")}, {Path.GetFileName(file)}");
                if (generated.Contains(file))
                {
                    left.Add(new FileLeft(file, PassedOver.Generated, Refusals.GeneratedLeft(relative(file))));
                    continue;
                }
                if (untaken(file) is { } notTaken)
                {
                    left.Add(new FileLeft(file, PassedOver.NotTakenIn, notTaken));
                    continue;
                }
                if (ReadOnlyFiles.Holds(file))
                {
                    left.Add(new FileLeft(file, PassedOver.ReadOnly, ReadOnlyFiles.Refused(file, relative(file)) ?? Refusals.ReadOnly(relative(file))));
                    continue;
                }
                recording.Watch(file);
                if (editors.Find(file) is { } shown)
                {
                    await TidiedAsync(shown);
                    // Its step taken now, so what the person types there next stays theirs.
                    recording.TakeOpen();
                    continue;
                }
                if (editors.Borrow(file) is not ({ } borrowed, { } hold))
                {
                    left.Add(new FileLeft(file, PassedOver.Unread, $"{relative(file)}: it cannot be read as text"));
                    continue;
                }
                using (hold)
                {
                    await TidiedAsync(borrowed);
                    if (borrowed.IsModified && editors.SaveSaying(borrowed) is { } unsaved)
                    {
                        changed.RemoveAll(c => PathNames.Same(c.Path, file));
                        left.Add(new FileLeft(file, PassedOver.Unsaved, $"{relative(file)}: it could not be saved: {unsaved}"));
                    }
                }
            }
            foreach ((string unsaved, Exception refused) in await recording.LandedAsync(null))
            {
                changed.RemoveAll(c => PathNames.Same(c.Path, unsaved));
                left.Add(new FileLeft(unsaved, PassedOver.Unsaved, $"{relative(unsaved)}: it could not be saved: {refused.Message}"));
            }
        }
        return new FilesTidied(files.Count, changed, left, stopped);

        async Task TidiedAsync(OpenDocument document)
        {
            if (document.ReadOnlyWhy is { } readOnly)
            {
                left.Add(new FileLeft(document.Path, PassedOver.ReadOnly, $"{relative(document.Path)}: {readOnly}"));
                return;
            }
            // Its text's version says whether the run changed it, never the text made into a string twice.
            int before = document.Text.Version;
            string? done = await TidyOneAsync(kind, document, stop);
            if (done is null)
            {
                left.Add(new FileLeft(document.Path, PassedOver.Unread, $"{relative(document.Path)}: the language server does not read it"));
            }
            else if (document.Text.Version != before)
            {
                changed.Add(new FileTidied(document.Path, done));
            }
        }
    }

    /// <summary>A run over what the Explorer has chosen, held as its action's work from its first moment until it tells what
    /// it came to, Stop ending it between files with what it did kept.</summary>
    internal async Task TidyChosenAsync(Tidying kind, ChosenFiles chosen)
    {
        using IDisposable held = BackgroundWork.Scope();
        string name = TitleOf(kind);
        if (context.Work?.Of(ActionOf(kind)) is { } busy)
        {
            context.Say(ActionWork.Busy(busy));
            return;
        }
        using Working? working = context.Work?.Begin(ActionOf(kind), $"{name}: finding the files {chosen.Of}…");
        CancellationToken stop = working?.Stopping ?? CancellationToken.None;
        string said;
        bool done = false;
        try
        {
            IReadOnlyList<string> files = await BackgroundWork.Run(() => chosen.Files(kind), stop);
            if (files.Count == 0)
            {
                said = Refusals.NothingToTidy(name, chosen.Of);
            }
            else
            {
                FilesTidied tidied = await TidyFilesAsync(kind, files, context.WholeCode ?? (cancel => Task.FromResult<Func<string, string?>>(file => null)), Relative,
                    context.History(), stage => working?.Say($"{stage}…"), stop);
                said = tidied.Said(kind, chosen.Of);
                done = !tidied.Stopped;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            said = $"{name} was stopped before it changed a file";
        }
        catch (Exception refused) when (refused is InvalidOperationException or TimeoutException)
        {
            said = refused.Message;
        }
        catch (LanguageServerFailed failed)
        {
            said = $"{name} failed: {failed.Reason}";
        }
        context.Say(said);
        working?.Tell(done, said);
    }

    /// <summary>A file as a run's words name it: by its path from the open folder where it is under it.</summary>
    private string Relative(string path) => context.Root is { } root && PathNames.IsUnder(path, root) ? Path.GetRelativePath(root, path) : path;

    /// <summary>One file of a run, as its own command makes it, in the status bar's words; null where no language server
    /// reads a file that needs one.</summary>
    private async Task<string?> TidyOneAsync(Tidying kind, OpenDocument document, CancellationToken stop)
    {
        if (kind == Tidying.Reformat)
        {
            return (await ReformatAsync(document, null, () => ServedAsync(document, stop), stop))?.Said;
        }
        if (XamlService.IsXaml(document.Path))
        {
            Reformatted done = await context.Languages.Xaml.ReformatXamlAsync(document, null, TitleOf(kind));
            return !done.Applied ? done.Said : done.Changed ? "Code cleanup reformatted" : "Already clean";
        }
        if (context.CSharp?.For(document)?.Lsp is not { } lsp)
        {
            return null;
        }
        if (kind == Tidying.Cleanup)
        {
            return CleanedUp(await CleanUpAsync(lsp, document));
        }
        (bool removed, bool sorted) = await TidyUsings(lsp, document);
        return Optimized(removed, sorted);
    }

    /// <summary>How long a run waits for a web language's server to take a file it read without a tab.</summary>
    private static TimeSpan WebServed => Waits.Of(TimeSpan.FromSeconds(30));

    /// <summary>The language server of a file a run lays out, a web language's waited for while it starts; null for none.</summary>
    private async Task<LspDocument?> ServedAsync(OpenDocument document, CancellationToken stop)
    {
        if (context.CSharp?.For(document)?.Lsp is { } csharp)
        {
            return csharp;
        }
        if (context.Web?.Invoke() is not { } web || web.For(document) is not { } served)
        {
            return null;
        }
        // A script's server is the Godot editor's, which a run never starts.
        if (served.Kind == Web.WebServerKind.GDScript)
        {
            return served.Lsp;
        }
        Deadline until = Deadline.In(WebServed);
        // Looked at every 50 ms: a web server's document comes as the server starts, raising nothing.
        while (served.Lsp is null && !web.CannotStart(document) && !until.Passed && !stop.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
        }
        return served.Lsp;
    }
}
