using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Velopack;

namespace Passenger.App;

/// <summary>Passenger as its Windows installer left it (tools/package-win.sh): Velopack's, per user, in
/// <c>%LocalAppData%\PassengerIDE\current</c> beside its manifest (<c>sq.version</c>), Update.exe above. Its installer,
/// updater and uninstaller run Passenger.exe with a hook's flag before anything else of it, and Passenger answers by
/// registering or removing the file types it opens and the passenger-ide command's folder on the person's own Path
/// (<see cref="PassengerCommand"/>), then exits. A copy anywhere else (a build, the tests, macOS) never
/// starts Velopack, which would only add to a log of its own in local app data at every start.</summary>
internal static partial class Installation
{
    /// <summary>The types a double-click opens in Passenger, each under a ProgID of its own: a solution as the
    /// workspace, a project as its folder (<see cref="LaunchOptions"/>).</summary>
    private static readonly (string Extension, string ProgId, string Name)[] FileTypes =
    [
        (".sln", "Passenger.sln", "Solution File"),
        (".slnx", "Passenger.slnx", "Solution File"),
        (".slnf", "Passenger.slnf", "Solution Filter File"),
        (".csproj", "Passenger.csproj", "C# Project File"),
    ];

    private const string Classes = @"Software\Classes";

    /// <summary>Whether Velopack's Update.exe runs this copy as the version an update replaces (its obsolete hook, which
    /// Passenger answers with nothing): Update.exe renames current\ the moment this process has gone, and is refused for as
    /// long as anything of it holds the folder, so it is answered before anything else of Passenger loads, out of current\
    /// at once.</summary>
    public static bool Obsolete(string[] args) => args is [ObsoleteHook, ..] or [LegacyObsoleteHook, ..];

    /// <summary>The obsolete hook's flags, Velopack's and Squirrel's before it, which VelopackApp answers alike.</summary>
    private const string ObsoleteHook = "--veloapp-obsolete";
    private const string LegacyObsoleteHook = "--squirrel-obsolete";

    /// <summary>Whether this copy is one Velopack packed, whose start Main hands to Velopack first.</summary>
    public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "sq.version"));

    /// <summary>Velopack's start with Passenger's hooks, which are Windows' alone.</summary>
    public static VelopackApp Hooked(VelopackApp app) => OperatingSystem.IsWindows() ? WithHooks(app) : app;

    /// <summary>An update runs the new version's hook too: what it opens may have changed, and current\, the path the
    /// types and the Path name, stays where it was; an install updated from a release before the command takes it then.</summary>
    [SupportedOSPlatform("windows")]
    private static VelopackApp WithHooks(VelopackApp app) => app
        .OnAfterInstallFastCallback(_ => TakeInstall())
        .OnAfterUpdateFastCallback(_ => TakeInstall())
        .OnBeforeUninstallFastCallback(_ => TakeUninstall());

    /// <summary>The file types and the command's folder, each made whatever the other came to, a failure failing the hook.</summary>
    [SupportedOSPlatform("windows")]
    private static void TakeInstall()
    {
        try
        {
            RegisterFileTypes();
        }
        finally
        {
            PassengerCommand.PutOnPath();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TakeUninstall()
    {
        try
        {
            PassengerCommand.TakeOffPath();
        }
        finally
        {
            UnregisterFileTypes();
        }
    }

    /// <summary>Each type under HKEY_CURRENT_USER, so no administrator is asked: its ProgID, Passenger among the
    /// programs that open it, and Passenger as its default only where no other program has claimed it. Windows keeps a
    /// choice the person made above either.</summary>
    [SupportedOSPlatform("windows")]
    private static void RegisterFileTypes()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Passenger.exe has no path");
        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(Classes);
        foreach ((string extension, string progId, string name) in FileTypes)
        {
            using (RegistryKey type = classes.CreateSubKey(progId))
            {
                type.SetValue(string.Empty, name);
                using RegistryKey icon = type.CreateSubKey("DefaultIcon");
                icon.SetValue(string.Empty, $"\"{exe}\",0");
                using RegistryKey command = type.CreateSubKey(@"shell\open\command");
                command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
            }
            bool claimed = Owner(extension) is { } owner && owner != progId;
            using RegistryKey ext = classes.CreateSubKey(extension);
            using (RegistryKey openWith = ext.CreateSubKey("OpenWithProgids"))
            {
                openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            if (!claimed)
            {
                ext.SetValue(string.Empty, progId);
            }
        }
        AssociationsChanged();
    }

    /// <summary>What <see cref="RegisterFileTypes"/> wrote, and a type's key when nothing else is left in it.</summary>
    [SupportedOSPlatform("windows")]
    private static void UnregisterFileTypes()
    {
        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(Classes);
        foreach ((string extension, string progId, string _) in FileTypes)
        {
            classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
            using (RegistryKey? ext = classes.OpenSubKey(extension, writable: true))
            {
                if (ext is null)
                {
                    continue;
                }
                using (RegistryKey? openWith = ext.OpenSubKey("OpenWithProgids", writable: true))
                {
                    openWith?.DeleteValue(progId, throwOnMissingValue: false);
                }
                if (ext.GetValue(string.Empty) as string == progId)
                {
                    ext.DeleteValue(string.Empty, throwOnMissingValue: false);
                }
                DeleteIfEmpty(ext, "OpenWithProgids");
            }
            DeleteIfEmpty(classes, extension);
        }
        AssociationsChanged();
    }

    /// <summary>The ProgID that opens a type: the person's own, else the machine's (Visual Studio's .sln).</summary>
    [SupportedOSPlatform("windows")]
    private static string? Owner(string extension)
    {
        foreach (RegistryKey hive in (RegistryKey[])[Registry.CurrentUser, Registry.LocalMachine])
        {
            using RegistryKey? type = hive.OpenSubKey($@"{Classes}\{extension}");
            if (type?.GetValue(string.Empty) is string { Length: > 0 } owner)
            {
                return owner;
            }
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteIfEmpty(RegistryKey parent, string name)
    {
        bool empty;
        using (RegistryKey? key = parent.OpenSubKey(name))
        {
            empty = key is { ValueCount: 0, SubKeyCount: 0 };
        }
        if (empty)
        {
            parent.DeleteSubKey(name, throwOnMissingSubKey: false);
        }
    }

    /// <summary>Tells Explorer the types changed, so their icons and Open with change without a sign-out.</summary>
    private static void AssociationsChanged() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    private const int ShcneAssocChanged = 0x08000000;

    private const uint ShcnfIdList = 0;

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
