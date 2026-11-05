using Passenger.Languages.CSharp;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>A C# symbol's base and derived symbols through the server's type hierarchy, which Base and Derived Symbols go
/// to and an agent's hierarchy of a member answers.</summary>
internal static class RelatedSymbols
{
    /// <summary>Base Symbols: a type's base types and interfaces, or the members a member overrides or implements, the
    /// nearest first, through every level of the hierarchy that has source; <paramref name="name"/> is the name at the
    /// offset, <paramref name="syntax"/> its file's parse.</summary>
    public static async Task<IReadOnlyList<Target>> BaseAsync(LspDocument lsp, int offset, string name, ParsedText? syntax, Func<RoslynSession?> csharp)
    {
        // An override's own keyword takes the server to the member it overrides, a library's (decompiled) too.
        List<LspLocation> found = syntax is not null && Declarations.OverrideKeywordAt(syntax, offset) is { } keyword ? [.. await lsp.DefinitionAsync(keyword)] : [];
        if (await lsp.PrepareTypeHierarchyAsync(offset) is not { } type)
        {
            return [.. found.Select(l => new Target(l))];
        }
        IReadOnlyList<HierarchyItem> bases = await Walk(type, (server, item) => server.SupertypesAsync(item), csharp);
        if (IsType(type, name))
        {
            return [.. bases.Select(TargetOf)];
        }
        // A member: those of the base types by its name that it overrides or implements, which its references take in.
        if (bases.Count == 0 || name.Length == 0 || csharp() is not { } session)
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
    /// the members that override or implement a member; <paramref name="name"/> is the name at the offset.</summary>
    public static async Task<IReadOnlyList<Target>> DerivedAsync(LspDocument lsp, int offset, string name, Func<RoslynSession?> csharp)
    {
        if (await lsp.PrepareTypeHierarchyAsync(offset) is { } type && IsType(type, name))
        {
            return [.. (await Walk(type, (server, item) => server.SubtypesAsync(item), csharp)).Select(TargetOf)];
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
    private static async Task<IReadOnlyList<HierarchyItem>> Walk(HierarchyItem from, Func<LanguageServer, HierarchyItem, Task<IReadOnlyList<HierarchyItem>>> next,
        Func<RoslynSession?> csharp)
    {
        List<HierarchyItem> found = [];
        HashSet<(string, LspPosition)> seen = [(DocumentUris.ToPath(from.Uri), from.SelectionRange.Start)];
        Queue<HierarchyItem> waiting = new([from]);
        while (waiting.Count > 0 && found.Count < MostTypes)
        {
            // Asked of the server running now at each level: the one that answered the last may have been started again.
            LanguageServer server = csharp()?.Server ?? throw new LanguageServerFailed("the C# language server is not running");
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
    public static bool IsType(HierarchyItem type, string name) => name.Length > 0 && Bare(type.Name) == name;

    /// <summary>A name without its type parameters or parameters, as the caret's word is.</summary>
    private static string Bare(string name) => name.Split('<', '(')[0].Trim();

    /// <summary>A place to go, and the name of what is there when the list to choose from should say it (a type's).</summary>
    public sealed record Target(LspLocation Location, string? Name = null);

    private static Target TargetOf(HierarchyItem item) => new(new LspLocation(item.Uri, item.SelectionRange), item.Name);

    /// <summary>Whether two places are one, by their files' paths made once for the list (<see cref="DocumentUris.ToPaths"/>).</summary>
    private static bool SamePlace(Dictionary<string, string> paths, LspLocation one, LspLocation other) =>
        one.Range.Start == other.Range.Start && paths[one.Uri] == paths[other.Uri];

    /// <summary>Whether a place is in a type's declaration.</summary>
    private static bool Inside(Dictionary<string, string> paths, LspLocation place, HierarchyItem type) =>
        paths[place.Uri] == paths[type.Uri] && !Before(place.Range.Start, type.Range.Start) && !Before(type.Range.End, place.Range.Start);

    private static bool Before(LspPosition one, LspPosition other) => one.Line < other.Line || (one.Line == other.Line && one.Character < other.Character);
}
