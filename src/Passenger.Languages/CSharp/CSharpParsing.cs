using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Passenger.Languages.CSharp;

/// <summary>The options every in-process parse of a C# file is made with: the preprocessor symbols and C# version its
/// project compiles it with (<see cref="Defining"/>), or for a file in no project the symbols a Debug build of any .NET
/// project defines, at the newest C# the parser reads (<see cref="Default"/>). One options object a set of symbols and a
/// version, so the files of one framework share theirs and a parse knows by reference whether they changed.</summary>
public static class CSharpParsing
{
    private static readonly ConcurrentDictionary<(LanguageVersion Version, string Symbols), CSharpParseOptions> Made = new();

    /// <summary>A file in no project's.</summary>
    public static CSharpParseOptions Default { get; } = Defining(["DEBUG", "TRACE", "NET", "NETCOREAPP"], null);

    /// <summary>The options defining these symbols, each once, any that is not an identifier left out as the compiler leaves
    /// it out, at a C# version as a project's LangVersion writes it ("12", "latest"); one the parser does not know (a newer
    /// C#'s), or none, is its preview, the newest it reads.</summary>
    public static CSharpParseOptions Defining(IEnumerable<string> symbols, string? languageVersion)
    {
        string[] valid = [.. symbols.Where(SyntaxFacts.IsValidIdentifier).Distinct(StringComparer.Ordinal)];
        LanguageVersion version = languageVersion is not null && LanguageVersionFacts.TryParse(languageVersion, out LanguageVersion written) ? written : LanguageVersion.Preview;
        return Made.GetOrAdd((version, string.Join(';', valid)),
            static (key, defined) => new CSharpParseOptions(key.Version, DocumentationMode.Parse, preprocessorSymbols: defined), valid);
    }
}
