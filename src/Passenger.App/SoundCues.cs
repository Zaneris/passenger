using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.App;

/// <summary>What a sound cue tells: what the caret came to, or what ended.</summary>
public enum SoundCue
{
    ErrorLine,
    WarningLine,
    BreakpointLine,
    FoldedLine,
    ChangedLine,
    BuildPassed,
    BuildFailed,
    TestsPassed,
    TestsFailed,
    DebuggerStopped,
}

/// <summary>The sound cues: short tones Passenger makes itself, each a few notes of its own, written once as a WAV into a
/// folder of the state's cache and played through the window's one player (<see cref="MainWindow.PlaySoundAsync"/>);
/// whether one plays is Settings' (<see cref="AppSettings.SoundCues"/> and each cue's own), read as it would play.</summary>
public static class SoundCues
{
    /// <summary>The rate the tones are made at.</summary>
    private const int Rate = 44_100;

    /// <summary>How loud a tone is at its start, of the most a WAV holds: a cue under what the person listens to.</summary>
    private const float Loudness = 0.45f;

    /// <summary>Which version of the tones a file holds, so a cue's file made by an earlier Passenger is made again.</summary>
    private const int Made = 1;

    /// <summary>Whether cues play now: always, never, or while a screen reader runs.</summary>
    public static bool Play(AppSettings settings, bool screenReaderRuns) => settings.SoundCues switch
    {
        SoundCuesPlayed.Always => true,
        SoundCuesPlayed.WithScreenReader => screenReaderRuns,
        _ => false,
    };

    /// <summary>Whether Settings turns a cue on.</summary>
    public static bool IsOn(AppSettings settings, SoundCue cue) => cue switch
    {
        SoundCue.ErrorLine => settings.SoundOnErrorLine,
        SoundCue.WarningLine => settings.SoundOnWarningLine,
        SoundCue.BreakpointLine => settings.SoundOnBreakpointLine,
        SoundCue.FoldedLine => settings.SoundOnFoldedLine,
        SoundCue.ChangedLine => settings.SoundOnChangedLine,
        SoundCue.BuildPassed or SoundCue.BuildFailed => settings.SoundOnBuildEnd,
        SoundCue.TestsPassed or SoundCue.TestsFailed => settings.SoundOnTestsEnd,
        _ => settings.SoundOnDebuggerStop,
    };

    /// <summary>A cue's notes in turn, each its pitch in hertz and how long it sounds: the lines' cues one or two short notes,
    /// a pass rising, a failure falling.</summary>
    private static (double Hertz, double Seconds)[] Notes(SoundCue cue) => cue switch
    {
        SoundCue.ErrorLine => [(440, 0.07), (330, 0.09)],
        SoundCue.WarningLine => [(523, 0.09)],
        SoundCue.BreakpointLine => [(880, 0.05)],
        SoundCue.FoldedLine => [(587, 0.04), (740, 0.05)],
        SoundCue.ChangedLine => [(660, 0.04)],
        SoundCue.BuildPassed => [(523, 0.07), (659, 0.07), (784, 0.11)],
        SoundCue.BuildFailed => [(392, 0.09), (311, 0.09), (262, 0.14)],
        SoundCue.TestsPassed => [(659, 0.07), (784, 0.07), (1047, 0.11)],
        SoundCue.TestsFailed => [(523, 0.09), (415, 0.09), (349, 0.14)],
        _ => [(740, 0.06), (0, 0.04), (740, 0.06)],
    };

    /// <summary>A cue's WAV in <paramref name="folder"/>, made there the first time it is asked. A worker's.</summary>
    public static string FileOf(SoundCue cue, string folder)
    {
        string wav = Path.Combine(folder, $"{cue}-{Made}.wav");
        if (!File.Exists(wav))
        {
            Directory.CreateDirectory(folder);
            AtomicFile.Write(wav, stream => Write(stream, cue), null, AtomicFile.Temporary, AtomicFile.Move);
        }
        return wav;
    }

    /// <summary>A cue's notes as a WAV: each a sine with its octave above, rising in 5 ms and dying away, a pitch of 0 a rest.</summary>
    private static void Write(Stream wav, SoundCue cue)
    {
        List<float> samples = [];
        foreach ((double hertz, double seconds) in Notes(cue))
        {
            int count = (int)(seconds * Rate);
            for (int i = 0; i < count; i++)
            {
                double time = (double)i / Rate;
                double envelope = Math.Min(1, time / 0.005) * Math.Exp(-4 * time / seconds);
                double tone = hertz == 0 ? 0 : (0.8 * Math.Sin(2 * Math.PI * hertz * time)) + (0.2 * Math.Sin(4 * Math.PI * hertz * time));
                samples.Add((float)(Loudness * envelope * tone));
            }
        }
        int read = 0;
        Sounds.Write(wav, 1, Rate, buffer =>
        {
            int taken = Math.Min(buffer.Length, samples.Count - read);
            samples.CopyTo(read, buffer, 0, taken);
            read += taken;
            return taken;
        });
    }
}
