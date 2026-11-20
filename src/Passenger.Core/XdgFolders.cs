namespace Passenger.Core;

/// <summary>The XDG base folders the person's settings and data are kept in: the variable's folder where it names a full
/// path, else HOME's, each read through one reader of variables, so a program's own environment and this process's are
/// read alike.</summary>
public static class XdgFolders
{
    /// <summary>The variable naming the settings folder.</summary>
    public const string ConfigVariable = "XDG_CONFIG_HOME";

    /// <summary>The variable naming the data folder.</summary>
    public const string DataVariable = "XDG_DATA_HOME";

    /// <summary>The variable naming the cache folder.</summary>
    public const string CacheVariable = "XDG_CACHE_HOME";

    /// <summary>The settings folder: XDG_CONFIG_HOME's, else HOME's <c>.config</c>; null where neither names a full path.</summary>
    public static string? Config(Func<string, string?> variable) => Of(variable, ConfigVariable, ConfigUnderHome);

    /// <summary>The data folder: XDG_DATA_HOME's, else HOME's <c>.local/share</c>; null where neither names a full path.</summary>
    public static string? Data(Func<string, string?> variable) => Of(variable, DataVariable, DataUnderHome);

    /// <summary>Where the settings folder lies below HOME when XDG_CONFIG_HOME names none.</summary>
    public static string ConfigUnderHome => ".config";

    /// <summary>Where the data folder lies below HOME when XDG_DATA_HOME names none.</summary>
    public static string DataUnderHome => Path.Combine(".local", "share");

    /// <summary>Where the cache folder lies below HOME when XDG_CACHE_HOME names none.</summary>
    public static string CacheUnderHome => ".cache";

    /// <summary>This process's variables, HOME being the person's home as .NET finds it: HOME's own, else the account's (the
    /// profile folder on Windows, where HOME is seldom set).</summary>
    public static string? Own(string name) => name == "HOME"
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : Environment.GetEnvironmentVariable(name);

    private static string? Of(Func<string, string?> variable, string named, string underHome) =>
        variable(named) is { Length: > 0 } set && Path.IsPathRooted(set) ? set
        : variable("HOME") is { Length: > 0 } home && Path.IsPathRooted(home) ? Path.Combine(home, underHome)
        : null;
}
