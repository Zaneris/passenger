using System.Text;
using System.Text.Json;
using Passenger.Core;
using Passenger.WpfPreview;

namespace Passenger.UnitTests.Xaml;

/// <summary>The WPF previewer's answers read as Passenger reads them, from the UTF-8 bytes it writes a line at a time with
/// Windows' line ends: a frame with its picture and boxes, an error with its place, nothing to draw, and a line of
/// another's handed on as text; a frame costing what it is read as, never its text made a string first.</summary>
public class WpfPreviewRepliesTests
{
    [Fact]
    public async Task Each_answer_the_previewer_writes_is_read_as_it_meant_it()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
        byte[] written = Written(
            new WpfPreviewReply { Kind = WpfPreviewKinds.Frame, Sequence = 3, Png = png, Scale = 2, Elements = [new WpfPreviewBox(4, 6, 0, 0, 120.5, 40)] },
            new WpfPreviewReply { Kind = WpfPreviewKinds.Error, Sequence = 4, Message = "'Grid' has no property 'Colour'", Line = 7, Column = 12 },
            new WpfPreviewReply { Kind = WpfPreviewKinds.Nothing, Sequence = 5, Message = "A ResourceDictionary is not drawn" });
        byte[] said = [.. "a word of the runtime's own\r\n"u8, .. written];

        List<string> others = [];
        List<WpfPreviewReply> read = [];
        await foreach (WpfPreviewReply reply in JsonLines.ReadAsync(new ChunkedStream(said, 5), WpfPreviewJson.Reply, (line, _) => others.Add(line),
            TestContext.Current.CancellationToken))
        {
            read.Add(reply);
        }

        Assert.Equal(["a word of the runtime's own"], others);
        Assert.Equal([3, 4, 5], read.Select(r => r.Sequence));
        Assert.Equal(png, read[0].Png ?? []);
        Assert.Equal((2.0, new WpfPreviewBox(4, 6, 0, 0, 120.5, 40)), (read[0].Scale, Assert.Single(read[0].Elements ?? [])));
        Assert.Equal(("'Grid' has no property 'Colour'", 7, 12), (read[1].Message, read[1].Line, read[1].Column));
        Assert.Equal((WpfPreviewKinds.Nothing, "A ResourceDictionary is not drawn"), (read[2].Kind, read[2].Message));
    }

    [Fact]
    public async Task A_frame_costs_its_picture_and_never_its_text()
    {
        byte[] png = new byte[39_999];
        new Random(2).NextBytes(png);
        WpfPreviewReply frame = new() { Kind = WpfPreviewKinds.Frame, Sequence = 1, Png = png };
        byte[] said = Written(frame, frame with { Sequence = 2 });

        long[] after = new long[2];
        int count = 0;
        await foreach (WpfPreviewReply reply in JsonLines.ReadAsync(new MemoryStream(said), WpfPreviewJson.Reply, (_, _) => { },
            TestContext.Current.CancellationToken))
        {
            after[count++] = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(png.AsSpan().SequenceEqual(reply.Png), "A frame's picture read back other than the previewer wrote it");
        }

        long allocated = after[1] - after[0];
        Assert.True(allocated < png.Length + 8 * 1024,
            $"A frame holding a {png.Length:N0}-byte picture allocated {allocated:N0} bytes as it was read, so each frame the previewer draws costs its text again");
    }

    /// <summary>Answers as the previewer writes them: each serialized and written as a line of UTF-8, with Windows' line end.</summary>
    private static byte[] Written(params WpfPreviewReply[] replies)
    {
        MemoryStream bytes = new();
        using (StreamWriter protocol = new(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { NewLine = "\r\n" })
        {
            foreach (WpfPreviewReply reply in replies)
            {
                protocol.WriteLine(JsonSerializer.Serialize(reply, WpfPreviewJson.Options));
            }
        }
        return bytes.ToArray();
    }
}
