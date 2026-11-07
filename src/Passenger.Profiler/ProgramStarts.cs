using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A program's start, which a live session waits out where its runtime can hang for good: before .NET 11's first
/// release candidate, a session enabling a managed EventSource (the runtime's metrics, ASP.NET Core's events, logging) runs
/// that source's command under EventSource's listeners' lock, and the command may need a type the program's own start is
/// making (ArrayPool's EventSource, as its first array is rented), whose making waits for that lock. Past its start is
/// judged by its age, the system's record of when it started, which reads none of its code and holds for a program
/// Passenger did not start: a start slower than <see cref="Settling"/>, or a first rented array later than it, can still
/// meet the hang.</summary>
public static class ProgramStarts
{
    /// <summary>How long a program is taken to be starting, as much longer on a slower machine as every wait of the IDE's.</summary>
    public static TimeSpan Settling => Waits.Of(TimeSpan.FromSeconds(5));

    /// <summary>How long a session at process <paramref name="processId"/> waits before it starts: none for a program past
    /// <see cref="Settling"/>, gone or unreadable, or on a runtime that takes sessions as it starts.</summary>
    public static TimeSpan DueAt(int processId)
    {
        if (DotnetProcesses.StartedAt(processId) is not { } started)
        {
            return TimeSpan.Zero;
        }
        TimeSpan age = DateTime.UtcNow - started;
        return age >= Settling ? TimeSpan.Zero : Due(RuntimeVersion(processId), age, Settling);
    }

    /// <summary>How long a session waits at a program <paramref name="age"/> old on runtime <paramref name="version"/> (its
    /// CoreLib's product version, "10.0.14-servicing.26522.126+…"; null when unread): the rest of <paramref name="settling"/>,
    /// or none.</summary>
    public static TimeSpan Due(string? version, TimeSpan age, TimeSpan settling) =>
        TakesSessionsAsItStarts(version) || age >= settling ? TimeSpan.Zero : settling - (age < TimeSpan.Zero ? TimeSpan.Zero : age);

    /// <summary>Whether a runtime of <paramref name="version"/> takes a session as its program starts: .NET 11 from its first
    /// release candidate on (any label but a preview's or an alpha's), whose runtime makes those EventSources before the
    /// program runs. An unread version does not.</summary>
    public static bool TakesSessionsAsItStarts(string? version)
    {
        if (version is null)
        {
            return false;
        }
        string plain = version.Split('+')[0];
        int dash = plain.IndexOf('-', StringComparison.Ordinal);
        if (!Version.TryParse(dash < 0 ? plain : plain[..dash], out Version? number))
        {
            return false;
        }
        string label = dash < 0 ? string.Empty : plain[(dash + 1)..];
        return number.Major > 11 || number.Major == 11 && !label.StartsWith("preview", StringComparison.Ordinal) && !label.StartsWith("alpha", StringComparison.Ordinal);
    }

    /// <summary>The product version of the runtime process <paramref name="processId"/> runs on, as the CoreLib beside its
    /// CoreCLR names it; null when its runtime cannot be found (a single-file app's) or read.</summary>
    public static string? RuntimeVersion(int processId)
    {
        if (DotnetProcesses.RuntimeFolder(processId) is not { } folder)
        {
            return null;
        }
        try
        {
            return FileVersionInfo.GetVersionInfo(Path.Combine(folder, "System.Private.CoreLib.dll")).ProductVersion;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>What a reading says while it waits out a program's start (<paramref name="reading"/> "reading its metrics").</summary>
    public static string Waiting(string name, string reading) => $"Waiting for {name} to finish starting: {reading} as it starts can hang it on .NET 10 or earlier…";

    /// <summary>What an answer says of a wait it made first.</summary>
    public static string Waited(string name, string reading, TimeSpan due) =>
        $"Waited {Math.Ceiling(due.TotalSeconds):0} s for {name} to finish starting: {reading} as it starts can hang it on .NET 10 or earlier";
}
