using System.Net.Sockets;
using System.Text;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Git's askpass helper, Passenger's own executable, asking the IDE over an operation's socket: a question the IDE
/// never takes is given up at the helper's limit, saying so, so git ends with no answer rather than waiting for good; and
/// each end takes the other's words once they are whole, never waiting for the other to end its side of the socket, which
/// Windows' Unix sockets can fail to pass on (#1455).</summary>
[Collection(Beside.Name)]
public sealed class AskPassHelperTests
{
    private static readonly TimeSpan Answering = TimeSpan.FromSeconds(30);

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

    [Fact]
    public async Task The_helper_takes_an_answer_once_whole_though_the_IDE_never_ends_its_side()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string socket = Path.Combine(SocketFolders.Own(StateStore.DefaultRoot), $"{Environment.ProcessId}-unended");
        File.Delete(socket);
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socket));
        listener.Listen(1);
        try
        {
            Task<(string? Answer, string? Why)> put = Waiting.Apart(() => GitAskPass.Put(socket, "Password for 'https://example.invalid':", Answering), cancel);
            using (Socket ide = await listener.AcceptAsync(cancel))
            {
                await ide.SendAsync("?+harbour-light-7\0"u8.ToArray(), cancel);
                bool answered = await Waiting.Within(put, Answering, cancel);
                Assert.True(answered, "The helper given a whole answer waited on for the IDE to end its side, so git waits on it for good where Windows never passes that end on");
            }
            (string? answer, string? why) = await put;
            Assert.True(answer == "harbour-light-7", $"The helper took \"{answer}\" ({why}) of the IDE's answer \"harbour-light-7\"");
        }
        finally
        {
            File.Delete(socket);
        }
    }

    [Fact]
    public async Task The_IDE_answers_a_question_once_whole_though_the_helper_never_ends_its_side()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        // A clone held at its first request: git runs on asking nothing, while the test asks as its helper would.
        await using StandInHttp remote = new(exchange => exchange.UntilClosedAsync());
        string sockets = Directory.CreateDirectory(Path.Combine(SocketFolders.Own(StateStore.DefaultRoot), $"{Environment.ProcessId}-asked")).FullName;
        GitAsker asker = new(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger"), sockets,
            (question, _) => Task.FromResult(question.Kind == GitQuestionKind.User ? new GitAnswer("tester") : GitAnswer.No($"asked \"{question.Prompt}\"")));
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<GitResult> cloning = Git.CloneAsync($"{remote.Root}/repo.git", Path.Combine(TestFolders.Create("asked-"), "repo"), _ => { }, stop.Token, asker);
        try
        {
            Assert.True(await Waiting.UntilAsync(() => cloning.IsCompleted || Directory.EnumerateFiles(sockets).Any(), Answering, cancel) && !cloning.IsCompleted,
                "The clone opened no socket for git's questions");
            using Socket asking = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await asking.ConnectAsync(new UnixDomainSocketEndPoint(Directory.EnumerateFiles(sockets).Single()), cancel);
            await asking.SendAsync("Username for 'http://127.0.0.1:1': \0"u8.ToArray(), cancel);
            List<byte> reply = [];
            Task replied = Waiting.Apart(async () =>
            {
                byte[] buffer = new byte[256];
                int read;
                while (!reply.Contains(0) && (read = await asking.ReceiveAsync(buffer, cancel)) > 0)
                {
                    reply.AddRange(buffer.AsSpan(0, read));
                }
            }, cancel);
            bool answered = await Waiting.Within(replied, Answering, cancel);
            asking.Dispose();
            await Waiting.Within(replied, Answering, cancel);
            string said = Encoding.UTF8.GetString([.. reply]);
            Assert.True(answered && said == "?+tester\0",
                $"The IDE said \"{said}\" to a whole question while its helper's side stayed open, so git waits on its helper for good where Windows never passes that end on");
        }
        finally
        {
            await stop.CancelAsync();
            Assert.True(await Waiting.Within(cloning, Answering, cancel), "The clone held at the stand-in did not end once stopped");
            Directory.Delete(sockets, recursive: true);
        }
    }
}
