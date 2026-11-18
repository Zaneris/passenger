namespace Passenger.Core;

/// <summary>What a .NET installation's folder holds, newest first: its SDKs (each a folder under sdk holding the CLI's
/// dotnet.dll, with the runtime it runs on beside it) and its runtimes (under shared/Microsoft.NETCore.App).</summary>
public sealed record DotnetInstallation(string Root, IReadOnlyList<string> Sdks, IReadOnlyList<string> Runtimes)
{
    private const string Runtime = "Microsoft.NETCore.App";

    /// <summary>The installation in <paramref name="root"/> as its folders stand; one it cannot read holds nothing.</summary>
    public static DotnetInstallation Read(string root)
    {
        IReadOnlyList<string> runtimes = Framework(root, Runtime);
        return new DotnetInstallation(root, Versions(Path.Combine(root, "sdk"), folder => File.Exists(Path.Combine(folder, "dotnet.dll")) && Runs(folder, runtimes)), runtimes);
    }

    /// <summary>Whether an SDK's folder has the runtime it says it runs on, at that version or a later one of its major, as
    /// the host rolls forward: without it the SDK is short of what it needs and never starts. One that says none is taken
    /// at its folder's word.</summary>
    private static bool Runs(string sdk, IReadOnlyList<string> runtimes) => RuntimeConfig.Read(Path.Combine(sdk, "dotnet.dll"))?.VersionOf(Runtime) is not { } needs
        || runtimes.Any(r => Serves(r, needs));

    /// <summary>Whether a shared framework's version installed serves a program asking for <paramref name="needs"/>, as the
    /// host rolls forward by default: the same major, at that version or later; with <paramref name="anyMajor"/> (a
    /// rollForward of Major or LatestMajor) a later major too.</summary>
    public static bool Serves(string installed, string needs, bool anyMajor = false) =>
        (anyMajor || installed.Split('.')[0] == needs.Split('.')[0]) && VersionOrder.Instance.Compare(installed, needs) >= 0;

    /// <summary>The versions of a shared framework (Microsoft.WindowsDesktop.App) an installation's folder holds, newest first.</summary>
    public static IReadOnlyList<string> Framework(string root, string name) => Versions(Path.Combine(root, "shared", name), Directory.Exists);

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

/// <summary>This machine's .NET as Passenger finds it (<see cref="Dotnet.Find()"/>): none at all, or the dotnet found, how,
/// and what its installation holds; for a process the system translates, the system's own SDK it cannot load. Read off the
/// UI thread, as it looks at the disk.</summary>
public sealed record DotnetMachine(DotnetFound? Found, DotnetInstallation? Installation)
{
    /// <summary>The machine's .NET, or the installation a folder's projects take (<see cref="Dotnet.Find(string?)"/>).</summary>
    public static DotnetMachine Read(string? folder = null) =>
        Of(Dotnet.Find(folder), Dotnet.Translated, Dotnet.Every, DotnetInstallation.Read);

    /// <summary>The machine with <paramref name="found"/>, its installation and, for a process the system translates
    /// (<paramref name="translated"/>), <see cref="SystemsOwn"/> sought among <paramref name="every"/> dotnet there is.</summary>
    internal static DotnetMachine Of(DotnetFound? found, bool translated, Func<IEnumerable<DotnetFound>> every, Func<string, DotnetInstallation> read)
    {
        DotnetInstallation? installation = found is null ? null : read(found.Root);
        return new DotnetMachine(found, installation)
        {
            Translated = translated,
            SystemsOwn = translated && found is not null ? SystemsOwnOf(found, installation!, every, read) : null,
        };
    }

    /// <summary>Whether it was read by an x64 process the system translates (<see cref="Dotnet.Translated"/>), which loads
    /// MSBuild only from an x64 SDK.</summary>
    public bool Translated { get; init; }

    /// <summary>For a process the system translates, which loads MSBuild only from an x64 SDK, where the dotnet found holds
    /// none: the installation of the system's own processor holding the SDK there is instead, the one found where it is
    /// that, else the first other holding one; null where the x64 one has an SDK, or no installation has.</summary>
    public DotnetInstallation? SystemsOwn { get; init; }

    private static DotnetInstallation? SystemsOwnOf(DotnetFound found, DotnetInstallation installation, Func<IEnumerable<DotnetFound>> every, Func<string, DotnetInstallation> read)
    {
        bool x64 = Dotnet.IsX64(found.Host);
        if (installation.NewestSdk is not null)
        {
            return x64 ? null : installation;
        }
        foreach (DotnetFound other in every())
        {
            if (!PathNames.Same(other.Root, found.Root) && !Dotnet.IsX64(other.Host) && read(other.Root) is { NewestSdk: not null } own)
            {
                return own;
            }
        }
        return null;
    }

    /// <summary>The newest SDK the dotnet found has, or null with none (no dotnet, or a runtime alone).</summary>
    public string? NewestSdk => Installation?.NewestSdk;
}
