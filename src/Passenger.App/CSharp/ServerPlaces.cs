using System.Buffers;
using Microsoft.Win32.SafeHandles;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Places in a file as a language server counts its lines (<see cref="ServerLines"/>) and as the editor does,
/// each way read in the file's text: as the editor has it when open, else as its edits read it from disk
/// (<see cref="WorkspaceEdits"/>), whose lone '\r's become '\n's one for one, so the server's lines are the same.</summary>
public static class ServerPlaces
{
    /// <summary>A file's text as the editor has it, else as read from disk (<see cref="EditorArea.TextOf"/>); null when it
    /// cannot be read.</summary>
    public static TextSnapshot? TextOf(EditorArea editors, string path) => editors.TextOf(path);

    /// <summary>A file's text as its edits read it from disk (<see cref="EditorArea.Read"/>); null when it cannot be read.
    /// Safe from any thread.</summary>
    public static TextSnapshot? Read(string path) => EditorArea.Read(path);

    /// <summary>Files' texts for a list of places, each read once for it (<see cref="TextOf"/>).</summary>
    public static Func<string, TextSnapshot?> Texts(EditorArea editors)
    {
        Dictionary<string, TextSnapshot?> read = new(PathNames.Comparer);
        return path => read.TryGetValue(path, out TextSnapshot? text) ? text : read[path] = TextOf(editors, path);
    }

    /// <summary>A server's position as the editor's line and column in a text; as the server's in a file that cannot be
    /// read, the best there is to say of it.</summary>
    public static TextPosition PositionOf(TextSnapshot? text, LspPosition at) =>
        text is null ? new TextPosition(at.Line, at.Character) : text.GetPosition(ServerLines.Of(text).OffsetOf(at));

    /// <summary>A server's position in a file as the editor's line and column there.</summary>
    public static TextPosition PositionOf(EditorArea editors, string path, LspPosition at) => PositionOf(TextOf(editors, path), at);

    /// <summary>An XML reader's line and column (from 1) in a file as the editor's line and column there (<see
    /// cref="ServerLines.OfXml"/>); null for no line, or a file that cannot be read.</summary>
    public static TextPosition? XmlPositionOf(EditorArea editors, string path, int line, int column) =>
        line > 0 && TextOf(editors, path) is { } text
            ? text.GetPosition(ServerLines.OfXml(text).OffsetOf(line - 1, Math.Max(0, column - 1)))
            : null;

    /// <summary>The editor's line and column in a text as the server's; as they are in a file that cannot be read.</summary>
    public static LspPosition ServerPositionOf(TextSnapshot? text, TextPosition at) =>
        text is null ? new LspPosition(at.Line, at.Column) : ServerLines.Of(text).PositionOf(text.GetOffset(at));

    /// <summary>A text's lines as the server counts them where they are not the editor's, else null (as for a text that
    /// could not be read): what a long list of places keeps of each file it read, since few hold such a break.</summary>
    public static ServerLines? Differing(TextSnapshot? text) =>
        text is not null && ServerLines.Of(text) is { } lines && lines.LineCount != text.LineCount ? lines : null;

    /// <summary>A file's lines from disk as the server counts them where they are not the editor's (<see cref="Differing"/> of
    /// <see cref="Read"/>), else null: its bytes looked over in a pooled buffer, and read as text only where they may hold
    /// such a break, are not UTF-8 or could not be read. Safe from any thread.</summary>
    public static ServerLines? DifferingOnDisk(string path) => MayBreakApart(path) ? Differing(Read(path)) : null;

    /// <summary>Whether a file's bytes may hold a break the server counts and the editor does not, as the editor reads them
    /// (<see cref="FileText.Load"/>): false for binary bytes, which it does not read, and for UTF-8 (after its BOM, or with
    /// none and every byte UTF-8) that holds none; true for UTF-16, 8-bit text and a file that could not be read here.</summary>
    private static bool MayBreakApart(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = RandomAccess.GetLength(handle);
            if (length > Array.MaxLength - 1)
            {
                return true;
            }
            // A byte more than its length, to learn whether it grew since.
            byte[] rented = ArrayPool<byte>.Shared.Rent((int)length + 1);
            try
            {
                int filled = 0;
                while (filled < rented.Length && RandomAccess.Read(handle, rented.AsSpan(filled), filled) is int read and > 0)
                {
                    filled += read;
                }
                return filled > length || MayBreakApart(rented.AsSpan(0, filled));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether a file's bytes, as the editor reads them, may hold a break the server counts and the editor does not.</summary>
    private static bool MayBreakApart(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return ServerLines.BreaksApart(bytes[3..]);
        }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return true;
        }
        if (FileText.LooksBinary(bytes))
        {
            return false;
        }
        return !System.Text.Unicode.Utf8.IsValid(bytes) || ServerLines.BreaksApart(bytes);
    }

    /// <summary>A server's position as the editor's line and column through lines kept by <see cref="Differing"/>.</summary>
    public static TextPosition PositionOf(ServerLines? lines, LspPosition at) =>
        lines is null ? new TextPosition(at.Line, at.Character) : lines.Text.GetPosition(lines.OffsetOf(at));

    /// <summary>Whether Roslyn's compilers read the file (C#, Visual Basic, Razor), so the lines a compiler's message or a
    /// stack frame (its PDB's) names in it are counted as the server counts them, to be read through its text.</summary>
    public static bool CountedByRoslyn(string path) =>
        PathNames.HasExtension(path, [".cs", ".csx", ".vb", RazorFiles.Component, RazorFiles.Page]);

    /// <summary>Whether a file is XAML, whose compilers (Avalonia's XamlIl, WPF's markup compiler) count its lines as an XML
    /// reader does (<see cref="ServerLines.OfXml"/>) in the places they write: a PDB's sequence points, an element's own.</summary>
    public static bool CountedAsXml(string path) => Xaml.XamlService.IsXaml(path);

    /// <summary>Whether a file's compiler counts its lines apart from the editor's, Roslyn's or XAML's.</summary>
    public static bool CountedApart(string path) => CountedByRoslyn(path) || CountedAsXml(path);

    /// <summary>A text's lines as its file's compiler counts them (Roslyn's, else an XML reader's for XAML) where they are
    /// not the editor's, else null, as for a text that could not be read or a file no such compiler reads.</summary>
    public static ServerLines? Compiled(string path, TextSnapshot? text) =>
        text is null ? null
        : CountedByRoslyn(path) ? Differing(text)
        : CountedAsXml(path) && ServerLines.OfXml(text) is { } lines && lines.LineCount != text.LineCount ? lines : null;

    /// <summary>A place a compiler wrote of a file (a PDB's line, a XAML element's; from zero) as the editor's line and
    /// column in the file's text; as written in a file that cannot be read.</summary>
    public static TextPosition PositionOfCompiled(string path, TextSnapshot? text, LspPosition at) => PositionOf(Compiled(path, text), at);

    /// <summary>The editor's line and column in a file's text as its compiler counts them; as they are in a file that
    /// cannot be read.</summary>
    public static LspPosition CompiledPositionOf(string path, TextSnapshot? text, TextPosition at) =>
        text is not null && Compiled(path, text) is { } lines ? lines.PositionOf(text.GetOffset(at)) : new LspPosition(at.Line, at.Column);

    /// <summary>Edits in a text's offsets as the server's, in its lines, for a workspace edit applied to that text.</summary>
    public static List<LspTextEdit> EditsOf(TextSnapshot text, IEnumerable<TextEdit> edits)
    {
        ServerLines lines = ServerLines.Of(text);
        return [.. edits.OrderBy(e => e.Start).Select(e => new LspTextEdit(
            lines.RangeOf(TextRange.FromBounds(Math.Min(e.Start, text.Length), Math.Min(e.End, text.Length))), e.Text))];
    }
}
