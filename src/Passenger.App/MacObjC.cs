using System.Runtime.InteropServices;

namespace Passenger.App;

/// <summary>macOS's Objective-C runtime, declared once: a class and a selector by name, and objc_msgSend in each shape a
/// message the App sends takes.</summary>
internal static partial class MacObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Class(string name);

    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Selector(string name);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Send(nint receiver, nint selector, string argument);

    /// <summary>A message answering a BOOL, its last argument where an NSError is put (left as it was if none is).</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Send(nint receiver, nint selector, nint argument, nint second, ref nint error);
}
