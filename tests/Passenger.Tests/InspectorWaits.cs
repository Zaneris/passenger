using Passenger.App;
using Passenger.App.LiveTree;

namespace Passenger.Tests;

/// <summary>Waits on an app's inspector that end at once where it ended or was refused, saying why and what the Live Visual
/// Tree says, rather than waiting out their limit for an answer that can no longer come.</summary>
internal static class InspectorWaits
{
    /// <summary>The app's inspector once it has said hello, <paramref name="what"/> naming the wait.</summary>
    public static LiveApp Hello(MainWindow window, Func<LiveApp?> app, Func<string> what, TimeSpan limit)
    {
        Ui.WaitFor(() => app() is { State: not LiveAppState.Waiting }, what, limit);
        return app() is { State: LiveAppState.Connected } connected ? connected : throw Gone(window, app(), what);
    }

    /// <summary>Waits until <paramref name="shown"/> holds of the app, while its inspector has not ended or been refused.</summary>
    public static void Until(MainWindow window, Func<LiveApp?> app, Func<bool> shown, Func<string> what, TimeSpan limit)
    {
        Ui.WaitFor(() => shown() || app() is { State: LiveAppState.Ended or LiveAppState.Refused }, what, limit);
        if (!shown())
        {
            throw Gone(window, app(), what);
        }
    }

    private static Xunit.Sdk.XunitException Gone(MainWindow window, LiveApp? app, Func<string> what) =>
        new($"The app's inspector is {app?.State.ToString() ?? "not made"}{(app?.Problem is { } why ? $" ({why})" : string.Empty)} while waiting for {what()}"
            + (app is not null && window.LiveApp == app ? $"; the Live Visual Tree says \"{window.LiveTree.PlaceholderText ?? window.LiveTree.StatusLine}\"" : string.Empty)
            + $": {Ui.Said(window)}");
}
