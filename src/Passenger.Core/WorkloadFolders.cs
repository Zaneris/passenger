namespace Passenger.Core;

/// <summary>Where a .NET installation in <see cref="Root"/> keeps the workloads of an SDK it runs: in its own folder, and,
/// where it carries the userlocal marker of that SDK's feature band (as a .NET a package manager installs for every user
/// does), first in the user's own .dotnet folder (<see cref="UserLocal"/>), where a workload install then puts them and
/// the SDK looks before its own folder. In each, the workloads' packs lie under packs, their template packs under
/// template-packs and their manifests under sdk-manifests. Framework-free, since the project host compiles it in.</summary>
public sealed record WorkloadFolders(string Root, string? UserLocal)
{
    /// <summary>The folder of the workloads' packs: reference, SDK and runtime packs, each a folder by its id and version.</summary>
    public const string Packs = "packs";

    /// <summary>The folder of the workloads' template packages, which dotnet new lists.</summary>
    public const string TemplatePacks = "template-packs";

    /// <summary>The folder of the workloads' manifests, whose targets every project's evaluation imports.</summary>
    public const string Manifests = "sdk-manifests";

    /// <summary>The variable naming the folder the SDK keeps a person's own .dotnet in, in place of the home folder.</summary>
    public const string CliHomeVariable = "DOTNET_CLI_HOME";

    /// <summary>The folders, in the order the SDK looks in them: the user's own first.</summary>
    public IReadOnlyList<string> All => UserLocal is { } own ? [own, Root] : [Root];

    /// <summary>Where a workload install puts the workloads: the user's own folder where the SDK installs there, else the
    /// installation's.</summary>
    public string Installs => UserLocal ?? Root;

    /// <summary>Each folder's own folder of one kind (<see cref="Packs"/>, <see cref="TemplatePacks"/>), in <see cref="All"/>'s order.</summary>
    public IEnumerable<string> Under(string kind) => All.Select(folder => Path.Combine(folder, kind));

    /// <summary>Where the .NET in <paramref name="root"/> keeps the workloads of the SDK <paramref name="sdk"/>, as
    /// <c>dotnet --version</c> names it (null for one unknown, which installs into the installation): the .dotnet folder
    /// under <paramref name="home"/> (<see cref="CliHome"/>) too where its marker is there, unless that folder is the
    /// installation itself.</summary>
    public static WorkloadFolders Of(string root, string? sdk, string? home)
    {
        if (home is not { Length: > 0 } || sdk is null || UserLocalMarker(root, sdk) is not { } marker || !File.Exists(marker))
        {
            return new WorkloadFolders(root, null);
        }
        string own = PathNames.Canonical(Path.Combine(home, ".dotnet"));
        return new WorkloadFolders(root, PathNames.SameFile(own, root) ? null : own);
    }

    /// <summary>Whether the .NET in <paramref name="root"/> carries the userlocal marker of any feature band, so an SDK of
    /// it may install workloads for each user; where none, no SDK need be asked. False where it cannot be read.</summary>
    public static bool MayInstallForEachUser(string root)
    {
        string bands = Path.Combine(root, "metadata", "workloads");
        try
        {
            return Directory.Exists(bands) && Directory.EnumerateDirectories(bands).Any(band => File.Exists(Path.Combine(band, Marker)));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The file whose presence in the .NET at <paramref name="root"/> makes the SDK <paramref name="sdk"/> install
    /// workloads into each user's own folder: the marker under metadata/workloads/ for its feature band, its pre-release
    /// left out (11.0.100 for 11.0.100-rc.3.26522.148, 10.0.100 for 10.0.113), which Fedora's packaged .NET and others
    /// carry. Null for a version that names no band.</summary>
    public static string? UserLocalMarker(string root, string sdk)
    {
        string[] parts = sdk.Split('-', '+')[0].Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch))
        {
            return null;
        }
        return Path.Combine(root, "metadata", "workloads", $"{major}.{minor}.{patch / 100 * 100}", Marker);
    }

    /// <summary>The folder the SDK keeps a person's own .dotnet under, as <paramref name="variable"/> reads the environment:
    /// DOTNET_CLI_HOME, else the home folder (USERPROFILE on Windows, HOME elsewhere); null where neither is set.</summary>
    public static string? CliHome(Func<string, string?> variable)
    {
        return Set(CliHomeVariable) ?? Set(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");

        string? Set(string name) => variable(name) is { Length: > 0 } value ? value : null;
    }

    /// <summary>The marker's name in its band's folder.</summary>
    private const string Marker = "userlocal";
}
