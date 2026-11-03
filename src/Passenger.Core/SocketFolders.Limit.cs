using System.Text;

namespace Passenger.Core;

/// <summary>How long a Unix socket's path may be: what its address holds (104 bytes on macOS, 108 elsewhere), its ending NUL
/// among them. Apart from the rest, so the inspectors, which make no socket folder, check a path the same way.</summary>
public static partial class SocketFolders
{
    /// <summary>The bytes a Unix socket's address holds for its path, its ending NUL among them.</summary>
    public static int PathLimit => OperatingSystem.IsMacOS() ? 104 : 108;

    /// <summary>Whether a path fits a Unix socket's address, as .NET's endpoint takes it (UTF-8, and its NUL).</summary>
    public static bool Fits(string path) => path.Length > 0 && Encoding.UTF8.GetByteCount(path) < PathLimit;

    /// <summary>Why a path does not fit, in words: how long it is and what fits.</summary>
    public static string TooLong(string path) =>
        $"its path {path} is {Encoding.UTF8.GetByteCount(path)} bytes, past the {PathLimit - 1} a Unix socket's address holds here";
}
