namespace Passenger.Core;

/// <summary>What a .NET installation's folder holds, newest first: its SDKs (each a folder under sdk holding the CLI's
/// dotnet.dll, with the runtime it runs on beside it) and its runtimes (under shared/Microsoft.NETCore.App).</summary>
public sealed record DotnetInstallation(string Root, IReadOnlyList<string> Sdks, IReadOnlyList<string> Runtimes)
{
    private const string Runtime = "Microsoft.NETCore.App";

    /// <summary>The installation in <paramref name="root"/> as its folders stand; one it cannot read holds nothing.</summary>
    public static DotnetInstallation Read(string root)
    {
        IReadOnlyList<string> runtimes = Versions(Path.Combine(root, "shared", Runtime), Directory.Exists);
        return new DotnetInstallation(root, Versions(Path.Combine(root, "sdk"), folder => File.Exists(Path.Combine(folder, "dotnet.dll")) && Runs(folder, runtimes)), runtimes);
    }

    /// <summary>Whether an SDK's folder has the runtime it says it runs on, at that version or a later one of its major, as
    /// the host rolls forward: without it the SDK is short of what it needs and never starts. One that says none is taken
    /// at its folder's word.</summary>
    private static bool Runs(string sdk, IReadOnlyList<string> runtimes) => RuntimeConfig.Read(Path.Combine(sdk, "dotnet.dll"))?.VersionOf(Runtime) is not { } needs
        || runtimes.Any(r => r.Split('.')[0] == needs.Split('.')[0] && VersionOrder.Instance.Compare(r, needs) >= 0);

    /// <summary>The newest SDK, or null for a runtime alone.</summary>
    public string? NewestSdk => Sdks.Count > 0 ? Sdks[0] : null;

    private static IReadOnlyList<string> Versions(string folder, Func<string, bool> holds)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(folder).Where(holds).Select(Path.GetFileName).OfType<string>()
                .Where(v => v.Length > 0 && char.IsAsciiDigit(v[0])).OrderDescending(VersionOrder.Instance)];
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>This machine's .NET as Passenger finds it (<see cref="Dotnet.Find"/>): none at all, or the dotnet found, how,
/// and what its installation holds. Read off the UI thread, as it looks at the disk.</summary>
public sealed record DotnetMachine(DotnetFound? Found, DotnetInstallation? Installation)
{
    public static DotnetMachine Read()
    {
        DotnetFound? found = Dotnet.Find();
        return new DotnetMachine(found, found is null ? null : DotnetInstallation.Read(found.Root));
    }

    /// <summary>The newest SDK the dotnet found has, or null with none (no dotnet, or a runtime alone).</summary>
    public string? NewestSdk => Installation?.NewestSdk;
}
