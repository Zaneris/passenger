using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>A .NET program a debugger can attach to: its process, its executable's name, its command line, and the user it
/// runs as, <see cref="IsMine"/> when that is the person running Passenger.</summary>
public sealed partial record DotnetProcess(int Id, string Name, string CommandLine, string User, bool IsMine)
{
    /// <summary>The container it runs in, by name, or null for a program of this machine. Its <see cref="Id"/> is then the
    /// container's own, and 0 until Passenger's tools in the container have said which it is.</summary>
    public string? Container { get; init; }

    /// <summary>Started held with DOTNET_MODIFIABLE_ASSEMBLIES=debug, so a debugger attached to it may change its code
    /// (Hot Reload).</summary>
    public bool HotReload { get; init; }

    /// <summary>What a person should know before attaching (a Release build's code, a container's processor emulated), or null.</summary>
    public string? Note { get; init; }

    /// <summary>Why no debugger can attach to it (its runtime's diagnostics turned off), or null when one can.</summary>
    public string? WhyNot { get; init; }

    /// <summary>What the program is to a person: the .dll the dotnet host runs ("Shop.Api" for "dotnet Shop.Api.dll"),
    /// else the executable's name without its extension.</summary>
    public string Program => Hosted().Match(CommandLine) is { Success: true } dll && Executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? dll.Groups["name"].Value
        : Executable;

    /// <summary>The name without Windows' .exe; any other dot is the name's own ("Passenger.Tests").</summary>
    private string Executable => Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Name[..^4] : Name;

    /// <summary>Whether a search typed into Attach to Process finds it: in its name, its process id, its user, its command
    /// line or its container's name.</summary>
    public bool Matches(string search) => search.Length == 0
        || Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Container?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
        || Id.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.Ordinal)
        || User.Contains(search, StringComparison.OrdinalIgnoreCase)
        || CommandLine.Contains(search, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("""(?<name>[^\\/"\s]+)\.dll(?=["\s]|$)""", RegexOptions.IgnoreCase)]
    private static partial Regex Hosted();
}

/// <summary>The .NET programs on this machine a debugger can attach to: CoreCLR's alone, found by what their runtime
/// opens for a debugger (.NET Framework and Mono open neither). On macOS and Linux that is its debugger transport's pipes
/// in the user's temporary folder, named for the process and the time it started, so a crashed program's are told from a
/// process given its number since; on Windows its diagnostic port's named pipe. Passenger's own process is left out,
/// since no process debugs itself.</summary>
public static partial class DotnetProcesses
{
    /// <summary>The runtime's debugger transport (dbgshim's handshake and ICorDebug's channel) on macOS and Linux.</summary>
    [GeneratedRegex(@"^clr-debug-pipe-(?<id>\d+)-(?<started>\d+)-in$")]
    private static partial Regex Transport();

    /// <summary>A diagnostic port's pipe on Windows.</summary>
    [GeneratedRegex(@"^dotnet-diagnostic-(?<id>\d+)$")]
    private static partial Regex DiagnosticPort();

    /// <summary>Every .NET program a debugger can attach to now, by name then process id. Slow enough to be read off the UI
    /// thread: each process's command line and user is a call into the system.</summary>
    public static IReadOnlyList<DotnetProcess> List()
    {
        List<DotnetProcess> found = [];
        foreach ((int id, long? started) in Candidates())
        {
            if (id != Environment.ProcessId && Read(id, started) is { } process)
            {
                found.Add(process);
            }
        }
        return [.. found.DistinctBy(p => p.Id).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)];
    }

    /// <summary>The program with this process id, when it is one a debugger can attach to.</summary>
    public static DotnetProcess? Find(int id) => List().FirstOrDefault(p => p.Id == id);

    /// <summary>A process as the system describes it (name, command line, user), whether or not a debugger can attach to it;
    /// null when it has gone or cannot be read.</summary>
    public static DotnetProcess? Describe(int id) => Read(id, null);

    /// <summary>Whether a process with this id runs, any program's: one this user may not open (another user's, an elevated
    /// one) runs too.</summary>
    public static bool Runs(int id)
    {
        if (OperatingSystem.IsWindows())
        {
            // .NET's HasExited takes a process Windows will not open for one that has ended.
            using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)id);
            return process.IsInvalid ? Marshal.GetLastPInvokeError() == AccessDenied : WaitForSingleObject(process, 0) == WaitTimeout;
        }
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The folder of the CoreCLR a process runs on (its libcoreclr or coreclr.dll, where the runtime's own tools lie
    /// beside it), read from the modules it has mapped; null when it has gone, is not readable, or maps none (a single-file
    /// app's runtime is inside its executable).</summary>
    public static string? RuntimeFolder(int id)
    {
        try
        {
            string? runtime = OperatingSystem.IsWindows() ? RuntimeWindows(id)
                : OperatingSystem.IsMacOS() ? RuntimeMac(id)
                : RuntimeLinux(id);
            return Path.GetDirectoryName(runtime);
        }
        catch (Exception gone) when (gone is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string? RuntimeLinux(int id)
    {
        foreach (string line in File.ReadLines($"/proc/{id}/maps"))
        {
            // A mapped file's path is the line's last field and its only slashes.
            if (line.EndsWith("/libcoreclr.so", StringComparison.Ordinal))
            {
                return line[line.IndexOf('/', StringComparison.Ordinal)..];
            }
        }
        return null;
    }

    private static string? RuntimeWindows(int id)
    {
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
        foreach (System.Diagnostics.ProcessModule module in process.Modules)
        {
            if (module.FileName.EndsWith(@"\coreclr.dll", StringComparison.OrdinalIgnoreCase))
            {
                return module.FileName;
            }
        }
        return null;
    }

    /// <summary>Writes a minidump of a Windows process's heap to <paramref name="path"/> as the runtime's createdump.exe
    /// writes one (with its private memory, where the GC heap is), through the system's dbghelp, which holds the
    /// process's threads while it writes: createdump.exe dumps only the process that started it. Throws a
    /// <see cref="System.ComponentModel.Win32Exception"/> saying why when it cannot.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void WriteHeapDump(int id, string path)
    {
        const uint ProcessQueryInformation = 0x0400;
        const uint ProcessVmRead = 0x0010;
        // MiniDumpWithPrivateReadWriteMemory, DataSegs, HandleData, UnloadedModules, FullMemoryInfo, ThreadInfo, TokenInformation.
        const uint WithHeap = 0x200 | 0x1 | 0x4 | 0x20 | 0x800 | 0x1000 | 0x40000;
        using SafeProcessHandle process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)id);
        if (process.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        using SafeFileHandle file = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        if (!MiniDumpWriteDump(process, (uint)id, file, WithHeap, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    // macOS: proc_pidinfo's regions with their files (struct proc_regionwithpathinfo), each given for a process of the
    // same user: the region at or after an address, its start and size, then the file's vnode and path.
    private const int ProcPidRegionPathInfo = 8;
    private const int RegionPathInfoSize = 1272;
    private const int RegionAddressOffset = 80;
    private const int RegionSizeOffset = 88;
    private const int RegionPathOffset = 248;

    private static unsafe string? RuntimeMac(int id)
    {
        byte* info = stackalloc byte[RegionPathInfoSize];
        ulong at = 0;
        // Bounded by the regions a process can have, should a region ever be given back for an address past its end.
        for (int regions = 0; regions < 1_000_000; regions++)
        {
            if (ProcPidInfo(id, ProcPidRegionPathInfo, at, info, RegionPathInfoSize) < RegionPathInfoSize)
            {
                return null;
            }
            ReadOnlySpan<byte> path = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(info + RegionPathOffset);
            if (path.EndsWith("/libcoreclr.dylib"u8))
            {
                return Encoding.UTF8.GetString(path);
            }
            ulong next = *(ulong*)(info + RegionAddressOffset) + *(ulong*)(info + RegionSizeOffset);
            if (next <= at)
            {
                return null;
            }
            at = next;
        }
        return null;
    }

    /// <summary>The .NET programs a debugger can attach to that a process started, or that those started in turn (the app
    /// dotnet watch runs, under the CLI's own processes), the nearest first. Read off the UI thread, as the list is.</summary>
    public static IReadOnlyList<DotnetProcess> Under(int ancestor)
    {
        List<(DotnetProcess Process, int Depth)> found = [];
        foreach ((int id, long? started) in Candidates())
        {
            if (id != ancestor && id != Environment.ProcessId && Depth(id, ancestor) is int depth && Read(id, started) is { } process)
            {
                found.Add((process, depth));
            }
        }
        return [.. found.DistinctBy(f => f.Process.Id).OrderBy(f => f.Depth).Select(f => f.Process)];
    }

    /// <summary>How many parents up from a process its ancestor is, or null when it is none of its nearest few.</summary>
    private static int? Depth(int id, int ancestor)
    {
        int at = id;
        for (int depth = 1; depth <= 8; depth++)
        {
            if (ParentOf(at) is not int parent || parent <= 0 || parent == at)
            {
                return null;
            }
            if (parent == ancestor)
            {
                return depth;
            }
            at = parent;
        }
        return null;
    }

    /// <summary>The process that started a process, or null when it has gone or the system will not say.</summary>
    private static int? ParentOf(int id)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ParentWindows(id);
            }
            if (OperatingSystem.IsMacOS())
            {
                return ParentMac(id);
            }
            string stat = File.ReadAllText($"/proc/{id}/stat");
            // The fields after the name, which may hold spaces and parentheses itself: the parent is the 4th field.
            return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], CultureInfo.InvariantCulture);
        }
        catch (Exception gone) when (gone is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException
            or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Each process the runtime's pipes name, with the start its transport's pipes were made for (none on Windows,
    /// whose pipes go with their process).</summary>
    private static IEnumerable<(int Id, long? Started)> Candidates()
    {
        string folder = OperatingSystem.IsWindows() ? @"\\.\pipe\" : Path.GetTempPath();
        Regex named = OperatingSystem.IsWindows() ? DiagnosticPort() : Transport();
        string[] names;
        try
        {
            names = Directory.GetFiles(folder);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string name in names)
        {
            if (named.Match(Path.GetFileName(name)) is { Success: true } match && int.TryParse(match.Groups["id"].ValueSpan, CultureInfo.InvariantCulture, out int id))
            {
                yield return (id, match.Groups["started"].Success && long.TryParse(match.Groups["started"].ValueSpan, CultureInfo.InvariantCulture, out long started) ? started : null);
            }
        }
    }

    /// <summary>A process as the system describes it, or null when it has gone, or is not the one its pipes were made for.</summary>
    private static DotnetProcess? Read(int id, long? started)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows(id);
            }
            if (OperatingSystem.IsMacOS())
            {
                return ReadMac(id, started);
            }
            return ReadLinux(id, started);
        }
        catch (Exception gone) when (gone is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // macOS: proc_pidinfo's BSD side, proc_pidpath and sysctl's KERN_PROCARGS2, each given for a process of the same user.
    private const int ProcPidTbsdInfo = 3;
    private const int BsdInfoSize = 136;
    private const int BsdParentOffset = 16;
    private const int BsdUidOffset = 20;
    private const int BsdStartOffset = 120;

    private static unsafe int? ParentMac(int id)
    {
        byte* info = stackalloc byte[BsdInfoSize];
        return ProcPidInfo(id, ProcPidTbsdInfo, 0, info, BsdInfoSize) == BsdInfoSize ? (int)*(uint*)(info + BsdParentOffset) : null;
    }

    private static unsafe DotnetProcess? ReadMac(int id, long? started)
    {
        byte* info = stackalloc byte[BsdInfoSize];
        if (ProcPidInfo(id, ProcPidTbsdInfo, 0, info, BsdInfoSize) != BsdInfoSize)
        {
            return null;
        }
        long began = *(long*)(info + BsdStartOffset);
        if (started is long expected && expected != began)
        {
            return null;
        }
        uint uid = *(uint*)(info + BsdUidOffset);
        IReadOnlyList<string> arguments = MacArguments(id);
        byte* path = stackalloc byte[4096];
        int length = ProcPidPath(id, path, 4096);
        string name = length > 0 ? Path.GetFileName(Encoding.UTF8.GetString(path, length))
            : arguments.Count > 0 ? Path.GetFileName(arguments[0])
            : id.ToString(CultureInfo.InvariantCulture);
        return new DotnetProcess(id, name, Joined(arguments), UserName(uid), uid == GetEffectiveUserId());
    }

    /// <summary>A process's arguments from KERN_PROCARGS2: its count, the executable's path, padding, then each argument.</summary>
    private static unsafe IReadOnlyList<string> MacArguments(int id)
    {
        const int CtlKern = 1;
        const int KernArgMax = 8;
        const int KernProcArgs2 = 49;
        int* query = stackalloc int[3];
        query[0] = CtlKern;
        query[1] = KernArgMax;
        int most = 0;
        nuint size = sizeof(int);
        if (SysCtl(query, 2, (byte*)&most, &size, null, 0) != 0 || most <= 0)
        {
            return [];
        }
        byte[] buffer = new byte[most];
        query[1] = KernProcArgs2;
        query[2] = id;
        size = (nuint)buffer.Length;
        fixed (byte* into = buffer)
        {
            if (SysCtl(query, 3, into, &size, null, 0) != 0 || size < sizeof(int))
            {
                return [];
            }
        }
        int count = BitConverter.ToInt32(buffer, 0);
        int at = sizeof(int);
        int end = (int)size;
        // The executable's path, then the padding after it.
        while (at < end && buffer[at] != 0)
        {
            at++;
        }
        while (at < end && buffer[at] == 0)
        {
            at++;
        }
        List<string> arguments = [];
        while (arguments.Count < count && at < end)
        {
            int start = at;
            while (at < end && buffer[at] != 0)
            {
                at++;
            }
            arguments.Add(Encoding.UTF8.GetString(buffer, start, at - start));
            at++;
        }
        return arguments;
    }

    /// <summary>A user's name, or their number when the system names none.</summary>
    private static unsafe string UserName(uint uid)
    {
        // struct passwd, whose first field is the name, fits in 128 bytes on macOS and Linux alike.
        byte* entry = stackalloc byte[128];
        byte[] strings = new byte[4096];
        IntPtr result;
        fixed (byte* text = strings)
        {
            if (GetPasswordEntry(uid, entry, text, (nuint)strings.Length, &result) == 0 && result != IntPtr.Zero && Marshal.ReadIntPtr((IntPtr)entry) is { } name && name != IntPtr.Zero)
            {
                return Marshal.PtrToStringUTF8(name) ?? uid.ToString(CultureInfo.InvariantCulture);
            }
        }
        return uid.ToString(CultureInfo.InvariantCulture);
    }

    // Linux: /proc.

    private static DotnetProcess? ReadLinux(int id, long? started)
    {
        string folder = $"/proc/{id}";
        string stat = File.ReadAllText($"{folder}/stat");
        // The fields after the name, which may hold spaces and parentheses itself: the start is the 22nd field.
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        if (started is long expected && (fields.Length < 20 || fields[19] != expected.ToString(CultureInfo.InvariantCulture)))
        {
            return null;
        }
        string[] arguments = File.ReadAllText($"{folder}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        string name;
        try
        {
            name = Path.GetFileName(new FileInfo($"{folder}/exe").LinkTarget ?? File.ReadAllText($"{folder}/comm").Trim());
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            name = arguments.Length > 0 ? Path.GetFileName(arguments[0]) : File.ReadAllText($"{folder}/comm").Trim();
        }
        uint uid = 0;
        foreach (string line in File.ReadLines($"{folder}/status"))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                uid = uint.Parse(line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[2], CultureInfo.InvariantCulture);
                break;
            }
        }
        return new DotnetProcess(id, name, Joined(arguments), UserName(uid), uid == GetEffectiveUserId());
    }

    /// <summary>Arguments as one command line, as a command is shown (<see cref="ChildProcesses.Quote"/>).</summary>
    private static string Joined(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(ChildProcesses.Quote));

    // Windows: the process opened for limited queries, which a process of another user allows unless it is protected.

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x100000;
    private const uint TokenQuery = 0x0008;
    private const int AccessDenied = 5;
    private const uint WaitTimeout = 0x102;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;

    /// <summary>A process's parent from its PROCESS_BASIC_INFORMATION, six pointer-sized fields with the parent's id last.</summary>
    private static unsafe int? ParentWindows(int id)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
        if (process.IsInvalid)
        {
            return null;
        }
        nint* information = stackalloc nint[6];
        uint returned = 0;
        if (NtQueryInformationProcess(process, ProcessBasicInformation, (byte*)information, (uint)(6 * sizeof(nint)), &returned) != 0)
        {
            return null;
        }
        int parent = (int)information[5];
        // Windows keeps a gone parent's id, which a later process may have been given: one that began after this is not its parent.
        using SafeProcessHandle above = OpenProcess(ProcessQueryLimitedInformation, false, (uint)parent);
        return !above.IsInvalid && GetProcessTimes(above, out long began, out _, out _, out _) && GetProcessTimes(process, out long start, out _, out _, out _)
            && began <= start ? parent : null;
    }

    private static DotnetProcess? ReadWindows(int id)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
        if (process.IsInvalid)
        {
            // Protected, or another user's the system will not describe: listed by its id, as the debugger may still be let in.
            return new DotnetProcess(id, id.ToString(CultureInfo.InvariantCulture), string.Empty, string.Empty, false);
        }
        string name = ImagePath(process) is { } path ? Path.GetFileName(path) : $"{id}";
        (string user, bool mine) = Owner(process);
        return new DotnetProcess(id, name, WindowsCommandLine(process) ?? string.Empty, user, mine);
    }

    private static unsafe string? ImagePath(SafeProcessHandle process)
    {
        char* buffer = stackalloc char[32768];
        uint length = 32768;
        return QueryFullProcessImageName(process, 0, buffer, &length) ? new string(buffer, 0, (int)length) : null;
    }

    /// <summary>The command line as the program was given it (NtQueryInformationProcess, Windows 8.1 on): a UNICODE_STRING
    /// pointing into the buffer after it.</summary>
    private static unsafe string? WindowsCommandLine(SafeProcessHandle process)
    {
        uint needed = 0;
        _ = NtQueryInformationProcess(process, ProcessCommandLineInformation, null, 0, &needed);
        if (needed == 0)
        {
            return null;
        }
        byte[] buffer = new byte[needed];
        fixed (byte* into = buffer)
        {
            if (NtQueryInformationProcess(process, ProcessCommandLineInformation, into, needed, &needed) != 0)
            {
                return null;
            }
            ushort bytes = *(ushort*)into;
            char* text = *(char**)(into + IntPtr.Size);
            return text is null ? null : new string(text, 0, bytes / sizeof(char));
        }
    }

    private static (string User, bool Mine) Owner(SafeProcessHandle process)
    {
        if (!OperatingSystem.IsWindows() || !OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
        {
            return (string.Empty, false);
        }
        using (token)
        {
            using WindowsIdentity owner = new(token.DangerousGetHandle());
            using WindowsIdentity me = WindowsIdentity.GetCurrent();
            string name = owner.Name;
            return (name[(name.LastIndexOf('\\') + 1)..], owner.User is { } sid && sid == me.User);
        }
    }

    [LibraryImport("libc", EntryPoint = "proc_pidinfo")]
    private static unsafe partial int ProcPidInfo(int pid, int flavor, ulong argument, byte* buffer, int size);

    [LibraryImport("libc", EntryPoint = "proc_pidpath")]
    private static unsafe partial int ProcPidPath(int pid, byte* buffer, uint size);

    [LibraryImport("libc", EntryPoint = "sysctl")]
    private static unsafe partial int SysCtl(int* name, uint length, byte* old, nuint* oldLength, byte* value, nuint valueLength);

    [LibraryImport("libc", EntryPoint = "getpwuid_r")]
    private static unsafe partial int GetPasswordEntry(uint uid, byte* entry, byte* strings, nuint size, IntPtr* result);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGroupId();

    /// <summary>This process's user and group ids as "uid:gid" on macOS and Linux (a container given them reads what this
    /// user's files allow); null on Windows.</summary>
    public static string? UserAndGroup => OperatingSystem.IsWindows() ? null : $"{GetEffectiveUserId()}:{GetEffectiveGroupId()}";

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MiniDumpWriteDump(SafeProcessHandle process, uint id, SafeFileHandle file, uint type, IntPtr exception, IntPtr stream, IntPtr callback);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* name, uint* size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static unsafe partial int NtQueryInformationProcess(SafeProcessHandle process, int kind, byte* information, uint length, uint* returned);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
