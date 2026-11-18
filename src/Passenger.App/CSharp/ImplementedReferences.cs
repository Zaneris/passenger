using Passenger.Languages.CSharp;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>A symbol's references without its implementations (<see cref="ReferencePlaces"/>), for every list of them: Show
/// Usages and Find Usages, and Code Spyglass's count.</summary>
internal static class ImplementedReferences
{
    public static ReferencePlaces.Place Of(LspLocation location) => new(location.Uri, location.Range.Start.Line, location.Range.Start.Character);

    /// <summary>The references but the places of the implementations named.</summary>
    public static IReadOnlyList<LspLocation> Without(IReadOnlyList<LspLocation> references, IReadOnlyList<LspLocation> implementations) =>
        implementations.Count == 0 ? references
            : [.. ReferencePlaces.WithoutImplementations([.. references.Select(Of)], [.. implementations.Select(Of)]).Select(i => references[i])];

    /// <summary>The references of the symbol at an offset, the server asked for its implementations to leave theirs out; all of
    /// them where it cannot say.</summary>
    public static async Task<IReadOnlyList<LspLocation>> WithoutAsync(LspDocument lsp, int offset, IReadOnlyList<LspLocation> references, CancellationToken cancel = default)
    {
        try
        {
            return Without(references, await lsp.ImplementationAsync(offset, cancel));
        }
        catch (LanguageServerFailed)
        {
            return references;
        }
    }
}
