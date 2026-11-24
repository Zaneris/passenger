using System.Runtime.InteropServices;

namespace Passenger.Inspector.WinUI;

/// <summary>Where a XAML file made an element, as WinUI's diagnostics give it: the file (ms-appx:///MainWindow.xaml) and its
/// line and column, from one, where its build recorded them.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SourceInfo
{
    public nint FileName;
    public uint LineNumber;
    public uint ColumnNumber;
    public uint CharPosition;
    public nint Hash;
}

/// <summary>An element as WinUI's diagnostics tell of it: its handle, where its XAML made it, its type, name and how many
/// children it has; the strings BSTRs the teller owns.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VisualElement
{
    public ulong Handle;
    public SourceInfo SrcInfo;
    public nint Type;
    public nint Name;
    public uint NumChildren;
}

/// <summary>Where an element came or went: under which parent, at which place among its children.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParentChildRelation
{
    public ulong Parent;
    public ulong Child;
    public uint ChildIndex;
}

/// <summary>What set a property's value (a style, a template), as WinUI's diagnostics list it for an element.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyChainSource
{
    public ulong Handle;
    public nint TargetType;
    public nint Name;
    public int Source;
    public SourceInfo SrcInfo;
}

/// <summary>A property's value on an element, and which of the element's sources gave it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyChainValue
{
    public uint Index;
    public nint Type;
    public nint DeclaringType;
    public nint ValueType;
    public nint ItemType;
    public nint Value;
    public int Overridden;
    public long MetadataBits;
    public nint PropertyName;
    public uint PropertyChainIndex;
}

/// <summary>A property's value on an element as WinUI's diagnostics read it: its name, declaring type, type, value (null for
/// null), where it came from (<see cref="Sources"/>), whether a source above overrides it, and which of the element's
/// <see cref="ValueGiver"/>s gave it.</summary>
internal sealed record PropertyRead(string Name, string DeclaringType, string Type, string? Value, int Source, bool Overridden, int Giver);

/// <summary>What gave an element's properties values: a style's target type and key, the kind of giver (<see cref="Sources"/>),
/// and where its XAML is.</summary>
internal sealed record ValueGiver(string Type, string? Name, int Source, string? File, int Line, int Column);

/// <summary>WinUI's BaseValueSource, as its diagnostics name what gave a value.</summary>
internal static class Sources
{
    public const int Default = 1;
    public const int BuiltInStyle = 2;
    public const int Style = 3;
    public const int Local = 4;
    public const int Inherited = 5;
    public const int ParentTemplate = 10;
    public const int Animation = 12;
    public const int VisualState = 14;
}

/// <summary>The site WinUI's XAML diagnostics give the TAP, an object WinUI keeps, called through its vtables as its header
/// (xamlom.h) lays them out: IXamlDiagnostics and IVisualTreeService. Any thread may call; WinUI answers about its
/// elements only on its UI thread.</summary>
internal sealed unsafe class Diagnostics(nint site)
{
    private static readonly Guid XamlDiagnosticsId = new("18C9E2B6-3F43-4116-9F2B-FF935D7770D2");
    private static readonly Guid VisualTreeServiceId = new("A593B11A-D17F-48BB-8F66-83910731C8A5");

    private readonly nint diagnostics = Face(site, XamlDiagnosticsId);
    private readonly nint service = Face(site, VisualTreeServiceId);

    // IVisualTreeService's slots after IUnknown's three.
    private const int Advise = 3;
    private const int Chain = 7;

    // IXamlDiagnostics' slots after IUnknown's three.
    private const int Dispatcher = 3;
    private const int InspectableFromHandle = 6;
    private const int HandleFromInspectable = 7;

    /// <summary>A value's metadata bit saying it is null.</summary>
    private const long IsNull = 0x20;

    private static nint Face(nint site, Guid id)
    {
        int done = Marshal.QueryInterface(site, in id, out nint face);
        Marshal.ThrowExceptionForHR(done);
        return face;
    }

    private static void* Slot(nint self, int slot) => (*(void***)self)[slot];

    /// <summary>Has WinUI tell <paramref name="callback"/> (an IVisualTreeServiceCallback) of every element there is, then of
    /// each that comes or goes.</summary>
    public void AdviseVisualTreeChange(nint callback) =>
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(service, Advise))(service, callback));

    /// <summary>The UI thread's dispatcher queue, as an IInspectable the caller releases.</summary>
    public nint DispatcherQueue()
    {
        nint queue;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(diagnostics, Dispatcher))(diagnostics, &queue));
        return queue;
    }

    /// <summary>The object an element's handle names, as an IInspectable the caller releases; zero for one gone.</summary>
    public nint Inspectable(ulong handle)
    {
        nint made;
        int done = ((delegate* unmanaged[Stdcall]<nint, ulong, nint*, int>)Slot(diagnostics, InspectableFromHandle))(diagnostics, handle, &made);
        return done < 0 ? 0 : made;
    }

    /// <summary>The handle WinUI names an object by; zero for one it does not know.</summary>
    public ulong HandleOf(nint inspectable)
    {
        ulong handle;
        int done = ((delegate* unmanaged[Stdcall]<nint, nint, ulong*, int>)Slot(diagnostics, HandleFromInspectable))(diagnostics, inspectable, &handle);
        return done < 0 ? 0 : handle;
    }

    /// <summary>The properties set on an element, each with its value and what gave it, and those givers; on the UI thread.</summary>
    public (List<PropertyRead> Values, List<ValueGiver> Givers) ValuesChain(ulong handle)
    {
        uint sourceCount;
        uint valueCount;
        PropertyChainSource* sources;
        PropertyChainValue* values;
        int done = ((delegate* unmanaged[Stdcall]<nint, ulong, uint*, PropertyChainSource**, uint*, PropertyChainValue**, int>)Slot(service, Chain))(
            service, handle, &sourceCount, &sources, &valueCount, &values);
        Marshal.ThrowExceptionForHR(done);
        List<ValueGiver> givers = [];
        List<PropertyRead> read = [];
        try
        {
            for (int i = 0; i < sourceCount; i++)
            {
                PropertyChainSource source = sources[i];
                givers.Add(new ValueGiver(Text(source.TargetType) ?? string.Empty, Text(source.Name), source.Source, Text(source.SrcInfo.FileName), (int)source.SrcInfo.LineNumber,
                    (int)source.SrcInfo.ColumnNumber));
            }
            for (int i = 0; i < valueCount; i++)
            {
                PropertyChainValue value = values[i];
                read.Add(new PropertyRead(Text(value.PropertyName) ?? string.Empty, Text(value.DeclaringType) ?? string.Empty, Text(value.ValueType) ?? Text(value.Type) ?? string.Empty,
                    (value.MetadataBits & IsNull) != 0 ? null : Text(value.Value), value.PropertyChainIndex < sourceCount ? sources[value.PropertyChainIndex].Source : 0,
                    value.Overridden != 0, (int)value.PropertyChainIndex));
            }
        }
        finally
        {
            for (int i = 0; i < sourceCount; i++)
            {
                Free(sources[i].TargetType);
                Free(sources[i].Name);
                Free(sources[i].SrcInfo.FileName);
                Free(sources[i].SrcInfo.Hash);
            }
            for (int i = 0; i < valueCount; i++)
            {
                Free(values[i].Type);
                Free(values[i].DeclaringType);
                Free(values[i].ValueType);
                Free(values[i].ItemType);
                Free(values[i].Value);
                Free(values[i].PropertyName);
            }
            Marshal.FreeCoTaskMem((nint)sources);
            Marshal.FreeCoTaskMem((nint)values);
        }
        return (read, givers);
    }

    /// <summary>A BSTR's text; null for none.</summary>
    public static string? Text(nint bstr) => bstr == 0 ? null : Marshal.PtrToStringBSTR(bstr);

    private static void Free(nint bstr)
    {
        if (bstr != 0)
        {
            Marshal.FreeBSTR(bstr);
        }
    }
}
