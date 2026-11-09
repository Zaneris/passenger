using Microsoft.CodeAnalysis;

namespace Passenger.Analyzers;

/// <summary>What the rules ask of symbols.</summary>
internal static class Symbols
{
    /// <summary>Whether <paramref name="one"/> is <paramref name="other"/>; never where there is no first.</summary>
    public static bool Same(ISymbol? one, ISymbol? other) => one is not null && SymbolEqualityComparer.Default.Equals(one, other);

    /// <summary>Whether <paramref name="symbol"/> is <paramref name="home"/> or inside it: one of its members, a type
    /// nested in it, or a local function or lambda of one of those.</summary>
    public static bool Within(ISymbol? symbol, INamedTypeSymbol home)
    {
        for (ISymbol? at = symbol; at is not null; at = at.ContainingSymbol)
        {
            if (Same(at, home))
            {
                return true;
            }
        }
        return false;
    }
}
