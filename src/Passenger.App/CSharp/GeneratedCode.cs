using System.Text;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Extension = Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

/// <summary>The code source generators made, where Go to Declaration, Find Usages, the hierarchies and an agent's places
/// lead into it: each document copied from the C# server under the state's cache (<see cref="Folder"/>), named by its hint
/// name, shown read-only and never given to the server as a file of its own: what is asked at a place in it names the
/// server's document (<see cref="AskableAsync"/>). While a copy's tab is open its text is taken from the server again as
/// what it is made from changes, and its banner says when no generator makes it now, or when it was copied in an earlier
/// run and is no longer followed. A UI-thread object.</summary>
public sealed class GeneratedCode
{
    /// <summary>How long after a change the open copies are asked for again.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    /// <summary>The copies' folder as last made of the state's root, swapped whole as any thread may read it.</summary>
    private static Known? known;

    private readonly Func<RoslynSession?> server;

    /// <summary>Where each copy made in this run comes from, by its path.</summary>
    private readonly Dictionary<string, Source> sources = new(PathNames.Comparer);

    /// <summary>The copies open in tabs.</summary>
    private readonly List<OpenDocument> open = [];
    private readonly Debounce again;

    public GeneratedCode(Func<RoslynSession?> server)
    {
        this.server = server;
        again = new Debounce(Pause, FollowAsync);
    }

    /// <summary>A copy's document as the server names it, and its generator's type.</summary>
    private sealed record Source(Extension.GeneratedSource Asked, string Generator);

    private sealed record Known(string Root, string Folder);

    /// <summary>The folder the copies are written in, as the file system names it.</summary>
    public static string Folder
    {
        get
        {
            string root = CSharpService.ServerCache;
            if (known is not { } kept || kept.Root != root)
            {
                known = kept = new Known(root, PathNames.Canonical(Path.Combine(root, "generated")));
            }
            return kept.Folder;
        }
    }

    /// <summary>Whether a file is a copy of a generator's document, which the editor shows read-only.</summary>
    public static bool IsCopy(string path) => PathNames.IsUnder(path, Folder);

    /// <summary>Places the server named, each in a generator's document taken to that document's copy, written now, the
    /// rest as they were; all as they were when the server cannot give the generators' texts, which what opens them says.</summary>
    public async Task<IReadOnlyList<LspLocation>> CopiedAsync(IReadOnlyList<LspLocation> places, CancellationToken cancel = default)
    {
        UiThread.Check();
        string[] uris = [.. places.Select(p => p.Uri).Where(u => DocumentUris.GeneratedName(u) is not null).Distinct(StringComparer.Ordinal)];
        if (uris.Length == 0 || server() is not { } session)
        {
            return places;
        }
        IReadOnlyList<Extension.GeneratedText?>? texts;
        try
        {
            texts = await session.GeneratedAsync([.. uris.Select(u => new Extension.GeneratedSource(u, null, null))], cancel);
        }
        catch (LanguageServerFailed)
        {
            return places;
        }
        if (texts is null)
        {
            return places;
        }
        Dictionary<string, string> copies = new(StringComparer.Ordinal);
        for (int i = 0; i < uris.Length; i++)
        {
            if (texts[i] is { } made && await TakeAsync(made) is { } copy)
            {
                copies[uris[i]] = DocumentUris.FromPath(copy);
            }
        }
        return [.. places.Select(p => copies.TryGetValue(p.Uri, out string? copy) ? p with { Uri = copy } : p)];
    }

    /// <summary>A copy as the document the server has now, to ask about (<see cref="RoslynSession.Unopened"/>), else why it
    /// can't be asked. The copy takes the server's text first, so a place in it is a place in that document.</summary>
    public async Task<(LspDocument? Lsp, string? Why)> AskableAsync(OpenDocument copy, CancellationToken cancel = default)
    {
        UiThread.Check();
        string unanswered = Refusals.Generated(copy.Name, OriginOf(copy.Path));
        if (server() is not { } session || session.Server.ExtensionProblem is not null)
        {
            return (null, unanswered);
        }
        return await MadeAsync(session, copy.Path, cancel) is not { } made ? (null, Refusals.GeneratedNoLonger(copy.Name))
            : await TakeAsync(made) is null ? (null, unanswered)
            : (session.Unopened(made.Uri, copy.Text), null);
    }

    /// <summary>What the server holds of a copy now, null where no generator makes it: one of this run's by the document it
    /// came from, an earlier run's by the place each document of its project and name would be copied to.</summary>
    private async Task<Extension.GeneratedText?> MadeAsync(RoslynSession session, string copy, CancellationToken cancel)
    {
        if (sources.TryGetValue(copy, out Source? source))
        {
            return (await session.GeneratedAsync([source.Asked], cancel))?[0];
        }
        IReadOnlyList<Extension.GeneratedText?> named = GeneratedCopies.ProjectNameOf(Folder, copy) is { } project
            ? await session.GeneratedNamedAsync(project, Path.GetFileName(copy), cancel) ?? [] : [];
        return named.FirstOrDefault(made => made is not null && PathNames.Same(PlaceOf(made), copy));
    }

    /// <summary>A copy opened in a tab, followed while it is open.</summary>
    public void Opened(OpenDocument document)
    {
        UiThread.Check();
        if (!open.Contains(document))
        {
            open.Add(document);
            document.Notice = NoticeOf(document);
        }
    }

    public void Closed(OpenDocument document)
    {
        UiThread.Check();
        open.Remove(document);
    }

    /// <summary>What the open copies are made from may have changed (an edit, a project loaded, the server's word that its
    /// answers moved, a server started again): they are asked for again once the changes pause.</summary>
    public void Changed()
    {
        UiThread.Check();
        if (open.Count > 0)
        {
            again.Restart();
        }
    }

    /// <summary>Lets go of the open copies' following.</summary>
    public void Stop() => again.Stop();

    /// <summary>The open copies of this run's asked for again, each taken where its text changed or said to be made no
    /// longer; one of an earlier run's said to be followed no longer.</summary>
    private async Task FollowAsync()
    {
        foreach (OpenDocument unknown in open.Where(d => !sources.ContainsKey(d.Path)))
        {
            unknown.Notice = NoticeOf(unknown);
        }
        OpenDocument[] followed = [.. open.Where(d => sources.ContainsKey(d.Path))];
        if (followed.Length == 0 || server() is not { } session)
        {
            return;
        }
        IReadOnlyList<Extension.GeneratedText?>? texts;
        try
        {
            texts = await session.GeneratedAsync([.. followed.Select(d => sources[d.Path].Asked)]);
        }
        catch (LanguageServerFailed)
        {
            // A server that ended is started again, and a server started is a change that asks again.
            return;
        }
        for (int i = 0; i < followed.Length && texts is not null; i++)
        {
            if (texts[i] is { } made)
            {
                await TakeAsync(made);
            }
            else if (open.Contains(followed[i]))
            {
                followed[i].Notice = Refusals.GeneratedNoLonger(followed[i].Name);
            }
        }
    }

    /// <summary>A generator's document written to its copy where the copy differs, an open tab of it taking the copy's text
    /// (another window's write of it too); the copy's path, or null when it could not be written.</summary>
    private async Task<string?> TakeAsync(Extension.GeneratedText made)
    {
        string copy = PlaceOf(made);
        bool? written = await BackgroundWork.Run(() => Write(copy, made.Text));
        if (written is null)
        {
            return null;
        }
        sources[copy] = new Source(new Extension.GeneratedSource(made.Uri, made.ProjectPath, made.ProjectName), made.Generator);
        if (open.FirstOrDefault(d => PathNames.Same(d.Path, copy)) is { } shown)
        {
            if (written == true)
            {
                shown.Reload();
            }
            else
            {
                await shown.TakeDiskTextAsync(CancellationToken.None);
            }
            shown.Notice = NoticeOf(shown);
        }
        return copy;
    }

    /// <summary>Where a generator's document is copied.</summary>
    private static string PlaceOf(Extension.GeneratedText made) =>
        GeneratedCopies.PlaceOf(Folder, made.ProjectPath, made.ProjectName, made.Generator, DocumentUris.GeneratedName(made.Uri) ?? "generated.cs");

    /// <summary>Writes a copy's text unless the copy holds it already: whether it wrote, or null when it could not.</summary>
    private static bool? Write(string copy, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            if (File.Exists(copy) && File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes))
            {
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            AtomicFile.Write(copy, bytes);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What made a copy, as far as is known: its generator and project where this run made it ("made by
    /// RegexGenerator for Shop"), else its project as its folder names it; null for a path that is no copy.</summary>
    public string? OriginOf(string path)
    {
        UiThread.Check();
        return sources.TryGetValue(path, out Source? source) ? $"made by {Maker(source)}"
            : GeneratedCopies.ProjectNameOf(Folder, path) is { } project ? $"made for {project}"
            : null;
    }

    /// <summary>What a copy's banner says beside its origin: the generator and project it comes from while it is followed.</summary>
    private string NoticeOf(OpenDocument document) => sources.TryGetValue(document.Path, out Source? source)
        ? $"Made by {Maker(source)}"
        : "Copied in an earlier run and followed no longer: shown as its generator made it then";

    /// <summary>A copy's generator by its type's own name, and its project.</summary>
    private static string Maker(Source source) => $"{source.Generator[(source.Generator.LastIndexOf('.') + 1)..]} for {source.Asked.ProjectName}";
}
