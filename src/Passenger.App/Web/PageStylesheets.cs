using System.Collections.Concurrent;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>A class a page's stylesheets define, by one rule of one stylesheet: the stylesheet's file (as the editor opens
/// it) and name (as completion shows it), its text and the rule; and whether completion offers it there, which another
/// component's scoped class is not.</summary>
public sealed record DefinedClass(string Name, string Sheet, string Path, CssSheet Text, CssClassRule Rule, bool Offered);

/// <summary>The classes the stylesheets a markup file's pages link define, with their rules; why each linked stylesheet that
/// could not be read was not; and whether every one was, so that a class none defines is one no stylesheet gives.</summary>
public sealed record PageClasses(IReadOnlyDictionary<string, IReadOnlyList<DefinedClass>> Classes, IReadOnlyList<string> Unread, int Sheets)
{
    public static PageClasses None { get; } = new(new Dictionary<string, IReadOnlyList<DefinedClass>>(StringComparer.Ordinal), [], 0);

    public bool Complete => Sheets > 0 && Unread.Count == 0;
}

/// <summary>The stylesheets a markup file's pages link, found as the app serves them: a Razor file's are those every page of
/// its project links (and of the projects that reference it, a Blazor Web App's host page linking its client's components'),
/// an HTML file's its own links; a component's own scoped stylesheet always. An address is the app's own <c>wwwroot</c>, a
/// package's static web assets (<c>_content/Name/…</c>, from the package folder its restore resolved) or a referenced
/// project's <c>wwwroot</c>, the CSS isolation bundle (<c>App.styles.css</c>, each component's <c>.razor.css</c>), or a
/// stylesheet on the web (<see cref="RemoteStylesheets"/>), and each <c>@import</c> is followed. What is read from disk is
/// kept until the folder's watcher says it was written; an open file is read as the editor has it. Asked on the UI thread;
/// every read is made off it.</summary>
public sealed class PageStylesheets
{
    /// <summary>How deep <c>@import</c>s are followed.</summary>
    private const int ImportDepth = 8;

    private readonly ConcurrentDictionary<string, CssSheet> sheets = new(PathNames.Comparer);
    private readonly ConcurrentDictionary<string, IReadOnlyList<LinkedSheet>> links = new(PathNames.Comparer);
    private readonly ConcurrentDictionary<string, RestoredVersions> restored = new(PathNames.Comparer);

    /// <summary>Each project folder's own files, by the index's list they were taken from, taken again once it changes.</summary>
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> From, IReadOnlyList<string> Files)> owned = new(PathNames.Comparer);

    /// <summary>The open files' texts now, which an open page or stylesheet is read from.</summary>
    public Func<OpenTexts> Texts { get; init; } = () => OpenTexts.None;

    /// <summary>The open folder's files as its index has them: its root and their paths from it.</summary>
    public Func<(string? Root, IReadOnlyList<string> Files)> Files { get; init; } = () => (null, []);

    /// <summary>The folder's projects as evaluated.</summary>
    public Func<IReadOnlyList<ProjectModel>> Projects { get; init; } = () => [];

    public required RemoteStylesheets Remote { get; init; }

    /// <summary>What the pages link may have changed (a stylesheet or a page written, a restore, a download allowed), on the
    /// UI thread: what was found of it is asked for again.</summary>
    public event EventHandler? Changed;

    /// <summary>The source of the diagnostics that say a class is defined by no stylesheet.</summary>
    public const string DiagnosticSource = "Passenger.Stylesheets";

    /// <summary>Files the folder's watcher heard written, come or gone: what was read of them is read again when next asked.</summary>
    public void FilesChanged(IReadOnlyCollection<string> paths, bool every)
    {
        UiThread.Check();
        if (every)
        {
            sheets.Clear();
            links.Clear();
            restored.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        bool touched = false;
        foreach (string path in paths)
        {
            touched |= sheets.TryRemove(path, out _) | links.TryRemove(path, out _) | restored.TryRemove(path, out _)
                || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
        }
        if (touched)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Settings changed what may be downloaded: the classes are found again.</summary>
    public void DownloadsChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The classes a markup file writes that no stylesheet its pages link defines, said quietly (information, not a
    /// warning: a class may be a script's), on the server's lines of the text; none until every stylesheet linked was read.</summary>
    public static IReadOnlyList<Lsp.Diagnostic> Unknown(Text.TextSnapshot text, bool razor, PageClasses classes)
    {
        if (!classes.Complete)
        {
            return [];
        }
        Lsp.ServerLines lines = Lsp.ServerLines.Of(text);
        return [.. MarkupTags.Classes(text.ToString(), razor).Where(c => !classes.Classes.ContainsKey(c.Name)).Select(c => new Lsp.Diagnostic
        {
            Range = new Lsp.LspRange(lines.PositionOf(c.Start), lines.PositionOf(c.Start + c.Name.Length)),
            Severity = Lsp.DiagnosticSeverity.Information,
            Source = DiagnosticSource,
            Message = $"No stylesheet the page links defines the class {c.Name}",
        })];
    }

    /// <summary>The classes the stylesheets a markup file's pages link define.</summary>
    public async Task<PageClasses> ClassesAsync(string path, CancellationToken cancel)
    {
        UiThread.Check();
        OpenTexts texts = Texts();
        (string? root, IReadOnlyList<string> files) = Files();
        IReadOnlyList<ProjectModel> projects = Projects();
        return await BackgroundWork.Run(() => CollectAsync(path, texts, root, files, projects, cancel), cancel);
    }

    private async Task<PageClasses> CollectAsync(string path, OpenTexts texts, string? root, IReadOnlyList<string> files, IReadOnlyList<ProjectModel> projects, CancellationToken cancel)
    {
        Dictionary<string, List<DefinedClass>> classes = new(StringComparer.Ordinal);
        List<string> unread = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int count = 0;
        bool razor = RazorFiles.Is(path);
        ProjectModel? project = projects.Where(p => !p.IsProgram && PathNames.Within(path, p.Folder)).MaxBy(p => p.Folder.Length);
        if (razor && ScopedSheetOf(path) is { } own && File.Exists(own))
        {
            count++;
            Take(own, Path.GetFileName(own), Read(own, texts), offered: true);
            seen.Add(own);
        }
        foreach ((string page, ProjectModel? serving, LinkedSheet link) in Linked(path, razor, project, projects, texts, root, files))
        {
            cancel.ThrowIfCancellationRequested();
            await FollowAsync(link.Href, link.Integrity, page, serving, 0);
        }
        return new PageClasses(classes.ToDictionary(c => c.Key, c => (IReadOnlyList<DefinedClass>)c.Value, StringComparer.Ordinal), unread, count);

        async Task FollowAsync(string href, string? integrity, string from, ProjectModel? serving, int depth)
        {
            if (depth > ImportDepth || !seen.Add(Key(href, from)))
            {
                return;
            }
            if (WebAddress(href) is { } address)
            {
                RemoteStylesheets.Fetched fetched = await Remote.FetchAsync(address, integrity, cancel);
                count++;
                if (fetched is not { Path: { } copy, Text: { } text })
                {
                    unread.Add(fetched.Why ?? $"{address} could not be read");
                    return;
                }
                // A copy never changes once kept: its rules are read once.
                CssSheet sheet = sheets.GetOrAdd(copy, _ => CssRules.Read(text));
                Take(copy, Path.GetFileName(address.AbsolutePath) is { Length: > 0 } name ? name : address.Host, sheet, offered: true);
                foreach (string imported in sheet.Imports)
                {
                    await FollowAsync(new Uri(address, imported).AbsoluteUri, null, from, serving, depth + 1);
                }
                return;
            }
            if (Bundle(href, serving, root, files) is { } bundled)
            {
                foreach (string scoped in bundled.Where(s => seen.Add(s)))
                {
                    count++;
                    Take(scoped, Path.GetFileName(scoped), Read(scoped, texts), offered: false);
                }
                return;
            }
            count++;
            if (Local(href, from, serving, projects) is not { } file || !File.Exists(file) && texts.Open(file) is null)
            {
                unread.Add($"{href} is not among the files the app serves");
                return;
            }
            if (!seen.Add(file))
            {
                return;
            }
            CssSheet local = Read(file, texts);
            Take(file, Path.GetFileName(file), local, offered: true);
            foreach (string imported in local.Imports)
            {
                await FollowAsync(imported, null, file, serving, depth + 1);
            }
        }

        void Take(string file, string name, CssSheet sheet, bool offered)
        {
            foreach (CssClassRule rule in sheet.Rules)
            {
                if (!classes.TryGetValue(rule.Name, out List<DefinedClass>? defined))
                {
                    classes[rule.Name] = defined = [];
                }
                // Another component's scoped class is known, so never said to be missing, but offered nowhere else.
                defined.Add(new DefinedClass(rule.Name, name, file, sheet, rule, offered));
            }
        }
    }

    /// <summary>What a link from a page names, told apart so each is followed once.</summary>
    private static string Key(string href, string from) => Uri.TryCreate(href, UriKind.Absolute, out Uri? absolute) && !absolute.IsFile ? absolute.AbsoluteUri : $"{from}|{href}";

    /// <summary>Each stylesheet the pages of a markup file link, with the page and the project that serves it.</summary>
    private IEnumerable<(string Page, ProjectModel? Serving, LinkedSheet Link)> Linked(string path, bool razor, ProjectModel? project, IReadOnlyList<ProjectModel> projects,
        OpenTexts texts, string? root, IReadOnlyList<string> files)
    {
        if (!razor)
        {
            foreach (LinkedSheet link in LinksOf(path, texts))
            {
                yield return (path, project is { IsWeb: true } && PathNames.IsUnder(path, Path.Combine(project.Folder, "wwwroot")) ? project : null, link);
            }
            yield break;
        }
        if (project is null || root is null)
        {
            yield break;
        }
        IEnumerable<ProjectModel> serving = [project, .. projects.Where(p => p.ProjectReferences.Contains(project.Path, PathNames.Comparer))];
        foreach (ProjectModel each in serving)
        {
            foreach (string page in PagesOf(each, OwnFiles(each.Folder, root, files)))
            {
                foreach (LinkedSheet link in LinksOf(page, texts))
                {
                    yield return (page, each, link);
                }
            }
        }
    }

    /// <summary>A project's files that may link stylesheets: its Razor files, and the HTML pages of its wwwroot.</summary>
    private static IEnumerable<string> PagesOf(ProjectModel project, IReadOnlyList<string> files)
    {
        string wwwroot = Path.Combine(project.Folder, "wwwroot");
        foreach (string file in files)
        {
            string extension = Path.GetExtension(file);
            if (RazorFiles.Is(file) || (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)) && PathNames.IsUnder(file, wwwroot))
            {
                yield return file;
            }
        }
    }

    /// <summary>The files of the folder's index under a project's folder, but its build's and its packages' folders.</summary>
    private IReadOnlyList<string> OwnFiles(string folder, string root, IReadOnlyList<string> files)
    {
        if (owned.TryGetValue(folder, out (IReadOnlyList<string> From, IReadOnlyList<string> Files) kept) && ReferenceEquals(kept.From, files))
        {
            return kept.Files;
        }
        List<string> own = [];
        foreach (string relative in files)
        {
            string file = Path.Combine(root, relative);
            if (PathNames.IsUnder(file, folder) && !Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar)
                    .Any(n => n.Equals("bin", StringComparison.OrdinalIgnoreCase) || n.Equals("obj", StringComparison.OrdinalIgnoreCase) || n == "node_modules"))
            {
                own.Add(file);
            }
        }
        owned[folder] = (files, own);
        return own;
    }

    /// <summary>The stylesheets a page links, as the editor has it, else as last read from disk.</summary>
    private IReadOnlyList<LinkedSheet> LinksOf(string page, OpenTexts texts)
    {
        if (texts.Open(page) is { } open)
        {
            return PageLinks.Stylesheets(open.ToString(), RazorFiles.Is(page));
        }
        return links.GetOrAdd(page, file =>
        {
            try
            {
                string text = File.ReadAllText(file);
                return text.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) ? PageLinks.Stylesheets(text, RazorFiles.Is(file)) : [];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        });
    }

    /// <summary>A stylesheet's rules, as the editor has it, else as last read from disk.</summary>
    private CssSheet Read(string file, OpenTexts texts)
    {
        if (texts.Open(file) is { } open)
        {
            return CssRules.Read(open.ToString());
        }
        return sheets.GetOrAdd(file, path =>
        {
            try
            {
                return CssRules.Read(File.ReadAllText(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return CssSheet.Empty;
            }
        });
    }

    /// <summary>A component's or page's own scoped stylesheet's path (Counter.razor.css).</summary>
    private static string? ScopedSheetOf(string markup) => RazorFiles.Is(markup) ? markup + ".css" : null;

    /// <summary>The address on the web an href names from a page or a stylesheet on the web; null for the app's own.</summary>
    private static Uri? WebAddress(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }
        return Uri.TryCreate(href, UriKind.Absolute, out Uri? address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps) ? address : null;
    }

    /// <summary>The scoped stylesheets the CSS isolation bundle an href names takes in: every component's and page's of the
    /// project that serves it and of the projects it references; null for another href.</summary>
    private IReadOnlyList<string>? Bundle(string href, ProjectModel? serving, string? root, IReadOnlyList<string> files)
    {
        string name = Path.GetFileName(UrlPath(href));
        if (serving is null || !name.EndsWith(".styles.css", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (root is null)
        {
            return [];
        }
        IEnumerable<string> folders = [serving.Folder, .. serving.ProjectReferences.Select(r => System.IO.Path.GetDirectoryName(r)!)];
        return [.. folders.SelectMany(folder => OwnFiles(folder, root, files)).Where(f => f.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && RazorFiles.Is(f[..^4]))];
    }

    /// <summary>An address's path from the app's root, without its query or fragment.</summary>
    private static string UrlPath(string href)
    {
        int end = href.IndexOfAny(['?', '#']);
        return (end < 0 ? href : href[..end]).Replace('\\', '/');
    }

    /// <summary>The file an href names as the app serves it: from a page of a project, from its root; from an HTML page that no
    /// project serves, or a stylesheet, from beside it.</summary>
    private string? Local(string href, string from, ProjectModel? serving, IReadOnlyList<ProjectModel> projects)
    {
        string path = UrlPath(href);
        if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal) && serving is not null)
        {
            return null;
        }
        bool sheet = from.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
        if (serving is null || (sheet && !path.StartsWith('/')))
        {
            return PathNames.Canonical(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(from)!, path.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar)));
        }
        string[] parts = path.TrimStart('/').Split('/');
        if (parts is ["_content", { Length: > 0 } name, .. { Length: > 0 } rest])
        {
            string below = System.IO.Path.Combine(rest);
            if (projects.FirstOrDefault(p => serving.ProjectReferences.Contains(p.Path, PathNames.Comparer) && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } referenced)
            {
                return System.IO.Path.Combine(referenced.Folder, "wwwroot", below);
            }
            return PackageAsset(serving, name, below);
        }
        return System.IO.Path.Combine(serving.Folder, "wwwroot", System.IO.Path.Combine(parts));
    }

    /// <summary>A package's static web asset in the package folder the project's restore resolved it to; null where its
    /// restore resolved no such package.</summary>
    private string? PackageAsset(ProjectModel project, string package, string below)
    {
        if (project.Property(Host.CarriedProperties.NuGetPackageRoot) is not { Length: > 0 } root
            || (project.Property(Host.CarriedProperties.ProjectAssetsFile) ?? Passenger.Xaml.Projects.ProjectInputs.FindAssets(project.Path)) is not { } assets)
        {
            return null;
        }
        RestoredVersions versions = restored.GetOrAdd(assets, file =>
        {
            try
            {
                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return RestoredVersions.Read(stream);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return RestoredVersions.None;
            }
        });
        string? version = versions.Of(project.TargetFrameworks.Count == 1 ? null : project.TargetFrameworks.FirstOrDefault(), package);
        return version is null ? null : System.IO.Path.Combine(root, package.ToLowerInvariant(), version.ToLowerInvariant(), "staticwebassets", below);
    }
}
