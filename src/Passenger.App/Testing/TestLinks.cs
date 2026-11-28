namespace Passenger.App.Testing;

/// <summary>How a test class is named after the type it tests, which Go to Test follows both ways: the type's name with
/// one of the words test classes end in.</summary>
public static class TestLinks
{
    /// <summary>The words a test class's name ends in after its type's, the longer of two alike first.</summary>
    public static readonly IReadOnlyList<string> Suffixes = ["Tests", "Test", "Fixtures", "Fixture", "Specs", "Spec", "Should", "Facts"];

    /// <summary>A type's name without its namespace or the types it is nested in.</summary>
    public static string Short(string fullName) => fullName[(fullName.LastIndexOfAny(['.', '+']) + 1)..];

    /// <summary>Whether a class's name is a type's with one of the <see cref="Suffixes"/>.</summary>
    public static bool NamedAfter(string testClass, string type) =>
        testClass.StartsWith(type, StringComparison.Ordinal) && Suffixes.Any(s => testClass.Length == type.Length + s.Length && testClass.EndsWith(s, StringComparison.Ordinal));

    /// <summary>The type a test class's name names, its name less its suffix; null for a name that ends in none.</summary>
    public static string? Tested(string testClass) =>
        Suffixes.FirstOrDefault(s => testClass.Length > s.Length && testClass.EndsWith(s, StringComparison.Ordinal)) is { } suffix ? testClass[..^suffix.Length] : null;
}
