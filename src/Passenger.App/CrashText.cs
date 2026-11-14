using System.Globalization;

namespace Passenger.App;

/// <summary>A crash as <c>crash.log</c> keeps it in the log folder: a line saying when, the process and the version
/// that ended on it, then the exception whole, as the runtime writes it; and what the next start says of it.</summary>
public static class CrashText
{
    /// <summary>How the first line ends, after the time, the process and the version.</summary>
    private const string Ended = "ended on this error:";

    private const string Time = "yyyy-MM-dd HH:mm:ss.fff";

    public static string Of(object thrown, DateTime at, int process, string version) =>
        $"{at.ToString(Time, CultureInfo.InvariantCulture)} [{process}] Passenger {version} {Ended}{Environment.NewLine}{thrown}{Environment.NewLine}";

    /// <summary>The crash a file's text keeps, its time the text's own, else <paramref name="written"/>; null for a text
    /// keeping none.</summary>
    public static KeptCrash? Read(string text, DateTime written)
    {
        string[] lines = text.ReplaceLineEndings("\n").Trim('\n').Split('\n');
        bool headed = lines[0].EndsWith(Ended, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(lines[0]) || (headed && lines.Length == 1))
        {
            return null;
        }
        DateTime at = headed && lines[0].Length >= Time.Length
            && DateTime.TryParseExact(lines[0][..Time.Length], Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kept) ? kept : written;
        string first = headed ? lines[1] : lines[0];
        int colon = first.IndexOf(": ", StringComparison.Ordinal);
        string type = colon < 0 ? first : first[..colon];
        string message = colon < 0 ? string.Empty : first[(colon + 2)..];
        return new KeptCrash(at, type[(type.LastIndexOf('.') + 1)..], Said(message), text.Trim());
    }

    /// <summary>What the window says of an exception a run before this one ended on.</summary>
    public static string Said(string message) =>
        message.Length > 0 ? $"Passenger ended on this error as it last ran: {message}" : "Passenger ended on this error as it last ran";
}

/// <summary>An exception a run ended on: when, its type's name, what the window says of it, and the file's text whole.</summary>
public sealed record KeptCrash(DateTime At, string Kind, string Message, string Details);
