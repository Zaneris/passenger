namespace Passenger.Core;

/// <summary>A file written only so that a watcher hears it change (the watchers' beat), whose bytes nobody reads whole: written
/// in place, cut short and filled, with no temporary file, no sync to the disk and no metadata copied, all of which a file
/// whose bytes matter takes through <see cref="AtomicFile"/> and which this needs none of. Called on any thread.</summary>
public static class MarkerFiles
{
    /// <summary>Writes <paramref name="bytes"/> over the file at <paramref name="path"/>, made where it is not there.</summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
        file.Write(bytes);
    }
}
