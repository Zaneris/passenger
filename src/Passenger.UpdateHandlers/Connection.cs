using System.Runtime.InteropServices;
using System.Text;

namespace Passenger.UpdateHandlers;

/// <summary>The hook's end of Passenger's socket, made with the system's own socket calls, so the hook adds no thread to the
/// program but its own. A send to a Passenger gone fails with an error, never the program's end by SIGPIPE, whatever the
/// program set that signal to. Nothing here throws: what fails says so by its answer.</summary>
internal sealed unsafe partial class Connection : IDisposable
{
    /// <summary>AF_UNIX, on every system.</summary>
    private const int UnixFamily = 1;

    /// <summary>SOCK_STREAM, on every system.</summary>
    private const int StreamKind = 1;

    /// <summary>MSG_PEEK, on every system.</summary>
    private const int Peek = 2;

    /// <summary>EINTR on macOS and Linux: a receive a signal cut short, to be made again.</summary>
    private const int Interrupted = 4;

    /// <summary>A socket on macOS and Linux, Windows' SOCKET there.</summary>
    private readonly nint handle;

    private Connection(nint handle) => this.handle = handle;

    /// <summary>Connected to Passenger's socket at <paramref name="path"/>, which waits for nothing of Passenger's (the system
    /// queues the connection for Passenger to take); null where it cannot be (Passenger gone, the path too long).</summary>
    public static Connection? Open(string path)
    {
        byte[]? address = Address(path);
        if (address is null)
        {
            return null;
        }
        fixed (byte* named = address)
        {
            if (OperatingSystem.IsWindows())
            {
                byte* data = stackalloc byte[WindowsStartData];
                if (WindowsStart(WinsockVersion, data) != 0)
                {
                    return null;
                }
                nint made = WindowsSocket(UnixFamily, StreamKind, 0, 0, 0, NotInherited);
                if (made == InvalidSocket)
                {
                    return null;
                }
                if (WindowsConnect(made, named, address.Length) != 0)
                {
                    WindowsClose(made);
                    return null;
                }
                return new Connection(made);
            }
            int socket = UnixSocket(UnixFamily, OperatingSystem.IsLinux() ? StreamKind | LinuxCloseOnRun : StreamKind, 0);
            if (socket < 0)
            {
                return null;
            }
            // macOS takes neither SOCK_CLOEXEC nor MSG_NOSIGNAL: both are the socket's own there.
            if (OperatingSystem.IsMacOS() && (UnixControl(socket, MacCloseOnRun) != 0 || !MacNoSignal(socket))
                || UnixConnect(socket, named, (uint)address.Length) != 0)
            {
                UnixClose(socket);
                return null;
            }
            return new Connection(socket);
        }
    }

    /// <summary>A sockaddr_un naming <paramref name="path"/> in UTF-8, as .NET's own Unix endpoint names it: on macOS a length
    /// and a family byte then 104 bytes of path, elsewhere a two-byte family then 108; null for a path too long for it.</summary>
    private static byte[]? Address(string path)
    {
        int room = OperatingSystem.IsMacOS() ? 104 : 108;
        byte[] name = Encoding.UTF8.GetBytes(path);
        if (name.Length >= room)
        {
            return null;
        }
        byte[] address = new byte[2 + room];
        if (OperatingSystem.IsMacOS())
        {
            address[0] = (byte)address.Length;
            address[1] = UnixFamily;
        }
        else
        {
            address[0] = UnixFamily;
        }
        name.CopyTo(address, 2);
        return address;
    }

    /// <summary>Bytes from Passenger into <paramref name="buffer"/> from <paramref name="from"/>: with <paramref name="wait"/>,
    /// waiting for them, their count, or 0 once Passenger has gone; without, a look at what has come that waits for nothing
    /// and takes nothing, made once before the reads that wait, so every call they make is ready before the program's Main.</summary>
    public int Receive(byte[] buffer, int from, bool wait)
    {
        fixed (byte* start = buffer)
        {
            byte* into = start + from;
            int room = buffer.Length - from;
            if (OperatingSystem.IsWindows())
            {
                uint waits = 0;
                uint never = 1;
                if (!wait && WindowsControl(handle, WindowsNoWaiting, &never) != 0)
                {
                    return 0;
                }
                int read = WindowsReceive(handle, into, room, wait ? 0 : Peek);
                if (!wait)
                {
                    WindowsControl(handle, WindowsNoWaiting, &waits);
                }
                return Math.Max(read, 0);
            }
            while (true)
            {
                nint read = UnixReceive((int)handle, into, (nuint)room, wait ? 0 : Peek | UnixNoWaiting);
                if (read >= 0 || Marshal.GetLastPInvokeError() != Interrupted)
                {
                    return (int)Math.Max(read, 0);
                }
            }
        }
    }

    /// <summary>Every byte of <paramref name="bytes"/> to Passenger through the system's send, which is called even for none;
    /// false once Passenger has gone.</summary>
    public bool Send(byte[] bytes)
    {
        fixed (byte* start = bytes)
        {
            int sent = 0;
            do
            {
                nint wrote = OperatingSystem.IsWindows()
                    ? WindowsSend(handle, start + sent, bytes.Length - sent, 0)
                    : UnixSend((int)handle, start + sent, (nuint)(bytes.Length - sent), OperatingSystem.IsLinux() ? LinuxNoSignal : 0);
                if (wrote < 0 && (OperatingSystem.IsWindows() || Marshal.GetLastPInvokeError() != Interrupted))
                {
                    return false;
                }
                sent += (int)Math.Max(wrote, 0);
            }
            while (sent < bytes.Length);
            return true;
        }
    }

    /// <summary>SO_NOSIGPIPE set on a macOS socket, so a send to a peer gone fails with EPIPE; false where it cannot be.</summary>
    private static bool MacNoSignal(int socket)
    {
        int on = 1;
        return UnixSetOption(socket, MacSocketLevel, MacNoSignalPipe, &on, sizeof(int)) == 0;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsClose(handle);
        }
        else
        {
            UnixClose((int)handle);
        }
    }

    /// <summary>MSG_DONTWAIT: Linux's, else macOS's.</summary>
    private static int UnixNoWaiting => OperatingSystem.IsLinux() ? 0x40 : 0x80;

    /// <summary>SOCK_CLOEXEC, Linux's, added to a socket's type.</summary>
    private const int LinuxCloseOnRun = 0x80000;

    /// <summary>FIOCLEX, macOS's: an ioctl taking no argument, so its variadic call loses nothing on arm64.</summary>
    private const nuint MacCloseOnRun = 0x20006601;

    /// <summary>MSG_NOSIGNAL, Linux's: a send to a peer gone fails with EPIPE, raising no SIGPIPE.</summary>
    private const int LinuxNoSignal = 0x4000;

    /// <summary>SOL_SOCKET, macOS's.</summary>
    private const int MacSocketLevel = 0xffff;

    /// <summary>SO_NOSIGPIPE, macOS's.</summary>
    private const int MacNoSignalPipe = 0x1022;

    [LibraryImport("libc", EntryPoint = "socket")]
    private static partial int UnixSocket(int family, int type, int protocol);

    [LibraryImport("libc", EntryPoint = "connect")]
    private static partial int UnixConnect(int socket, byte* address, uint length);

    [LibraryImport("libc", EntryPoint = "ioctl")]
    private static partial int UnixControl(int descriptor, nuint request);

    [LibraryImport("libc", EntryPoint = "setsockopt")]
    private static partial int UnixSetOption(int socket, int level, int option, void* value, uint length);

    [LibraryImport("libc", EntryPoint = "recv", SetLastError = true)]
    private static partial nint UnixReceive(int socket, byte* buffer, nuint length, int flags);

    [LibraryImport("libc", EntryPoint = "send", SetLastError = true)]
    private static partial nint UnixSend(int socket, byte* buffer, nuint length, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int UnixClose(int descriptor);

    /// <summary>Winsock 2.2, as WSAStartup asks for it.</summary>
    private const ushort WinsockVersion = 0x0202;

    /// <summary>Room for WSADATA, 408 bytes on 64-bit Windows.</summary>
    private const int WindowsStartData = 512;

    /// <summary>WSA_FLAG_NO_HANDLE_INHERIT: the socket is kept from what the program runs.</summary>
    private const uint NotInherited = 0x80;

    /// <summary>INVALID_SOCKET.</summary>
    private const nint InvalidSocket = -1;

    /// <summary>FIONBIO: whether the socket's calls wait.</summary>
    private const int WindowsNoWaiting = unchecked((int)0x8004667E);

    [LibraryImport("ws2_32.dll", EntryPoint = "WSAStartup")]
    private static partial int WindowsStart(ushort version, byte* data);

    [LibraryImport("ws2_32.dll", EntryPoint = "WSASocketW")]
    private static partial nint WindowsSocket(int family, int type, int protocol, nint information, uint group, uint flags);

    [LibraryImport("ws2_32.dll", EntryPoint = "connect")]
    private static partial int WindowsConnect(nint socket, byte* address, int length);

    [LibraryImport("ws2_32.dll", EntryPoint = "ioctlsocket")]
    private static partial int WindowsControl(nint socket, int command, uint* argument);

    [LibraryImport("ws2_32.dll", EntryPoint = "recv")]
    private static partial int WindowsReceive(nint socket, byte* buffer, int length, int flags);

    [LibraryImport("ws2_32.dll", EntryPoint = "send")]
    private static partial int WindowsSend(nint socket, byte* buffer, int length, int flags);

    [LibraryImport("ws2_32.dll", EntryPoint = "closesocket")]
    private static partial int WindowsClose(nint socket);
}
