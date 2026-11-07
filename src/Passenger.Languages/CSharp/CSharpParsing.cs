using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;

namespace Passenger.Languages.CSharp;

/// <summary>The options every in-process parse of a C# file is made with: the preprocessor symbols and C# version its
/// project compiles it with (<see cref="Defining"/>), or for a file in no project the symbols a Debug build of any .NET
/// project defines, at the newest C# the parser reads (<see cref="Default"/>). One options object a set of symbols and a
/// version, so the files of one framework share theirs and a parse knows by reference whether they changed.</summary>
public static class CSharpParsing
{
    private static readonly ConcurrentDictionary<(LanguageVersion Version, string Symbols), CSharpParseOptions> Made = new();

    /// <summary>The most text changed that goes unmeasured: a person's typing, which can't take a file's brackets anywhere a
    /// stack can't hold, and costs what it changed.</summary>
    private const int MostTyped = 1000;

    /// <summary>A file in no project's.</summary>
    public static CSharpParseOptions Default { get; } = Defining(["DEBUG", "TRACE", "NET", "NETCOREAPP"], null);

    /// <summary>A text's tree, every in-process parse's way to one: its parser runs out of no stack (<see cref="DeepText"/>),
    /// and a text nested too deeply for it is a tree of nothing, its spans still the text's.</summary>
    public static SyntaxTree Parse(string text, CSharpParseOptions? options = null, string path = "") => Parse(SourceText.From(text), options, path);

    /// <inheritdoc cref="Parse(string, CSharpParseOptions?, string)"/>
    public static SyntaxTree Parse(SourceText text, CSharpParseOptions? options = null, string path = "")
    {
        SourceText read = TooDeep(text) ? SourceText.From(new string(' ', text.Length)) : text;
        return DeepText.Read(text.Length, () => CSharpSyntaxTree.ParseText(read, options, path));
    }

    /// <summary>A tree as a text changed from its own has it, the parser reading again only what changed.</summary>
    public static SyntaxTree Changed(SyntaxTree tree, SourceText text)
    {
        if (text.GetChangeRanges(tree.GetText()).Sum(c => (long)c.NewLength) > MostTyped && TooDeep(text))
        {
            return Parse(text, (CSharpParseOptions)tree.Options, tree.FilePath);
        }
        return DeepText.Read(text.Length, () => tree.WithChangedText(text));
    }

    /// <summary>Whether a person's text reads as a type's name.</summary>
    public static bool ReadsAsType(string text) => !DeepText.TooDeep(text) && DeepText.Read(text.Length, () => !SyntaxFactory.ParseTypeName(text).ContainsDiagnostics);

    /// <summary>Whether a text's brackets go past what its parser is given.</summary>
    private static bool TooDeep(SourceText text)
    {
        char[] stretch = ArrayPool<char>.Shared.Rent(4096);
        Brackets brackets = new();
        for (int at = 0; at < text.Length && !brackets.TooDeep; at += stretch.Length)
        {
            int count = Math.Min(stretch.Length, text.Length - at);
            text.CopyTo(at, stretch, 0, count);
            brackets.Read(stretch.AsSpan(0, count));
        }
        ArrayPool<char>.Shared.Return(stretch);
        return brackets.TooDeep;
    }

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
