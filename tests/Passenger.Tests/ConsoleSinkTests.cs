using Avalonia.Headless.XUnit;
using Passenger.App.ToolWindows;

namespace Passenger.Tests;

/// <summary>A source's lines pumped into a merged log under its name (a container's in the Containers window): every line
/// shown, its last too where it ends with no line break, and a line that never breaks shown in pieces as it comes; plain
/// output copied once on its way in and once into the console's text; and a flood past what the console keeps cut to its end.</summary>
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

    /// <summary>Plain output, no control code in it, costs what the console keeps of it and little more: what writes it
    /// copies it into room kept from batch to batch, allocating under 128 bytes a kilobyte (1,024 characters) once warm,
    /// and the window's thread takes each batch as one string and copies it once into the console's text, under 5.5 KB a
    /// kilobyte: a copy more of it would pass either bound.</summary>
    [AvaloniaFact]
    public void Plain_output_is_copied_once_as_it_is_written_and_once_into_the_consoles_text()
    {
        OutputView console = new(toolbar: false);
        string batch = string.Concat(Enumerable.Repeat(new string('x', 79) + "\n", 205));
        for (int i = 0; i < 4; i++)
        {
            console.Sink.Write(batch);
            console.Flush();
        }
        const int Batches = 40;
        long written = 0;
        long taken = 0;
        for (int i = 0; i < Batches; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            console.Sink.Write(batch);
            long between = GC.GetAllocatedBytesForCurrentThread();
            console.Flush();
            taken += GC.GetAllocatedBytesForCurrentThread() - between;
            written += between - before;
        }
        double kilobytes = Batches * batch.Length / 1024.0;
        Assert.True(written / kilobytes < 128, $"Writing plain output allocated {written / kilobytes:N0} bytes a kilobyte of it: it is copied more than once on its way in");
        Assert.True(taken / kilobytes < 5.5 * 1024, $"Taking plain output into the console allocated {taken / kilobytes:N0} bytes a kilobyte of it on the window's thread: it is copied more than once into the text");
        Assert.Equal(44 * batch.Length, console.Document.Snapshot.Length);
    }

    /// <summary>A flood of more than twice what the console keeps, written while it takes no batch, as a program writing
    /// without pause outruns the window: what waits is cut to the newest as it comes, and the console shows the end of
    /// what was written whole and in order, wherever the cuts fell.</summary>
    [AvaloniaFact]
    public void A_flood_past_what_the_console_keeps_shows_the_end_of_it_whole_and_in_order()
    {
        OutputView console = new(toolbar: false) { Limit = 20_000 };
        System.Text.StringBuilder written = new();
        for (int i = 0; i < 1_000; i++)
        {
            string line = $"line {i:D4} {new string((char)('a' + (i % 26)), 60 + (i % 97))}\n";
            written.Append(line);
            console.Sink.Write(line);
        }
        string shown = Shown(console);
        Assert.True(shown.Length is > 10_000 and <= 20_000, $"The console kept {shown.Length:N0} characters of a flood of {written.Length:N0}, keeping 20,000");
        Assert.True(written.ToString().EndsWith(shown, StringComparison.Ordinal), $"The console's text is not the end of what was written: it begins \"{shown[..Math.Min(40, shown.Length)]}\"");
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
