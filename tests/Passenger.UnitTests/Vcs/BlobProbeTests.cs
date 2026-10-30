using System.Text;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>Whether a conflict's blobs are binary, read from cat-file --batch's answer as it comes in pieces: each blob
/// looked at by its first probe's bytes alone however large it is, a NUL past them not seen, and the answers read up to
/// one that is cut short.</summary>
public class BlobProbeTests
{
    [Fact]
    public async Task Each_blob_is_looked_at_by_at_most_the_probes_bytes_however_large_it_is()
    {
        byte[] large = new byte[3 * 1024 * 1024];
        large.AsSpan().Fill((byte)'x');
        large[FileText.BinaryProbe + 10] = 0;
        byte[] asset = new byte[20_000];
        asset[100] = 0;
        byte[] wide = [0xFF, 0xFE, (byte)'a', 0, (byte)'b', 0];
        byte[] output = Answers(("aaaa", Encoding.ASCII.GetBytes("abc")), ("bbbb", large), ("cccc", asset), ("dddd", null), ("eeee", wide));
        List<int> looked = [];

        Dictionary<string, bool> found = await BlobProbe.ReadAsync(new ChunkedStream(output, read => 1 + (read * 7919 % 50_000)), ["aaaa", "bbbb", "cccc", "dddd", "eeee"], bytes =>
        {
            looked.Add(bytes.Length);
            return FileText.LooksBinary(bytes);
        });

        int[] probed = [3, FileText.BinaryProbe, FileText.BinaryProbe, wide.Length];
        Assert.True(probed.SequenceEqual(looked), $"The probe looked at {string.Join(", ", looked)} bytes of the blobs, not at most {FileText.BinaryProbe} of each");
        Assert.Equal(new Dictionary<string, bool> { ["aaaa"] = false, ["bbbb"] = false, ["cccc"] = true, ["dddd"] = false, ["eeee"] = false }, found);
    }

    [Fact]
    public async Task An_answer_cut_short_or_not_a_blobs_leaves_it_and_those_after_it_unanswered()
    {
        byte[] whole = Answers(("aaaa", [1, 0, 2]), ("bbbb", new byte[100]));
        byte[] cut = whole[..^40];
        Dictionary<string, bool> found = await BlobProbe.ReadAsync(new ChunkedStream(cut, 9), ["aaaa", "bbbb"], FileText.LooksBinary);
        Assert.Equal(new Dictionary<string, bool> { ["aaaa"] = true }, found);

        byte[] odd = [.. Answers(("aaaa", [0])), .. Encoding.ASCII.GetBytes("bbbb ambiguous\n"), .. Answers(("cccc", [0]))];
        Assert.Equal(new Dictionary<string, bool> { ["aaaa"] = true }, await BlobProbe.ReadAsync(new MemoryStream(odd), ["aaaa", "bbbb", "cccc"], FileText.LooksBinary));
    }

    /// <summary>cat-file --batch's answer for each blob: its header, its bytes and a line break, or "missing" for none.</summary>
    private static byte[] Answers(params (string Blob, byte[]? Bytes)[] blobs)
    {
        using MemoryStream output = new();
        foreach ((string blob, byte[]? bytes) in blobs)
        {
            output.Write(Encoding.ASCII.GetBytes(bytes is null ? $"{blob} missing\n" : $"{blob} blob {bytes.Length}\n"));
            if (bytes is not null)
            {
                output.Write(bytes);
                output.WriteByte((byte)'\n');
            }
        }
        return output.ToArray();
    }
}
