using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Localization;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;
using Avalonia.Media;

namespace Passenger.App.CSharp;

/// <summary>The same popups and commands over a XAML file, answered by Passenger's own XAML (<see cref="XamlService"/>)
/// rather than a language server: completion as it is typed, after a tag's <c>&lt;</c>, a name's <c>.</c> or
/// <c>:</c>, a value's quotes and a markup extension's brace.</summary>
internal sealed partial class XamlAssist : LanguageAssist
{
    private readonly AssistContext context;
    private readonly PopupPlacement popups;
    private readonly CodeCompletion completion;
    /// <summary>The XAML items of the last list made, by its rows' items, which a commit and a badge look up.</summary>
    private readonly Dictionary<CompletionItem, XamlItem> xamlOffered = [];

    /// <summary>The XAML file, its text's version and the caret its last list was asked at, until that list closes (one that
    /// found nothing stays), so a list made from the project as it was is asked again once the project is read anew.</summary>
    private (XamlDocument Xaml, int Version, int Caret)? xamlAsked;

    public XamlAssist(AssistContext context, PopupPlacement popups, CodeCompletion completion)
    {
        this.context = context;
        this.popups = popups;
        this.completion = completion;
        completion.Closed += (_, _) => xamlAsked = null;
    }

    private Passenger.Editor.CodeEditor Editor => context.Editor;

    /// <summary>The XAML code style as Settings leaves it, which a file's .editorconfig overrides.</summary>
    public XamlFormatSettings XamlFormat { get; set; } = new();

    public override bool Typed(string text)
    {
        if (context.XamlCurrent is null)
        {
            return false;
        }
        char last = text[^1];
        int caret = Editor.Selections.Primary.Caret;
        if (XamlTrigger(text) || (AssistContext.IsIdentifierChar(last) && !char.IsDigit(last) && context.WordStart(caret) == caret - 1))
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.XamlCurrent is not { } xaml)
        {
            return false;
        }
        await StartXamlCompletion(xaml);
        return true;
    }

    public override void Commit(CompletionItem item, int start)
    {
        if (xamlOffered.TryGetValue(item, out XamlItem? offered))
        {
            CommitXaml(offered, start);
        }
    }

    /// <summary>A XAML colour's badge, the colour itself; null for any other item.</summary>
    public override IBrush? Swatch(CompletionItem item) =>
        xamlOffered.TryGetValue(item, out XamlItem? offered) ? CodeAssist.Swatch(offered.Colour) : null;

    /// <summary>The hover card's words for a XAML name, then each language's value of a localized string.</summary>
    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (context.XamlOf(document) is not { } xaml)
        {
            return false;
        }
        if (await XamlDescription(xaml, offset) is { } described && !cancel.IsCancellationRequested)
        {
            card.AddCode(described);
        }
        if (await XamlLocalizationHoverAsync(xaml, offset) is { } languages && !cancel.IsCancellationRequested)
        {
            card.AddText(languages);
        }
        return true;
    }

    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.XamlCurrent is not { } xaml)
        {
            return false;
        }
        await XamlGoTo(xaml, offset);
        return true;
    }

    /// <summary>Where Go to Declaration goes from a XAML name, every place had at once: the project's source or XAML, a
    /// library's type decompiled (read only), the theme's XAML from its Source Link, a localized string's entry in each
    /// language's file; why not, where none of them can be had.</summary>
    public override async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        if (context.XamlOf(document) is not { } xaml)
        {
            return null;
        }
        if (await XamlSymbolAt(xaml, offset, null) is not { } found)
        {
            return new Declared([], Refusals.NoDeclaration);
        }
        List<(string Path, TextPosition Position)> places = [];
        List<string> unreached = [];
        foreach (XamlTarget target in (IReadOnlyList<XamlTarget>)[.. found.Symbol.Targets, .. await XamlLocalizedTargetsAsync(found.File, offset)])
        {
            ((string Path, TextPosition Position)? place, string? said) = await XamlPlaceAsync(target, found.File);
            if (place is { } reached)
            {
                places.Add(reached);
            }
            else if (said is not null)
            {
                unreached.Add(said);
            }
        }
        return new Declared(Declared.Served(places, ServerPlaces.Texts(context.Editors)), places.Count == 0 && unreached.Count > 0
            ? $"{found.Symbol.Name}'s declaration cannot be reached: {string.Join("; ", unreached)}" : Refusals.NoDeclaration);
    }

    public override async Task<bool> UsagesAsync(int offset)
    {
        if (context.XamlCurrent is not { } xaml)
        {
            return false;
        }
        await XamlUsages(xaml, offset);
        return true;
    }

    public override bool Rename(int caret)
    {
        if (context.XamlCurrent is not { } xaml || context.Editors.Active is null)
        {
            return false;
        }
        IdeErrors.Observe(popups.Carried("Rename", () => XamlRename(xaml), closeModal: false));
        return true;
    }

    /// <summary>Any XAML file, of a project Passenger reads or not, by Passenger's own layout.</summary>
    public override async Task<CodeActions.Reformatted?> FormatAsync(OpenDocument document, TextRange? within) =>
        XamlService.IsXaml(document.Path) ? await ReformatXamlAsync(document, within, "Reformat Code") : null;

    /// <summary>Whether what was just typed in a XAML file opens completion: a tag's '&lt;' (or its end tag's '/'), a
    /// name's '.' or ':', a value's opening quote or a markup extension's brace or comma.</summary>
    private bool XamlTrigger(string typed)
    {
        if (typed.Length != 1)
        {
            return false;
        }
        TextSnapshot text = Editor.Document.Snapshot;
        int caret = Editor.Selections.Primary.Caret;
        char before = caret >= 2 ? text[caret - 2] : '\0';
        return typed[0] switch
        {
            '<' or '.' or ':' or '{' or '#' => true,
            '/' => before == '<',
            '"' or '\'' => before == '=',
            ' ' or ',' => InsideBraces(text, caret),
            _ => false,
        };
    }

    /// <summary>Whether the caret is between a value's markup extension braces, where a space or comma starts its next
    /// argument.</summary>
    private static bool InsideBraces(TextSnapshot text, int caret)
    {
        int open = 0;
        for (int at = caret - 1; at >= 0 && at > caret - 400; at--)
        {
            char c = text[at];
            if (c is '"' or '\'' or '\n' or '>' or '<')
            {
                return false;
            }
            if (c == '}')
            {
                open--;
            }
            else if (c == '{' && ++open > 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The XAML list for the caret, in place of the server's.</summary>
    private async Task StartXamlCompletion(XamlDocument xaml)
    {
        CancellationTokenSource cancel = completion.Asking();
        int caret = Editor.Selections.Primary.Caret;
        TextDocument asked = Editor.Document;
        (XamlDocument, int, int) at = (xaml, asked.Version, caret);
        xamlAsked = at;
        try
        {
            XamlCompletionList? list = await xaml.CompleteAsync(caret, cancel.Token);
            if (cancel.IsCancellationRequested || Editor.Document != asked)
            {
                return;
            }
            if (list is null || list.Items.Count == 0)
            {
                completion.Close();
                // Nothing, perhaps for want of the types a restore landing now brings: asked again once the project is read anew.
                xamlAsked = at;
                return;
            }
            xamlOffered.Clear();
            List<CompletionItem> items = [];
            foreach (XamlItem offered in list.Items)
            {
                CompletionItem item = new() { Label = offered.Label, Kind = KindOf(offered.Kind), Detail = offered.Detail };
                xamlOffered[item] = offered;
                items.Add(item);
            }
            completion.Open(new CodeCompletion.Completion(items, Math.Min(list.Start, Editor.Selections.Primary.Caret), [], false)
            {
                Own = this,
                WordCharacters = list.WordCharacters,
            });
        }
        catch (OperationCanceledException)
        {
            // Typed on: the list is asked for again.
        }
    }

    /// <summary>A XAML project was read anew (its restore landed, a file it reads was written): a list shown in one of
    /// its files, or asked there and found empty at the caret as it still stands, was made from the project as it was,
    /// so it is asked again of the project as it is.</summary>
    public void ProjectRead(XamlProject project)
    {
        if (xamlAsked is not (XamlDocument xaml, int version, int caret) || context.XamlCurrent != xaml
            || !Passenger.Core.PathNames.Same(xaml.Loading.ProjectPath, project.Inputs.ProjectPath))
        {
            return;
        }
        if (completion.IsOpen || (Editor.Document.Version == version && Editor.Selections.Primary.Caret == caret))
        {
            IdeErrors.Observe(completion.Start(null));
        }
    }

    /// <summary>A XAML item put in place of what was typed from <paramref name="start"/>: a snippet's caret where its
    /// <c>$0</c> is, any other's after it, and completion opened again when the item says (a property's values once its
    /// quotes are in).</summary>
    private void CommitXaml(XamlItem item, int start)
    {
        int caret = Editor.Selections.Primary.Caret;
        TextRange replaced = TextRange.FromBounds(start, Math.Max(start, caret));
        if (item.IsSnippet)
        {
            Editor.InsertSnippet(replaced, Snippet.FromProtocol(item.Insert));
        }
        else
        {
            // Said, since with nothing typed (a list a property opened) the item is an insertion at the caret, which stays before it.
            Editor.ApplyEdits([new TextEdit(replaced.Start, replaced.Length, item.Insert)], SelectionSet.Single(replaced.Start + item.Insert.Length));
        }
        if (item.Reopen)
        {
            IdeErrors.Observe(completion.Start(null));
        }
    }

    /// <summary>The name at an offset and its file, read as the file stands; null where there is none, said in the status
    /// bar for a command (<paramref name="none"/>).</summary>
    private async Task<(XamlFile File, XamlSymbol Symbol)?> XamlSymbolAt(XamlDocument xaml, int offset, string? none)
    {
        XamlFile? file = await xaml.FileAsync();
        XamlSymbol? symbol = file is null ? null : await BackgroundWork.Run(() => XamlNavigation.At(file, offset));
        if (file is null || symbol is null || symbol.Targets.Count == 0)
        {
            if (none is not null)
            {
                context.Say(none);
            }
            return null;
        }
        return (file, symbol);
    }

    /// <summary>Go to Declaration in XAML: straight to the one place a name leads, or the places to choose from (a localized
    /// string's generated property and each language's entry of its key).</summary>
    private async Task XamlGoTo(XamlDocument xaml, int offset)
    {
        if (await XamlSymbolAt(xaml, offset, Refusals.NoDeclaration) is not { } found)
        {
            return;
        }
        IReadOnlyList<XamlTarget> targets = [.. found.Symbol.Targets, .. await XamlLocalizedTargetsAsync(found.File, offset)];
        if (targets.Count == 1)
        {
            await Arrive(targets[0], found.File);
            return;
        }
        List<Choice> choices = [.. targets.Select(t => new Choice(t.Name, t.Path is { } path ? Path.GetFileName(path)
            : t.Kind == XamlTargetKind.Theme ? "Theme" : t.Type?.Origin.AssemblyName ?? string.Empty, t))];
        popups.ShowModal(new ChoicePopup($"Declarations of {found.Symbol.Name}", choices), offset, "Go to Declaration",
            choice => choice?.Value is XamlTarget target ? Arrive(target, found.File) : Task.CompletedTask);
    }

    /// <summary>Where in the theme library's own XAML (Fluent's) a control's theme, a part of its template, or a resource
    /// the theme defines is written, its file had from the library's Source Link (to open read-only), and what to say of it;
    /// no place, and why, where it cannot be had. The files are those compiled into the theme, then, where none of those
    /// named for the target has it, those the files that merge others merge in: the theme's root (Fluent's FluentTheme.xaml,
    /// merging its resources in) and its control list (FluentControls.xaml, merging Button.xaml in), whose own text says them.</summary>
    internal async Task<((string Path, TextPosition Position)? Place, string Said)> ThemePlaceAsync(XamlTarget target, XamlFile file)
    {
        if (context.Xaml?.Invoke() is not { } service)
        {
            return (null, Refusals.NoFolder);
        }
        Passenger.Xaml.Metadata.AssemblyTypes? theme = file.Types.Assemblies.Where(a => a.Name.Contains(".Themes.", StringComparison.Ordinal) && a.XamlFiles.Count > 0)
            .OrderBy(a => a.Name.EndsWith(".Fluent", StringComparison.Ordinal) ? 0 : 1).FirstOrDefault();
        if (theme is null)
        {
            return (null, "The project references no theme whose XAML can be read");
        }
        Said said = new();
        string assembly = LibrarySources.Implementation(theme.Path);
        List<string> known = [.. theme.XamlFiles.Union(theme.ClassXamlFiles, StringComparer.Ordinal)];
        // What the theme's root merges in, where a resource the controls' files share is kept.
        List<string> merged = [];
        Task<string?> Fetch(string path)
        {
            context.Say($"Reading {theme.Name}'s {path.TrimStart('/')} from its Source Link…");
            return service.LibraryFiles.FileAsync(assembly, theme.Name, path, said, CancellationToken.None);
        }
        IReadOnlyList<string> names = target.Type is { } type ? [.. file.Types.SelfAndBases(type).Select(t => t.Name)] : [];
        string? Named() => names.Select(n => known.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == n)).FirstOrDefault(f => f is not null);
        IEnumerable<string> Candidates()
        {
            if (target.Type is not null)
            {
                return Named() is { } named ? [named] : [];
            }
            if (target.Part is not { } key)
            {
                return [];
            }
            return known.Where(f => key.StartsWith(Path.GetFileNameWithoutExtension(f), StringComparison.Ordinal)).OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Length)
                .Concat(merged).Concat(known.Where(f => f.Contains("/Accents/", StringComparison.Ordinal)));
        }
        HashSet<string> tried = new(StringComparer.Ordinal);
        async Task<(string Path, TextPosition Position, string Said)?> FoundAsync()
        {
            foreach (string candidate in Candidates().Distinct(StringComparer.Ordinal).Where(c => !tried.Contains(c)).ToList())
            {
                if (tried.Count >= 12)
                {
                    break;
                }
                tried.Add(candidate);
                if (await Fetch(candidate) is { } local && EditorArea.ReadText(local) is { } text && XamlNavigation.ThemePlace(target, text) is { } at)
                {
                    return (local, TextSnapshot.From(text).GetPosition(at), $"{theme.Name}'s {candidate.TrimStart('/')}, from its Source Link");
                }
            }
            return null;
        }
        bool namedFor = target.Type is not null ? Named() is not null
            : target.Part is { } part && known.Any(f => part.StartsWith(Path.GetFileNameWithoutExtension(f), StringComparison.Ordinal));
        (string Path, TextPosition Position, string Said)? found = namedFor ? await FoundAsync() : null;
        if (found is null)
        {
            foreach (string hub in theme.ClassXamlFiles.Concat(theme.XamlFiles.Where(f => f.EndsWith("Controls.xaml", StringComparison.Ordinal))).Distinct(StringComparer.Ordinal))
            {
                if (await Fetch(hub) is { } local)
                {
                    List<string> included = [.. Included(EditorArea.ReadText(local) ?? string.Empty, hub, theme.Name)];
                    known.AddRange(included.Where(f => !known.Contains(f)));
                    merged.AddRange(theme.ClassXamlFiles.Contains(hub) ? included.Where(f => !merged.Contains(f)) : []);
                }
            }
            found = await FoundAsync();
        }
        return found is { } place ? ((place.Path, place.Position), place.Said) : (null, said.Last ?? $"{target.Name} is not in {theme.Name}'s XAML");
    }

    /// <summary>The files a theme's XAML merges in, as paths in its project: avares:// ones of its own assembly, and those
    /// beside the file.</summary>
    private static IEnumerable<string> Included(string text, string from, string assembly)
    {
        string folder = from[..(from.LastIndexOf('/') + 1)];
        foreach (string source in XamlNavigation.ThemeIncludes(text))
        {
            string own = $"avares://{assembly}/";
            if (source.StartsWith(own, StringComparison.Ordinal))
            {
                yield return "/" + source[own.Length..];
            }
            else if (!source.Contains("://", StringComparison.Ordinal))
            {
                yield return source.StartsWith('/') ? source : folder + source;
            }
        }
    }

    /// <summary>The last reason a library file could not be had, kept as it is said.</summary>
    private sealed class Said : IProgress<string>
    {
        public string? Last { get; private set; }

        public void Report(string value) => Last = value;
    }

    /// <summary>Goes where a XAML name leads (<see cref="XamlPlaceAsync"/>), saying in the status bar what there is to say.</summary>
    internal async Task Arrive(XamlTarget target, XamlFile? file = null)
    {
        ((string Path, TextPosition Position)? place, string? said) = await XamlPlaceAsync(target, file);
        if (place is { } found)
        {
            context.Navigate(found);
        }
        if (said is not null)
        {
            context.Say(said);
        }
    }

    /// <summary>Where a XAML name leads: a place in the project's source, a range of a file, a library's type decompiled (a
    /// file to open read-only), or the theme's own XAML from its Source Link; with what to say of it, or why there is none.</summary>
    internal async Task<((string Path, TextPosition Position)? Place, string? Said)> XamlPlaceAsync(XamlTarget target, XamlFile? file)
    {
        if (target.Kind == XamlTargetKind.Theme && file is not null)
        {
            return await ThemePlaceAsync(target, file);
        }
        if (target.Place is { } place)
        {
            return ((place.Path, ServerPlaces.PositionOf(context.Editors, place.Path, new LspPosition(place.Line, place.Column))), null);
        }
        if (target.Path is { } path)
        {
            return ((path, target.Range is { } range ? PositionIn(path, range.Start) : new TextPosition(0, 0)), null);
        }
        if (target.Type is { } type)
        {
            (string Path, TextPosition Position)? opened = await BackgroundWork.Run(() => LibrarySources.Decompile(type, target.Member));
            return opened is { } decompiled ? (decompiled, null) : (null, $"{type.FullName} could not be decompiled from {type.Origin.AssemblyName}");
        }
        return (null, null);
    }

    /// <summary>Where an offset of a file is: in its open text, else its text on disk.</summary>
    private TextPosition PositionIn(string path, int offset)
    {
        TextSnapshot text = context.Editors.TextOf(path) ?? TextSnapshot.Empty;
        return text.GetPosition(Math.Min(offset, text.Length));
    }

    /// <summary>A range of a file as a place for the Find window, which reads it in the server's lines as the server's are.</summary>
    private LspLocation Location(string path, int start, int end) => Location(path, ServerPlaces.TextOf(context.Editors, path) ?? TextSnapshot.Empty, start, end);

    /// <summary>A range of a file's text as it was found in, in the server's lines.</summary>
    private static LspLocation Location(string path, TextSnapshot text, int start, int end) =>
        new(DocumentUris.FromPath(path), ServerLines.Of(text).RangeOf(TextRange.FromBounds(Math.Min(start, text.Length), Math.Min(end, text.Length))));

    /// <summary>Find Usages in XAML: an element's name where the file names it and where its code-behind uses the field
    /// Avalonia or WPF makes for it; a resource key where any of the project's XAML uses it; a type or member of the
    /// folder's C# where its C# and XAML use it.</summary>
    private async Task XamlUsages(XamlDocument xaml, int offset)
    {
        if (await XamlSymbolAt(xaml, offset, "No usages found") is not { } found)
        {
            return;
        }
        (XamlFile file, XamlSymbol symbol) = found;
        if (await XamlUsesAsync(file, symbol) is not { } usages)
        {
            context.Say(XamlUsesWhat);
            return;
        }
        if (usages.Count == 0)
        {
            context.Say("No usages found");
            return;
        }
        context.ShowUsages($"Usages of {symbol.Name}", usages);
    }

    /// <summary>What Find Usages finds in XAML, said where it finds nothing else.</summary>
    internal const string XamlUsesWhat = "Find Usages in XAML finds an element's name, a resource's key, or a type or member of the folder's C#";

    /// <summary>Where a XAML name is used: an element's name where the file names it and where its code-behind uses its
    /// field; a resource key where any of the project's XAML uses it, its definitions included; a type or member the
    /// folder's C# declares where Find Usages from its declaration finds it. Null for any other name.</summary>
    internal async Task<IReadOnlyList<LspLocation>?> XamlUsesAsync(XamlFile file, XamlSymbol symbol)
    {
        List<LspLocation> usages = [];
        if (symbol.Named is { } named)
        {
            usages.AddRange(XamlNavigation.NameUses(file, named.Value).Select(r => Location(file.Path, r.Start, r.End)));
            usages.AddRange((await CodeBehindUses(file, named.Value)).Uses);
        }
        else if (symbol.Targets.Count > 0 && symbol.Targets[0].Kind == XamlTargetKind.Resource)
        {
            string key = symbol.Name;
            OpenTexts texts = context.Editors.Texts();
            foreach (string path in file.Project.Files.Keys.Order(StringComparer.Ordinal))
            {
                string text = texts.Text(path) ?? string.Empty;
                if (!text.Contains(key, StringComparison.Ordinal))
                {
                    continue;
                }
                XmlTree tree = await BackgroundWork.Run(() => XmlTree.Parse(text));
                usages.AddRange(XamlNavigation.ResourceUses(tree, key).Select(r => Location(path, r.Start, r.End)));
            }
            usages.AddRange(symbol.Targets.Where(t => t.Path is not null && t.Range is not null).Select(t => Location(t.Path!, t.Range!.Value.Start, t.Range.Value.End)));
        }
        else if (symbol.Targets.FirstOrDefault(t => t.Kind is XamlTargetKind.Type or XamlTargetKind.Member && t.Place is not null) is { Place: { } declared })
        {
            usages.AddRange(await DeclaredUsesAsync(declared));
        }
        else
        {
            return null;
        }
        return usages;
    }

    /// <summary>The usages of the C# symbol declared at a place of the folder's code, as Find Usages finds them from its
    /// declaration: the server's, from the file as the editor has it, with XAML's; none from a server not loaded.</summary>
    private async Task<IReadOnlyList<LspLocation>> DeclaredUsesAsync(SourcePlace declared)
    {
        if (context.CSharp?.Session is not { IsLoaded: true } session || context.Editors.TextOf(declared.Path) is not { } text)
        {
            return [];
        }
        OpenDocument? open = context.Editors.Find(declared.Path);
        LspDocument? shown = null;
        try
        {
            LspDocument server = open is not null && context.CSharp?.For(open)?.Lsp is { } attached ? attached : shown = session.Open(declared.Path, new TextDocument(text.ToString()));
            int offset = server.OffsetOf(new LspPosition(declared.Line, declared.Column));
            return [.. await server.ReferencesAsync(offset), .. await CodeUsesAsync(await server.SymbolAtAsync(offset))];
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
        finally
        {
            shown?.Dispose();
        }
    }

    /// <summary>Where XAML uses the C# symbol at an offset of a C# file (<see cref="CodeUsesAsync(Roslyn.Extension.NamedSymbol?)"/>);
    /// none for any other file.</summary>
    internal async Task<IReadOnlyList<LspLocation>> CodeUsesAsync(OpenDocument document, LspDocument lsp, int offset) =>
        document.Language == Languages.Language.CSharp ? await CodeUsesAsync(await lsp.SymbolAtAsync(offset)) : [];

    /// <summary>Where the XAML of the projects that can use it uses a C# symbol the server named, its declaration in the
    /// folder's code, as places for the Find window (<see cref="XamlService.UsesAsync"/>): a binding's path, a handler, a
    /// type in a tag or a value, <c>x:Static</c>'s member, an attribute's property or event. None for a symbol XAML never
    /// names, or with no XAML Passenger reads.</summary>
    internal async Task<IReadOnlyList<LspLocation>> CodeUsesAsync(Roslyn.Extension.NamedSymbol? named)
    {
        if (named is not { Id: { } id, Path: { } declared, At: { } at } || context.Xaml?.Invoke() is not { } xaml || context.CSharp is not { } csharp
            || CodeSymbol.Of(id, new SourcePlace(PathNames.Canonical(declared), at.Line, at.Character)) is not { } symbol
            || csharp.ProjectsUsing(declared) is not { Count: > 0 } projects)
        {
            return [];
        }
        return [.. (await xaml.UsesAsync(projects, symbol)).SelectMany(file =>
        {
            ServerLines lines = ServerLines.Of(file.Text);
            string uri = DocumentUris.FromPath(file.Path);
            return file.Uses.Select(use => new LspLocation(uri, lines.RangeOf(use)));
        })];
    }

    /// <summary>The code-behind's uses of the field Avalonia's generator or WPF's MarkupCompile makes for a named element:
    /// the server's references from a mention of the name in a file of the <c>x:Class</c> whose definition is that
    /// generated field, its generated declaration and uses left out; with the version of the open file's text both
    /// questions were asked of, which the uses are in (null for a file not open).</summary>
    private async Task<(IReadOnlyList<LspLocation> Uses, int? Version)> CodeBehindUses(XamlFile file, string name)
    {
        if (context.CSharp?.Session is not { IsLoaded: true } session || file.ClassName is not { } className)
        {
            return ([], null);
        }
        IEnumerable<string> files = file.Types.PlacesOf(className).Select(p => p.Path).Append(file.Path + ".cs").Where(File.Exists).Distinct(StringComparer.Ordinal);
        // The name alone or after this. or base., the field's own; never another object's member of that name.
        Regex word = new($@"(?:(?<![\w.])|(?<=(?<![\w.])(?:this|base)\s*\.\s*)){Regex.Escape(name)}\b", RegexOptions.CultureInvariant);
        foreach (string path in files)
        {
            OpenDocument? open = context.Editors.Find(path);
            for (int tried = 0; tried < 5; tried++)
            {
                // Read again for each mention: an open file's text may have been typed in while the last was asked about.
                string text = context.Editors.TextOf(path)?.ToString() ?? string.Empty;
                int? version = open?.Text.Version;
                if (word.Matches(text).Skip(tried).FirstOrDefault() is not { } mention)
                {
                    break;
                }
                LspDocument? shown = null;
                try
                {
                    LspDocument server = open is not null && context.CSharp?.For(open)?.Lsp is { } attached ? attached : shown = session.Open(path, new TextDocument(text));
                    Task<IReadOnlyList<LspLocation>> definitions = server.DefinitionAsync(mention.Index);
                    Task<IReadOnlyList<LspLocation>> references = server.ReferencesAsync(mention.Index);
                    await Task.WhenAll(definitions, references);
                    if (!(await definitions).Any(d => IsGenerated(d, className, file.Path)))
                    {
                        continue;
                    }
                    return ([.. (await references).Where(r => !IsGenerated(r, className, file.Path))], version);
                }
                catch (LanguageServerFailed)
                {
                    return ([], null);
                }
                finally
                {
                    shown?.Dispose();
                }
            }
        }
        return ([], null);
    }

    /// <summary>Whether a place is in the C# made for a class's XAML: Avalonia's generator's <c>Class.g.cs</c>, or the
    /// <c>obj/…/File.g.cs</c> (<c>.g.i.cs</c> at design time) WPF's MarkupCompile writes for the XAML file.</summary>
    private static bool IsGenerated(LspLocation place, string className, string xamlPath)
    {
        if (!place.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return place.Uri.Contains(className + ".g.cs", StringComparison.Ordinal);
        }
        string path = DocumentUris.ToPath(place.Uri);
        string stem = Path.GetFileNameWithoutExtension(xamlPath);
        return Path.GetFileName(path) is { } name && (name.Equals(stem + ".g.cs", StringComparison.OrdinalIgnoreCase) || name.Equals(stem + ".g.i.cs", StringComparison.OrdinalIgnoreCase))
            && path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rename in XAML: an element's name, where the file names it and where its code-behind uses its field, as one
    /// command undone whole.</summary>
    private async Task XamlRename(XamlDocument xaml)
    {
        int caret = Editor.Selections.Primary.Caret;
        if (await XamlSymbolAt(xaml, caret, "Rename in XAML renames an element's name") is not { Symbol.Named: { } named } found)
        {
            return;
        }
        string old = named.Value;
        RenameBox box = new(old);
        popups.ShowModal(box, named.ValueStart, async name =>
        {
            if (name is null || name == old)
            {
                return;
            }
            if (await XamlRenameAsync(xaml.Document.Path, old, name) is { } renamed)
            {
                context.Say(renamed.Problem ?? renamed.Said);
            }
        });
    }

    /// <summary>An element's name renamed where its XAML file (<paramref name="path"/>, open or not) names it and where its
    /// code-behind uses its field, as one command undone whole: the files it changed, once written, and the words for it;
    /// refused where the name cannot be the field's (a C# keyword, another element's of its name scope, or what the
    /// code-behind's class declares) or a file could not be written; null when the project's XAML is not read.</summary>
    internal async Task<RefactorOutcome?> XamlRenameAsync(string path, string old, string name)
    {
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name))
        {
            return RefactorOutcome.Refused($"'{name}' cannot name a field in the code-behind");
        }
        if (Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)
        {
            return RefactorOutcome.Refused($"'{name}' is a C# keyword, not a name for the code-behind's field");
        }
        using Asked asked = WorkspaceEdits.Track(context.Editors);
        if (context.Xaml?.Invoke() is not { } service || await service.FileAsync(path) is not { } file)
        {
            return null;
        }
        if (XamlRefactorings.ElementNameProblem(file, old, name) is { } taken)
        {
            return RefactorOutcome.Refused(taken);
        }
        Dictionary<string, (int? Version, List<LspTextEdit> Edits)> changes = new(StringComparer.Ordinal);
        void Add(LspLocation place, int? version)
        {
            if (!changes.TryGetValue(place.Uri, out (int? Version, List<LspTextEdit> Edits) entry))
            {
                changes[place.Uri] = entry = (version, []);
            }
            entry.Edits.Add(new LspTextEdit(place.Range, name));
        }
        // The uses are in the text as the rename began, from which what is typed meanwhile carries them on.
        TextSnapshot read = WorkspaceEdits.Answered(context.Editors, asked)(file.Path) ?? TextSnapshot.Empty;
        foreach (TextRange use in XamlNavigation.NameUses(file, old))
        {
            Add(Location(file.Path, read, use.Start, use.End), null);
        }
        (IReadOnlyList<LspLocation> behind, int? answered) = await CodeBehindUses(file, old);
        foreach (LspLocation use in behind)
        {
            Add(use, answered);
        }
        // Each file's edits name the text they were found in: carried on from it, else left out as edited meanwhile.
        JsonElement edit = JsonSerializer.SerializeToElement(new
        {
            documentChanges = changes.Select(c => new { textDocument = new { uri = c.Key, version = c.Value.Version }, edits = c.Value.Edits }),
        }, Json.Options);
        List<string> dropped = [];
        int files;
        string named = $"Rename {old} to {name}";
        using CommandHistory.Recording recording = context.Editors.Commands.Record(named);
        try
        {
            files = TypeRename.Apply(context.Editors, edit, [], asked, dropped, recording).Files;
            await WorkspaceEdits.LandedAsync(recording, context.Say);
        }
        catch (WorkspaceEditFailed failed)
        {
            return RefactorOutcome.Refused($"Rename failed: {failed.Message}");
        }
        return new RefactorOutcome(dropped.Count > 0 ? $"Renamed, but not in {string.Join(", ", dropped)}: it was edited meanwhile"
            : files > 1 ? $"Renamed in {files} files" : "Renamed", null, [.. changes.Keys.Select(DocumentUris.ToPath)
                .Where(p => !(context.Editors.Find(p) is { } open && dropped.Contains(open.Name))).Select(p => new RefactoredFile(p, "changed", []))], dropped);
    }

    /// <summary>Show Context Actions in XAML: the fixes of the problems on the caret's line, the file checked as it stands,
    /// then the refactorings there; Refactor This (<paramref name="refactor"/>): Rename where a name is, then those.</summary>
    public async Task FixesAsync(CodeActions actions, XamlDocument xaml, IReadOnlyList<EditorFix> own, bool refactor = false)
    {
        int caret = Editor.Selections.Primary.Caret;
        List<XamlFix> fixes = [];
        List<Choice> refactorings = [];
        // A file whose project could not be read still has the running app's fixes.
        if (await xaml.FileAsync() is { } file)
        {
            if (!refactor)
            {
                IReadOnlyList<XamlDiagnostic> found = await BackgroundWork.Run(() => XamlDiagnostics.Of(file));
                TextSnapshot text = TextSnapshot.From(file.Text);
                int line = text.GetLineFromOffset(Math.Min(caret, text.Length));
                fixes.AddRange(found.Where(d => text.GetLineFromOffset(Math.Min(d.Range.Start, text.Length)) == line).SelectMany(d => d.Fixes));
            }
            else if (await BackgroundWork.Run(() => XamlNavigation.At(file, caret)) is { Named: { } named })
            {
                refactorings.Add(new Choice("Rename…", named.Value, CodeActions.XamlRefactoring.Rename));
            }
            refactorings.AddRange(await BackgroundWork.Run(() => CodeActions.XamlRefactoringsAt(file, caret)));
        }
        if (fixes.Count == 0 && own.Count == 0 && refactorings.Count == 0)
        {
            context.Say(refactor ? "No refactorings here" : "No fixes here");
            return;
        }
        // A failed binding's fixes (the running app's word) come first, then the file's own problems', then the refactorings.
        List<Choice> choices = [.. own.Select(f => new Choice(f.Title, f.Detail, f)), .. fixes.Select(f => new Choice(f.Title, f.Detail ?? Path.GetFileName(f.Path), f)),
            .. refactorings];
        OpenDocument? document = context.Editors.Active;
        popups.ShowModal(new ChoicePopup(refactor ? "Refactor This" : "Fixes", choices), caret, refactor ? "Refactor This" : "Show Context Actions", choice =>
        {
            if (choice?.Value is XamlFix fix)
            {
                return ApplyFixAsync(actions, fix);
            }
            else if (choice?.Value is EditorFix chosen && document is not null)
            {
                return actions.ApplyOwnFix(document, chosen);
            }
            else if (choice?.Value is CodeActions.XamlRefactoring refactoring && context.Editors.Active == document)
            {
                // Not awaited: its dialog waits for the person, which no screenshot or test should wait out.
                IdeErrors.Observe(actions.RunXamlRefactoring(refactoring));
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>A XAML fix made against its files' texts as they are now (open, or on disk), the files it makes made, as one
    /// command, said once those not open have landed.</summary>
    private async Task ApplyFixAsync(CodeActions actions, XamlFix fix)
    {
        string? refused = await actions.ApplyFileChangesAsync(fix.Title, [new XamlFileChange(fix.Path, fix.Edits), .. fix.Others]);
        context.Say(refused is null ? fix.Title : $"{fix.Title} was not applied: {refused}");
    }

    /// <summary>The hover card's words for a XAML name.</summary>
    private async Task<string?> XamlDescription(XamlDocument xaml, int offset)
    {
        if (await XamlSymbolAt(xaml, offset, null) is not { } found)
        {
            return null;
        }
        return XamlNavigation.Describe(found.File, found.Symbol);
    }

    /// <summary>A C# definition that lands in the field Avalonia's generator or WPF's MarkupCompile made for a named
    /// element goes to the element's name in its XAML instead; null for any other.</summary>
    internal async Task<XamlTarget?> NamedElementOfAsync(OpenDocument document, IReadOnlyList<LspLocation> definitions, int offset)
    {
        foreach (LspLocation place in definitions)
        {
            string? xaml;
            if (place.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                xaml = MarkupCompiled(DocumentUris.ToPath(place.Uri), place.Range.Start.Line);
            }
            else
            {
                Match generated = GeneratedFile().Match(Uri.UnescapeDataString(place.Uri));
                xaml = generated.Success && context.Xaml?.Invoke() is { } service ? await service.ClassFileAsync(generated.Groups["class"].Value, document.Path) : null;
            }
            string name = AssistContext.NameAt(document.Text.Snapshot, offset);
            if (xaml is not null && NameIn(context.ReadOpenOrDisk(xaml), name) is { } found)
            {
                return new XamlTarget(XamlTargetKind.Element, name) { Path = xaml, Range = found.ValueRange };
            }
        }
        return null;
    }

    /// <summary>The attribute naming the first element a XAML text names <paramref name="name"/>, however it is quoted or
    /// spaced, by <c>x:Name</c> under whatever prefix XAML's language has, or by <c>Name</c>; null for none.</summary>
    private static XmlAttribute? NameIn(string text, string name) =>
        XmlTree.Parse(text).Elements.Select(XamlFile.NameAttribute).FirstOrDefault(a => a is { IsValueClosed: true } && a.Value == name);

    /// <summary>The XAML file a line of the C# WPF's MarkupCompile wrote (<c>obj/…/MainWindow.g.cs</c>, or the design-time
    /// <c>.g.i.cs</c>) was made from: the file its nearest <c>#line</c> above names; null for a file that is not one.</summary>
    private static string? MarkupCompiled(string path, int line)
    {
        string name = Path.GetFileName(path);
        if (!(name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase))
            || !path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string[] lines = (EditorArea.ReadText(path) ?? string.Empty).Split('\n');
        for (int at = Math.Min(line, lines.Length - 1); at >= 0; at--)
        {
            if (LineDirective().Match(lines[at]) is { Success: true } directive)
            {
                string xaml = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, directive.Groups["file"].Value.Replace('\\', Path.DirectorySeparatorChar)));
                return File.Exists(xaml) ? Passenger.Core.PathNames.Canonical(xaml) : null;
            }
        }
        return null;
    }

    [GeneratedRegex(@"^\s*#line\s+\d+\s+""(?<file>[^""]+\.xaml)""")]
    private static partial Regex LineDirective();

    [GeneratedRegex(@"(?<class>[\w.]+)\.g\.cs")]
    private static partial Regex GeneratedFile();

    /// <summary>Reformat Code's edits for an open XAML file as it stands, by <paramref name="settings"/> with its
    /// .editorconfig over them, its own indent and line breaks; only those in the lines of <paramref name="within"/> when
    /// given. Worked out off the UI thread.</summary>
    public static Task<XamlFormatted> FormatXamlAsync(OpenDocument document, XamlFormatSettings settings, TextRange? within = null)
    {
        TextSnapshot text = document.Text.Snapshot;
        string path = document.Path;
        IndentOptions indent = document.Indentation.Options;
        string eol = document.Text.LineEnding;
        return BackgroundWork.Run(() =>
        {
            EditorConfig config = EditorConfig.For(path);
            return XamlFormatter.Format(text.ToString(), settings.Over(key => config[key]), indent, eol, within);
        });
    }

    /// <summary>A XAML file reformatted by Passenger's own layout, only <paramref name="within"/> when given, as one
    /// undoable edit: what came of it.</summary>
    internal async Task<CodeActions.Reformatted> ReformatXamlAsync(OpenDocument document, TextRange? within, string what)
    {
        using ChangeTracker asked = new(document.Text);
        XamlFormatted formatted = await FormatXamlAsync(document, XamlFormat, within);
        if (formatted.Problem is { } problem)
        {
            return new CodeActions.Reformatted(false, false, $"{what} was not applied: {problem}");
        }
        if (formatted.Edits.Count == 0 || !context.Editors.Documents.Contains(document))
        {
            return new CodeActions.Reformatted(true, false, "Already formatted");
        }
        if (asked.Map(formatted.Edits) is not { } edits)
        {
            return new CodeActions.Reformatted(false, false, $"{what} was not applied: the code it changes was edited meanwhile");
        }
        WorkspaceEdits.ApplyTo(context.Editors, document, edits);
        return new CodeActions.Reformatted(true, true, "Reformatted");
    }

    /// <summary>The localized string a XAML file reads at an offset (an <c>{x:Static}</c> of a generated class's property), and its
    /// resource's files, read off the UI thread with the open ones as the editor has them; null elsewhere.</summary>
    private async Task<(LocalizedStatic Read, ResourceSet? Set)?> XamlLocalizedAtAsync(XamlFile file, int offset)
    {
        Func<string, TextSnapshot?> open = context.Editors.Texts().Open;
        return await BackgroundWork.Run(() => XamlLocalization.At(file, offset) is { } read ? (read, read.SetOf(open)) : ((LocalizedStatic, ResourceSet?)?)null);
    }

    /// <summary>What the hover card says of a XAML file's localized string, beside what the name is: each language's value, as
    /// C#'s card says it; that no file gives the resource where none is found.</summary>
    private async Task<string?> XamlLocalizationHoverAsync(XamlDocument xaml, int offset)
    {
        if (await xaml.FileAsync() is not { } file || await XamlLocalizedAtAsync(file, offset) is not ({ } read, var set))
        {
            return null;
        }
        return set is null ? $"'{read.Key}' ({read.Type.Name}): no .resx gives {read.Manager}" : CSharpAssist.LocalizationCard(read.Key, read.Type.Name, set);
    }

    /// <summary>Where Go to Declaration goes from a XAML file's localized string beyond the generated property: each file's entry
    /// of its key, named by its language; none elsewhere. Shared with the agents' definition.</summary>
    internal async Task<IReadOnlyList<XamlTarget>> XamlLocalizedTargetsAsync(XamlFile file, int offset)
    {
        if (await XamlLocalizedAtAsync(file, offset) is not ({ } read, { } set))
        {
            return [];
        }
        return [.. set.Files.Select(f => (File: f, Entry: f.Entry(read.Key))).Where(f => f.Entry is not null)
            .Select(f => new XamlTarget(XamlTargetKind.File, $"{(f.File.Culture.Length == 0 ? "neutral" : f.File.Culture)}: {f.File.Name}")
            {
                Path = f.File.Path,
                Range = TextRange.FromBounds(f.Entry!.NameStart, f.Entry.NameEnd),
            })];
    }

    /// <summary>The protocol's kind for a XAML item, which gives its badge its letter and colour.</summary>
    private static CompletionItemKind KindOf(XamlItemKind kind) => kind switch
    {
        XamlItemKind.Element or XamlItemKind.Type or XamlItemKind.ClosingTag => CompletionItemKind.Class,
        XamlItemKind.Property or XamlItemKind.AttachedProperty or XamlItemKind.Member => CompletionItemKind.Property,
        XamlItemKind.Event => CompletionItemKind.Event,
        XamlItemKind.Directive or XamlItemKind.Value => CompletionItemKind.Keyword,
        XamlItemKind.Namespace => CompletionItemKind.Module,
        XamlItemKind.EnumValue => CompletionItemKind.EnumMember,
        XamlItemKind.Colour => CompletionItemKind.Color,
        XamlItemKind.Resource => CompletionItemKind.Constant,
        XamlItemKind.Extension => CompletionItemKind.Class,
        XamlItemKind.Asset => CompletionItemKind.File,
        XamlItemKind.StyleClass or XamlItemKind.PseudoClass or XamlItemKind.ElementName => CompletionItemKind.Reference,
        _ => CompletionItemKind.Text,
    };
}
