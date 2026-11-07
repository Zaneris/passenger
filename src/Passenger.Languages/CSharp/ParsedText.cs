using Microsoft.CodeAnalysis;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>A C# file's syntax at one version of its text: Roslyn's tree and the editor's snapshot it read. Lines are
/// the snapshot's, never the tree's text's: Roslyn ends a line at characters the editor does not. Immutable, so it may
/// be read on any thread.</summary>
public sealed record ParsedText(SyntaxTree Tree, TextSnapshot Text)
{
    public SyntaxNode Root => Tree.GetRoot();

    /// <summary>Where the text went too deep to read, null where it was read (<see cref="CSharpParsing.DeepAt(SyntaxTree)"/>).</summary>
    public int? DeepAt => CSharpParsing.DeepAt(Tree);
}
