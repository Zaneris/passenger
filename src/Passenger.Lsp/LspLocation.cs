namespace Passenger.Lsp;

// The protocol's places in a document: a position, a range of two, and a document's range by its URI.

public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End)
{
    /// <summary>Whether a place is in the range, either end included.</summary>
    public bool Holds(LspPosition at) =>
        (Start.Line < at.Line || (Start.Line == at.Line && Start.Character <= at.Character))
        && (at.Line < End.Line || (at.Line == End.Line && at.Character <= End.Character));
}

public sealed record LspLocation(string Uri, LspRange Range);
