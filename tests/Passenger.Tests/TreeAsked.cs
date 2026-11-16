using Passenger.App.LiveTree;
using Passenger.Inspector;

namespace Passenger.Tests;

/// <summary>An app's tree as its inspector last answered, asked again each time the last answer is read, so a wait on what
/// it shows never waits inside itself.</summary>
internal sealed class TreeAsked(LiveApp app)
{
    private Task<AppTree>? asking;
    private List<Element> read = [];

    /// <summary>The elements' types as last read.</summary>
    public string Shown => string.Join(", ", read.Select(e => e.Type));

    /// <summary>Whether the last answer holds an element so; the next answer is asked for once this one is read.</summary>
    public bool Holds(Func<Element, bool> wanted)
    {
        asking ??= app.TreeAsync();
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
