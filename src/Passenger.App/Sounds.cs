using System.Diagnostics;
using Passenger.Core;

namespace Passenger.App;

/// <summary>A sound file played through the system's own player, one at a time, a new one ending the last: macOS's afplay
/// (WAV, MP3, AIFF), a Linux desktop's pw-play or paplay (WAV, Ogg, FLAC, MP3), else aplay (WAV), and on Windows
/// PowerShell's sound player (WAV). What the system's player cannot play is said as why.</summary>
public static class Sounds
{
    private static Process? playing;

    /// <summary>The program that plays a file here and its arguments, or why none can.</summary>
    public static (ProcessStartInfo? Start, string? WhyNot) Player(string file)
    {
        string extension = Path.GetExtension(file).ToLowerInvariant();
        if (OperatingSystem.IsMacOS())
        {
            return extension is ".wav" or ".mp3" or ".aiff" or ".aif" or ".m4a"
                ? (ChildProcesses.Command("/usr/bin/afplay", [file]), null)
                : (null, $"macOS's player plays no {extension} file: open it in Godot's editor to hear it");
        }
        if (OperatingSystem.IsWindows())
        {
            if (extension != ".wav" || Programs.OnPath("powershell.exe") is not { } powershell)
            {
                return (null, $"Windows' player plays WAV files alone, not {extension}: open it in Godot's editor to hear it");
            }
            ProcessStartInfo start = ChildProcesses.Command(powershell, ["-NoProfile", "-NonInteractive", "-Command", "(New-Object System.Media.SoundPlayer $env:SOUND_TO_PLAY).PlaySync()"]);
            start.Environment["SOUND_TO_PLAY"] = file;
            return (start, null);
        }
        foreach (string program in (string[])["pw-play", "paplay"])
        {
            if (Programs.OnPath(program) is { } found)
            {
                return (ChildProcesses.Command(found, [file]), null);
            }
        }
        return extension == ".wav" && Programs.OnPath("aplay") is { } aplay ? (ChildProcesses.Command(aplay, ["-q", file]), null)
            : (null, "No sound player was found (pw-play, paplay or aplay): install PipeWire's or PulseAudio's tools to hear it");
    }

    /// <summary>Plays a file, ending what played before; why it cannot, where it cannot. On the UI thread.</summary>
    public static async Task<string?> PlayAsync(string file)
    {
        UiThread.Check();
        Stop();
        (ProcessStartInfo? start, string? whyNot) = await BackgroundWork.Run(() => Player(file));
        if (start is null)
        {
            return whyNot;
        }
        try
        {
            playing = await ChildProcesses.StartAsync(start);
            return null;
        }
        catch (Exception failed) when (failed is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"The sound player could not start: {failed.Message}";
        }
    }

    /// <summary>Ends the sound playing, if one is.</summary>
    public static void Stop()
    {
        if (Interlocked.Exchange(ref playing, null) is { } last)
        {
            ChildProcesses.Kill(last);
            last.Dispose();
        }
    }
}
