using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Text;

/// <summary>Why a file cannot be renamed in its folder now, found without renaming it, so a change that renames one (a
/// type's rename with its file) is refused before it changes anything: on Windows another program holding it open without
/// letting it be renamed, which a save's replace of it needs too; elsewhere a folder this process may not write in.</summary>
public static partial class Unmovable
{
    private const uint DeleteAccess = 0x00010000;
    private const uint BackupSemantics = 0x02000000;
    private const int SharingViolation = 32;
    private const int AccessDenied = 5;
    private const int WriteAndSearch = 2 | 1;
    private const int PermissionDenied = 13;
    private const int ReadOnlyVolume = 30;

    /// <summary>Why the file at a path cannot be renamed now, naming it; null when nothing stands in the way that can be
    /// told beforehand (one not there is left for the rename itself to say).</summary>
    public static string? Why(string path)
    {
        string name = Path.GetFileName(path);
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle handle = CreateFile(path, DeleteAccess, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
            return handle.IsInvalid ? Marshal.GetLastPInvokeError() switch
            {
                SharingViolation => $"{name} is open in another program, which keeps it from being renamed",
                AccessDenied => $"{name} may not be renamed here: access is denied",
                _ => null,
            } : null;
        }
        string folder = Path.GetDirectoryName(path)!;
        return Access(folder, WriteAndSearch) == 0 ? null : Marshal.GetLastPInvokeError() switch
        {
            PermissionDenied or ReadOnlyVolume => $"{name} cannot be renamed, as its folder {Path.GetFileName(folder)} may not be written in",
            _ => null,
        };
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [UnsupportedOSPlatform("windows")]
    [LibraryImport("libc", EntryPoint = "access", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);
}
