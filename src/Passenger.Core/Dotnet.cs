using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Passenger.Core;

/// <summary>Where the .NET command line is, found with the login shell's PATH (<see cref="ShellEnvironment"/>).</summary>
public static class Dotnet
{
    /// <summary>The SDK each folder's dotnet names, by the folder and the global.json over it as it stood when asked.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Sdks = new(PathNames.Comparer);

    /// <summary>The SDK a folder's dotnet runs (its global.json's, else the newest), as <c>dotnet --version</c> names it
    /// there: asked once for the folder and the global.json over it as it stands, and again after <see cref="ForgetSdks"/>;
    /// an <see cref="InvalidOperationException"/> saying why when it names none, which is asked again next time.</summary>
    public static async Task<string> SdkAsync(string folder, CancellationToken cancel = default)
    {
        string key = $"{folder}\n{GlobalJsonOver(folder)}";
        Lazy<Task<string>> once = Sdks.GetOrAdd(key, _ => new Lazy<Task<string>>(() => AskSdkAsync(folder)));
        try
        {
            return await once.Value.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception) when (once.Value.IsFaulted)
        {
            Sdks.TryRemove(KeyValuePair.Create(key, once));
            throw;
        }
    }

    /// <summary>Lets go of the SDKs named, once one is installed or removed.</summary>
    public static void ForgetSdks() => Sdks.Clear();

    private static async Task<string> AskSdkAsync(string folder)
    {
        ChildResult version = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["--version"], folder), limit: TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        string sdk = version.Output.Trim();
        return version.Succeeded && sdk.Length > 0 ? sdk : throw new InvalidOperationException($"dotnet --version failed: {version.Said}");
    }

    /// <summary>The global.json dotnet reads in a folder (the nearest over it) with its last write and length; empty for none.</summary>
    public static string GlobalJsonOver(string folder)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            FileInfo file = new(Path.Combine(at, "global.json"));
            if (file.Exists)
            {
                return $"{file.FullName}\n{file.LastWriteTimeUtc.Ticks}\n{file.Length}";
            }
        }
        return string.Empty;
    }

    /// <summary>The <c>dotnet</c> that runs this app (framework-dependent, its runtime three folders under the host), else
    /// one on DOTNET_ROOT or the PATH, else where the installers put it.</summary>
    public static string Host()
    {
        ShellEnvironment.Ready();
        string name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string runtime = RuntimeEnvironment.GetRuntimeDirectory();
        string fromRuntime = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", name));
        if (File.Exists(fromRuntime))
        {
            return fromRuntime;
        }
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, name)))
        {
            return Path.Combine(root, name);
        }
        return Programs.OnPath(name) ?? Installed(name).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No .NET command line found: install the .NET SDK, or set DOTNET_ROOT to where it is.");
    }

    /// <summary>Where Microsoft's installers, the install script and the package managers put the .NET command line.</summary>
    private static IEnumerable<string> Installed(string name)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", name);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/share/dotnet/dotnet";
            yield return "/opt/homebrew/opt/dotnet/libexec/dotnet";
        }
        else
        {
            yield return "/usr/share/dotnet/dotnet";
            yield return "/usr/lib/dotnet/dotnet";
            yield return "/usr/lib64/dotnet/dotnet";
        }
        yield return Path.Combine(home, ".dotnet", name);
    }
}
