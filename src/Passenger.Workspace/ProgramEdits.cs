using System.Text;
using Passenger.Core;
using Passenger.Host;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>A file-based program's head following files and folders that moved or were renamed: each directive whose value
/// is a path from the program's folder (<c>#:project</c>, <c>#:include</c>, <c>#:exclude</c>, <c>#:ref</c>) names where
/// its file is now, from where the program is now, a wildcard's by the folder it begins in. A quoted value stays quoted, a
/// value keeps its separators and the item metadata after it (<c>Aliases=Lib</c>), and the rest of the file keeps its
/// bytes, encoding, byte order mark and line endings.
/// Paths are canonical.</summary>
public static class ProgramEdits
{
    private static readonly string[] PathKinds = ["project", "include", "exclude", "ref"];

    /// <summary>The program (at its path now) following the moves: a directive naming what moved names where it went, and
    /// a program that moved itself reads its paths from where it was, so one naming a file it left behind still finds it.
    /// A value naming a property or a list stays as written. Whether it changed; a file that does not read throws.</summary>
    public static bool MoveDirectives(string program, IReadOnlyList<(string From, string To)> moves)
    {
        FileText file = FileText.Load(program) ?? throw new IOException($"{Path.GetFileName(program)} is not a text file");
        string text = file.Text;
        string folder = Path.GetDirectoryName(program)!;
        string Moved(string path) => moves.FirstOrDefault(m => PathNames.Within(path, m.From)) is { From: not null } move ? PathNames.Moved(path, move.From, move.To) : path;
        string before = Path.GetDirectoryName(moves.FirstOrDefault(m => PathNames.Within(program, m.To)) is { To: not null } back ? PathNames.Moved(program, back.To, back.From) : program)!;
        List<(int Start, int Length)> lines = [];
        List<(int Start, int Length, string With)> edits = [];
        foreach ((int line, string directive) in ProgramFiles.HeadAt(Lines(text, lines)))
        {
            // A directive after a comment on its line is left as written, its value's place in the line unsure.
            if (ProgramFiles.Read(directive) is not { Value.Length: > 0 } read || !PathKinds.Contains(read.Kind) || read.Value.IndexOfAny(['$', ';', '@', '%']) >= 0
                || !text.AsSpan(lines[line].Start, lines[line].Length).Trim().SequenceEqual(directive))
            {
                continue;
            }
            string value = read.Value;
            int wild = value.IndexOfAny(['*', '?']);
            string path = (wild < 0 ? value : value[..Math.Max(0, value.LastIndexOfAny(['\\', '/'], wild))]).TrimEnd('\\', '/');
            if (path.Trim().Length == 0)
            {
                continue;
            }
            string now = Moved(Full(before, path));
            if (PathNames.Same(Full(folder, path), now))
            {
                continue;
            }
            string named = Path.IsPathFullyQualified(path) ? now : Path.GetRelativePath(folder, now);
            string moved = (path.Contains('\\') ? named.Replace('/', '\\') : named.Replace('\\', '/')) + value[path.Length..];
            int start = lines[line].Start + (lines[line].Length - text.AsSpan(lines[line].Start, lines[line].Length).TrimStart().Length) + read.Start;
            // Quoted only where white space would part it from its metadata: an SDK before .NET 11 refuses a quote.
            bool quoted = text[start] == '"' || moved.Contains('"', StringComparison.Ordinal)
                || read.Start + read.Length < directive.Length && moved.Any(char.IsWhiteSpace);
            edits.Add((start, read.Length, quoted ? $"\"{moved.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : moved));
        }
        if (edits.Count == 0)
        {
            return false;
        }
        StringBuilder edited = new(text);
        foreach ((int start, int length, string with) in edits.OrderByDescending(e => e.Start))
        {
            edited.Remove(start, length).Insert(start, with);
        }
        FileText.SaveOver(program, file, edited.ToString());
        return true;

        static string Full(string folder, string relative) => Path.GetFullPath(Path.Combine(folder, relative.Replace('\\', '/')));
    }

    /// <summary>A text's lines as a reader reads them, ended by "\r\n", "\r" or "\n", each one's place kept in
    /// <paramref name="places"/> as it is read.</summary>
    private static IEnumerable<string> Lines(string text, List<(int Start, int Length)> places)
    {
        int start = 0;
        while (start < text.Length)
        {
            int found = text.AsSpan(start).IndexOfAny('\r', '\n');
            int end = found < 0 ? text.Length : start + found;
            places.Add((start, end - start));
            yield return text[start..end];
            start = found < 0 ? text.Length : end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
    }
}
