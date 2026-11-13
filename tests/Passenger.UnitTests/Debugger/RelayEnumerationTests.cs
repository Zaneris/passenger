using Passenger.Debugger.Relay;

namespace Passenger.UnitTests.Debugger;

/// <summary>An enumerator in the app read many elements at a time gives the engine what the app's own would, an element
/// at a time: each element, a failure where the app's failed (once, as the app's moves past it), then its end.</summary>
public class RelayEnumerationTests
{
    private const int Failure = unchecked((int)0x80131304);

    [Fact]
    public void Elements_read_many_at_a_time_come_one_at_a_time_as_the_apps_enumerator_would_give_them()
    {
        Random random = new(1544);
        for (int round = 0; round < 200; round++)
        {
            int?[] items = [.. Enumerable.Range(0, random.Next(0, 40)).Select(i => random.Next(6) == 0 ? (int?)null : i)];
            int each = random.Next(1, 4);
            List<(int Hr, int? Element)> alone = Walk(new AppEnumerator(items), null, each);
            AppEnumerator batched = new(items);
            List<(int Hr, int? Element)> read = Walk(batched, new EnumBuffer<int>(), each);
            Assert.True(alone.SequenceEqual(read), $"Round {round}: read in batches the walk gave {string.Join(" ", read)} where the app's enumerator gives {string.Join(" ", alone)}");
            Assert.True(batched.Asked <= 1 + items.Count(each => each is null) + items.Length / 8,
                $"Round {round}: {batched.Asked} round trips for {items.Length} elements, where a batch of 8 needs one per failure and per 8");
        }
    }

    [Fact]
    public void Skipping_lets_go_of_what_it_passes_over_and_counts_a_failure_held_as_one()
    {
        AppEnumerator app = new([0, 1, null, 3, 4]);
        EnumBuffer<int> buffer = new();
        Span<int> one = stackalloc int[1];
        Assert.Equal(EnumBuffer<int>.S_OK, buffer.Next(1, one, out _, app.Next, 8));
        List<int> letGo = [];
        Assert.Equal(EnumBuffer<int>.S_OK, buffer.Skip(2, letGo.Add, app.Skip));
        Assert.Equal([1], letGo);
        Assert.Equal(EnumBuffer<int>.S_OK, buffer.Next(1, one, out int fetched, app.Next, 8));
        Assert.True(fetched == 1 && one[0] == 3, $"After skipping an element and a failure the next element was {one[0]} ({fetched} fetched), not 3");
    }

    [Fact]
    public void Clearing_lets_go_of_every_element_held()
    {
        EnumBuffer<int> buffer = new();
        Span<int> one = stackalloc int[1];
        buffer.Next(1, one, out _, new AppEnumerator([0, 1, 2, 3]).Next, 8);
        List<int> letGo = [];
        buffer.Clear(letGo.Add);
        Assert.Equal([1, 2, 3], letGo);
        Assert.Equal(0, buffer.HeldCount);
    }

    private static List<(int Hr, int? Element)> Walk(AppEnumerator app, EnumBuffer<int>? buffer, int each)
    {
        List<(int, int?)> walked = [];
        int[] into = new int[each];
        for (int step = 0; step < 100; step++)
        {
            int fetched;
            int hr = buffer is null ? app.NextInto(each, into, out fetched) : buffer.Next(each, into, out fetched, app.Next, 8);
            for (int i = 0; i < fetched; i++)
            {
                walked.Add((0, into[i]));
            }
            walked.Add((hr, null));
            if (hr == EnumBuffer<int>.S_FALSE)
            {
                break;
            }
        }
        return walked;
    }

    /// <summary>The app's enumerator of a frame's values: each element, or a failure (null) that ends a Next with what came
    /// before it and moves past itself.</summary>
    private sealed class AppEnumerator(int?[] items)
    {
        private int _at;

        public int Asked { get; private set; }

        public int NextInto(int count, Span<int> into, out int fetched)
        {
            Asked++;
            fetched = 0;
            while (fetched < count && _at < items.Length)
            {
                if (items[_at] is not int item)
                {
                    _at++;
                    return Failure;
                }
                into[fetched++] = item;
                _at++;
            }
            return fetched < count ? EnumBuffer<int>.S_FALSE : EnumBuffer<int>.S_OK;
        }

        public int Next(int count, List<int> into)
        {
            int[] got = new int[count];
            int hr = NextInto(count, got, out int fetched);
            into.AddRange(got.AsSpan(0, fetched));
            return hr;
        }

        public int Skip(int count)
        {
            _at = Math.Min(items.Length, _at + count);
            return EnumBuffer<int>.S_OK;
        }
    }
}
