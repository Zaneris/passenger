using System.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>The IDE's reply to git's helper as the helper reads it (<see cref="GitAskPass.Read"/>): the byte saying the
/// question was taken, then whether there is an answer, then the answer to its end byte; and the helper waits for no more
/// once the reply is whole (<see cref="GitAskPass.Whole"/>).</summary>
public class AskPassRepliesTests
{
    [Fact]
    public void Gits_helper_reads_the_IDEs_reply_whole_as_the_persons_answer_or_none() => Cases.All(
        ("an answer is read after the byte saying it was taken or without it", () => Cases.Each<(string, string)>(
        [
            ("?+harbour-light-7", "harbour-light-7"),
            ("+harbour-light-7", "harbour-light-7"),
            ("?+pass+word?", "pass+word?"),
            ("?+harbour-light-7\0", "harbour-light-7"),
        ], row => An_answer_is_read_after_the_byte_saying_it_was_taken_or_without_it(row.Item1, row.Item2))),
        ("a question the ide refused is none with no words of the helpers", A_question_the_ide_refused_is_none_with_no_words_of_the_helpers),
        ("a reply with its answer to its end or none is whole so the helper waits for nothing more", () => Cases.Each<string>(
        [
            "?+harbour-light-7\0",
            "?-",
            "?*",
        ], A_reply_with_its_answer_to_its_end_or_none_is_whole_so_the_helper_waits_for_nothing_more)),
        ("a reply short of its answers end is not whole so the helper reads on", () => Cases.Each<string>(
        [
            "",
            "?+harbour",
            "+harbour",
        ], A_reply_short_of_its_answers_end_is_not_whole_so_the_helper_reads_on)));

    private void An_answer_is_read_after_the_byte_saying_it_was_taken_or_without_it(string reply, string answer)
    {
        (string? read, string? why) = GitAskPass.Read(Encoding.UTF8.GetBytes(reply));
        Assert.True(read == answer && why is null, $"The reply \"{reply}\" read as \"{read}\" ({why}), not \"{answer}\", so git is given another answer than the person's");
    }

    private void A_question_the_ide_refused_is_none_with_no_words_of_the_helpers()
    {
        (string? read, string? why) = GitAskPass.Read("?-"u8);
        Assert.True(read is null && why is null, $"A refused question read as \"{read}\" ({why})");
    }

    private void A_reply_with_its_answer_to_its_end_or_none_is_whole_so_the_helper_waits_for_nothing_more(string reply)
    {
        Assert.True(GitAskPass.Whole(Encoding.UTF8.GetBytes(reply)),
            $"The reply \"{reply}\" was not taken as whole, so the helper waits for the IDE to end its side, which Windows may never pass on, and git with it");
    }

    private void A_reply_short_of_its_answers_end_is_not_whole_so_the_helper_reads_on(string reply)
    {
        Assert.False(GitAskPass.Whole(Encoding.UTF8.GetBytes(reply)), $"The reply \"{reply}\" was taken as whole, so git would be given part of an answer, or none");
    }
}
