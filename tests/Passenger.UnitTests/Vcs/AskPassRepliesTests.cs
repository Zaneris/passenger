using System.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>The IDE's reply to git's helper as the helper reads it (<see cref="GitAskPass.Read"/>): the byte saying the
/// question was taken, then whether there is an answer, then the answer to its end byte; a reply that stops short is none,
/// saying where; and the helper waits for no more once the reply is whole (<see cref="GitAskPass.Whole"/>).</summary>
public class AskPassRepliesTests
{
    [Theory]
    [InlineData("?+harbour-light-7", "harbour-light-7")]
    [InlineData("+harbour-light-7", "harbour-light-7")]
    [InlineData("?+", "")]
    [InlineData("?+pass+word?", "pass+word?")]
    [InlineData("?+harbour-light-7\0", "harbour-light-7")]
    [InlineData("+harbour-light-7\0", "harbour-light-7")]
    [InlineData("?+\0", "")]
    public void An_answer_is_read_after_the_byte_saying_it_was_taken_or_without_it(string reply, string answer)
    {
        (string? read, string? why) = GitAskPass.Read(Encoding.UTF8.GetBytes(reply));
        Assert.True(read == answer && why is null, $"The reply \"{reply}\" read as \"{read}\" ({why}), not \"{answer}\", so git is given another answer than the person's");
    }

    [Fact]
    public void A_question_the_ide_refused_is_none_with_no_words_of_the_helpers()
    {
        (string? read, string? why) = GitAskPass.Read("?-"u8);
        Assert.True(read is null && why is null, $"A refused question read as \"{read}\" ({why})");
    }

    [Theory]
    [InlineData("?", "the IDE took its question and ended before answering")]
    [InlineData("", "the IDE ended without taking its question")]
    public void A_reply_that_stops_short_is_none_saying_how_far_it_came(string reply, string said)
    {
        (string? read, string? why) = GitAskPass.Read(Encoding.UTF8.GetBytes(reply));
        Assert.True(read is null && why == said, $"The reply \"{reply}\" read as \"{read}\" ({why}), not none saying \"{said}\", so git's errors would not say why");
    }

    [Theory]
    [InlineData("?+harbour-light-7\0")]
    [InlineData("?+\0")]
    [InlineData("?-")]
    [InlineData("-")]
    [InlineData("?*")]
    public void A_reply_with_its_answer_to_its_end_or_none_is_whole_so_the_helper_waits_for_nothing_more(string reply)
    {
        Assert.True(GitAskPass.Whole(Encoding.UTF8.GetBytes(reply)),
            $"The reply \"{reply}\" was not taken as whole, so the helper waits for the IDE to end its side, which Windows may never pass on, and git with it");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("?+harbour")]
    [InlineData("+harbour")]
    public void A_reply_short_of_its_answers_end_is_not_whole_so_the_helper_reads_on(string reply)
    {
        Assert.False(GitAskPass.Whole(Encoding.UTF8.GetBytes(reply)), $"The reply \"{reply}\" was taken as whole, so git would be given part of an answer, or none");
    }
}
