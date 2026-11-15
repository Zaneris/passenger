namespace Passenger.Core;

/// <summary>A type's names as a person reads them: C#'s keyword for it (<see cref="PrimitiveTypes"/>), and its own name
/// without its namespace or the types it is nested in.</summary>
public static class TypeNames
{
    /// <summary>The full name a C# keyword names, or null.</summary>
    public static string? FromKeyword(string keyword) => PrimitiveTypes.OfKeyword(keyword)?.SystemName;

    /// <summary>The C# keyword for a full name, or null.</summary>
    public static string? Keyword(string fullName) => PrimitiveTypes.Named(fullName)?.Keyword;

    /// <summary>A type's own name from its full name, in C#'s or metadata's spelling: "Shop.Steps+Basket" and
    /// "Shop.Steps.Basket" are both "Basket".</summary>
    public static string Short(string fullName) => fullName[(fullName.LastIndexOfAny(['.', '+']) + 1)..];
}
