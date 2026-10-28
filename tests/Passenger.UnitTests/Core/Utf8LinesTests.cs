using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A peer's output read as lines of UTF-8 bytes (a project host's, a test host's), whatever pieces its pipe cuts them
/// into: each line whole and without its end, however long, and a "\r\n" cut between two reads still one end.</summary>
public class Utf8LinesTests
{
    [Fact]
    public async Task Lines_come_without_their_ends_a_lone_CR_stays_in_its_line_and_a_last_line_without_an_end_is_a_line()
    {
        Assert.Equal(["one", string.Empty, "two", "a\rb", string.Empty, "last"], await LinesAsync("one\r\n\ntwo\na\rb\r\n\r\nlast", chunk: 1_000));
        Assert.Equal(["one", "two"], await LinesAsync("one\ntwo\n", chunk: 1_000));
        Assert.Empty(await LinesAsync(string.Empty, chunk: 1_000));
    }

    /// <summary>Every place the text can be cut into two reads, and reads of one byte: the CR of a "\r\n" at the end of one read
    /// and its LF at the start of the next end one line, and a character of several bytes cut in two comes whole.</summary>
    [Fact]
    public async Task A_CRLF_or_a_character_cut_between_two_reads_reads_as_if_it_came_in_one()
    {
        const string Text = "first\r\nsé€\U0001F600\r\n\r\nthird\n";
        string[] expected = ["first", "sé€\U0001F600", string.Empty, "third"];
        byte[] bytes = Encoding.UTF8.GetBytes(Text);
        for (int cut = 0; cut <= bytes.Length; cut++)
        {
            int at = cut;
            Assert.Equal(expected, await LinesAsync(bytes, read => read == 0 ? Math.Max(1, at) : bytes.Length));
        }
        Assert.Equal(expected, await LinesAsync(bytes, _ => 1));
    }

    [Theory]
    [InlineData(64 * 1024 - 1)]
    [InlineData(64 * 1024)]
    [InlineData(64 * 1024 + 1)]
    [InlineData(1_000_000)]
    public async Task A_line_longer_than_the_buffer_comes_whole_and_the_short_lines_after_it_too(int length)
    {
        string longLine = new('x', length);
        string text = $"before\n{longLine}\r\nafter\n{longLine}{longLine}\nshort\n";
        Assert.Equal(["before", longLine, "after", longLine + longLine, "short"], await LinesAsync(text, chunk: 8_192));
    }

    /// <summary>Lines of every length from none to a few hundred thousand bytes, with either end, read in seeded random pieces.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_seeded_run_of_random_lines_in_random_reads_comes_back_line_for_line(int seed)
    {
        Random random = new(seed);
        List<string> lines = [];
        StringBuilder text = new();
        for (int i = 0; i < 300; i++)
        {
            int length = random.Next(10) switch
            {
                0 => random.Next(300_000),
                < 3 => random.Next(70_000),
                _ => random.Next(200),
            };
            string line = string.Create(length, random.Next(), static (span, seed) =>
            {
                Random letters = new(seed);
                for (int c = 0; c < span.Length; c++)
                {
                    span[c] = (char)letters.Next('a', 'z' + 1);
                }
            });
            lines.Add(line);
            text.Append(line).Append(random.Next(2) == 0 ? "\n" : "\r\n");
        }
        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        Assert.Equal(lines, await LinesAsync(bytes, _ => random.Next(3) == 0 ? random.Next(1, 16) : random.Next(1, 200_000)));
    }

    private static Task<List<string>> LinesAsync(string text, int chunk) => LinesAsync(Encoding.UTF8.GetBytes(text), _ => chunk);

    private static async Task<List<string>> LinesAsync(byte[] bytes, Func<int, int> chunks)
    {
        using Utf8Lines reader = new(new ChunkedStream(bytes, chunks));
        List<string> lines = [];
        while (await reader.NextAsync(TestContext.Current.CancellationToken) is { } line)
        {
            lines.Add(Encoding.UTF8.GetString(line.Span));
        }
        return lines;
    }
}
