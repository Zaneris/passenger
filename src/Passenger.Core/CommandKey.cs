namespace Passenger.Core;

/// <summary>The modifier a person holds for a command (Save, Copy, Find Action) on the system this process runs on: Cmd on
/// Apple's (macOS, iOS, Mac Catalyst), Ctrl elsewhere. The system's, whatever platform draws the windows, a headless one
/// too: Passenger's keymaps, an agent's primary in any app and the headless platform an unseen app is given all take it.</summary>
public static class CommandKey
{
    /// <summary>Whether it is Cmd (Meta), else Ctrl.</summary>
    public static bool IsCmd { get; } = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
}
