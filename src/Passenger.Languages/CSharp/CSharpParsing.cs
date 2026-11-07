using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Text;

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

    /// <summary>What's said where a text goes too deep (<see cref="DeepAt(SyntaxTree)"/>).</summary>
    public const string TooDeep = "Passenger read no deeper than this, so the file is read as text: it has no colours, folds or C# language server, "
        + "and what it declares reads as missing";

    /// <summary>A file in no project's.</summary>
    public static CSharpParseOptions Default { get; } = Defining(["DEBUG", "TRACE", "NET", "NETCOREAPP"], null);

    /// <summary>A text's tree, every in-process parse's way to one: its parser runs out of no stack (<see cref="DeepText"/>),
    /// and a text nested too deeply for it is a tree of nothing, its spans still the text's.</summary>
    public static SyntaxTree Parse(string text, CSharpParseOptions? options = null, string path = "") => Parse(SourceText.From(text), options, path);

    /// <inheritdoc cref="Parse(string, CSharpParseOptions?, string)"/>
    public static SyntaxTree Parse(SourceText text, CSharpParseOptions? options = null, string path = "") =>
        DeepText.Read(text.Length, () => CSharpSyntaxTree.ParseText(CodeBrackets.DeepAt(text, options) is int at ? new Unread(text.Length, at) : text, options, path));

    /// <summary>A tree as a text changed from its own has it, the parser reading again only what changed.</summary>
    public static SyntaxTree Changed(SyntaxTree tree, SourceText text)
    {
        bool typed = text.GetChangeRanges(tree.GetText()).Sum(c => (long)c.NewLength) <= MostTyped;
        CSharpParseOptions options = (CSharpParseOptions)tree.Options;
        return DeepText.Read(text.Length, () => !typed && CodeBrackets.DeepAt(text, options) is int at
            ? CSharpSyntaxTree.ParseText(new Unread(text.Length, at), options, tree.FilePath)
            : tree.WithChangedText(text));
    }

    /// <summary>Whether a person's text reads as a type's name.</summary>
    public static bool ReadsAsType(string text) => !DeepText.TooDeep(text) && DeepText.Read(text.Length, () => !SyntaxFactory.ParseTypeName(text).ContainsDiagnostics);

    /// <summary>Where a tree's text went too deep, read as nothing; null for a text read.</summary>
    public static int? DeepAt(SyntaxTree tree) => tree.TryGetText(out SourceText? text) && text is Unread unread ? unread.At : null;

    /// <summary>Where a person's text as it stands goes too deep, null where it doesn't: asked before it goes to a reader with
    /// no such bound of its own.</summary>
    public static int? DeepAt(TextSnapshot text, CSharpParseOptions options)
    {
        SnapshotText read = new(text);
        return DeepText.Read(text.Length, () => CodeBrackets.DeepAt(read, options));
    }

    /// <summary>What's parsed in place of a text too deep: nothing, its length, and where it went too deep.</summary>
    private sealed class Unread(int length, int at) : SourceText
    {
        public int At => at;

        public override Encoding? Encoding => null;

        public override int Length => length;

        public override char this[int position] => ' ';

        public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count) => destination.AsSpan(destinationIndex, count).Fill(' ');
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
