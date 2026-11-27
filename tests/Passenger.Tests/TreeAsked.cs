using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Inspector;

namespace Passenger.Tests;

/// <summary>An app's tree as its inspector last answered, asked again each time the last answer is read, so a wait on what
/// it shows never waits inside itself; the ask under way as the wait ends is let go, its fault as the app ends heard by none.</summary>
internal sealed class TreeAsked(LiveApp app)
{
    private Task<AppTree>? asking;
    private List<Element> read = [];

    /// <summary>The elements' types as last read.</summary>
    public string Shown => string.Join(", ", read.Select(e => e.Type));

    /// <summary>Whether the last answer holds an element so; the next answer is asked for once this one is read.</summary>
    public bool Holds(Func<Element, bool> wanted)
    {
        if (asking is null)
        {
            asking = app.TreeAsync();
            // The ask left under way as the wait ends fails once the app ends, which nothing hears.
            UnawaitedWork.Dismiss(asking);
        }
        Task<AppTree> answered = asking;
        if (answered.IsCompleted)
        {
            asking = null;
            read = [];
            if (answered.IsCompletedSuccessfully)
            {
                read = [.. AppTree.Walk(answered.Result.Roots).Select(e => e.Element)];
            }
        }
        return read.Any(wanted);
    }
}
