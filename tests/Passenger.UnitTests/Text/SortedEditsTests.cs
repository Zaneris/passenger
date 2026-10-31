using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>An offset carried through a change's sorted edits, found by halving: where reading the edits from the first
/// puts it, before or after text inserted at it, inside a replaced range at its start or end, over seeded changes whose
/// insertions stand where a replacement ends or begins.</summary>
public class SortedEditsTests
{
    [Fact]
    public void An_offset_carried_by_halving_lands_where_reading_the_edits_from_the_first_puts_it()
    {
        Random random = new(1040);
        for (int run = 0; run < 300; run++)
        {
            List<TextEdit> edits = [];
            int at = random.Next(0, 3);
            for (int i = random.Next(0, 12); i > 0; i--)
            {
                int length = random.Next(0, 3) == 0 ? 0 : random.Next(1, 4);
                edits.Add(new TextEdit(at, length, new string('x', random.Next(0, 4))));
                at += length + random.Next(0, 3);
            }
            SortedEdits sorted = SortedEdits.Of([.. edits]);
            for (int offset = 0; offset <= at + 2; offset++)
            {
                foreach (bool afterInsertions in new[] { false, true })
                {
                    int read = TextEditing.MapOffset(offset, edits, afterInsertions);
                    Assert.True(read == sorted.Map(offset, afterInsertions),
                        $"Offset {offset} (after insertions: {afterInsertions}) through [{string.Join(", ", edits.Select(e => $"{e.Start}+{e.Length}→{e.Text.Length}"))}] "
                        + $"is carried to {sorted.Map(offset, afterInsertions)}, not {read}, so a caret or a fold lands beside where the change put it");
                }
            }
        }
    }
}
