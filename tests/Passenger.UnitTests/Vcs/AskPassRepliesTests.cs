using System.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>The IDE's reply to git's helper as the helper reads it (<see cref="GitAskPass.Read"/>): the byte saying the
/// question was taken, then whether there is an answer, then the answer; a reply that stops short is none, saying where.</summary>
public class AskPassRepliesTests
{
    [Theory]
    [InlineData("?+harbour-light-7", "harbour-light-7")]
    [InlineData("+harbour-light-7", "harbour-light-7")]
    [InlineData("?+", "")]
    [InlineData("?+pass+word?", "pass+word?")]
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
}
