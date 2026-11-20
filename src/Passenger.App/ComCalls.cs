using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Passenger.App;

/// <summary>A COM object called by its vtable's slots, as the system's headers number them, and let go of once: what the
/// App's calls into Windows' shell and WebView2 share.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ComCalls
{
    /// <summary>The method at <paramref name="index"/> of the object's vtable, cast to its signature by the caller.</summary>
    public static void* Slot(nint self, int index) => (*(void***)self)[index];

    /// <summary>The object let go of, where there is one.</summary>
    public static void Release(nint reference)
    {
        if (reference != 0)
        {
            Marshal.Release(reference);
        }
    }
}
