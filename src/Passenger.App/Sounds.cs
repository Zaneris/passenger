using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Passenger.Core;

namespace Passenger.App;

/// <summary>A sound file played through the system's own player, one at a time, a new one ending the last: macOS's afplay
/// (WAV, MP3, AIFF), a Linux desktop's pw-play or paplay (WAV, Ogg, FLAC, MP3), else aplay (WAV), and on Windows
/// PowerShell's sound player (WAV). An Ogg Vorbis or MP3 sound, a Godot game's other kinds, is decoded to a WAV first
/// (<see cref="Playable"/>), so each plays on every system. What the system's player cannot play is said as why.</summary>
public static class Sounds
{
    private static readonly OnePlaying<Process> Playing = new(static last =>
    {
        ChildProcesses.Kill(last);
        last.Dispose();
    });

    /// <summary>How many decoded sounds the cache keeps, the newest.</summary>
    private const int Kept = 16;

    /// <summary>A sound as the system's player is handed it: the file itself, or an Ogg Vorbis or MP3 decoded to a 16-bit
    /// WAV in <paramref name="cache"/>, named by the file's path, length and time so a sound is decoded once while it
    /// stands; why where it cannot be read. A worker's.</summary>
    public static (string? Playable, string? WhyNot) Playable(string file, string cache)
    {
        string extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not (".ogg" or ".mp3"))
        {
            return (file, null);
        }
        string kind = extension == ".ogg" ? "Ogg Vorbis" : "MP3";
        try
        {
            FileInfo source = new(file);
            string key = $"{PathNames.Canonical(file)}\n{source.Length}\n{source.LastWriteTimeUtc.Ticks}";
            string wav = Path.Combine(cache, $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24]}.wav");
            if (!File.Exists(wav))
            {
                Directory.CreateDirectory(cache);
                AtomicFile.Write(wav, stream => Decode(file, extension, stream), null, AtomicFile.Temporary, static (temporary, target) => File.Move(temporary, target, overwrite: true));
                Prune(cache);
            }
            return (wav, null);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException
            or InvalidOperationException or IndexOutOfRangeException)
        {
            return (null, $"{Path.GetFileName(file)} could not be read as {kind}: {unread.Message}");
        }
    }

    /// <summary>A sound's samples written as a 16-bit PCM WAV, its sizes written once the samples are.</summary>
    private static void Decode(string file, string extension, Stream wav)
    {
        if (extension == ".ogg")
        {
            using NVorbis.VorbisReader reader = new(file);
            Write(wav, reader.Channels, reader.SampleRate, buffer => reader.ReadSamples(buffer, 0, buffer.Length));
        }
        else
        {
            using NLayer.MpegFile mpeg = new(file);
            Write(wav, mpeg.Channels, mpeg.SampleRate, buffer => mpeg.ReadSamples(buffer, 0, buffer.Length));
        }
    }

    private static void Write(Stream wav, int channels, int rate, Func<float[], int> read)
    {
        if (channels is < 1 or > 8 || rate <= 0)
        {
            throw new InvalidDataException($"its stream says {channels} channels at {rate} Hz");
        }
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        wav.Write(header);
        float[] samples = new float[4096 * channels];
        byte[] pcm = new byte[samples.Length * 2];
        long written = 0;
        for (int count; (count = read(samples)) > 0;)
        {
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Clamp(MathF.Round(samples[i] * short.MaxValue), short.MinValue, short.MaxValue));
            }
            wav.Write(pcm, 0, count * 2);
            written += count * 2;
            if (written > uint.MaxValue - 36)
            {
                throw new InvalidDataException("it is longer than a WAV holds");
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + written));
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)written);
        wav.Position = 0;
        wav.Write(header);
    }

    /// <summary>The cache's decoded sounds but the newest <see cref="Kept"/> deleted.</summary>
    private static void Prune(string cache)
    {
        foreach (FileInfo old in new DirectoryInfo(cache).EnumerateFiles("*.wav").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Kept))
        {
            try
            {
                old.Delete();
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                // One a player still reads (Windows) goes at a later prune.
            }
        }
    }

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

    /// <summary>Plays a file, ending what played before and any sound still starting; why it cannot, where it cannot, and
    /// nothing of one a later sound overtook. On the UI thread.</summary>
    public static async Task<string?> PlayAsync(string file)
    {
        UiThread.Check();
        int ask = Playing.Ask();
        (ProcessStartInfo? start, string? whyNot) = await BackgroundWork.Run(() => Player(file));
        if (Playing.Overtaken(ask))
        {
            return null;
        }
        if (start is null)
        {
            return whyNot;
        }
        try
        {
            Playing.Started(ask, await ChildProcesses.StartAsync(start));
            return null;
        }
        catch (Exception failed) when (ChildFailures.Of(failed))
        {
            return $"The sound player could not start: {failed.Message}";
        }
    }

    /// <summary>Ends the sound playing, if one is, and any still starting. On the UI thread.</summary>
    public static void Stop()
    {
        UiThread.Check();
        Playing.Stop();
    }
}
