using System.Text;
using Passenger.Text;

namespace Passenger.Vcs;

/// <summary>A file as a revision (or a conflict's side) has it, read as its editor reads the file: its text, or for a
/// binary file its bytes alone, never read as text.</summary>
public sealed record FileVersion(string? Text, byte[]? Binary)
{
    public bool IsBinary => Binary is not null;

    /// <summary>A version's bytes read in <paramref name="encoding"/> (the open file's) where that reads them all, else as
    /// <see cref="FileText.FromBytes"/> reads a file at <paramref name="path"/>.</summary>
    public static FileVersion Read(byte[] bytes, Encoding? encoding, string path) =>
        FileText.FromBytes(bytes, encoding, () => EditorConfig.For(path).Charset) is { } text ? new(text.Text, null) : new(null, bytes);
}
