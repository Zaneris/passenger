using System.Text;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>What taking an encoding would do, whichever way it is taken.</summary>
public enum Safety
{
    /// <summary>Nothing changes but the name: the bytes read as the text the editor has, or the text writes as the bytes
    /// the file has.</summary>
    Same,

    /// <summary>The text, or the bytes, change, with nothing lost.</summary>
    Changes,

    /// <summary>Something is lost or most likely wrong: bytes not stored in it, or characters it cannot hold.</summary>
    Unsafe,
}

/// <summary>What another encoding would do to an open file, weighed before Reload or Convert is asked: reading the
/// file's bytes again in it, and writing the text in it.</summary>
public sealed record EncodingChange(FileEncoding To, Safety Reload, Safety Convert, string? Unheld, bool OtherBom)
{
    /// <summary>Weighs an encoding for a file whose bytes on disk are <paramref name="bytes"/> (null when it has none) and
    /// whose text, as the editor holds it, is <paramref name="text"/>.</summary>
    public static EncodingChange Weigh(byte[]? bytes, string text, Encoding current, bool crBreaks, FileEncoding to)
    {
        string? unheld = FileText.Unheld(to.Encoding, text);
        bool otherBom = bytes is not null && FileText.Bom(bytes) is { } bom && bom.CodePage != to.CodePage;
        return new EncodingChange(to, ReloadSafety(bytes, text, current, otherBom, to), ConvertSafety(bytes, text, crBreaks, unheld, to), unheld, otherBom);
    }

    /// <summary>Whether a choice of the same encoding with a BOM or without is all it is, which Add BOM and Remove BOM
    /// make without a question.</summary>
    public static bool OnlyBom(Encoding current, FileEncoding to) => current.CodePage == to.CodePage && to.CodePage == 65001;

    private static Safety ReloadSafety(byte[]? bytes, string text, Encoding current, bool otherBom, FileEncoding to)
    {
        // A BOM of another encoding, or none where the encoding needs one (UTF-16, read back only by its BOM).
        if (bytes is null || otherBom || (to.CodePage is 1200 or 1201 && FileText.Bom(bytes) is null))
        {
            return Safety.Unsafe;
        }
        FileText read = FileText.Decode(bytes, to.Encoding);
        if (!Written(read.Text, to.Encoding, read.HasBom, read.CrBreaks).AsSpan().SequenceEqual(bytes))
        {
            return Safety.Unsafe;
        }
        // A file read as UTF-8 because its bytes are UTF-8 is most likely in no other encoding.
        if (current.CodePage == 65001 && to.CodePage != 65001 && bytes.Any(b => b >= 0x80))
        {
            return Safety.Unsafe;
        }
        return read.Text == text ? Safety.Same : Safety.Changes;
    }

    private static Safety ConvertSafety(byte[]? bytes, string text, bool crBreaks, string? unheld, FileEncoding to)
    {
        if (unheld is not null)
        {
            return Safety.Unsafe;
        }
        return bytes is not null && Written(text, to.Encoding, to.Bom, crBreaks).AsSpan().SequenceEqual(bytes) ? Safety.Same : Safety.Changes;
    }

    /// <summary>The bytes a text makes written in an encoding, as a save writes them.</summary>
    private static byte[] Written(string text, Encoding encoding, bool bom, bool crBreaks) =>
        [.. bom ? FileText.Preamble(encoding) : [], .. encoding.GetBytes(crBreaks ? LineEndings.In(text, "\r") : text)];

    /// <summary>Each character of a text an encoding cannot hold made '?', as Convert Anyway writes it.</summary>
    public static List<TextEdit> Lost(Encoding encoding, string text)
    {
        List<TextEdit> lost = [];
        if (FileText.Unheld(encoding, text) is null)
        {
            return lost;
        }
        Encoding strict = (Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        for (int i = 0; i < text.Length;)
        {
            int width = char.IsSurrogatePair(text, i) ? 2 : 1;
            if (text[i] >= 0x80)
            {
                try
                {
                    strict.GetByteCount(text.AsSpan(i, width));
                }
                catch (EncoderFallbackException)
                {
                    lost.Add(new TextEdit(i, width, "?"));
                }
            }
            i += width;
        }
        return lost;
    }
}
