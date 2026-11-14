using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Passenger.Core;

namespace Passenger.App.Welcome;

/// <summary>The jump list of Passenger's taskbar button, pinned or running: its recent folders and solutions under Recent
/// Solutions and its tasks, each a shell link starting Passenger.exe with its arguments. The list belongs to the process's
/// AppUserModelID, the one the installer's shortcut carries (tools/package-win.sh), so it shows on a button pinned from
/// either. The shell's COM objects are called by their vtable slots, as ShObjIdl_core.h numbers them, on a thread of a
/// single-threaded apartment (the window's), every reference let go before it returns.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class WindowsJumpList
{
    private static readonly Guid DestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid CustomDestinationList = new("6332DEBF-87B5-4670-90C0-5E57B408A49E");
    private static readonly Guid EnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid ObjectCollection = new("5632B1A4-E38A-400A-928A-D4CD63230295");
    private static readonly Guid ObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    private static readonly Guid ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>PKEY_Title, the words a jump list shows for a link.</summary>
    private static readonly PropertyKey Title = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    /// <summary>ICustomDestinationList's SetAppID, BeginList, AppendCategory, AddUserTasks, CommitList, DeleteList and
    /// AbortList.</summary>
    private const int SetAppID = 3;
    private const int BeginList = 4;
    private const int AppendCategory = 5;
    private const int AddUserTasks = 7;
    private const int CommitList = 8;
    private const int DeleteList = 10;
    private const int AbortList = 11;

    /// <summary>E_ACCESSDENIED, AppendCategory's answer where the person turned off Windows' showing of recently opened
    /// items; the list still takes its tasks.</summary>
    private const int RecentItemsOff = unchecked((int)0x80070005);

    /// <summary>IObjectArray's GetCount and GetAt, IObjectCollection's AddObject.</summary>
    private const int GetCount = 3;
    private const int GetAt = 4;
    private const int AddObject = 5;

    /// <summary>IShellLinkW's SetDescription, GetArguments, SetArguments, SetIconLocation and SetPath.</summary>
    private const int SetDescription = 7;
    private const int GetArguments = 10;
    private const int SetArguments = 11;
    private const int SetIconLocation = 17;
    private const int SetPath = 20;

    /// <summary>IPropertyStore's SetValue and Commit.</summary>
    private const int SetValue = 6;
    private const int Commit = 7;

    private const uint InProcess = 1;

    /// <summary>VT_LPWSTR.</summary>
    private const ushort Words = 31;

    /// <summary>The longest arguments read back from a link the person took off the list.</summary>
    private const int LongestArguments = 32768;

    /// <summary>Sets the list of <paramref name="appId"/>, else the process's: as many of <paramref name="recent"/> as the
    /// taskbar shows, the newest first and titled apart, then the tasks, the recent ones left out where the person turned
    /// recent items off. Those the person took off it since it was last set (Remove from this list) are told to
    /// <paramref name="removed"/> by path and left out, as the shell refuses a list holding one. Null once the list is
    /// kept, else why the shell refused it, the list left as it was.</summary>
    public static string? Set(IEnumerable<(string Path, RecentShortcut Shortcut)> recent, IReadOnlyList<RecentShortcut> tasks, Action<IReadOnlyList<string>> removed, string? appId = null)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return null;
        }
        nint list = 0;
        nint gone = 0;
        nint category = 0;
        nint jobs = 0;
        bool begun = false;
        try
        {
            int made = Create(DestinationList, CustomDestinationList, out list);
            if (made < 0)
            {
                return Failed("the shell's destination list was not made", made);
            }
            if (appId is not null && (made = Text(list, SetAppID, appId)) < 0)
            {
                return Failed("its app ID was not taken", made);
            }
            uint slots;
            Guid arrayId = ObjectArray;
            int done = ((delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)Slot(list, BeginList))(list, &slots, &arrayId, &gone);
            if (done < 0)
            {
                return Failed("the list was not begun", done);
            }
            begun = true;
            HashSet<string> taken = RemovedArguments(gone);
            List<string> forgotten = [];
            List<(string Path, RecentShortcut Shortcut)> shown = [];
            foreach ((string path, RecentShortcut shortcut) in recent)
            {
                if (taken.Contains(WindowsCommandLine.Of(shortcut.Arguments)))
                {
                    forgotten.Add(path);
                }
                else if (shown.Count < slots)
                {
                    shown.Add((path, shortcut));
                }
                else if (taken.Count == forgotten.Count)
                {
                    break;
                }
            }
            if (forgotten.Count > 0)
            {
                removed(forgotten);
            }
            if (shown.Count > 0)
            {
                if ((done = Links(exe, RecentShortcut.ToldApart(shown, RecentRow.Home), out category)) < 0)
                {
                    return Failed("the recent ones' links were not made", done);
                }
                fixed (char* name = MainMenu.RecentSolutions)
                {
                    done = ((delegate* unmanaged[Stdcall]<nint, char*, nint, int>)Slot(list, AppendCategory))(list, name, category);
                }
                if (done < 0 && done != RecentItemsOff)
                {
                    return Failed("the recent ones were not added", done);
                }
            }
            if ((done = Links(exe, tasks, out jobs)) < 0 || (done = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(list, AddUserTasks))(list, jobs)) < 0)
            {
                return Failed("its tasks were not added", done);
            }
            if ((done = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(list, CommitList))(list)) < 0)
            {
                return Failed("it was not kept", done);
            }
            begun = false;
            return null;
        }
        finally
        {
            if (begun)
            {
                ((delegate* unmanaged[Stdcall]<nint, int>)Slot(list, AbortList))(list);
            }
            Release(jobs);
            Release(category);
            Release(gone);
            Release(list);
        }
    }

    /// <summary>Takes away the list of <paramref name="appId"/> whole; null once it is gone, else why the shell refused.</summary>
    public static string? Delete(string appId)
    {
        nint list = 0;
        try
        {
            int done = Create(DestinationList, CustomDestinationList, out list);
            if (done >= 0)
            {
                done = Text(list, DeleteList, appId);
            }
            return done < 0 ? $"The taskbar's jump list of {appId} was not deleted (0x{done:X8})" : null;
        }
        finally
        {
            Release(list);
        }
    }

    private static string Failed(string what, int result) => $"The taskbar's jump list was not set: {what} (0x{result:X8})";

    /// <summary>The arguments of each link in the list of those the person took off the jump list.</summary>
    private static HashSet<string> RemovedArguments(nint gone)
    {
        HashSet<string> taken = new(StringComparer.Ordinal);
        uint count = 0;
        if (gone == 0 || ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(gone, GetCount))(gone, &count) < 0)
        {
            return taken;
        }
        char[] words = new char[LongestArguments];
        Guid linkId = ShellLinkW;
        for (uint i = 0; i < count; i++)
        {
            nint link = 0;
            // An item of another kind (a file's shell item) is no link of Passenger's, and fails the ask.
            if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(gone, GetAt))(gone, i, &linkId, &link) < 0)
            {
                continue;
            }
            fixed (char* into = words)
            {
                if (((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(link, GetArguments))(link, into, words.Length) >= 0)
                {
                    taken.Add(new string(into));
                }
            }
            Release(link);
        }
        return taken;
    }

    /// <summary>A collection of links starting Passenger.exe, one for each shortcut, its words its title, its tip the
    /// link's description and its icon Passenger's.</summary>
    private static int Links(string exe, IReadOnlyList<RecentShortcut> shortcuts, out nint collection)
    {
        int done = Create(EnumerableObjectCollection, ObjectCollection, out collection);
        foreach (RecentShortcut shortcut in shortcuts)
        {
            if (done < 0)
            {
                break;
            }
            nint link = 0;
            nint store = 0;
            try
            {
                if ((done = Create(ShellLink, ShellLinkW, out link)) < 0
                    || (done = Text(link, SetPath, exe)) < 0
                    || (done = Text(link, SetArguments, WindowsCommandLine.Of(shortcut.Arguments))) < 0
                    || (done = Text(link, SetDescription, shortcut.Tip)) < 0)
                {
                    break;
                }
                fixed (char* icon = exe)
                {
                    done = ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(link, SetIconLocation))(link, icon, 0);
                }
                Guid storeId = PropertyStore;
                if (done < 0 || (done = Marshal.QueryInterface(link, in storeId, out store)) < 0 || (done = Titled(store, shortcut.Title)) < 0)
                {
                    break;
                }
                done = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(collection, AddObject))(collection, link);
            }
            finally
            {
                Release(store);
                Release(link);
            }
        }
        return done;
    }

    /// <summary>A link's PKEY_Title, kept.</summary>
    private static int Titled(nint store, string title)
    {
        nint words = Marshal.StringToCoTaskMemUni(title);
        try
        {
            PropertyKey key = Title;
            PropVariant value = new() { Type = Words, Value = words };
            int done = ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Slot(store, SetValue))(store, &key, &value);
            return done < 0 ? done : ((delegate* unmanaged[Stdcall]<nint, int>)Slot(store, Commit))(store);
        }
        finally
        {
            Marshal.FreeCoTaskMem(words);
        }
    }

    private static int Text(nint self, int slot, string text)
    {
        fixed (char* words = text)
        {
            return ((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(self, slot))(self, words);
        }
    }

    private static int Create(Guid type, Guid face, out nint made)
    {
        Guid kind = type;
        Guid asked = face;
        nint into;
        int done = CoCreateInstance(&kind, 0, InProcess, &asked, &into);
        made = done < 0 ? 0 : into;
        return done;
    }

    private static void* Slot(nint self, int index) => (*(void***)self)[index];

    private static void Release(nint reference)
    {
        if (reference != 0)
        {
            Marshal.Release(reference);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* type, nint outer, uint context, Guid* face, nint* made);

    private readonly record struct PropertyKey(Guid Format, uint Id);

    /// <summary>A PROPVARIANT holding a pointer: its type, three reserved words, then the union, two pointers wide on the
    /// 64-bit systems Passenger runs on.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public nint Value;
    }
}
