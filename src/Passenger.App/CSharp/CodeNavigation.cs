using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Going from code to code: Go to Declaration and Find Usages as the file's language answers them, Go to
/// Implementation, Go to Type Declaration, Base and Derived Symbols through the server's type hierarchy, and the next or
/// previous error; straight to one place, else the places to choose from.</summary>
internal sealed class CodeNavigation(AssistContext context, PopupPlacement popups, QuickInfo quickInfo)
{
    private CodeEditor Editor => context.Editor;

    public async Task GoToDefinition(int offset)
    {
        using (BackgroundWork.Scope())
        {
            await context.Languages.DefinitionAsync(offset);
        }
    }

    public async Task GoToImplementation(int offset)
    {
        using (BackgroundWork.Scope())
        {
            if (context.Ask("Go to Implementation") is not { } lsp)
            {
                return;
            }
            try
            {
                Go(await lsp.ImplementationAsync(offset), offset, "Implementations", "No implementations found");
            }
            catch (LanguageServerFailed failed)
            {
                context.Failed("Go to Implementation", failed);
            }
        }
    }

    public Task GoToTypeDeclaration(int offset) => GoToRelated("Type Declaration", offset,
        async (lsp, at) => [.. (await lsp.TypeDefinitionAsync(at)).Select(l => new Target(l))], "Cannot find a type declaration to go to");

    public Task GoToBaseSymbols(int offset) => GoToRelated("Base Symbols", offset, BaseSymbolsAsync, "No base symbols found");

    public Task GoToDerivedSymbols(int offset) => GoToRelated("Derived Symbols", offset, DerivedSymbolsAsync, "No derived symbols found");

    /// <summary>Goes to what the server finds from the symbol at an offset (its type's declaration, its base or derived
    /// symbols): straight there when it is one place, else the places to choose from; the status bar says when none.</summary>
    private async Task GoToRelated(string what, int offset, Func<LspDocument, int, Task<IReadOnlyList<Target>>> find, string none)
    {
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            if (context.Ask(what) is { } lsp)
            {
                TextDocument asked = Editor.Document;
                string name = context.NameAt(offset);
                IReadOnlyList<Target> found = await find(lsp, offset);
                // The editor shows another file now: the places were for the one it showed.
                if (Editor.Document == asked)
                {
                    Go([.. found.Select(t => t.Location)], offset, name.Length > 0 ? $"{what} of {name}" : what, none, [.. found.Select(t => t.Name)]);
                }
            }
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed(what, failed);
        }
    }

    /// <summary>Base Symbols: a type's base types and interfaces, or the members a member overrides or implements, the
    /// nearest first, through every level of the hierarchy that has source.</summary>
    private async Task<IReadOnlyList<Target>> BaseSymbolsAsync(LspDocument lsp, int offset)
    {
        string name = context.NameAt(offset);
        // An override's own keyword takes the server to the member it overrides, a library's (decompiled) too.
        List<LspLocation> found = context.Syntax is { } syntax && Declarations.OverrideKeywordAt(syntax, offset) is { } keyword ? [.. await lsp.DefinitionAsync(keyword)] : [];
        if (await lsp.PrepareTypeHierarchyAsync(offset) is not { } type)
        {
            return [.. found.Select(l => new Target(l))];
        }
        IReadOnlyList<HierarchyItem> bases = await Walk(type, (server, item) => server.SupertypesAsync(item));
        if (IsType(type, name))
        {
            return [.. bases.Select(TargetOf)];
        }
        // A member: those of the base types by its name that it overrides or implements, which its references take in.
        if (bases.Count == 0 || name.Length == 0 || context.CSharp?.Session is not { } session)
        {
            return [.. found.Select(l => new Target(l))];
        }
        IReadOnlyList<LspLocation> family = await lsp.ReferencesAsync(offset);
        IReadOnlyList<SymbolInformation> symbols = [.. (await session.SymbolsAsync(name)).Where(s => Bare(s.Name) == name)];
        Dictionary<string, string> paths = DocumentUris.ToPaths([.. family.Select(f => f.Uri), .. symbols.Select(s => s.Location.Uri), .. bases.Select(b => b.Uri), .. found.Select(f => f.Uri)]);
        IReadOnlyList<SymbolInformation> named = [.. symbols.Where(s => family.Any(f => SamePlace(paths, f, s.Location)))];
        foreach (LspLocation place in bases.SelectMany(b => named.Where(s => Inside(paths, s.Location, b)).Select(s => s.Location)))
        {
            if (!found.Any(f => SamePlace(paths, f, place)))
            {
                found.Add(place);
            }
        }
        return [.. found.Select(l => new Target(l))];
    }

    /// <summary>Derived Symbols: the types that derive from a type or implement an interface, through every level, or
    /// the members that override or implement a member.</summary>
    private async Task<IReadOnlyList<Target>> DerivedSymbolsAsync(LspDocument lsp, int offset)
    {
        if (await lsp.PrepareTypeHierarchyAsync(offset) is { } type && IsType(type, context.NameAt(offset)))
        {
            return [.. (await Walk(type, (server, item) => server.SubtypesAsync(item))).Select(TargetOf)];
        }
        // The server's implementations of a member with a body hold the member itself, which is not derived from it.
        IReadOnlyList<LspLocation> own = await lsp.DefinitionAsync(offset);
        IReadOnlyList<LspLocation> found = await lsp.ImplementationAsync(offset);
        Dictionary<string, string> paths = DocumentUris.ToPaths([.. own.Select(o => o.Uri), .. found.Select(l => l.Uri)]);
        return [.. found.Where(l => !own.Any(o => SamePlace(paths, o, l)))
            .OrderBy(l => paths[l.Uri], StringComparer.Ordinal).ThenBy(l => l.Range.Start.Line).ThenBy(l => l.Range.Start.Character)
            .Select(l => new Target(l))];
    }

    /// <summary>A type hierarchy walked one way from a type, nearest first, each type once, as far as the server has
    /// source for: at most <see cref="MostTypes"/>, so an interface the whole solution implements stays quick.</summary>
    private async Task<IReadOnlyList<HierarchyItem>> Walk(HierarchyItem from, Func<LanguageServer, HierarchyItem, Task<IReadOnlyList<HierarchyItem>>> next)
    {
        List<HierarchyItem> found = [];
        HashSet<(string, LspPosition)> seen = [(DocumentUris.ToPath(from.Uri), from.SelectionRange.Start)];
        Queue<HierarchyItem> waiting = new([from]);
        while (waiting.Count > 0 && found.Count < MostTypes)
        {
            // Asked of the server running now at each level: the one that answered the last may have been started again.
            LanguageServer server = context.CSharp?.Session?.Server ?? throw new LanguageServerFailed("the C# language server is not running");
            foreach (HierarchyItem type in (await next(server, waiting.Dequeue())).OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (found.Count < MostTypes && seen.Add((DocumentUris.ToPath(type.Uri), type.SelectionRange.Start)))
                {
                    found.Add(type);
                    waiting.Enqueue(type);
                }
            }
        }
        return found;
    }

    private const int MostTypes = 200;

    /// <summary>Whether the type hierarchy's type is the symbol named at the caret rather than the type its member is in.</summary>
    private static bool IsType(HierarchyItem type, string name) => name.Length > 0 && Bare(type.Name) == name;

    /// <summary>A name without its type parameters or parameters, as the caret's word is.</summary>
    private static string Bare(string name) => name.Split('<', '(')[0].Trim();

    /// <summary>A place to go, and the name of what is there when the list to choose from should say it (a type's).</summary>
    private sealed record Target(LspLocation Location, string? Name = null);

    private static Target TargetOf(HierarchyItem item) => new(new LspLocation(item.Uri, item.SelectionRange), item.Name);

    /// <summary>Whether two places are one, by their files' paths made once for the list (<see cref="DocumentUris.ToPaths"/>).</summary>
    private static bool SamePlace(Dictionary<string, string> paths, LspLocation one, LspLocation other) =>
        one.Range.Start == other.Range.Start && paths[one.Uri] == paths[other.Uri];

    /// <summary>Whether a place is in a type's declaration.</summary>
    private static bool Inside(Dictionary<string, string> paths, LspLocation place, HierarchyItem type) =>
        paths[place.Uri] == paths[type.Uri] && !Before(place.Range.Start, type.Range.Start) && !Before(type.Range.End, place.Range.Start);

    private static bool Before(LspPosition one, LspPosition other) => one.Line < other.Line || (one.Line == other.Line && one.Character < other.Character);

    public async Task FindUsages(int offset)
    {
        using (BackgroundWork.Scope())
        {
            await context.Languages.UsagesAsync(offset);
        }
    }

    /// <summary>Find Usages through the language server: the usages of the name at an offset, for the Find window.</summary>
    public async Task ServerUsagesAsync(int offset)
    {
        if (context.Ask("Find Usages") is not { } lsp)
        {
            return;
        }
        try
        {
            IReadOnlyList<LspLocation> usages = await lsp.ReferencesAsync(offset);
            if (usages.Count == 0)
            {
                context.Say("No usages found");
                return;
            }
            int start = context.WordStart(offset);
            int end = offset;
            while (end < Editor.Document.Snapshot.Length && AssistContext.IsIdentifierChar(Editor.Document.Snapshot[end]))
            {
                end++;
            }
            string name = Editor.Document.Snapshot.GetText(start, end - start);
            context.ShowUsages(($"Usages of {(name.Length > 0 ? name : "symbol")}", usages));
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed("Find Usages", failed);
        }
    }

    /// <summary>Straight to the one place, else the places to choose from; the status bar says <paramref name="none"/>
    /// when there are none.</summary>
    public void Go(IReadOnlyList<LspLocation> locations, int offset, string title, string none, IReadOnlyList<string?>? names = null)
    {
        if (locations.Count == 0)
        {
            context.Say(none);
        }
        else if (locations.Count == 1)
        {
            Arrive(locations[0]);
        }
        else
        {
            Pick(title, locations, offset, names);
        }
    }

    /// <summary>The places to choose from: a type by its name with its file and line beside it, any other place by its file
    /// and line with its line of code beside it.</summary>
    private void Pick(string title, IReadOnlyList<LspLocation> locations, int offset, IReadOnlyList<string?>? names = null)
    {
        string root = context.Root ?? string.Empty;
        Dictionary<string, string> paths = DocumentUris.ToPaths(locations.Select(l => l.Uri));
        Func<string, LspPosition, (int Line, string Code)> preview = LinePreviews();
        List<Choice> choices = locations.Select((l, i) =>
        {
            string path = paths[l.Uri];
            (int line, string code) = preview(path, l.Range.Start);
            string shown = $"{PathNames.Shown(path, root)}:{line + 1}";
            return names?[i] is { } name
                ? new Choice(name, shown, l)
                : new Choice(shown, code, l, DetailIsCode: true);
        }).ToList();
        popups.ShowModal(new ChoicePopup(title, choices), offset, title, choice =>
        {
            if (choice?.Value is LspLocation location)
            {
                Arrive(location);
            }
            return Task.CompletedTask;
        });
    }

    public void Arrive(LspLocation location)
    {
        string path = DocumentUris.ToPath(location.Uri);
        context.Navigate((path, ServerPlaces.PositionOf(context.Editors, path, location.Range.Start)));
    }

    /// <summary>The line of each of a list of places, in the editor's lines, and its code, each closed file read once for
    /// the list, not once a place, since it is read on the UI thread; an open file's from its text.</summary>
    public Func<string, LspPosition, (int Line, string Code)> LinePreviews()
    {
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(context.Editors);
        return (path, at) =>
        {
            TextSnapshot? text = texts(path);
            TextPosition place = ServerPlaces.PositionOf(text, at);
            return (place.Line, text is not null && place.Line < text.LineCount ? text.GetLineText(place.Line).Trim() : string.Empty);
        };
    }

    /// <summary>The next (or previous) error or warning after the caret, wrapping.</summary>
    public void StepToMarker(bool forward)
    {
        List<Marker> problems = Editor.Markers.Where(m => m.Kind is MarkerKind.Error or MarkerKind.Warning).ToList();
        if (problems.Count == 0)
        {
            context.Say("No errors or warnings in this file");
            return;
        }
        int caret = Editor.Selections.Primary.Caret;
        Marker target = forward
            ? problems.FirstOrDefault(m => m.Range.Start > caret, problems[0])
            : problems.LastOrDefault(m => m.Range.Start < caret, problems[^1]);
        Editor.Selections = SelectionSet.Single(target.Range.Start);
        IdeErrors.Observe(quickInfo.Show(target.Range.Start, Editor.PointOf(target.Range.Start)));
    }
}
