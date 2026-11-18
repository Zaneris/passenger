using Avalonia.Headless.XUnit;
using Passenger.App.Containers;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A source's lines pumped into a merged log under its name (a container's in the Containers window): every line
/// shown, its last too where it ends with no line break, and a line that never breaks shown in pieces as it comes; plain
/// output copied once on its way in and once into the console's text; a flood past what the console keeps cut to its end;
/// and a program's colours and styles drawn as it set them, every other control sequence showing nothing, a secret masked
/// where a colour code splits it.</summary>
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

    /// <summary>A program's colours and styles in each form SGR gives them, written across separate writes, are the looks
    /// of its text: the theme's sixteen terminal colours by their tokens, the 256 and 24-bit ones as colours, bold, italic,
    /// underline, inverse and faint, standard error's own colour where it sets none; while a cursor move, a screen's
    /// clearing, a title, a link, a DCS string and the bell show nothing, and an OSC a line break comes into, never ended,
    /// is given up at it, the next line shown.</summary>
    [AvaloniaFact]
    public void A_programs_colours_and_styles_are_its_texts_looks_and_every_other_control_sequence_shows_nothing()
    {
        OutputView console = new(toolbar: false);
        console.Sink.Write("plain \u001b[31mred\u001b[0m \u001b[1;3;4;94mbright\u001b[22;23;24m blue\u001b[39m \u001b[38;5;196mcube\u001b[0m ");
        console.Sink.Write("\u001b[38;2;10;200;30mrgb\u001b[0m \u001b[7minverse\u001b[0m \u001b[2mfaint\u001b[0m\n");
        console.Sink.Write("\u001b[2J\u001b[H\u001b[5Amoved\u001b]0;a title\u0007 titled \u001b]8;;https://example.com\u0007linked\u001b]8;;\u0007 \u001bPqdata\u001b\\done\u0007\n");
        console.Sink.Write("before \u001b]0;never ended\nafter\n");
        console.Sink.Write("\u001b[33myellow\u001b[0m error\n", ConsoleStream.Error);
        string shown = Shown(console);
        Assert.Equal("plain red bright blue cube rgb inverse faint\nmoved titled linked done\nbefore \nafter\nyellow error\n", shown);

        Assert.Null(ConsoleColoursTests.LookAt(console, "plain"));
        Assert.Equal(new TextLook(LookColour.Of(TokenKeys.Terminal_Ansi1), default, LookStyle.None), ConsoleColoursTests.LookAt(console, "red"));
        Assert.Equal(new TextLook(LookColour.Of(TokenKeys.Terminal_Ansi12), default, LookStyle.Bold | LookStyle.Italic | LookStyle.Underline), ConsoleColoursTests.LookAt(console, "bright"));
        Assert.Equal(new TextLook(LookColour.Of(TokenKeys.Terminal_Ansi12), default, LookStyle.None), ConsoleColoursTests.LookAt(console, " blue"));
        Assert.Equal(new TextLook(LookColour.Of(0xFF0000), default, LookStyle.None), ConsoleColoursTests.LookAt(console, "cube"));
        Assert.Equal(new TextLook(LookColour.Of(0x0AC81E), default, LookStyle.None), ConsoleColoursTests.LookAt(console, "rgb"));
        Assert.Equal(new TextLook(LookColour.Of(TokenKeys.Editor_Background), LookColour.Of(TokenKeys.Editor_Text), LookStyle.None), ConsoleColoursTests.LookAt(console, "inverse"));
        Assert.Equal(Passenger.Terminal.TerminalColours.Faint, ConsoleColoursTests.LookAt(console, "faint")!.Value.Opacity);
        Assert.Equal(new TextLook(LookColour.Of(TokenKeys.Terminal_Ansi3), default, LookStyle.None), ConsoleColoursTests.LookAt(console, "yellow"));
        TextSnapshot text = console.Document.Snapshot;
        IReadOnlyList<SyntaxSpan> spans = console.Editor.Highlighter!.GetSpans(text, 0, text.Length);
        Assert.Contains(spans, s => s.Kind == TokenKind.ErrorOutput && text.GetText(s.Start, s.Length) == " error\n");
        Assert.DoesNotContain(spans, s => s.End > shown.IndexOf("moved", StringComparison.Ordinal) && s.Start < shown.IndexOf("yellow", StringComparison.Ordinal));
    }

    /// <summary>What a stream shows masked (a container's command's), read through its colour codes first: a secret a code
    /// splits is masked as one written whole would be, and the colours around it are kept where they were.</summary>
    [AvaloniaFact]
    public void A_secret_a_colour_code_splits_is_masked_and_the_colours_around_it_kept()
    {
        OutputView console = new(toolbar: false);
        Func<string, string> masking = ContainerSecrets.Masking(known: ["s3cr3t-value"]);
        StringReader written = new("\u001b[32mok\u001b[0m key s3cr\u001b[1m3t-value\u001b[0m \u001b[34mafter\u001b[0m\n\u001b[35mmore\u001b[0m DB_PASSWORD=hun\u001b[31mter2\u001b[0m\n");
        Ui.Await(Waiting.Apart(() => console.Sink.PumpAsync(written, ConsoleStream.Output, shown: masking)), "the stream pumped");
        string shown = Shown(console);
        Assert.Equal($"ok key {ContainerSecrets.Masked} after\nmore DB_PASSWORD={ContainerSecrets.Masked}\n", shown);
        Assert.Equal(TokenKeys.Terminal_Ansi2, ConsoleColoursTests.LookAt(console, "ok")?.Fore.Token);
        Assert.Equal(TokenKeys.Terminal_Ansi4, ConsoleColoursTests.LookAt(console, "after")?.Fore.Token);
        Assert.Equal(TokenKeys.Terminal_Ansi5, ConsoleColoursTests.LookAt(console, "more")?.Fore.Token);
        Assert.Null(ConsoleColoursTests.LookAt(console, " key"));
    }

    /// <summary>A merged log's source keeps its colours from line to line as it set them, its name before each line
    /// plain, and another source's lines take none of them.</summary>
    [AvaloniaFact]
    public void A_merged_logs_source_keeps_its_own_colours_its_name_plain()
    {
        OutputView console = new(toolbar: false);
        Ui.Await(Waiting.Apart(() => console.Sink.PumpAsync(new StringReader("\u001b[31mred line\nstill red"), ConsoleStream.Output, prefix: Prefix)), "the first source pumped");
        Ui.Await(Waiting.Apart(() => console.Sink.PumpAsync(new StringReader("db line\n"), ConsoleStream.Output, prefix: "db | ")), "the second source pumped");
        Assert.Equal($"{Prefix}red line\n{Prefix}still red\ndb | db line\n", Shown(console));
        Assert.Equal(TokenKeys.Terminal_Ansi1, ConsoleColoursTests.LookAt(console, "red line")?.Fore.Token);
        Assert.Equal(TokenKeys.Terminal_Ansi1, ConsoleColoursTests.LookAt(console, "still red")?.Fore.Token);
        TextSnapshot text = console.Document.Snapshot;
        IReadOnlyList<SyntaxSpan> spans = console.Editor.Highlighter!.GetSpans(text, 0, text.Length);
        int second = text.ToString().IndexOf($"\n{Prefix}", StringComparison.Ordinal) + 1;
        foreach (int name in (int[])[0, second])
        {
            Assert.DoesNotContain(spans, s => s.Start < name + Prefix.Length && s.End > name);
        }
        Assert.Null(ConsoleColoursTests.LookAt(console, "db | db line"));
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
