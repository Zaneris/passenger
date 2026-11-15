using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A name many messages share (a collection's elements' type, a test's file and class) is read once and handed out
/// again however its bytes' hash falls in this process, whose seed is the process's own: names whose hashes meet are found
/// here, as some runs of the IDE meet them.</summary>
public sealed class SharedNamesTests
{
    [Fact]
    public void Two_names_read_in_turn_whose_hashes_meet_are_each_read_once()
    {
        string[] meeting = Meeting("Shared.Order", 2);
        string order = Read(meeting[0]);
        string lines = Read(meeting[1]);
        for (int turn = 0; turn < 3; turn++)
        {
            Assert.True(ReferenceEquals(order, Read(meeting[0])) && ReferenceEquals(lines, Read(meeting[1])),
                "Two names read in turn whose hashes met were read anew at each turn: in the runs where a collection's two element types meet so, "
                + "each of its variables carries a copy of its type's name");
        }
    }

    [Fact]
    public void A_name_read_between_others_whose_hashes_meet_its_own_is_read_once()
    {
        string[] meeting = Meeting("Shared.Passed", 3);
        string passed = Read(meeting[0]);
        foreach (string other in meeting[1..])
        {
            Read(other);
            Assert.True(ReferenceEquals(passed, Read(meeting[0])),
                "A name read between each of two others whose hashes met its own was read anew: a test run's every result would carry a copy of its state");
        }
    }

    /// <summary>The name and as many others as make up <paramref name="count"/> whose hashes meet in one pair, in this process.</summary>
    private static string[] Meeting(string name, int count)
    {
        int pair = SharedNames.PairOf(Encoding.ASCII.GetBytes(name));
        IEnumerable<string> others = Enumerable.Range(0, 100_000).Select(i => $"{name}.{i}")
            .Where(other => SharedNames.PairOf(Encoding.ASCII.GetBytes(other)) == pair);
        return [name, .. others.Take(count - 1)];
    }

    private static string Read(string name)
    {
        Utf8JsonReader reader = new(JsonSerializer.SerializeToUtf8Bytes(name));
        reader.Read();
        return SharedNames.Read(ref reader)!;
    }
}
