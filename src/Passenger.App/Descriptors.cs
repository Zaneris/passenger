namespace Passenger.App;

/// <summary>The process's open files as macOS and Linux number them.</summary>
internal static class Descriptors
{
    /// <summary>Marks a descriptor to close as the process becomes another program (<see cref="Core.Libc.CloseOnExec"/>).</summary>
    public static bool CloseOnExec(int descriptor) => Core.Libc.CloseOnExec(descriptor);

    /// <summary>Marks every descriptor but standard input, output and error to close as the process becomes another program.</summary>
    public static void CloseAllOnExec()
    {
        foreach (string open in Directory.EnumerateFileSystemEntries("/dev/fd"))
        {
            if (int.TryParse(Path.GetFileName(open), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int descriptor)
                && descriptor > 2)
            {
                CloseOnExec(descriptor);
            }
        }
    }
}
