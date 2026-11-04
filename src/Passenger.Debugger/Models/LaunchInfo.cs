namespace Passenger.Debugger.Models;

public class LaunchInfo
{
    public required LaunchRequestConsoleType LaunchRequestConsoleType { get; set; }
    public required string? Cwd { get; set; }
    public required string Program { get; set; }
    public required bool StopAtEntry { get; set; }
    public required List<string> Arguments { get; set; }
    public required Dictionary<string, string> Env { get; set; }

    /// <summary>Whether the program's code may be changed as it runs (Hot Reload): its modules get Edit and Continue as they load.</summary>
    public bool HotReload { get; set; }

    /// <summary>Whether no one is to see the program's windows: on Windows it runs on a desktop of its own.</summary>
    public bool Unseen { get; set; }
}
