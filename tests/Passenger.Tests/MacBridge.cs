using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>Avalonia's macOS accessibility bridge brought into the headless test process, making a control's element as it
/// makes one for VoiceOver, and the Objective-C runtime the tests send NSAccessibility's selectors through, as AppKit does.</summary>
internal static partial class MacBridge
{
    /// <summary>An autorelease pool around a test's messages, which answer objects autoreleased as AppKit's own calls do.</summary>
    public sealed class Pool : IDisposable
    {
        private readonly nint pool = PoolPush();

        public void Dispose() => PoolPop(pool);
    }

    /// <summary>What was thrown under a call Avalonia's native side made into .NET (one of MicroCom's vtables), which in a
    /// person's Passenger stops its run loop and ends it as the exception reaches the call; headless, nothing hears it.</summary>
    public sealed class Faults : IDisposable
    {
        private readonly List<string> thrown = [];

        public Faults() => AppDomain.CurrentDomain.FirstChanceException += Heard;

        /// <summary>Each exception thrown under a call of the bridge's since this began, with where it was thrown.</summary>
        public string[] Thrown
        {
            get
            {
                lock (thrown)
                {
                    return [.. thrown];
                }
            }
        }

        public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= Heard;

        private void Heard(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (new StackTrace(1, fNeedFileInfo: false).GetFrames().Any(frame => frame.GetMethod()?.DeclaringType?.Namespace == "Avalonia.Native.Interop.Impl"))
            {
                lock (thrown)
                {
                    thrown.Add($"{e.Exception.GetType().Name} from {e.Exception.TargetSite?.DeclaringType?.Name}.{e.Exception.TargetSite?.Name}: {e.Exception.Message}");
                }
            }
        }
    }

    /// <summary>Whether VoiceOver hears an element open, as AXExpanded.</summary>
    public static bool Expanded(nint element) => (byte)Send(element, "isAccessibilityExpanded") != 0;

    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    private static bool loaded;

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct NSRange(nuint Location, nuint Length);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct CGPoint(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct CGRect(double X, double Y, double Width, double Height);

    /// <summary>Avalonia's element for a control's peer, made by its own +acquire: as AppKit's questions make it, through
    /// the peer's native wrapper Avalonia hands its bridge: given as a reference to the smart pointer holding it, the
    /// address of a slot holding the wrapper's pointer.</summary>
    public static nint ElementOf(Control control)
    {
        Load();
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(control);
        Assembly avalonia = Assembly.Load("Avalonia.Native");
        Type wrapper = avalonia.GetType("Avalonia.Native.AvnAutomationPeer", throwOnError: true)!;
        Type face = avalonia.GetType("Avalonia.Native.Interop.IAvnAutomationPeer", throwOnError: true)!;
        object wrapped = wrapper.GetMethod("Wrap", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [peer])!;
        Type runtime = Type.GetType("MicroCom.Runtime.MicroComRuntime, MicroCom.Runtime", throwOnError: true)!;
        MethodInfo pointer = runtime.GetMethods().Single(m => m.Name == "GetNativeIntPtr" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2).MakeGenericMethod(face);
        nint native = (nint)pointer.Invoke(null, [wrapped, false])!;
        nint held = Marshal.AllocHGlobal(IntPtr.Size);
        nint element;
        try
        {
            Marshal.WriteIntPtr(held, native);
            element = Send(Class(MacTextElements.AvaloniaClassName), Selector("acquire:"), held);
        }
        finally
        {
            Marshal.FreeHGlobal(held);
        }
        Assert.True(element != 0, $"Avalonia's {MacTextElements.AvaloniaClassName} made no element for the {control.GetType().Name}'s peer");
        return element;
    }

    /// <summary>Loads Avalonia's native bridge, as the person's Passenger does, and takes its elements as text areas.</summary>
    private static void Load()
    {
        if (!loaded)
        {
            NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native", "libAvaloniaNative.dylib"));
            loaded = true;
        }
        Assert.True(MacTextElements.Install() is null, $"The text areas could not be put in Avalonia's bridge: {MacTextElements.Install()}");
        Type property = Assembly.Load("Avalonia.Native").GetType("Avalonia.Native.Interop.AvnAutomationProperty", throwOnError: true)!;
        Assert.True(Convert.ToInt32(Enum.Parse(property, "ValueProvider_Value"), System.Globalization.CultureInfo.InvariantCulture) == MacTextElements.AvaloniaValueProperty,
            "Avalonia's bridge numbers its value property otherwise than the text areas pass it over");
    }

    public static string ClassName(nint instance) => Marshal.PtrToStringUTF8(ClassNameOf(instance)) ?? string.Empty;

    public static nint Send(nint receiver, string selector) => Send(receiver, Selector(selector));

    public static nint Send(nint receiver, string selector, nint argument) => Send(receiver, Selector(selector), argument);

    public static nint Send(nint receiver, string selector, NSRange argument) => Send(receiver, Selector(selector), argument);

    public static NSRange RangeOf(nint receiver, string selector) => SendForRange(receiver, Selector(selector));

    public static NSRange RangeFor(nint receiver, string selector, nint argument) => SendForRange(receiver, Selector(selector), argument);

    public static NSRange RangeAt(nint receiver, CGPoint point) => SendForRange(receiver, Selector("accessibilityRangeForPosition:"), point);

    public static CGRect FrameFor(nint receiver, NSRange range) => RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? SendForRectInMemory(receiver, Selector("accessibilityFrameForRange:"), range)
        : SendForRect(receiver, Selector("accessibilityFrameForRange:"), range);

    public static bool Allows(nint receiver, string selector) => (byte)Send(receiver, Selector("isAccessibilitySelectorAllowed:"), Selector(selector)) != 0;

    /// <summary>A string of Foundation's as .NET's.</summary>
    public static string? String(nint value) => value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String")));

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Class(string name);

    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(Runtime, EntryPoint = "object_getClassName")]
    private static partial nint ClassNameOf(nint instance);

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint PoolPush();

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void PoolPop(nint pool);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, NSRange argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial NSRange SendForRange(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial NSRange SendForRange(nint receiver, nint selector, nint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial NSRange SendForRange(nint receiver, nint selector, CGPoint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial CGRect SendForRect(nint receiver, nint selector, NSRange argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend_stret")]
    private static partial CGRect SendForRectInMemory(nint receiver, nint selector, NSRange argument);
}
