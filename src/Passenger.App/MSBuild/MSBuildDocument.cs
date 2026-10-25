using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Host;
using Passenger.Packages;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml.Syntax;

namespace Passenger.App.MSBuild;

/// <summary>One open MSBuild file's code help. Completion reads its text as typed with the project's last evaluation;
/// the project is evaluated again with the open files' unsaved text once typing pauses, so its errors and values are
/// the screen's, and its errors and warnings in this file, with the advisories of the packages it references, are the
/// file's markers. An evaluation that fails (XML that does not read, an unknown SDK) keeps the last one that did for
/// names and values, and says why on its line.</summary>
public sealed class MSBuildDocument : IDisposable
{
    /// <summary>How long typing pauses before the project is evaluated again with the text as typed.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly MSBuildService service;
    private readonly Debounce inspecting;
    private (int Version, XmlTree Tree)? parsed;
    private IReadOnlyList<InsightDiagnostic> diagnostics = [];
    private readonly AnchoredList<Marker> markers = new(m => m.Range, (m, range) => m with { Range = range });
    private bool disposed;

    internal MSBuildDocument(MSBuildService service, OpenDocument document)
    {
        this.service = service;
        Document = document;
        inspecting = new Debounce(Settle, InspectAsync, document.Checking);
        document.Text.Changed += OnChanged;
        First = InspectAsync();
    }

    public OpenDocument Document { get; }

    /// <summary>The project the file was last evaluated through; null before, or with none to evaluate it through.</summary>
    public string? Project { get; private set; }

    /// <summary>The project's last evaluation that succeeded, for names and values; null before one has.</summary>
    public MSBuildInsight? Insight { get; private set; }

    /// <summary>The text version the last evaluation (successful or not) was of.</summary>
    public int? EvaluatedVersion { get; private set; }

    /// <summary>The file's first evaluation, which hover and Go to Declaration wait for.</summary>
    public Task First { get; }

    /// <summary>The file's markup as it stands, parsed once a version.</summary>
    public XmlTree Tree
    {
        get
        {
            int version = Document.Text.Version;
            if (parsed is { } kept && kept.Version == version)
            {
                return kept.Tree;
            }
            XmlTree tree = XmlTree.Parse(Document.Text.Snapshot.ToString());
            parsed = (version, tree);
            return tree;
        }
    }

    /// <summary>Whether the last evaluation read one of these files, or the file is one of them; with files come or gone
    /// among them, whether one is in a folder its wildcards walked.</summary>
    internal bool Read(IReadOnlyCollection<string> paths, bool cameOrWent) =>
        paths.Any(p => PathNames.Same(p, Document.Path)) || Insight?.Raw.Inputs.Any(i => paths.Any(p => PathNames.Same(p, i.Path))) == true
            || (Project is { } project && paths.Any(p => PathNames.Same(p, project)))
            || (cameOrWent && Insight is { } last && paths.Any(last.Walked));

    /// <summary>Whether the last evaluation walked folders, whose files coming or going would change what its items take in.</summary>
    internal bool Walks => Insight?.Raw.Folders.Count > 0;

    /// <summary>Has the file evaluated again once typing would have paused.</summary>
    internal void Again() => inspecting.Restart();

    private async Task<MSBuildContext> ContextAsync()
    {
        XmlTree tree = Tree;
        IReadOnlyList<string> sdks = await service.SdksAsync();
        MSBuildSchema schema = await BackgroundWork.Run(() => MSBuildSchema.Instance);
        return new MSBuildContext(Document.Path, tree, schema, Insight, sdks);
    }

    /// <summary>What completion offers at a caret, for the text as it stands: package ids and versions asked of the feeds.</summary>
    public async Task<MSBuildCompletionList?> CompleteAsync(int caret, CancellationToken cancel)
    {
        MSBuildContext context = await ContextAsync();
        MSBuildCompletionList? list = await BackgroundWork.Run(() => MSBuildCompletion.At(context, caret), cancel);
        if (list?.Packages is not { } ask || service.Feed is not { } feeds)
        {
            return list;
        }
        IPackageFeed? feed = null;
        try
        {
            feed = await feeds();
            if (ask.Id is null)
            {
                IReadOnlyList<PackageSummary> found = await feed.SearchAsync(ask.Typed.Trim(), false, cancel);
                return list with
                {
                    Items = [.. found.Select((p, i) => new MSBuildItem(p.Id, MSBuildItemKind.Package, p.Id, p.Version, p.Description.Length > 0 ? p.Description : null)
                    {
                        Package = new PackageChoice(p.Id, p.Version),
                        SortText = $"{i:D4}",
                    })],
                };
            }
            IReadOnlyList<string> versions = await feed.VersionsAsync(ask.Id, true, cancel);
            VulnerabilityData known = await service.KnownVulnerabilitiesAsync();
            return list with
            {
                Items = [.. versions.OrderByDescending(v => v, VersionOrder.Instance).Select((v, i) => new MSBuildItem(v, MSBuildItemKind.Version, v,
                    known.Of(ask.Id, v) is [{ } gravest, ..] ? $"{gravest.Severity} severity vulnerability"
                        : VersionOrder.IsPrerelease(v) ? "prerelease" : null) { SortText = $"{i:D4}" })],
            };
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed) && !cancel.IsCancellationRequested)
        {
            service.Say($"{feed?.Name ?? "The folder's NuGet feeds"} did not answer: {failed.Message}");
            return null;
        }
    }

    /// <summary>The name at an offset and what completion and the cards read, once the file's first evaluation is in.</summary>
    public async Task<(MSBuildSymbol Symbol, MSBuildContext Context)?> SymbolAsync(int offset)
    {
        await First;
        MSBuildContext context = await ContextAsync();
        return MSBuildNavigation.At(context.Tree, offset) is { } symbol ? (symbol, context) : null;
    }

    /// <summary>The hover card's words for the name at an offset.</summary>
    public async Task<string?> DescribeAsync(int offset)
    {
        if (await SymbolAsync(offset) is not { } found)
        {
            return null;
        }
        VulnerabilityData known = found.Symbol.Kind == MSBuildSymbolKind.Item && MSBuildSyntax.PackageItems.Contains(found.Symbol.Name)
            ? await service.KnownVulnerabilitiesAsync()
            : VulnerabilityData.None;
        return await BackgroundWork.Run(() => MSBuildNavigation.Describe(found.Symbol, found.Context, (id, version) => Advise(known, id, version)?.Message));
    }

    /// <summary>Where Go to Declaration may go from the name at an offset; each file gone into is evaluated through this
    /// file's project.</summary>
    public async Task<(MSBuildSymbol Symbol, IReadOnlyList<MSBuildTarget> Targets)?> TargetsAsync(int offset)
    {
        if (await SymbolAsync(offset) is not { } found)
        {
            return null;
        }
        IReadOnlyList<MSBuildTarget> targets = await BackgroundWork.Run(() => MSBuildNavigation.Targets(found.Symbol, found.Context));
        if (Project is { } project)
        {
            foreach (MSBuildTarget target in targets)
            {
                service.CameFrom(target.Place.Path, project);
            }
        }
        return (found.Symbol, targets);
    }

    /// <summary>Evaluates the project through which the file is read, its open files' text as it stands, and shows what it
    /// found in this file, unless the text changed meanwhile (the next pause asks again).</summary>
    private async Task InspectAsync()
    {
        using IDisposable held = BackgroundWork.Scope();
        using IDisposable checking = Document.Checking();
        if (disposed || service.ProjectFor(Document.Path) is not { } project)
        {
            return;
        }
        int version = Document.Text.Version;
        Dictionary<string, string> texts = service.Texts();
        Dictionary<string, string> properties = service.Properties();
        MSBuildInsight insight;
        try
        {
            if (await service.HostAsync() is not { } host)
            {
                return;
            }
            insight = await BackgroundWork.Run(async () => new MSBuildInsight(await host.InspectAsync(project, properties, texts)));
        }
        catch (ProjectHostFailed failed)
        {
            if (!disposed)
            {
                service.Say($"MSBuild could not evaluate {Path.GetFileName(project)}: {failed.Reason}");
            }
            return;
        }
        if (disposed)
        {
            return;
        }
        MSBuildFiles.Learn(insight.Raw.Installed);
        await TakeAsync(insight, version);
        service.Inspected(this, insight, texts);
    }

    /// <summary>Another open file's evaluation of this file's project, taken up when it read this file's text as it stands
    /// (its unsaved text, or the disk's when it has none), so the project's files never ask again of each other.</summary>
    internal async Task AdoptAsync(MSBuildInsight insight, IReadOnlyDictionary<string, string> texts)
    {
        using IDisposable held = BackgroundWork.Scope();
        using IDisposable checking = Document.Checking();
        int version = Document.Text.Version;
        bool same = Document.IsModified
            ? texts.TryGetValue(Document.Path, out string? read) && read == Document.Text.Snapshot.ToString()
            : !texts.ContainsKey(Document.Path);
        if (same && !disposed)
        {
            await TakeAsync(insight, version);
        }
        else
        {
            Again();
        }
    }

    /// <summary>An evaluation of the text at a version: kept for names and values when it succeeded, and its problems in
    /// this file shown, unless the text changed meanwhile (the next pause asks again).</summary>
    private async Task TakeAsync(MSBuildInsight insight, int version)
    {
        Project = insight.Project;
        if (insight.Evaluated)
        {
            Insight = insight;
        }
        if (Document.Text.Version != version)
        {
            return;
        }
        EvaluatedVersion = version;
        diagnostics = [.. insight.Raw.Diagnostics.Where(d => d.File.Length > 0 && PathNames.Same(PathNames.Canonical(d.File), Document.Path)
            || (d.File.Length == 0 && PathNames.Same(insight.Project, Document.Path)))];
        Show(await AdvisoriesAsync(version));
    }

    /// <summary>The packages this file references or pins whose version a known advisory affects: NuGet's audit warning on
    /// the version (or the id, when the version is the central one).</summary>
    private async Task<IReadOnlyList<Marker>> AdvisoriesAsync(int version)
    {
        XmlTree tree = Tree;
        List<(XmlElement Element, string Id, string Version, TextRange Range)> packages = [];
        IReadOnlyDictionary<string, (string Version, MSBuildPlace Place)> central = Insight?.CentralVersions() ?? new Dictionary<string, (string, MSBuildPlace)>();
        foreach (XmlElement element in tree.Elements.Where(e => MSBuildSyntax.PackageItems.Contains(e.LocalName) && MSBuildSyntax.RoleOf(e) == MSBuildRole.Item))
        {
            if (element.Attribute("Include") is not { HasValue: true } include || include.Value.Trim() is not { Length: > 0 } id || MSBuildSyntax.IsExpression(id))
            {
                continue;
            }
            XmlAttribute? written = element.Attribute("VersionOverride") ?? element.Attribute("Version");
            string? pinned = written?.Value.Trim() ?? MSBuildSyntax.ValueOf(element, "VersionOverride", tree.Text) ?? MSBuildSyntax.ValueOf(element, "Version", tree.Text);
            if (pinned is { Length: > 0 } && !MSBuildSyntax.IsExpression(pinned))
            {
                packages.Add((element, id, pinned, written?.ValueRange ?? include.ValueRange));
            }
            else if (pinned is null && element.LocalName == "PackageReference" && central.TryGetValue(id, out (string Version, MSBuildPlace Place) given) && given.Version.Length > 0)
            {
                packages.Add((element, id, given.Version, include.ValueRange));
            }
        }
        if (packages.Count == 0)
        {
            return [];
        }
        VulnerabilityData known = await service.KnownVulnerabilitiesAsync();
        if (disposed || Document.Text.Version != version)
        {
            return [];
        }
        return [.. packages.Select(p => (p.Range, Advice: Advise(known, p.Id, p.Version))).Where(p => p.Advice is not null)
            .Select(p => new Marker(p.Range, MarkerKind.Warning, $"{p.Advice!.Value.Code}: {p.Advice.Value.Message}"))];
    }

    /// <summary>NuGet's audit words for a package version's gravest known advisory (NU1901 low to NU1904 critical).</summary>
    private static (string Code, string Message)? Advise(VulnerabilityData known, string id, string version) =>
        known.Of(id, version) is [{ } gravest, ..]
            ? (gravest.Severity switch
            {
                AdvisorySeverity.Low => "NU1901",
                AdvisorySeverity.Moderate => "NU1902",
                AdvisorySeverity.High => "NU1903",
                _ => "NU1904",
            }, $"Package '{id}' {version} has a known {gravest.Severity.ToString().ToLowerInvariant()} severity vulnerability, {gravest.Address}")
            : null;

    /// <summary>The evaluation's problems in this file placed on its lines, with the advisories.</summary>
    private void Show(IReadOnlyList<Marker> advisories)
    {
        if (disposed)
        {
            return;
        }
        TextSnapshot text = Document.Text.Snapshot;
        XmlTree tree = Tree;
        markers.Set(Document.Text, [.. diagnostics.Select(d => new Marker(RangeOf(text, tree, d), d.Error ? MarkerKind.Error : MarkerKind.Warning,
            (d.Code is { Length: > 0 } code ? $"{code}: " : string.Empty) + Plain(d.Message))), .. advisories]);
        Document.Markers = markers.Items;
    }

    /// <summary>Where a problem MSBuild places at a line and column goes: an import not found on its Project, a
    /// condition's on its value, an element's on its name; one with no place (an unknown SDK) on the Project's Sdk.</summary>
    private static TextRange RangeOf(TextSnapshot text, XmlTree tree, InsightDiagnostic diagnostic)
    {
        if (diagnostic.Line <= 0 || diagnostic.Line > text.LineCount)
        {
            return tree.Root is { } root ? root.Attribute("Sdk") is { HasValue: true } sdk ? sdk.ValueRange : root.NameRange : new TextRange(0, 0);
        }
        LineSpan line = text.GetLine(diagnostic.Line - 1);
        int at = line.Start + Math.Clamp(diagnostic.Column - 1, 0, line.Length);
        if (at < line.End && text[at] == '<' && tree.ElementAt(at + 1) is { } element && element.Start == at)
        {
            return diagnostic.Code == "MSB4019" && element.Attribute("Project") is { HasValue: true } project && project.ValueRange.Length > 0
                ? project.ValueRange
                : element.NameRange;
        }
        if (tree.ElementAt(at) is { } owner && owner.Attributes.FirstOrDefault(a => a.NameStart == at) is { } attribute)
        {
            return attribute.HasValue && attribute.ValueRange.Length > 0 ? attribute.ValueRange : attribute.NameRange;
        }
        int end = at;
        while (end < line.End && MarkupNames.IsNameChar(text[end]))
        {
            end++;
        }
        return end > at ? TextRange.FromBounds(at, end) : TextRange.FromBounds(line.Start + (line.Length - text.GetText(line.Start, line.Length).TrimStart().Length), line.End);
    }

    /// <summary>A message without the place it repeats at its end, as the XML reader's "Line 5, position 5." is.</summary>
    private static string Plain(string message)
    {
        int at = message.LastIndexOf(" Line ", StringComparison.Ordinal);
        return at > 0 && message.IndexOf(", position ", at, StringComparison.Ordinal) > at ? message[..at].TrimEnd() : message;
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (markers.Count > 0)
        {
            Document.Markers = markers.Items;
        }
        inspecting.Restart();
    }

    public void Dispose()
    {
        disposed = true;
        inspecting.Stop();
        Document.Text.Changed -= OnChanged;
        markers.Clear();
        if (Document.Markers.Count > 0)
        {
            Document.Markers = [];
        }
    }
}
