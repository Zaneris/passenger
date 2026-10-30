namespace Passenger.Lsp;

/// <summary>The language ids of the files Roslyn's server answers for, as it is told each document's and as Razor's
/// registrations name Razor's files, each written once.</summary>
[IdentifierHome]
public static class LanguageIds
{
    public const string CSharp = "csharp";
    public const string Razor = "aspnetcorerazor";
}
