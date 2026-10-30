namespace Passenger.App.Debugging;

/// <summary>The edits made to the files since a program's build read them, followed for the session that will debug it or
/// apply Hot Reload to it (<see cref="DebugController.Building"/>): its built lines, and its Hot Reload where it has one, so
/// an edit made while the program is built, brought up or attached to is one they carry and Apply Changes takes. A session
/// takes them as it opens; let go untaken, they follow no more and their Hot Reload ends. A UI-thread object.</summary>
public sealed class BuiltEdits : IDisposable
{
    private readonly Action<BuiltEdits> letGo;
    private bool over;

    internal BuiltEdits(BuiltLines lines, HotReload? reload, Action<BuiltEdits> letGo)
    {
        Lines = lines;
        Reload = reload;
        this.letGo = letGo;
    }

    public BuiltLines Lines { get; }

    public HotReload? Reload { get; }

    /// <summary>A session has taken them, and follows the edits from now: letting them go ends nothing.</summary>
    internal void Take()
    {
        if (!over)
        {
            over = true;
            letGo(this);
        }
    }

    /// <summary>No session took them: they follow no more, and their Hot Reload ends.</summary>
    public void Dispose()
    {
        if (!over)
        {
            over = true;
            letGo(this);
            Reload?.End();
        }
    }
}
