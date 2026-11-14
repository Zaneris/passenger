using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Passenger.MauiPreview;

/// <summary>The previewer's own Skia and HarfBuzz natives, which its .deps.json names: the runtime looks for a library
/// beside the helper host first, where a published Passenger carries the app's own of another version, and asks the
/// helper's resolver only where it finds none there.</summary>
internal static class OwnNatives
{
    /// <summary>Each assembly's libraries taken from the previewer's own, before any of them is called.</summary>
    public static void Use(params Assembly[] assemblies)
    {
        AssemblyDependencyResolver own = new(typeof(OwnNatives).Assembly.Location);
        foreach (Assembly assembly in assemblies)
        {
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => own.ResolveUnmanagedDllToPath(name) is { } path ? NativeLibrary.Load(path) : IntPtr.Zero);
        }
    }
}
