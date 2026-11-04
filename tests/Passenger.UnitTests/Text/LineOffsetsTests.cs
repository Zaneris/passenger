using System.Globalization;
using System.Text;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Many places turned into offsets at once (<see cref="TextSnapshot.GetOffsets"/>), as a server's diagnostics
/// and highlights are: each as one at a time gives it, at the cost of reading the lines between them.</summary>
public class LineOffsetsTests
{
    [Fact]
    public void Places_in_any_order_get_the_offsets_one_at_a_time_gives_whatever_their_breaks_lines_and_columns()
    {
        TextSnapshot text = TextSnapshot.From(Lines(10_000));
        Random random = new(1152);
        TextPosition[] places = new TextPosition[3000];
        for (int i = 0; i < places.Length; i++)
        {
            places[i] = new TextPosition(random.Next(-3, 10_005), random.Next(-2, 120));
        }
        int[] offsets = new int[places.Length];
        text.GetOffsets(places, offsets);
        for (int i = 0; i < places.Length; i++)
        {
            Assert.True(offsets[i] == text.GetOffset(places[i]),
                $"Line {places[i].Line}, column {places[i].Column} came to {offsets[i]} among many places, where alone it comes to {text.GetOffset(places[i])}");
        }
    }

    [Fact]
    public void Three_thousand_places_over_ten_thousand_lines_seek_a_line_from_the_ropes_root_a_few_times_not_once_each()
    {
        TextSnapshot text = TextSnapshot.From(Lines(10_000));
        Random random = new(1250);
        TextPosition[] places = new TextPosition[3000];
        for (int i = 0; i < places.Length; i++)
        {
            places[i] = new TextPosition(random.Next(0, 10_000), random.Next(0, 40));
        }
        int[] offsets = new int[places.Length];
        long before = TextSnapshot.LinesSought;
        text.GetOffsets(places, offsets);
        long together = TextSnapshot.LinesSought - before;
        before = TextSnapshot.LinesSought;
        foreach (TextPosition place in places)
        {
            text.GetOffset(place);
        }
        long apart = TextSnapshot.LinesSought - before;
        Assert.True(together < 20,
            $"3,000 places over 10,000 lines sought {together} lines from the rope's root together, {apart} one at a time: a walk down the rope for each place");
    }

    [Fact]
    public void Places_far_apart_seek_each_line_rather_than_read_every_line_between()
    {
        TextSnapshot text = TextSnapshot.From(Lines(50_000));
        TextPosition[] places = [.. Enumerable.Range(0, 100).Select(i => new TextPosition((99 - i) * 500, 3))];
        int[] offsets = new int[places.Length];
        long before = TextSnapshot.LinesSought;
        text.GetOffsets(places, offsets);
        Assert.Equal(100, TextSnapshot.LinesSought - before);
        Assert.Equal(text.GetOffset(places[0]), offsets[0]);
        Assert.Equal(text.GetOffset(places[^1]), offsets[^1]);
    }

    /// <summary>Lines of several lengths ending in "\n" and "\r\n", some empty, some holding a lone '\r', the last with no
    /// break.</summary>
    private static string Lines(int count)
    {
        StringBuilder text = new();
        for (int i = 0; i < count; i++)
        {
            string line = (i % 7) switch
            {
                0 => string.Empty,
                1 => "carriage\rinside",
                _ => new string('x', i % 90) + i.ToString(CultureInfo.InvariantCulture),
            };
            text.Append(line);
            if (i < count - 1)
            {
                text.Append(i % 3 == 0 ? "\r\n" : "\n");
            }
        }
        return text.ToString();
    }
}
