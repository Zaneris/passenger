using System.Net.Sockets;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Git's askpass helper, Passenger's own executable, asking the IDE over an operation's socket: a question the IDE
/// never takes is given up at the helper's limit, saying so, so git ends with no answer rather than waiting for good.</summary>
[Collection(Beside.Name)]
public sealed class AskPassHelperTests
{
    [Fact]
    public void A_question_no_server_takes_is_given_up_at_the_helpers_limit_saying_so()
    {
        string socket = Path.Combine(SocketFolders.Own(StateStore.DefaultRoot), $"{Environment.ProcessId}-untaken");
        File.Delete(socket);
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socket));
        listener.Listen(1);
        try
        {
            (string? answer, string? why) = GitAskPass.Put(socket, "Password for 'https://example.invalid':", TimeSpan.FromSeconds(1));
            Assert.True(answer is null && why == "the IDE had not taken its question within 1 s",
                $"A question on a socket nobody takes from came to \"{answer}\" ({why}), so git would wait on its helper for good saying nothing");
        }
        finally
        {
            File.Delete(socket);
        }
    }
}
