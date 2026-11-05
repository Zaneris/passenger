using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.App.Endpoints;

/// <summary>A multipart body as a server reads it, from one written in an .http file, whose lines end as the editor's do: its
/// framing (each delimiter, the line end before it, and each part's headers) ended in CRLF, by which its parts are found, and
/// each part's own lines as written. A part that is itself multipart is framed by its own boundary.</summary>
public static partial class MultipartBody
{
    private const string LineEnd = "\r\n";

    /// <summary>A body as it goes under a Content-Type: framed where that names a multipart boundary, else as written.</summary>
    public static string Framed(string body, string contentType)
    {
        if (BoundaryOf(contentType) is not { } boundary)
        {
            return body;
        }
        StringBuilder framed = new(body.Length);
        List<string> open = [boundary];
        string? nested = null;
        bool inHeaders = false;
        for (int at = 0, next; at < body.Length; at = next)
        {
            int end = body.AsSpan(at).IndexOfAny('\r', '\n') is >= 0 and int found ? at + found : body.Length;
            next = body.AsSpan(end).StartsWith(LineEnd) ? end + 2 : Math.Min(end + 1, body.Length);
            string line = body[at..end];
            string ending = next > end ? LineEnd : string.Empty;
            if (Delimiter(line, open) is { } delimiter)
            {
                EndLastLine(framed);
                framed.Append(line).Append(ending);
                int closing = delimiter.Closes ? delimiter.Boundary : delimiter.Boundary + 1;
                open.RemoveRange(closing, open.Count - closing);
                inHeaders = !delimiter.Closes;
                nested = null;
            }
            else if (!inHeaders)
            {
                framed.Append(body, at, next - at);
            }
            else if (line.Length > 0)
            {
                framed.Append(line).Append(ending);
                nested = line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase) ? BoundaryOf(line["Content-Type:".Length..]) : nested;
            }
            else
            {
                framed.Append(ending);
                inHeaders = false;
                if (nested is not null)
                {
                    open.Add(nested);
                }
            }
        }
        return framed.ToString();
    }

    /// <summary>The boundary a multipart Content-Type names; null for any other type, or one that names none.</summary>
    private static string? BoundaryOf(string contentType)
    {
        Match named = Boundary().Match(contentType);
        return contentType.TrimStart().StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) && named.Success
            ? named.Groups[named.Groups[1].Success ? 1 : 2].Value
            : null;
    }

    /// <summary>The open boundary a line delimits, the innermost first, and whether it closes it; null for any other line,
    /// which a boundary only begins (<c>--passengers</c>).</summary>
    private static (int Boundary, bool Closes)? Delimiter(string line, List<string> open)
    {
        ReadOnlySpan<char> text = line.AsSpan().TrimEnd(" \t");
        for (int i = open.Count - 1; i >= 0; i--)
        {
            if (text.StartsWith("--") && text[2..].StartsWith(open[i]))
            {
                ReadOnlySpan<char> rest = text[(2 + open[i].Length)..];
                if (rest.IsEmpty || rest.SequenceEqual("--"))
                {
                    return (i, !rest.IsEmpty);
                }
            }
        }
        return null;
    }

    /// <summary>Ends what is framed so far in CRLF, in place of any line end: the one before a delimiter is the delimiter's,
    /// no part of what comes before it.</summary>
    private static void EndLastLine(StringBuilder framed)
    {
        if (framed.Length == 0)
        {
            return;
        }
        if (framed[^1] == '\n')
        {
            framed.Length--;
        }
        if (framed.Length > 0 && framed[^1] == '\r')
        {
            framed.Length--;
        }
        framed.Append(LineEnd);
    }

    [GeneratedRegex(@"(?:^|;)\s*boundary\s*=\s*(?:""([^""]*)""|([^\s;]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Boundary();
}
