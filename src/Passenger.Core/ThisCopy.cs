using System.Runtime.InteropServices;
using System.Text;

namespace Passenger.Core;

/// <summary>What the place this copy of Passenger runs from says of it, read once as it starts (<see cref="Read()"/>): a
/// <c>data</c> folder in the copy's folder makes it portable, keeping all its state there (<see cref="Data"/>), and a
/// package manager's file beside its executable, or WinGet's own folder, leaves its updates to that manager
/// (<see cref="PackageManager"/>). The copy's folder is the one a person keeps it in: Passenger.exe's on Windows, the
/// archive's passenger/ folder on Linux (the executable in its app/), the one holding the app bundle on macOS (where
/// macOS runs a quarantined app from a copy of its own, the bundle's own place). A marker that cannot be what it says is
/// fatal, as a malformed flag is.</summary>
public static partial class ThisCopy
{
    /// <summary>The folder in the copy's folder that makes it portable.</summary>
    public const string DataName = "data";

    /// <summary>The file a package manager puts beside the executable to say it installed the copy.</summary>
    public const string PackageManagerName = "package-manager";

    /// <summary>The most a package manager's file may hold: a line naming the manager and one with its command.</summary>
    private const int MarkLimit = 4096;

    private static Found? found;

    /// <summary>The portable copy's state folder, by its canonical path; null for a copy that keeps its state where an
    /// installed one does, and before <see cref="Read()"/>.</summary>
    public static string? Data => found?.Data;

    /// <summary>The package manager that put this copy in place, or null.</summary>
    public static PackageManagerMark? PackageManager => found?.PackageManager;

    /// <summary>What the running copy's place says, as Passenger starts, before anything reads its state; a
    /// <see cref="FormatException"/> saying why where a marker cannot be what it says.</summary>
    public static void Read() => found = Of(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? WinGetPackages() : []);

    /// <summary>What the place of a copy whose executable is in <paramref name="programs"/> says, the folders WinGet installs
    /// portable packages under given; a <see cref="FormatException"/> where a marker cannot be what it says.</summary>
    public static Found Of(string programs, IReadOnlyList<string> winGetPackages)
    {
        string folder = FolderOf(programs);
        return new Found(folder, DataIn(folder), MarkIn(programs) ?? WinGetOf(programs, winGetPackages));
    }

    /// <summary>The folder a person keeps a copy in, from its executable's folder.</summary>
    public static string FolderOf(string programs)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(programs);
        if (MacBundles.Of(Path.Combine(trimmed, "Passenger")) is { } bundle)
        {
            return Path.GetDirectoryName(Untranslocated(bundle)) ?? trimmed;
        }
        // The Linux archive's passenger/ folder: the app in app/, its launcher beside.
        if (Path.GetFileName(trimmed) == "app" && Path.GetDirectoryName(trimmed) is { } above && File.Exists(Path.Combine(above, "passenger")))
        {
            return above;
        }
        return trimmed;
    }

    /// <summary>The portable state folder in a copy's folder, made canonical, or null where there is none.</summary>
    private static string? DataIn(string folder)
    {
        string data = Path.Combine(folder, DataName);
        if (!Directory.Exists(data))
        {
            return File.Exists(data) || new FileInfo(data).LinkTarget is not null
                ? throw new FormatException($"{data} is no folder, where a folder named {DataName} beside Passenger makes it portable, keeping its state there: "
                    + "remove it, or make it a folder")
                : null;
        }
        string canonical = PathNames.Canonical(data);
        try
        {
            using FileStream probe = new(Path.Combine(canonical, $".{Guid.NewGuid():N}{AtomicFile.Temporary}"), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception refused) when (refused is UnauthorizedAccessException or IOException)
        {
            throw new FormatException($"{canonical}, the folder this portable Passenger keeps its state in, cannot be written: {refused.Message}", refused);
        }
        return canonical;
    }

    /// <summary>The package manager a file beside the executable names, or null where there is none: its first line the
    /// manager, its second, where it has one, the command that updates the copy.</summary>
    private static PackageManagerMark? MarkIn(string programs)
    {
        string path = Path.Combine(programs, PackageManagerName);
        if (Directory.Exists(path))
        {
            throw new FormatException($"{path} is a folder, where a package manager's file says it installed Passenger");
        }
        if (!File.Exists(path))
        {
            return null;
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            throw new FormatException($"{path}, a package manager's file, cannot be read: {refused.Message}", refused);
        }
        return MarkOf(bytes) ?? throw new FormatException($"{path} is not a package manager's file: at most {MarkLimit} bytes of text, "
            + "a line naming the manager and, after it, a line with the command that updates Passenger");
    }

    /// <summary>A package manager's file read, or null where it is no such file: UTF-8 text of at most two lines, the
    /// manager's name first (none where the file is empty) and the command that updates the copy second.</summary>
    public static PackageManagerMark? MarkOf(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = bytes.Length <= MarkLimit ? new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes) : string.Empty;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        string[] lines = [.. text.TrimStart('\uFEFF').Split('\n').Select(line => line.Trim())];
        int last = Array.FindLastIndex(lines, line => line.Length > 0);
        if (bytes.Length > MarkLimit || last > 1 || lines.Any(line => line.Any(char.IsControl)))
        {
            return null;
        }
        string? manager = last >= 0 && lines[0].Length > 0 ? lines[0] : null;
        string? command = last == 1 ? lines[1] : null;
        return manager is null && command is not null ? null : new PackageManagerMark(manager, command);
    }

    /// <summary>WinGet's package of a copy it installed under one of its packages folders (<c>ID_SOURCE</c>), named with the
    /// command that upgrades it; null for any other copy.</summary>
    private static PackageManagerMark? WinGetOf(string programs, IReadOnlyList<string> packages)
    {
        foreach (string folder in packages)
        {
            if (PathNames.IsUnder(programs, folder))
            {
                string package = (PathNames.Relative(programs, folder) ?? string.Empty).Split('/')[0];
                int source = package.IndexOf('_', StringComparison.Ordinal);
                return source > 0 ? new PackageManagerMark("WinGet", $"winget upgrade --id {package[..source]}") : null;
            }
        }
        return null;
    }

    /// <summary>Where WinGet puts what it installs from a zip: the person's, and everyone's for a machine-wide install.</summary>
    private static string[] WinGetPackages() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages"),
    ];

    /// <summary>The place of a bundle macOS runs from a copy of its own (App Translocation, for a quarantined app opened
    /// where it was downloaded or unpacked), where its folder and what lies beside it are; the bundle's own path elsewhere,
    /// and where macOS does not say.</summary>
    private static string Untranslocated(string bundle)
    {
        if (!OperatingSystem.IsMacOS() || !bundle.Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            return bundle;
        }
        byte[] path = Encoding.UTF8.GetBytes(bundle);
        nint url = CFURLCreateFromFileSystemRepresentation(0, path, path.Length, isDirectory: true);
        if (url == 0)
        {
            return bundle;
        }
        try
        {
            nint original = SecTranslocateCreateOriginalPathForURL(url, 0);
            if (original == 0)
            {
                return bundle;
            }
            try
            {
                byte[] buffer = new byte[4096];
                int end;
                return CFURLGetFileSystemRepresentation(original, resolveAgainstBase: true, buffer, buffer.Length) && (end = Array.IndexOf(buffer, (byte)0)) > 0
                    ? Encoding.UTF8.GetString(buffer, 0, end)
                    : bundle;
            }
            finally
            {
                CFRelease(original);
            }
        }
        finally
        {
            CFRelease(url);
        }
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation)]
    private static partial nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte[] buffer, nint length, [MarshalAs(UnmanagedType.U1)] bool isDirectory);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFURLGetFileSystemRepresentation(nint url, [MarshalAs(UnmanagedType.U1)] bool resolveAgainstBase, byte[] buffer, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial nint SecTranslocateCreateOriginalPathForURL(nint translocated, nint error);

    /// <summary>What a copy's place says: the folder a person keeps it in, its portable state folder, its package manager.</summary>
    public sealed record Found(string Folder, string? Data, PackageManagerMark? PackageManager);
}

/// <summary>A package manager that put a copy of Passenger in place: its name, and the command that updates the copy,
/// each where its file gives one.</summary>
public sealed record PackageManagerMark(string? Manager, string? Command)
{
    /// <summary>The copy as a person is told of it: "a package pacman installed".</summary>
    public string Said => $"a package {Manager ?? "a package manager"} installed";

    /// <summary>What updates the copy, as a person is told: its command where the file names one.</summary>
    public string Updates => Command is { } command ? $"{command} updates it" : $"{Manager ?? "the package manager"} updates it";
}
