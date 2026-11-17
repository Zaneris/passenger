using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Passenger.Editor.UiAutomation;

// UI Automation's provider interfaces as UIAutomationCore.h lays out their tables, each method in its place there. A
// pointer UI Automation hands over or takes back is an nint, made and unwrapped by Uia, so nothing here makes a wrapper of
// a native object; a VARIANT answered is written where UI Automation points. A name may differ from the header's, the
// table's order being all UI Automation reads.

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IRawElementProviderSimple
{
    int GetProviderOptions();

    nint GetPatternProvider(int patternId);

    unsafe void GetPropertyValue(int propertyId, ComVariant* value);

    nint GetHostRawElementProvider();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IRawElementProviderFragment
{
    nint Toward(int direction);

    nint GetRuntimeId();

    UiaRect GetBoundingRectangle();

    nint GetEmbeddedFragmentRoots();

    void SetFocus();

    nint GetFragmentRoot();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    nint ElementProviderFromPoint(double x, double y);

    nint GetFocus();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("3589c92c-63f3-4367-99bb-ada653b77cf2")]
internal partial interface ITextProvider
{
    nint GetSelection();

    nint GetVisibleRanges();

    nint RangeFromChild(nint childElement);

    nint RangeFromPoint(UiaPoint point);

    nint GetDocumentRange();

    int GetSupportedTextSelection();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("0dc5e6ed-3e16-4bf1-8f9a-a979878bc195")]
internal partial interface ITextProvider2 : ITextProvider
{
    nint RangeFromAnnotation(nint annotationElement);

    nint GetCaretRange(out int isActive);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("5347ad7b-c355-46f8-aff5-909033582f63")]
internal partial interface ITextRangeProvider
{
    nint Clone();

    int Compare(nint range);

    int CompareEndpoints(int endpoint, nint targetRange, int targetEndpoint);

    void ExpandToEnclosingUnit(int unit);

    nint FindAttribute(int attributeId, Variant value, int backward);

    nint FindText([MarshalAs(UnmanagedType.BStr)] string text, int backward, int ignoreCase);

    unsafe void GetAttributeValue(int attributeId, ComVariant* value);

    nint GetBoundingRectangles();

    nint GetEnclosingElement();

    [return: MarshalAs(UnmanagedType.BStr)]
    string GetText(int maxLength);

    int Move(int unit, int count);

    int MoveEndpointByUnit(int endpoint, int unit, int count);

    void MoveEndpointByRange(int endpoint, nint targetRange, int targetEndpoint);

    void Select();

    void AddToSelection();

    void RemoveFromSelection();

    void ScrollIntoView(int alignToTop);

    nint GetChildren();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("c7935180-6fb3-4201-b174-7df73adbf64a")]
internal partial interface IValueProvider
{
    void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string GetValue();

    int GetIsReadOnly();
}

/// <summary>A VARIANT as Windows lays it out, for one passed by value: its type, then where its value is kept, as
/// <see cref="ComVariant"/> lays it out too.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct Variant
{
    public readonly ushort Type;
    private readonly ushort reserved1;
    private readonly ushort reserved2;
    private readonly ushort reserved3;
    public readonly nint Value;
    private readonly nint record;

    /// <summary>A VARIANT's bits as this, the value still the ComVariant's to let go of.</summary>
    public static Variant Of(ComVariant value) => Unsafe.BitCast<ComVariant, Variant>(value);

    /// <summary>Whether it holds a VARIANT_BOOL, and that bool.</summary>
    public bool? Bool => Type == (ushort)VarEnum.VT_BOOL ? (short)Value != 0 : null;
}

/// <summary>UI Automation's UiaRect: a rectangle on the screen, in its pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct UiaRect(double Left, double Top, double Width, double Height);

/// <summary>UI Automation's UiaPoint: a point on the screen, in its pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct UiaPoint(double X, double Y);

/// <summary>UI Automation's numbers (UIAutomationClient.h, UIAutomationCoreApi.h) and calls (UIAutomationCore.dll), and
/// how a managed provider is handed to it as a pointer and found again from one.</summary>
internal static partial class Uia
{
    /// <summary>The object id WM_GETOBJECT asks with for a window's UI Automation provider.</summary>
    public const int RootObjectId = -25;

    public const int ServerSideProvider = 0x2;
    public const int OverrideProvider = 0x8;
    public const int ProviderOwnsSetFocus = 0x10;

    public const int ParentDirection = 0;
    public const int NextSiblingDirection = 1;
    public const int PreviousSiblingDirection = 2;

    public const int ValuePattern = 10002;
    public const int TextPattern = 10014;
    public const int TextPattern2 = 10024;

    public const int ProcessIdProperty = 30002;
    public const int ControlTypeProperty = 30003;
    public const int NameProperty = 30005;
    public const int HasKeyboardFocusProperty = 30008;
    public const int IsKeyboardFocusableProperty = 30009;
    public const int IsEnabledProperty = 30010;
    public const int AutomationIdProperty = 30011;
    public const int ClassNameProperty = 30012;
    public const int HelpTextProperty = 30013;
    public const int IsControlElementProperty = 30016;
    public const int IsContentElementProperty = 30017;
    public const int IsOffscreenProperty = 30022;
    public const int FrameworkIdProperty = 30024;
    public const int IsValuePatternAvailableProperty = 30043;
    public const int ValueValueProperty = 30045;
    public const int ValueIsReadOnlyProperty = 30046;
    public const int IsTextPatternAvailableProperty = 30040;
    public const int IsTextPattern2AvailableProperty = 30119;

    public const int DocumentControlType = 50030;

    public const int TextSelectionChangedEvent = 20014;
    public const int TextChangedEvent = 20015;

    public const int IsReadOnlyAttribute = 40015;

    public const int SupportedTextSelectionMultiple = 2;

    public const int StartEndpoint = 0;

    public const int CharacterUnit = 0;
    public const int FormatUnit = 1;
    public const int WordUnit = 2;
    public const int LineUnit = 3;
    public const int ParagraphUnit = 4;
    public const int PageUnit = 5;
    public const int DocumentUnit = 6;

    /// <summary>UIA_E_ELEMENTNOTAVAILABLE: the element's control is gone, or its window's thread no longer runs.</summary>
    public const int ElementNotAvailable = unchecked((int)0x80040201);

    /// <summary>The pointer UI Automation calls a provider by for <typeparamref name="T"/>, one reference the receiver
    /// owns; zero for none.</summary>
    public static unsafe nint Of<T>(T? provider) where T : class => (nint)ComInterfaceMarshaller<T>.ConvertToUnmanaged(provider);

    /// <summary>The managed provider a pointer UI Automation hands back stands for, null where it is none of
    /// Passenger's.</summary>
    public static T? From<T>(nint pointer) where T : class => pointer != 0 && ComWrappers.TryGetObject(pointer, out object? found) ? found as T : null;

    /// <summary>A SAFEARRAY of doubles, which the receiver owns.</summary>
    public static unsafe nint Doubles(ReadOnlySpan<double> values)
    {
        nint array = SafeArrayCreateVector((ushort)VarEnum.VT_R8, 0, (uint)values.Length);
        if (array == 0 || values.IsEmpty)
        {
            return array;
        }
        Marshal.ThrowExceptionForHR(SafeArrayAccessData(array, out void* data));
        values.CopyTo(new Span<double>(data, values.Length));
        Marshal.ThrowExceptionForHR(SafeArrayUnaccessData(array));
        return array;
    }

    /// <summary>A SAFEARRAY of IUnknowns, one for each provider, which the receiver owns with their references.</summary>
    public static unsafe nint Unknowns<T>(IReadOnlyList<T> providers) where T : class
    {
        nint array = SafeArrayCreateVector((ushort)VarEnum.VT_UNKNOWN, 0, (uint)providers.Count);
        if (array == 0 || providers.Count == 0)
        {
            return array;
        }
        Marshal.ThrowExceptionForHR(SafeArrayAccessData(array, out void* data));
        for (int i = 0; i < providers.Count; i++)
        {
            ((nint*)data)[i] = Of(providers[i]);
        }
        Marshal.ThrowExceptionForHR(SafeArrayUnaccessData(array));
        return array;
    }

    /// <summary>The value UI Automation reads as a property or text attribute this provider does not give.</summary>
    [SupportedOSPlatform("windows")]
    public static ComVariant NotSupported()
    {
        Marshal.ThrowExceptionForHR(UiaGetReservedNotSupportedValue(out nint value));
        Marshal.AddRef(value);
        return ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, value);
    }

    [LibraryImport("oleaut32.dll")]
    private static partial nint SafeArrayCreateVector(ushort type, int lowerBound, uint count);

    [LibraryImport("oleaut32.dll")]
    private static unsafe partial int SafeArrayAccessData(nint array, out void* data);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayUnaccessData(nint array);

    [LibraryImport("UIAutomationCore.dll")]
    [SupportedOSPlatform("windows")]
    public static partial nint UiaReturnRawElementProvider(nint window, nint wParam, nint lParam, nint provider);

    [LibraryImport("UIAutomationCore.dll")]
    [SupportedOSPlatform("windows")]
    public static partial int UiaHostProviderFromHwnd(nint window, out nint provider);

    [LibraryImport("UIAutomationCore.dll")]
    [SupportedOSPlatform("windows")]
    public static partial int UiaRaiseAutomationEvent(nint provider, int eventId);

    [LibraryImport("UIAutomationCore.dll")]
    [SupportedOSPlatform("windows")]
    public static partial int UiaRaiseAutomationPropertyChangedEvent(nint provider, int propertyId, Variant oldValue, Variant newValue);

    [LibraryImport("UIAutomationCore.dll")]
    [SupportedOSPlatform("windows")]
    public static partial int UiaDisconnectProvider(nint provider);

    [LibraryImport("UIAutomationCore.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UiaClientsAreListening();

    [LibraryImport("UIAutomationCore.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int UiaGetReservedNotSupportedValue(out nint value);
}
