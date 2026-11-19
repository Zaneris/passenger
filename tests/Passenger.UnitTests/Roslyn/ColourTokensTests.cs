using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Roslyn;

/// <summary>Semantic tokens written again by the extension with each type taken from a table, how it leaves out a name the
/// server could not bind: every token kept stays at its line and column with its length and modifiers, however many before
/// it, on its line or the lines above, were left out, and takes the type the table gives.</summary>
public class ColourTokensTests
{
    [Fact]
    public void A_token_left_out_moves_none_after_it_on_its_line_or_below()
    {
        Token[] tokens = [new(0, 0, 3, 0, 0), new(0, 4, 3, 1, 0), new(0, 8, 1, 2, 1), new(2, 4, 2, 1, 0), new(2, 8, 1, 0, 2)];
        int[] types = [10, ColourTokens.LeftOut, 12];
        Assert.Equal([new Token(0, 0, 3, 10, 0), new(0, 8, 1, 12, 1), new(2, 8, 1, 10, 2)], Decoded(ColourTokens.Retyped(Encoded(tokens), types)));
    }

    [Fact]
    public void Leaving_out_the_first_token_places_the_next_from_the_files_start()
    {
        Token[] tokens = [new(0, 2, 3, 1, 0), new(0, 9, 4, 0, 0), new(1, 0, 2, 1, 0), new(1, 3, 2, 0, 0)];
        int[] types = [7, ColourTokens.LeftOut];
        Assert.Equal([new Token(0, 9, 4, 7, 0), new(1, 3, 2, 7, 0)], Decoded(ColourTokens.Retyped(Encoded(tokens), types)));
    }

    [Fact]
    public void A_token_whose_type_the_table_lacks_is_left_out_and_tokens_all_left_out_are_none()
    {
        Token[] tokens = [new(0, 0, 3, 5, 0), new(0, 4, 3, 0, 0)];
        Assert.Equal([new Token(0, 4, 3, 3, 0)], Decoded(ColourTokens.Retyped(Encoded(tokens), [3])));
        Assert.Empty(ColourTokens.Retyped(Encoded(tokens), [ColourTokens.LeftOut]));
        Assert.Empty(ColourTokens.Retyped([], [3]));
    }

    [Fact]
    public void Tokens_spread_over_many_lines_each_keep_their_place_whichever_are_left_out()
    {
        Random random = new(1962);
        for (int round = 0; round < 200; round++)
        {
            List<Token> tokens = [];
            int line = 0;
            int column = 0;
            for (int i = random.Next(0, 60); i > 0; i--)
            {
                if (random.Next(3) == 0)
                {
                    line += random.Next(1, 4);
                    column = 0;
                }
                column += random.Next(0, 6);
                int length = random.Next(1, 8);
                tokens.Add(new Token(line, column, length, random.Next(0, 6), random.Next(0, 8)));
                column += length;
            }
            int[] types = [.. Enumerable.Range(0, 5).Select(type => random.Next(3) == 0 ? ColourTokens.LeftOut : type + 20)];
            Token[] expected = [.. tokens.Where(t => t.Type < types.Length && types[t.Type] != ColourTokens.LeftOut).Select(t => t with { Type = types[t.Type] })];
            Assert.Equal(expected, Decoded(ColourTokens.Retyped(Encoded(tokens), types)));
        }
    }

    /// <summary>A token at its line and column.</summary>
    private sealed record Token(int Line, int Start, int Length, int Type, int Modifiers);

    /// <summary>The protocol's relative encoding of tokens in order.</summary>
    private static int[] Encoded(IEnumerable<Token> tokens)
    {
        List<int> data = [];
        Token last = new(0, 0, 0, 0, 0);
        foreach (Token token in tokens)
        {
            data.AddRange([token.Line - last.Line, token.Line == last.Line ? token.Start - last.Start : token.Start, token.Length, token.Type, token.Modifiers]);
            last = token;
        }
        return [.. data];
    }

    private static Token[] Decoded(int[] data)
    {
        Assert.Equal(0, data.Length % 5);
        List<Token> tokens = [];
        int line = 0;
        int start = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            start = data[i] == 0 ? start + data[i + 1] : data[i + 1];
            tokens.Add(new Token(line, start, data[i + 2], data[i + 3], data[i + 4]));
        }
        return [.. tokens];
    }
}
