using System.Net.Sockets;
using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A child's output read as text as it comes (ChildProcesses.Output on macOS and Linux): decoded as the process's own
/// reader decodes it, a byte order mark naming the encoding, whatever pieces its pipe cuts it into; and a read that waits
/// answers once its stream's read does, with what that brought or how it failed.</summary>
public class ChildTextTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static TheoryData<string, byte[]> Texts => new()
    {
        { "ASCII with its line ends", "one\r\ntwo\nthree\r"u8.ToArray() },
        { "UTF-8 of one to four bytes a character", Encoding.UTF8.GetBytes("café 漢字 \U0001F600 €") },
        { "UTF-8 after its mark", [0xEF, 0xBB, 0xBF, .. "mark"u8] },
        { "UTF-16 after its little-endian mark", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hé 漢")] },
        { "UTF-16 after its big-endian mark", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("hé 漢")] },
        { "UTF-32 after its little-endian mark", [0xFF, 0xFE, 0, 0, .. Encoding.UTF32.GetBytes("\U0001F600x")] },
        { "UTF-32 after its big-endian mark", [0, 0, 0xFE, 0xFF, .. new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes("x\U0001F600")] },
        { "a character cut short at the end", [.. "ok"u8, 0xE6, 0xBC] },
        { "bytes that are no UTF-8", [0xFF, 0x41, 0xC3, 0x28, 0x80] },
        { "the start of a mark alone", [0xEF, 0xBB] },
        { "a lone NUL", [0] },
        { "NULs that begin no mark", [0, 0, 0x41, 0] },
        { "nothing", [] },
    };

    /// <summary>Every place the bytes can be cut into two reads, and reads of one byte, each read taken a few characters
    /// at a time: the text is what the process's reader reads from the bytes whole.</summary>
    [Theory]
    [MemberData(nameof(Texts))]
    public async Task Bytes_cut_into_reads_anywhere_read_as_the_processs_own_reader_reads_them_whole(string what, byte[] bytes)
    {
        string expected = await new StreamReader(new MemoryStream(bytes), Utf8, detectEncodingFromByteOrderMarks: true)
            .ReadToEndAsync(TestContext.Current.CancellationToken);
        for (int cut = 0; cut <= bytes.Length; cut++)
        {
            int at = cut;
            string read = await TextAsync(bytes, reads => reads == 0 ? at : bytes.Length);
            Assert.True(read == expected, $"{what} cut after {cut} bytes read as [{read}], not [{expected}]");
        }
        Assert.Equal(expected, await TextAsync(bytes, _ => 1));
    }

    /// <summary>A read its stream makes wait answers once the stream's read ends: a first byte that begins no byte order mark
    /// at once, the start of a mark held until what follows tells, a character cut between two reads once whole, and
    /// nothing once the stream has ended, which lets the stream go.</summary>
    [Fact]
    public async Task A_read_that_waits_answers_as_its_streams_read_ends_with_the_characters_it_brought()
    {
        HeldStream stream = new();
        using ChildText text = new(stream, Utf8);
        char[] buffer = new char[64];
        CancellationToken cancel = TestContext.Current.CancellationToken;

        ValueTask<int> read = text.ReadAsync(buffer, cancel);
        Assert.False(read.IsCompleted, "A read with nothing written answered");
        stream.Give(">"u8);
        Assert.True(read.IsCompleted, "A prompt's one byte, which begins no byte order mark, was held back");
        Assert.Equal(">", new string(buffer, 0, await read));

        read = text.ReadAsync(buffer, cancel);
        stream.Give([0xE6, 0xBC]);
        Assert.False(read.IsCompleted, "Half a character was read as one");
        stream.Give([0xA2, (byte)'\n']);
        Assert.Equal("漢\n", new string(buffer, 0, await read));

        read = text.ReadAsync(buffer, cancel);
        stream.End();
        Assert.Equal(0, await read);
        Assert.True(stream.Disposed, "The stream was kept once its text had ended");
    }

    [Fact]
    public async Task The_start_of_a_byte_order_mark_waits_for_the_rest_and_the_mark_is_left_out()
    {
        HeldStream stream = new();
        using ChildText text = new(stream, Utf8);
        char[] buffer = new char[64];
        ValueTask<int> read = text.ReadAsync(buffer, TestContext.Current.CancellationToken);
        stream.Give([0xEF]);
        Assert.False(read.IsCompleted, "The first byte of a UTF-8 mark was read before the rest told whether it is one");
        stream.Give([0xBB]);
        stream.Give([0xBF, .. "x"u8]);
        Assert.Equal("x", new string(buffer, 0, await read));
    }

    /// <summary>A failed read of a pipe's socket fails the read waiting on it as a stream's failure, an IOException, as it
    /// fails a read that does not wait; a cancelled one is cancelled.</summary>
    [Fact]
    public async Task A_read_whose_stream_fails_fails_with_an_IOException_and_one_cancelled_is_cancelled()
    {
        HeldStream stream = new();
        using ChildText text = new(stream, Utf8);
        char[] buffer = new char[64];
        ValueTask<int> read = text.ReadAsync(buffer, TestContext.Current.CancellationToken);
        stream.Fail(new SocketException((int)SocketError.OperationAborted));
        IOException failed = await Assert.ThrowsAsync<IOException>(() => read.AsTask());
        Assert.IsType<SocketException>(failed.InnerException);

        read = text.ReadAsync(buffer, TestContext.Current.CancellationToken);
        stream.Fail(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.AsTask());

        using ChildText failing = new(new FailingStream(), Utf8);
        await Assert.ThrowsAsync<IOException>(() => failing.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void A_byte_order_mark_is_looked_for_until_what_came_begins_none()
    {
        Assert.Null(ChildText.MarkOf([0xFF, 0xFE], ended: false));
        Assert.Equal((Encoding.Unicode, 2), ChildText.MarkOf([0xFF, 0xFE], ended: true));
        Assert.Equal((Encoding.Unicode, 2), ChildText.MarkOf([0xFF, 0xFE, 0x41], ended: false));
        Assert.Equal((Encoding.UTF32, 4), ChildText.MarkOf([0xFF, 0xFE, 0, 0], ended: false));
        Assert.Equal(((Encoding?)null, 0), ChildText.MarkOf(">"u8, ended: false));
        Assert.Equal(((Encoding?)null, 0), ChildText.MarkOf([0xEF, 0x41], ended: false));
    }

    private static async Task<string> TextAsync(byte[] bytes, Func<int, int> chunks)
    {
        using ChildText text = new(new ChunkedStream(bytes, chunks), Utf8);
        StringBuilder read = new();
        char[] buffer = new char[3];
        int count;
        while ((count = await text.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
        {
            read.Append(buffer, 0, count);
        }
        return read.ToString();
    }

    /// <summary>A stream whose every read waits until the test gives it bytes, ends it or fails it, the reader going on at once.</summary>
    private sealed class HeldStream : Stream
    {
        private TaskCompletionSource<int>? waiting;
        private Memory<byte> into;

        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.Null(waiting);
            waiting = new TaskCompletionSource<int>();
            into = buffer;
            return new ValueTask<int>(waiting.Task);
        }

        public void Give(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(into.Span);
            Waiting().SetResult(bytes.Length);
        }

        public void End() => Waiting().SetResult(0);

        public void Fail(Exception failure) => Waiting().SetException(failure);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private TaskCompletionSource<int> Waiting()
        {
            TaskCompletionSource<int> read = waiting ?? throw new InvalidOperationException("No read waits");
            waiting = null;
            return read;
        }
    }

    /// <summary>A stream whose reads fail at once as a pipe's socket fails.</summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new SocketException((int)SocketError.ConnectionReset));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
