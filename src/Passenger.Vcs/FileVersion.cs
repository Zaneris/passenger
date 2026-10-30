using System.Text;
using Passenger.Text;

namespace Passenger.Vcs;

/// <summary>A file as a revision (or a conflict's side) has it, read as its editor reads the file: its text with how it
/// was stored (its encoding and byte order mark), or for a binary file none, its bytes never read as text.</summary>
public sealed record FileVersion(FileText? Stored)
{
    public string? Text => Stored?.Text;

    public bool IsBinary => Stored is null;

    /// <summary>A version's bytes read in <paramref name="encoding"/> (the open file's) where that reads them all, else as
    /// <see cref="FileText.FromBytes"/> reads a file at <paramref name="path"/>.</summary>
    public static FileVersion Read(byte[] bytes, Encoding? encoding, string path) =>
        new(FileText.FromBytes(bytes, encoding, () => EditorConfig.For(path).Charset));

    /// <summary>A revision's bytes (<see cref="StoredBytes"/>) read so: a binary file's, whose bytes were never kept, as
    /// binary.</summary>
    public static FileVersion Read(StoredBytes stored, Encoding? encoding, string path) =>
        stored.Text is { } bytes ? Read(bytes, encoding, path) : new((FileText?)null);
}
