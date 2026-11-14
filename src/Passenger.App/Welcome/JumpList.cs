using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Passenger.Core;

namespace Passenger.App.Welcome;

/// <summary>The Recent category of Passenger's taskbar jump list on Windows, each entry a link starting Passenger.exe on
/// its path, which hands it to the Passenger running (<see cref="LaunchHandOff"/>). The list is the process's app ID's,
/// which Velopack sets to its Start menu shortcut's, so it is the pinned icon's. Windows refuses a list holding an entry
/// the person removed from it, so <see cref="Write"/> leaves those out and answers them, for the recent list to forget.</summary>
[SupportedOSPlatform("windows")]
internal static partial class JumpList
{
    private static readonly Guid DestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");

    private static readonly Guid EnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");

    private static readonly Guid ShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary>PKEY_Title, the name an entry shows.</summary>
    private static readonly PropertyKey Title = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    private const string Category = "Recent";

    private const ushort StringValue = 31;

    private const uint InProcess = 1;

    /// <summary>Room for a link's arguments, which name one path, as long as Windows' longest.</summary>
    private const int MaxArguments = 32_768;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>The objects a write made, each its own wrapper, let go of as the write ends.</summary>
    private static readonly List<ComObject> Held = [];

    /// <summary>The list made anew of <paramref name="entries"/> (path, title), the newest first, as many as the person's
    /// taskbar shows, leaving out those the person removed from it since it was last written: their links' arguments,
    /// answered. The list is <paramref name="appId"/>'s where named (a test's own), else the process's. Called on the
    /// window's thread, a single-threaded apartment like the shell's own objects.</summary>
    public static IReadOnlyList<string> Write(string exe, IReadOnlyList<(string Path, string Title)> entries, string? appId = null)
    {
        try
        {
            return Written(exe, entries, appId);
        }
        finally
        {
            LetGo();
        }
    }

    /// <summary>The list of <paramref name="appId"/> taken away whole, as a test that wrote one leaves nothing behind.</summary>
    public static void Delete(string appId)
    {
        try
        {
            Check(Create<ICustomDestinationList>(DestinationList).DeleteList(appId), "be deleted");
        }
        finally
        {
            LetGo();
        }
    }

    /// <summary>Lets go of what a call made here, in the shell objects' apartment, rather than on the finalizer's thread
    /// whenever it runs.</summary>
    private static void LetGo()
    {
        foreach (ComObject made in Held)
        {
            made.FinalRelease();
        }
        Held.Clear();
    }

    private static IReadOnlyList<string> Written(string exe, IReadOnlyList<(string Path, string Title)> entries, string? appId)
    {
        ICustomDestinationList list = Create<ICustomDestinationList>(DestinationList);
        if (appId is not null)
        {
            Check(list.SetAppID(appId), "take the app ID");
        }
        Check(list.BeginList(out uint slots, typeof(IObjectArray).GUID, out nint removedPointer), "begin the jump list");
        bool committed = false;
        try
        {
            List<string> removed = RemovedArguments(Wrap<IObjectArray>(removedPointer));
            IReadOnlyList<string> gone = RecentLists.RemovedOf(entries.Select(e => e.Path), removed);
            List<(string Path, string Title)> kept = [.. entries.Where(e => !gone.Contains(e.Path)).Take((int)Math.Max(slots, 1))];
            if (kept.Count > 0)
            {
                IObjectCollection links = Create<IObjectCollection>(EnumerableObjectCollection);
                foreach ((string path, string title) in kept)
                {
                    Check(links.AddObject(Link(exe, path, title)), "add an entry");
                }
                int appended = list.AppendCategory(Category, links);
                // E_ACCESSDENIED: the person turned off recent items in jump lists, which leaves the category out.
                if (appended != unchecked((int)0x80070005))
                {
                    Check(appended, "add the Recent category");
                }
            }
            Check(list.CommitList(), "write the jump list");
            committed = true;
            return removed;
        }
        finally
        {
            if (!committed)
            {
                list.AbortList();
            }
        }
    }

    private static IShellLinkW Link(string exe, string path, string title)
    {
        IShellLinkW link = Create<IShellLinkW>(ShellLink);
        Check(link.SetPath(exe), "name Passenger.exe");
        Check(link.SetArguments(RecentLists.ArgumentsOf(path)), "name the path");
        Check(link.SetIconLocation(exe, 0), "give the icon");
        Check(link.SetDescription(path), "give the tip");
        IPropertyStore properties = (IPropertyStore)link;
        PropertyValue value = new() { Type = StringValue, Pointer = Marshal.StringToCoTaskMemUni(title) };
        try
        {
            PropertyKey key = Title;
            Check(properties.SetValue(ref key, ref value), "give the name");
            Check(properties.Commit(), "keep the name");
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Pointer);
        }
        return link;
    }

    /// <summary>The arguments of each link the person removed from the list.</summary>
    private static unsafe List<string> RemovedArguments(IObjectArray removed)
    {
        List<string> arguments = [];
        Check(removed.GetCount(out uint count), "count the removed entries");
        char[] room = new char[MaxArguments];
        fixed (char* buffer = room)
        {
            for (uint i = 0; i < count; i++)
            {
                if (removed.GetAt(i, typeof(IShellLinkW).GUID, out nint pointer) >= 0 && Wrap<IShellLinkW>(pointer).GetArguments(buffer, MaxArguments) >= 0)
                {
                    arguments.Add(new string(buffer));
                }
            }
        }
        return arguments;
    }

    private static T Create<T>(Guid kind)
    {
        Check(CoCreateInstance(kind, 0, InProcess, typeof(T).GUID, out nint pointer), $"make {typeof(T).Name}");
        return Wrap<T>(pointer);
    }

    private static T Wrap<T>(nint pointer)
    {
        try
        {
            ComObject made = (ComObject)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            Held.Add(made);
            return (T)(object)made;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new COMException($"The jump list could not {what}", result);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid kind, nint outer, uint context, in Guid iid, out nint made);
}

/// <summary>A PROPERTYKEY: the property set's id and the property's in it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid format, uint id)
{
    public Guid Format = format;
    public uint Id = id;
}

/// <summary>A PROPVARIANT holding a pointer: its type, three reserved words, and the largest of its values' room.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyValue
{
    public ushort Type;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Pointer;
    public nint Rest;
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
internal partial interface ICustomDestinationList
{
    [PreserveSig]
    int SetAppID(string id);

    [PreserveSig]
    int BeginList(out uint slots, in Guid iid, out nint removed);

    [PreserveSig]
    int AppendCategory(string category, IObjectArray entries);

    [PreserveSig]
    int AppendKnownCategory(int category);

    [PreserveSig]
    int AddUserTasks(IObjectArray tasks);

    [PreserveSig]
    int CommitList();

    [PreserveSig]
    int GetRemovedDestinations(in Guid iid, out nint removed);

    [PreserveSig]
    int DeleteList(string? id);

    [PreserveSig]
    int AbortList();
}

[GeneratedComInterface]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal partial interface IObjectArray
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, in Guid iid, out nint item);
}

[GeneratedComInterface]
[Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
internal partial interface IObjectCollection : IObjectArray
{
    [PreserveSig]
    int AddObject(IShellLinkW item);

    [PreserveSig]
    int AddFromArray(IObjectArray items);

    [PreserveSig]
    int RemoveObjectAt(uint index);

    [PreserveSig]
    int Clear();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    [PreserveSig]
    int GetPath(char* file, int size, nint findData, uint flags);

    [PreserveSig]
    int GetIDList(out nint list);

    [PreserveSig]
    int SetIDList(nint list);

    [PreserveSig]
    int GetDescription(char* name, int size);

    [PreserveSig]
    int SetDescription(string name);

    [PreserveSig]
    int GetWorkingDirectory(char* folder, int size);

    [PreserveSig]
    int SetWorkingDirectory(string folder);

    [PreserveSig]
    int GetArguments(char* arguments, int size);

    [PreserveSig]
    int SetArguments(string arguments);

    [PreserveSig]
    int GetHotkey(out ushort key);

    [PreserveSig]
    int SetHotkey(ushort key);

    [PreserveSig]
    int GetShowCmd(out int show);

    [PreserveSig]
    int SetShowCmd(int show);

    [PreserveSig]
    int GetIconLocation(char* path, int size, out int index);

    [PreserveSig]
    int SetIconLocation(string path, int index);

    [PreserveSig]
    int SetRelativePath(string path, uint reserved);

    [PreserveSig]
    int Resolve(nint window, uint flags);

    [PreserveSig]
    int SetPath(string file);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropertyValue value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropertyValue value);

    [PreserveSig]
    int Commit();
}
