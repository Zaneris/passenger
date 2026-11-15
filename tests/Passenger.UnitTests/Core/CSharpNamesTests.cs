using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A C# name's characters read where Roslyn is not (the project host, the text count of a parser's depth) are
/// Roslyn's own, so each reads a name as the compiler does.</summary>
public sealed class CSharpNamesTests
{
    [Fact]
    public void Every_character_starts_and_carries_a_name_on_as_roslyn_reads_it()
    {
        List<string> differ = [];
        for (int c = char.MinValue; c <= char.MaxValue; c++)
        {
            if (CSharpNames.Starts((char)c) != SyntaxFacts.IsIdentifierStartCharacter((char)c) || CSharpNames.Continues((char)c) != SyntaxFacts.IsIdentifierPartCharacter((char)c))
            {
                differ.Add($"U+{c:X4}");
            }
        }
        Assert.True(differ.Count == 0, $"{differ.Count} characters start or carry on a name otherwise than Roslyn reads them, so a name there is cut where the compiler reads on: {string.Join(", ", differ.Take(20))}");
    }
}
