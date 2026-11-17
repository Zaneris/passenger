using System.Runtime.InteropServices;

namespace Passenger.App;

/// <summary>macOS's Objective-C runtime, declared once: a class and a selector by name, objc_msgSend in each shape a
/// message the App sends takes, and what making a class of the App's own at run time takes.</summary>
internal static partial class MacObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Class(string name);

    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Selector(string name);

    [LibraryImport(Runtime, EntryPoint = "objc_getProtocol", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Protocol(string name);

    [LibraryImport(Runtime, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint AllocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(Runtime, EntryPoint = "objc_registerClassPair")]
    public static partial void RegisterClassPair(nint type);

    /// <summary>Adds a method to a class, its argument and return types written as the runtime encodes them.</summary>
    [LibraryImport(Runtime, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddMethod(nint type, nint selector, nint implementation, nint types);

    /// <summary>A class's method for a selector, its own or one it inherits.</summary>
    [LibraryImport(Runtime, EntryPoint = "class_getInstanceMethod")]
    public static partial nint InstanceMethod(nint type, nint selector);

    /// <summary>A method's types, as a C string the runtime keeps.</summary>
    [LibraryImport(Runtime, EntryPoint = "method_getTypeEncoding")]
    public static partial nint TypeEncoding(nint method);

    /// <summary>Gives a method another implementation, answering the one it had.</summary>
    [LibraryImport(Runtime, EntryPoint = "method_setImplementation")]
    public static partial nint SetImplementation(nint method, nint implementation);

    /// <summary>A protocol's method for a selector: its selector and its types, each zero if it declares none.</summary>
    [LibraryImport(Runtime, EntryPoint = "protocol_getMethodDescription")]
    public static partial MethodDescription ProtocolMethod(nint protocol, nint selector, [MarshalAs(UnmanagedType.U1)] bool required, [MarshalAs(UnmanagedType.U1)] bool instance);

    [LibraryImport(Runtime, EntryPoint = "object_setClass")]
    public static partial nint SetClass(nint instance, nint type);

    [LibraryImport(Runtime, EntryPoint = "object_getClassName")]
    public static partial nint ClassName(nint instance);

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint PoolPush();

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void PoolPop(nint pool);

    /// <summary>A message to the implementation a class's superclass has, as <c>super</c> sends it.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSendSuper")]
    public static partial nint SendSuper(in Super receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSendSuper")]
    public static partial nint SendSuper(in Super receiver, nint selector, nint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSendSuper")]
    public static partial nint SendSuper(in Super receiver, nint selector, int argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument, nint second, nint third);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, NSRange argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument, NSRange second);

    /// <summary>A message answering a range, which both of macOS's processors' conventions return in two registers.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial NSRange SendForRange(nint receiver, nint selector);

    /// <summary>A message answering a rectangle on Apple silicon, which returns it in four registers.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial CGRect SendForRectInRegisters(nint receiver, nint selector);

    /// <summary>A message answering a rectangle on an Intel Mac, which returns a structure that large through memory.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend_stret")]
    private static partial CGRect SendForRectInMemory(nint receiver, nint selector);

    public static CGRect SendForRect(nint receiver, nint selector) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? SendForRectInMemory(receiver, selector) : SendForRectInRegisters(receiver, selector);

    /// <summary>A range of characters as Foundation takes it, its start and length.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct NSRange(nuint Location, nuint Length);

    /// <summary>The receiver of a message to a superclass's implementation, and the superclass the implementation is looked
    /// up from.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Super(nint Receiver, nint Superclass);

    /// <summary>A protocol's method as the runtime describes it, its types a C string the runtime keeps.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct MethodDescription(nint Selector, nint Types);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument, nint second);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Send(nint receiver, nint selector, string argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, uint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, double argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, CGPoint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, CGRect argument);

    /// <summary>A message answering a BOOL.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool IsTrue(nint receiver, nint selector);

    /// <summary>A message answering a double, given one object.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial double Number(nint receiver, nint selector, nint argument);

    /// <summary>Begins a pool the objects made until its end (<see cref="EndPool"/>) are let go with.</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint BeginPool();

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void EndPool(nint pool);

    /// <summary>A point as CoreGraphics takes it, two doubles.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct CGPoint(double X, double Y);

    /// <summary>A rectangle as CoreGraphics takes it, its origin and size, four doubles.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct CGRect(double X, double Y, double Width, double Height);

    /// <summary>A message answering a BOOL, its last two arguments where an object it answers and an NSError are put (each
    /// left as it was if none is).</summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Send(nint receiver, nint selector, nint argument, ref nint answer, ref nint error);
}
