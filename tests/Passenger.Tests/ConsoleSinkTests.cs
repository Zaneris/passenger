using Avalonia.Headless.XUnit;
using Passenger.App.ToolWindows;

namespace Passenger.Tests;

/// <summary>A source's lines pumped into a merged log under its name (a container's in the Containers window): every line
/// shown, its last too where it ends with no line break, and a line that never breaks shown in pieces as it comes.</summary>
public sealed class ConsoleSinkTests
{
    private const string Prefix = "web | ";

    [AvaloniaFact]
    public void A_merged_logs_source_has_its_last_line_shown_where_it_ends_with_no_line_break()
    {
        OutputView console = new(toolbar: false);
        Ui.Await(Waiting.Apart(() => console.Sink.PumpAsync(new StringReader("a\r\nb"), ConsoleStream.Output, prefix: Prefix)), "the source's lines pumped");
        Ui.WaitFor(() => Shown(console) == $"{Prefix}a\n{Prefix}b\n", () => $"the merged log shows [{Shown(console)}], not both lines under the source's name");
    }

    [AvaloniaFact]
    public void A_merged_logs_line_of_ten_megabytes_with_no_break_is_shown_in_pieces_before_its_source_ends()
    {
        OutputView console = new(toolbar: false);
        Unbroken reader = new(10 * 1024 * 1024);
        Task pumped = Waiting.Apart(() => console.Sink.PumpAsync(reader, ConsoleStream.Output, prefix: Prefix));
        try
        {
            Ui.WaitFor(() => reader.AllRead && Shown(console) is { } shown && shown.Contains($"\n{Prefix}{new string('x', 1000)}", StringComparison.Ordinal) && shown.EndsWith('\n'),
                () => $"the merged log shows {Shown(console).Length} characters of the line, its source not yet ended");
            Assert.False(pumped.IsCompleted, "The source ended before its line was shown");
            string[] lines = Shown(console).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.All(l => l.Length <= Prefix.Length + (16 * 1024)), $"A piece of the line holds {lines.Max(l => l.Length)} characters");
        }
        finally
        {
            reader.End();
            Ui.Await(pumped, "the source's end pumped");
        }
    }

    private static string Shown(OutputView console)
    {
        console.Flush();
        return console.Document.Snapshot.ToString();
    }

    /// <summary>A source writing one line of a length and no break, then holding until <see cref="End"/>.</summary>
    private sealed class Unbroken(int characters) : TextReader
    {
        private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int left = characters;

        /// <summary>Whether all of it has been read.</summary>
        public bool AllRead => Volatile.Read(ref left) == 0;

        public void End() => ended.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (left == 0)
            {
                await ended.Task.WaitAsync(cancellationToken);
                return 0;
            }
            int read = Math.Min(left, buffer.Length);
            buffer.Span[..read].Fill('x');
            Volatile.Write(ref left, left - read);
            return read;
        }
    }
}
