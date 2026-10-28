using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Passenger.Languages.CSharp;

/// <summary>The options every in-process parse of a C# file is made with: the preprocessor symbols its project compiles it
/// with (<see cref="Defining"/>), or for a file in no project those a Debug build of any .NET project defines
/// (<see cref="Default"/>). One options object a set of symbols, so the files of one framework share theirs and a parse
/// knows by reference whether its symbols changed.</summary>
public static class CSharpParsing
{
    private static readonly ConcurrentDictionary<string, CSharpParseOptions> Made = new(StringComparer.Ordinal);

    /// <summary>A file in no project's.</summary>
    public static CSharpParseOptions Default { get; } = Defining(["DEBUG", "TRACE", "NET", "NETCOREAPP"]);

    /// <summary>The options defining these symbols, each once, any that is not an identifier left out as the compiler leaves
    /// it out.</summary>
    public static CSharpParseOptions Defining(IEnumerable<string> symbols)
    {
        string[] valid = [.. symbols.Where(SyntaxFacts.IsValidIdentifier).Distinct(StringComparer.Ordinal)];
        return Made.GetOrAdd(string.Join(';', valid), static (_, defined) => new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse, preprocessorSymbols: defined), valid);
    }
}
