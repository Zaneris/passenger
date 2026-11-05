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

/// <summary>The classes the stylesheets a markup file's pages link define, with their rules; why each stylesheet or page that
/// could not be read, or import cut off, was not; and whether every one was, so that a class none defines is one no
/// stylesheet gives.</summary>
public sealed record PageClasses(IReadOnlyDictionary<string, IReadOnlyList<DefinedClass>> Classes, IReadOnlyList<string> Unread, int Sheets)
{
    public static PageClasses None { get; } = new(new Dictionary<string, IReadOnlyList<DefinedClass>>(StringComparer.Ordinal), [], 0);

    public bool Complete => Sheets > 0 && Unread.Count == 0;
}

/// <summary>The stylesheets a markup file's pages link, found as the app serves them: a Razor file's are those every page of
/// its project links (and of the projects that reference it), an HTML file's its own; each page's <c>&lt;style&gt;</c>
/// blocks; a component's own scoped stylesheet always, another's <c>::deep</c> rules where that one renders it. An address
/// (after the page's <c>&lt;base href&gt;</c>, with that base's path if the app serves nothing there) is the app's own
/// <c>wwwroot</c>, a package's static web assets (under its own base path, <c>_content/Name/…</c> unless its build gives
/// another) or a referenced project's <c>wwwroot</c>, a project's CSS isolation bundle, or a stylesheet on the web
/// (<see cref="RemoteStylesheets"/>); each <c>@import</c> is followed from its stylesheet's address. What is read from disk
/// is kept until the folder's watcher says it was written; an open file is read as the editor has it. Asked on the UI
/// thread; every read is made off it.</summary>
public sealed class PageStylesheets
{
    /// <summary>How deep <c>@import</c>s are followed.</summary>
    private const int ImportDepth = 8;

    private readonly ConcurrentDictionary<string, CssSheet> sheets = new(PathNames.Comparer);
    private readonly ConcurrentDictionary<string, RestoredVersions> restored = new(PathNames.Comparer);

    /// <summary>What each page read from disk gives its classes.</summary>
    private readonly ConcurrentDictionary<string, PageText> pages = new(PathNames.Comparer);

    /// <summary>A component's markup, for which components it renders.</summary>
    private readonly ConcurrentDictionary<string, string> markup = new(PathNames.Comparer);

    /// <summary>The base path each package's static web assets are served under (<c>_content/Id</c> unless its build says
    /// another), by its folder.</summary>
    private readonly ConcurrentDictionary<string, string> bases = new(PathNames.Comparer);

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
            pages.Clear();
            restored.Clear();
            markup.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        bool touched = false;
        foreach (string path in paths)
        {
            touched |= sheets.TryRemove(path, out _) | pages.TryRemove(path, out _) | restored.TryRemove(path, out _)
                | markup.TryRemove(path, out _) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
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
        // Not counted: alone, it says nothing of the stylesheets of the pages that show the component.
        if (razor && ScopedSheetOf(path) is { } own && File.Exists(own))
        {
            if (Read(own, texts, unread) is { } scoped)
            {
                Take(own, Path.GetFileName(own), scoped, _ => true);
            }
            seen.Add(own);
        }
        // The components that render this one, whose scoped rules' ::deep classes reach it.
        HashSet<string>? parents = null;
        foreach ((string page, ProjectModel? serving) in Pages(path, razor, project, projects, root, files))
        {
            cancel.ThrowIfCancellationRequested();
            if (PageOf(page, serving, texts, unread) is not { } read)
            {
                continue;
            }
            if (read.Style is { } inline)
            {
                count++;
                Take(page, $"<style> in {Path.GetFileName(page)}", inline, _ => true);
            }
            foreach (LinkedSheet link in read.Links)
            {
                if (link.IsExpression)
                {
                    count++;
                    unread.Add($"{link.Href} is made by an expression, so which stylesheet it names is known only as the app runs");
                    continue;
                }
                await FollowAsync(Followed(link, page, serving, projects, texts), link.Integrity, page, serving, 0);
            }
        }
        return new PageClasses(classes.ToDictionary(c => c.Key, c => (IReadOnlyList<DefinedClass>)c.Value, StringComparer.Ordinal), unread, count);

        async Task FollowAsync(string href, string? integrity, string from, ProjectModel? serving, int depth)
        {
            if (!seen.Add(Key(href, from)))
            {
                return;
            }
            if (depth > ImportDepth)
            {
                unread.Add($"{href} is not read: @imports are followed {ImportDepth} deep");
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
                Take(copy, Path.GetFileName(address.AbsolutePath) is { Length: > 0 } name ? name : address.Host, sheet, _ => true);
                foreach (string imported in sheet.Imports)
                {
                    await FollowAsync(PageLinks.Imported(address.AbsoluteUri, imported), null, from, serving, depth + 1);
                }
                return;
            }
            if (BundleOf(href, serving, projects) is { } bundling)
            {
                count++;
                IReadOnlyList<string> bundled = Bundled(bundling, root, files);
                foreach (string scoped in bundled.Where(s => seen.Add(s)))
                {
                    string component = Path.GetFileNameWithoutExtension(scoped[..^4]);
                    if (Read(scoped, texts, unread) is { } rules)
                    {
                        Take(scoped, Path.GetFileName(scoped), rules, rule => rule.Deep && razor && (parents ??= Parents(path, bundled, texts)).Contains(component));
                    }
                }
                return;
            }
            count++;
            if (Local(href, from, serving, projects) is not { } file || !Exists(file, texts))
            {
                unread.Add($"{href} is not among the files the app serves");
                return;
            }
            if (!seen.Add(file))
            {
                return;
            }
            if (Read(file, texts, unread) is not { } local)
            {
                return;
            }
            Take(file, Path.GetFileName(file), local, _ => true);
            foreach (string imported in local.Imports)
            {
                await FollowAsync(serving is null ? imported : PageLinks.Imported(href, imported), null, file, serving, depth + 1);
            }
        }

        void Take(string file, string name, CssSheet sheet, Func<CssClassRule, bool> offered)
        {
            foreach (CssClassRule rule in sheet.Rules)
            {
                if (!classes.TryGetValue(rule.Name, out List<DefinedClass>? defined))
                {
                    classes[rule.Name] = defined = [];
                }
                // Another component's scoped class is known, so never said to be missing, but offered only where ::deep takes it.
                defined.Add(new DefinedClass(rule.Name, name, file, sheet, rule, offered(rule)));
            }
        }
    }

    /// <summary>The address a page's link is read from: the one a browser asks for where the app serves that, else that
    /// address without the page's base, where the app serves that.</summary>
    private string Followed(LinkedSheet link, string page, ProjectModel? serving, IReadOnlyList<ProjectModel> projects, OpenTexts texts)
    {
        return link.Mounted is { } mounted && !Found(link.Href) && Found(mounted) ? mounted : link.Href;

        bool Found(string href) => BundleOf(href, serving, projects) is not null || Local(href, page, serving, projects) is { } file && Exists(file, texts);
    }

    /// <summary>Whether a stylesheet's file is there to be read.</summary>
    private static bool Exists(string file, OpenTexts texts) => File.Exists(file) || texts.Open(file) is not null;

    /// <summary>What a link from a page names, told apart so each is followed once.</summary>
    private static string Key(string href, string from) => Uri.TryCreate(href, UriKind.Absolute, out Uri? absolute) && !absolute.IsFile ? absolute.AbsoluteUri : $"{from}|{href}";

    /// <summary>The pages a markup file's classes are styled by, with the project that serves each: an HTML file itself, a
    /// Razor file's every page of its project and of the projects that reference it.</summary>
    private IEnumerable<(string Page, ProjectModel? Serving)> Pages(string path, bool razor, ProjectModel? project, IReadOnlyList<ProjectModel> projects,
        string? root, IReadOnlyList<string> files)
    {
        if (!razor)
        {
            yield return (path, project is { IsWeb: true } && PathNames.IsUnder(path, Path.Combine(project.Folder, "wwwroot")) ? project : null);
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
                yield return (page, each);
            }
        }
    }

    /// <summary>What a page gives its classes, as the editor has it, else as last read from disk: null for a page gone, and for
    /// one that could not be read, which <paramref name="unread"/> is told of and which is read again next time.</summary>
    private PageText? PageOf(string page, ProjectModel? serving, OpenTexts texts, List<string> unread)
    {
        string? address = PageAddress(page, serving);
        if (texts.Open(page) is { } open)
        {
            return PageText.Of(open.ToString(), RazorFiles.Is(page), address);
        }
        if (pages.TryGetValue(page, out PageText? kept))
        {
            return kept;
        }
        try
        {
            return pages[page] = PageText.Of(File.ReadAllText(page), RazorFiles.Is(page), address);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            unread.Add($"{Path.GetFileName(page)} could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>A page's address as the project serving it serves it, where its file says it: an HTML file of its wwwroot;
    /// null for a Razor file, whose address its routes say, and a page no project serves.</summary>
    private static string? PageAddress(string page, ProjectModel? serving)
    {
        if (serving is null || RazorFiles.Is(page) || !PathNames.IsUnder(page, Path.Combine(serving.Folder, "wwwroot")))
        {
            return null;
        }
        string relative = Path.GetRelativePath(Path.Combine(serving.Folder, "wwwroot"), page);
        return "/" + string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
    }

    /// <summary>The components that render a component, directly or through others, by name: among those of the scoped
    /// stylesheets a bundle takes in and the rest of their folders' components, each whose markup writes one's tag.</summary>
    private HashSet<string> Parents(string component, IReadOnlyList<string> bundled, OpenTexts texts)
    {
        HashSet<string> folders = new(bundled.Select(s => Path.GetDirectoryName(s)!).Append(Path.GetDirectoryName(component)!), PathNames.Comparer);
        List<string> components = [.. bundled.Select(s => s[..^4]).Concat(folders.SelectMany(f => Directory.Exists(f) ? Directory.EnumerateFiles(f, "*.razor") : []))
            .Distinct(PathNames.Comparer)];
        HashSet<string> rendered = [Path.GetFileNameWithoutExtension(component)];
        HashSet<string> found = new(StringComparer.Ordinal);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (string each in components)
            {
                string name = Path.GetFileNameWithoutExtension(each);
                if (!found.Contains(name) && rendered.Any(child => Renders(MarkupOf(each, texts), child)))
                {
                    grew |= found.Add(name) & rendered.Add(name);
                }
            }
        }
        return found;
    }

    /// <summary>Whether markup writes a component's tag (<c>&lt;Child</c>, then a space, '/' or '&gt;').</summary>
    private static bool Renders(string text, string component)
    {
        for (int at = text.IndexOf("<" + component, StringComparison.Ordinal); at >= 0; at = text.IndexOf("<" + component, at + 1, StringComparison.Ordinal))
        {
            int after = at + 1 + component.Length;
            if (after >= text.Length || char.IsWhiteSpace(text[after]) || text[after] is '/' or '>')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A component's markup, as the editor has it, else as last read from disk.</summary>
    private string MarkupOf(string component, OpenTexts texts)
    {
        if (texts.Open(component) is { } open)
        {
            return open.ToString();
        }
        return markup.GetOrAdd(component, file =>
        {
            try
            {
                return File.ReadAllText(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        });
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

    /// <summary>A stylesheet's rules, as the editor has it, else as last read from disk: null for one that could not be read,
    /// which <paramref name="unread"/> is told of and which is read again next time.</summary>
    private CssSheet? Read(string file, OpenTexts texts, List<string> unread)
    {
        if (texts.Open(file) is { } open)
        {
            return CssRules.Read(open.ToString());
        }
        if (sheets.TryGetValue(file, out CssSheet? kept))
        {
            return kept;
        }
        try
        {
            return sheets[file] = CssRules.Read(File.ReadAllText(file));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            unread.Add($"{Path.GetFileName(file)} could not be read: {error.Message}");
            return null;
        }
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

    /// <summary>The project whose CSS isolation bundle an href names, as its build names and places it (a page may be shared
    /// with the app that makes it); null for another href, however it ends.</summary>
    private static ProjectModel? BundleOf(string href, ProjectModel? serving, IReadOnlyList<ProjectModel> projects)
    {
        string address = UrlPath(href);
        return serving is null ? null : ((IEnumerable<ProjectModel>)[serving, .. projects]).FirstOrDefault(p =>
            PageLinks.IsBundle(address, p.Property(Host.CarriedProperties.PackageId) ?? p.Name, p.Property(Host.CarriedProperties.StaticWebAssetBasePath)));
    }

    /// <summary>The scoped stylesheets a project's CSS isolation bundle takes in: every component's and page's of the project
    /// and of the projects it references.</summary>
    private IReadOnlyList<string> Bundled(ProjectModel bundling, string? root, IReadOnlyList<string> files)
    {
        if (root is null)
        {
            return [];
        }
        IEnumerable<string> folders = [bundling.Folder, .. bundling.ProjectReferences.Select(r => System.IO.Path.GetDirectoryName(r)!)];
        return [.. folders.SelectMany(folder => OwnFiles(folder, root, files)).Where(f => f.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && RazorFiles.Is(f[..^4]))];
    }

    /// <summary>An address's path from the app's root as the file system names it: without its query or fragment, its escapes read.</summary>
    private static string UrlPath(string href)
    {
        int end = href.IndexOfAny(['?', '#']);
        return Uri.UnescapeDataString(end < 0 ? href : href[..end]).Replace('\\', '/');
    }

    /// <summary>The file an href names as the app serves it: of a project, an address from its root, which no address leaves;
    /// of an HTML page that no project serves and its stylesheets, an address from beside the file that names it.</summary>
    private string? Local(string href, string from, ProjectModel? serving, IReadOnlyList<ProjectModel> projects)
    {
        string path = UrlPath(href);
        if (path.Length == 0)
        {
            return null;
        }
        if (serving is null)
        {
            return PathNames.Canonical(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(from)!, path.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar)));
        }
        string[] parts = path.TrimStart('/').Split('/');
        if (parts.Any(part => part == ".." || System.IO.Path.IsPathRooted(part)))
        {
            return null;
        }
        if (ReferencedAsset(serving, parts, projects) is { } referencedAsset)
        {
            return referencedAsset;
        }
        if (parts is ["_content", { Length: > 0 } name, .. { Length: > 0 } rest])
        {
            string below = System.IO.Path.Combine(rest);
            if (projects.FirstOrDefault(p => serving.ProjectReferences.Contains(p.Path, PathNames.Comparer) && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } referenced)
            {
                return System.IO.Path.Combine(referenced.Folder, "wwwroot", below);
            }
            return PackageAsset(serving, name, below);
        }
        string own = System.IO.Path.Combine(serving.Folder, "wwwroot", System.IO.Path.Combine(parts));
        return File.Exists(own) ? own : PackageAssetUnder(serving, parts) ?? own;
    }

    /// <summary>The file an address names in the <c>wwwroot</c> of a project the app references, under the base path that
    /// project serves its static web assets at (its <c>StaticWebAssetBasePath</c>, as its evaluation gives it); null where
    /// no referenced project's base path begins it.</summary>
    private static string? ReferencedAsset(ProjectModel serving, string[] parts, IReadOnlyList<ProjectModel> projects)
    {
        string address = string.Join('/', parts);
        foreach (ProjectModel referenced in projects.Where(p => serving.ProjectReferences.Contains(p.Path, PathNames.Comparer)))
        {
            string served = referenced.Property(Host.CarriedProperties.StaticWebAssetBasePath) is { Length: > 0 } own ? own.Replace('\\', '/').Trim('/') : $"_content/{referenced.Name}";
            if (served.Length > 0 && address.StartsWith(served + "/", StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.Combine(referenced.Folder, "wwwroot", System.IO.Path.Combine(address[(served.Length + 1)..].Split('/')));
            }
        }
        return null;
    }

    /// <summary>A package's static web asset in the package folder the project's restore resolved it to; null where its
    /// restore resolved no such package.</summary>
    private string? PackageAsset(ProjectModel project, string package, string below)
    {
        if (project.Property(Host.CarriedProperties.NuGetPackageRoot) is not { Length: > 0 } root || Restored(project) is not { } versions)
        {
            return null;
        }
        string? version = versions.Of(project.TargetFrameworks.Count == 1 ? null : project.TargetFrameworks.FirstOrDefault(), package);
        return version is null ? null : System.IO.Path.Combine(root, package.ToLowerInvariant(), version.ToLowerInvariant(), "staticwebassets", below);
    }

    /// <summary>The static web asset an address names in a package whose own base path (its <c>StaticWebAssetBasePath</c>,
    /// as its build's props give it) begins the address; null where no package the project's restore resolved serves it.</summary>
    private string? PackageAssetUnder(ProjectModel project, string[] parts)
    {
        if (project.Property(Host.CarriedProperties.NuGetPackageRoot) is not { Length: > 0 } root || Restored(project) is not { } versions)
        {
            return null;
        }
        string address = string.Join('/', parts);
        foreach ((string package, string version) in versions.All(project.TargetFrameworks.Count == 1 ? null : project.TargetFrameworks.FirstOrDefault()))
        {
            string folder = System.IO.Path.Combine(root, package.ToLowerInvariant(), version.ToLowerInvariant());
            string served = bases.GetOrAdd(folder, at => BasePathOf(at) ?? $"_content/{package}");
            if (address.StartsWith(served + "/", StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.Combine(folder, "staticwebassets", System.IO.Path.Combine(address[(served.Length + 1)..].Split('/')));
            }
        }
        return null;
    }

    /// <summary>The base path a package folder's build props give its static web assets; null where they give none.</summary>
    private static string? BasePathOf(string folder)
    {
        if (!Directory.Exists(System.IO.Path.Combine(folder, "staticwebassets")))
        {
            return null;
        }
        foreach (string build in (string[])["build", "buildTransitive", "buildMultiTargeting"])
        {
            string props = System.IO.Path.Combine(folder, build);
            if (!Directory.Exists(props))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(props, "*.props"))
            {
                try
                {
                    if (PageLinks.AssetBasePath(File.ReadAllText(file)) is { } path)
                    {
                        return path;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return null;
    }

    /// <summary>The package versions a project's restore resolved, as its assets file last read says them.</summary>
    private RestoredVersions? Restored(ProjectModel project)
    {
        if ((project.Property(Host.CarriedProperties.ProjectAssetsFile) ?? Passenger.Xaml.Projects.ProjectInputs.FindAssets(project.Path)) is not { } assets)
        {
            return null;
        }
        return restored.GetOrAdd(assets, file =>
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
    }

    /// <summary>What a page gives its classes: the stylesheets it links, and its <c>&lt;style&gt;</c> blocks' rules, if any.</summary>
    private sealed record PageText(IReadOnlyList<LinkedSheet> Links, CssSheet? Style)
    {
        /// <summary>A page's text read, passed over where it says nothing of either, as most of a project's components do.</summary>
        public static PageText Of(string text, bool razor, string? address) => new(
            text.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) || text.Contains("@import", StringComparison.OrdinalIgnoreCase) ? PageLinks.Stylesheets(text, razor, address) : [],
            text.Contains("<style", StringComparison.OrdinalIgnoreCase) && PageLinks.StyleText(text, razor) is { } css ? CssRules.Read(css) : null);
    }
}
