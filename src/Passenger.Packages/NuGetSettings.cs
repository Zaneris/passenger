using System.Xml;
using NuGet.Common;
using NuGet.Configuration;

namespace Passenger.Packages;

/// <summary>NuGet's settings for a folder, read past a nuget.config too deep for them: NuGet's reader recurses as deep as a
/// file's elements go, and a thread's stack that runs out ends the process.</summary>
public static class NuGetSettings
{
    /// <summary>How deep a config's elements may go, past what any is written to and short of what NuGet's reader reaches.</summary>
    private const int DeepestNesting = 64;

    private static readonly string[] Names = ["nuget.config", "NuGet.config", "NuGet.Config"];

    /// <summary>The settings of a folder and those above it and the user's; a config nested too deeply is a
    /// <see cref="NuGetConfigurationException"/>, as one that does not read is.</summary>
    public static ISettings Load(string root)
    {
        foreach (string file in Files(root))
        {
            if (TooDeep(file))
            {
                throw new NuGetConfigurationException($"{file} is nested too deeply to read");
            }
        }
        return Settings.LoadDefaultSettings(root);
    }

    /// <summary>The configs NuGet reads for a folder, by the places it looks in.</summary>
    private static IEnumerable<string> Files(string root)
    {
        for (DirectoryInfo? folder = new(root); folder is not null; folder = folder.Parent)
        {
            foreach (string name in Names)
            {
                yield return Path.Combine(folder.FullName, name);
            }
        }
        string user = NuGetEnvironment.GetFolderPath(NuGetFolderPath.UserSettingsDirectory);
        yield return Path.Combine(user, Settings.DefaultSettingsFileName);
        string extra = Path.Combine(user, "config");
        if (Directory.Exists(extra))
        {
            foreach (string file in Directory.EnumerateFiles(extra, "*.config"))
            {
                yield return file;
            }
        }
    }

    /// <summary>Whether a file's elements go past the deepest; none that is not there or does not read, which NuGet says.</summary>
    private static bool TooDeep(string file)
    {
        if (!File.Exists(file))
        {
            return false;
        }
        try
        {
            using XmlReader reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.Depth > DeepestNesting)
                {
                    return true;
                }
            }
        }
        catch (Exception unread) when (unread is XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return false;
    }
}
