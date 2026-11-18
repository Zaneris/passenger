namespace Passenger.Languages.CSharp;

/// <summary>The places a language server names for a symbol's references. A member's references hold the declarations of the
/// members that implement or override it, which the server names again as its implementations: an interface method with
/// eighteen implementations and three calls has twenty-one, of which the calls are what uses it.</summary>
public static class ReferencePlaces
{
    /// <summary>A place a server names: a document's URI, a line and a character, both from zero.</summary>
    public readonly record struct Place(string Uri, int Line, int Character);

    /// <summary>Whether two places are the same place of the same document, however the server's URI is cased.</summary>
    public static bool IsSame(Place place, Place other) =>
        place.Line == other.Line && place.Character == other.Character && string.Equals(place.Uri, other.Uri, StringComparison.OrdinalIgnoreCase);

    /// <summary>The references, by index, that are not the declaration of a member that implements or overrides the symbol.</summary>
    public static List<int> WithoutImplementations(IReadOnlyList<Place> references, IReadOnlyList<Place> implementations)
    {
        List<int> kept = [];
        for (int i = 0; i < references.Count; i++)
        {
            Place place = references[i];
            if (!implementations.Any(implemented => IsSame(place, implemented)))
            {
                kept.Add(i);
            }
        }
        return kept;
    }
}
