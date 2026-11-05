using System.Globalization;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>Files a command wrote whole, byte for byte, whatever they hold (an AppHost's publishers' output: a certificate, an
/// archive, an executable), never read as text: the bytes each held and was given are kept in a folder of their own, which
/// Undo and Redo put back only while each file is as the command left it. Every method reads or writes the disk, so each
/// runs off the window's thread.</summary>
public sealed class LandedFiles(string kept)
{
    /// <summary>A file landed, with the kept files of what it held (null for one the command made) and what it was given.</summary>
    private sealed record Landed(string Path, string? Before, string After);

    private readonly List<Landed> landed = [];

    /// <summary>The files landed that Undo puts back, in order.</summary>
    public IReadOnlyList<string> Paths => [.. landed.Select(file => file.Path)];

    /// <summary>Gives each file the bytes of the one it comes from, in order, passing over one that holds them already:
    /// those in <paramref name="undoneElsewhere"/> (an open file's text, with an undo of its own) are written with nothing
    /// kept. <paramref name="wrote"/> told of each. Why the first the disk refused was, null once each is; those before it
    /// stay landed, for Undo's put to take back.</summary>
    public string? Land(IReadOnlyList<(string From, string To)> files, IReadOnlySet<string> undoneElsewhere, Action<string> wrote)
    {
        foreach ((string from, string to) in files)
        {
            try
            {
                Land(to, File.ReadAllBytes(from), !undoneElsewhere.Contains(to));
                wrote(to);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return error.Message;
            }
        }
        return null;
    }

    private void Land(string path, byte[] bytes, bool keep)
    {
        byte[]? had = File.Exists(path) ? FileText.Read(path) : null;
        if (had is not null && had.AsSpan().SequenceEqual(bytes))
        {
            return;
        }
        if (!keep)
        {
            FileText.Restore(path, bytes);
            return;
        }
        Directory.CreateDirectory(kept);
        string at = Path.Combine(kept, landed.Count.ToString(CultureInfo.InvariantCulture));
        string? before = had is null ? null : $"{at}.before";
        if (had is not null)
        {
            AtomicFile.Write(before!, had);
        }
        string after = $"{at}.after";
        AtomicFile.Write(after, bytes);
        FileText.Restore(path, bytes);
        landed.Add(new Landed(path, before, after));
    }

    /// <summary>Why they cannot be taken back (or made again, given true) now, naming the first file not as that needs it;
    /// null when each is.</summary>
    public string? Unlike(bool redo)
    {
        foreach (Landed file in landed)
        {
            string name = Path.GetFileName(file.Path);
            string? held = redo ? file.Before : file.After;
            try
            {
                if (held is null)
                {
                    if (File.Exists(file.Path))
                    {
                        return $"{name} is already there";
                    }
                }
                else if (!File.Exists(file.Path))
                {
                    return $"{name} is no longer there";
                }
                else if (!FileText.Read(file.Path).AsSpan().SequenceEqual(File.ReadAllBytes(held)))
                {
                    return $"{name} has been changed since";
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"{name} could not be read: {error.Message}";
            }
        }
        return null;
    }

    /// <summary>Puts each file as it was before the command (or after it, given true), <paramref name="wrote"/> told of each.
    /// Why the disk refused one, those put before it put back first; null once each is.</summary>
    public string? Put(bool redo, Action<string> wrote)
    {
        List<Landed> put = [];
        foreach (Landed file in redo ? landed : Enumerable.Reverse(landed))
        {
            if (Placed(file, redo) is { } refused)
            {
                List<string> left = [refused];
                foreach (Landed back in Enumerable.Reverse(put))
                {
                    if (Placed(back, !redo) is { } unput)
                    {
                        left.Add(unput);
                    }
                }
                return string.Join("; ", left);
            }
            put.Add(file);
            wrote(file.Path);
        }
        return null;
    }

    /// <summary>A file given the bytes it held before the command or after it, or deleted where it was none: why the disk
    /// refused that, null once done.</summary>
    private static string? Placed(Landed file, bool redo)
    {
        string? held = redo ? file.After : file.Before;
        try
        {
            if (held is null)
            {
                File.Delete(file.Path);
            }
            else
            {
                FileText.Restore(file.Path, File.ReadAllBytes(held));
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(file.Path)} could not be {(held is null ? "deleted" : "written")}: {error.Message}";
        }
    }
}
