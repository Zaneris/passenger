namespace Passenger.Core;

/// <summary>What a .NET installation's folder holds, newest first: its SDKs (each a folder under sdk holding the CLI's
/// dotnet.dll) and its runtimes (under shared/Microsoft.NETCore.App).</summary>
public sealed record DotnetInstallation(string Root, IReadOnlyList<string> Sdks, IReadOnlyList<string> Runtimes)
{
    /// <summary>The installation in <paramref name="root"/> as its folders stand; one it cannot read holds nothing.</summary>
    public static DotnetInstallation Read(string root) => new(
        root,
        Versions(Path.Combine(root, "sdk"), folder => File.Exists(Path.Combine(folder, "dotnet.dll"))),
        Versions(Path.Combine(root, "shared", "Microsoft.NETCore.App"), Directory.Exists));

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
