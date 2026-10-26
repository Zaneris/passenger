using System.Runtime.InteropServices;

namespace Passenger.Debugger;

/// <summary>What the engine's reads throw when what they read fails them, told from a fault of the engine's own, which a
/// catch of these leaves to go on to the boundary that says it whole.</summary>
internal static class Failures
{
    /// <summary>A call into the runtime's debugging or metadata interfaces that failed: the exception .NET makes of its HRESULT
    /// (a COMException; for E_INVALIDARG an ArgumentException, E_NOTIMPL a NotImplementedException, E_NOINTERFACE an
    /// InvalidCastException, a corrupt signature a BadImageFormatException), a cast whose QueryInterface the object refuses,
    /// and the interop's own: InvalidOperationException (ObjectDisposedException among them), NotSupportedException for an
    /// interface the object lacks, OverflowException for a count or size past what it converts it to.</summary>
    public static bool OfRuntime(Exception error) => error is COMException or ArgumentException or NotImplementedException
        or InvalidCastException or InvalidOperationException or NotSupportedException or BadImageFormatException or OverflowException;

    /// <summary>A read of an assembly's or a PDB's file and metadata that failed: the file's IOException or
    /// UnauthorizedAccessException, and System.Reflection.Metadata's BadImageFormatException for what is not the image or
    /// metadata it should be, InvalidOperationException for an image without metadata, and ArgumentException or
    /// NotSupportedException for a stream it cannot read.</summary>
    public static bool OfReading(Exception error) => error is IOException or UnauthorizedAccessException or BadImageFormatException
        or InvalidOperationException or ArgumentException or NotSupportedException;
}
