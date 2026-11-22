using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Xaml;

/// <summary>A previewer's answers read as Passenger reads them, from the UTF-8 bytes it writes a line at a time with
/// Windows' line ends: a frame with its picture and boxes, an error with its place, nothing to draw, and a line of
/// another's handed on as text.</summary>
public class PreviewRepliesTests
{
    [Fact]
    public Task A_previewers_answers_are_read_from_its_bytes_a_line_at_a_time() =>
        Each_answer_the_previewer_writes_is_read_as_it_meant_it();

    private async Task Each_answer_the_previewer_writes_is_read_as_it_meant_it()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
        byte[] written = Written(
            new PreviewReply { Kind = PreviewKinds.Frame, Sequence = 3, Png = png, Scale = 2, Elements = [new PreviewBox(4, 6, 0, 0, 120.5, 40)],
                Notes = [new PreviewNote("d:DataContext did not load", 5), new PreviewNote("App.xaml's resources did not load")] },
            new PreviewReply { Kind = PreviewKinds.Error, Sequence = 4, Message = "'Grid' has no property 'Colour'", Line = 7, Column = 12 },
            new PreviewReply { Kind = PreviewKinds.Nothing, Sequence = 5, Message = "A ResourceDictionary is not drawn" });
        byte[] said = [.. "a word of the runtime's own\r\n"u8, .. written];

        List<string> others = [];
        List<PreviewReply> read = [];
        await foreach (PreviewReply reply in JsonLines.ReadAsync(new ChunkedStream(said, 5), PreviewWire.Reply, (line, _) => others.Add(line),
            TestContext.Current.CancellationToken))
        {
            read.Add(reply);
        }

        Assert.Equal(["a word of the runtime's own"], others);
        Assert.Equal([3, 4, 5], read.Select(r => r.Sequence));
        Assert.Equal(png, read[0].Png ?? []);
        Assert.Equal((2.0, new PreviewBox(4, 6, 0, 0, 120.5, 40)), (read[0].Scale, Assert.Single(read[0].Elements ?? [])));
        Assert.Equal([new PreviewNote("d:DataContext did not load", 5), new PreviewNote("App.xaml's resources did not load")], read[0].Notes ?? []);
        Assert.Equal(("'Grid' has no property 'Colour'", 7, 12), (read[1].Message, read[1].Line, read[1].Column));
        Assert.Equal((PreviewKinds.Nothing, "A ResourceDictionary is not drawn"), (read[2].Kind, read[2].Message));
    }

    /// <summary>Answers as the previewer writes them: each serialized and written as a line of UTF-8, with Windows' line end.</summary>
    private static byte[] Written(params PreviewReply[] replies)
    {
        MemoryStream bytes = new();
        using (StreamWriter protocol = new(bytes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { NewLine = "\r\n" })
        {
            foreach (PreviewReply reply in replies)
            {
                PreviewWire.Answer(protocol, reply);
            }
        }
        return bytes.ToArray();
    }
}
