namespace Passenger.App.Welcome;

/// <summary>One entry of what the system lists of Passenger outside its windows (the taskbar's jump list, the Dock's
/// menu): its words, the tip the system shows beside them, and the arguments Passenger is started with when it is chosen, which a
/// running Passenger takes as a second start's.</summary>
public sealed record RecentShortcut(string Title, string Tip, IReadOnlyList<string> Arguments)
{
    /// <summary>The recent folders, solutions and projects, given newest first by path and name, each a start of Passenger
    /// naming its path alone, those <paramref name="exists"/> finds gone left out; read as taken, so a list showing a few
    /// looks on disk only for those.</summary>
    public static IEnumerable<(string Path, RecentShortcut Shortcut)> Recent(IEnumerable<(string Path, string Name)> newest, Func<string, bool> exists) =>
        newest.Where(r => exists(r.Path)).Select(r => (r.Path, new RecentShortcut(r.Name, r.Path, [r.Path])));
}
