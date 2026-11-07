namespace Passenger.App;

/// <summary>The one thing running of those asked for in turn, each taking a while to start: an ask ends what runs and
/// overtakes every start under way, and a start overtaken as it went is ended the moment it lands, so none is left running
/// out of reach. <paramref name="end"/> ends what it is given. An object of one thread.</summary>
internal sealed class OnePlaying<T>(Action<T> end)
    where T : class
{
    private T? running;
    private int asked;

    /// <summary>Whether a start has been overtaken, so what it would start is not to run.</summary>
    public bool Overtaken(int ask) => ask != asked;

    /// <summary>Ends what runs and overtakes the starts under way; what a new start goes by.</summary>
    public int Ask()
    {
        Stop();
        return asked;
    }

    /// <summary>What a start brought: what runs now, or ended where the start was overtaken.</summary>
    public void Started(int ask, T started)
    {
        if (Overtaken(ask))
        {
            end(started);
        }
        else
        {
            running = started;
        }
    }

    /// <summary>Ends what runs and overtakes the starts under way.</summary>
    public void Stop()
    {
        asked++;
        if (running is { } last)
        {
            running = null;
            end(last);
        }
    }
}
