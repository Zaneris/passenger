namespace Passenger.Core;

/// <summary>NuGet's scratch folder, where its package-extraction locks are: a program given a temporary folder of its own
/// (a C# language server's TMPDIR) is given this process's scratch by name, so its restores take the same locks as
/// Passenger's builds and every other program's.</summary>
public static class NuGetScratch
{
    /// <summary>The variable NuGet takes its scratch folder from.</summary>
    public const string Variable = "NUGET_SCRATCH";

    /// <summary>The scratch folder as NuGet names it where <see cref="Variable"/> is unset, in a temporary folder (what
    /// <see cref="Path.GetTempPath"/> gives): NuGetScratch there, on Linux, whose /tmp every user shares, with the user's
    /// name after it.</summary>
    public static string In(string temporary, bool linux, string user) => Path.Combine(temporary, linux ? "NuGetScratch" + user : "NuGetScratch");

    /// <summary>This process's scratch folder, NuGet's in its temporary folder: on Linux made the user's alone, as NuGet makes
    /// it, since NuGet checks no folder it is given by name; null where it is not theirs, the program then keeping its own.</summary>
    public static string? Shared()
    {
        string scratch = In(Path.GetTempPath(), OperatingSystem.IsLinux(), Environment.UserName);
        if (!OperatingSystem.IsLinux())
        {
            return scratch;
        }
        try
        {
            return UserTemporary.Own(scratch);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PassengerLog.Write($"NuGet's scratch stays a program's own: {error.Message}");
            return null;
        }
    }
}
