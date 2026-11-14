using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A document's steps of undo joined into one (<see cref="TextDocument.JoinUndoSince"/>), as a command line of
/// several commands is one change: undone and redone at once, the steps before it left as they were, and the text saved
/// between two of them no place an undo stops at, where the text saved after the last still is.</summary>
public class UndoJoinTests
{
    [Fact]
    public void The_steps_made_since_a_place_undo_and_redo_as_one_and_those_before_it_alone()
    {
        TextDocument document = new("one two");
        Edit(document, 0, 3, "1");
        long place = document.UndoPlace;
        Edit(document, 2, 3, "2");
        Edit(document, 0, 0, "<");
        Edit(document, 4, 0, ">");
        document.JoinUndoSince(place);
        Assert.Equal("<1 2>", document.Snapshot.ToString());
        document.Undo();
        Assert.Equal("1 two", document.Snapshot.ToString());
        document.Redo();
        Assert.Equal("<1 2>", document.Snapshot.ToString());
        document.Undo();
        document.Undo();
        Assert.Equal("one two", document.Snapshot.ToString());
        Assert.False(document.CanUndo, "An undo was left after the step before the joined ones and the joined one were taken back");
    }

    [Fact]
    public void A_text_saved_after_the_last_step_is_saved_again_by_a_redo_and_one_saved_between_them_never_is()
    {
        TextDocument document = new("a b");
        long place = document.UndoPlace;
        Edit(document, 0, 1, "x");
        Edit(document, 2, 1, "y");
        document.MarkSaved();
        document.JoinUndoSince(place);
        document.Undo();
        Assert.True(document.IsModified, "The text before the joined step reads as saved");
        document.Redo();
        Assert.False(document.IsModified, "The text saved after the joined step's last edit reads as changed once redone");

        TextDocument between = new("a b");
        place = between.UndoPlace;
        Edit(between, 0, 1, "x");
        between.MarkSaved();
        Edit(between, 2, 1, "y");
        between.JoinUndoSince(place);
        between.Undo();
        Assert.True(between.IsModified, "The text before the joined step reads as saved");
        between.Redo();
        Assert.True(between.IsModified, "The text after the joined step reads as the one saved between its edits");
    }

    [Fact]
    public void One_step_made_since_the_place_or_none_is_left_as_it_is()
    {
        TextDocument document = new("abc");
        Edit(document, 0, 1, "x");
        long place = document.UndoPlace;
        document.JoinUndoSince(place);
        Edit(document, 1, 1, "y");
        document.JoinUndoSince(place);
        document.Undo();
        Assert.Equal("xbc", document.Snapshot.ToString());
    }

    private static void Edit(TextDocument document, int start, int length, string text) =>
        document.Edit([new TextEdit(start, length, text)], SelectionSet.Single(0), SelectionSet.Single(0));
}
