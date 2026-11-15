using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Passenger.Core;

namespace Passenger.Helper;

/// <summary>Runs one of Passenger's own helper programs on the runtime Passenger runs on: <c>passenger-helper ASSEMBLY
/// [ARGUMENTS]</c>. The helper is loaded into the default context as its own host would load it, every assembly and native
/// library from what its .deps.json names, with its runtimeconfig's properties, its folder as the base directory and itself
/// as the entry assembly, and its Main is run with the arguments after it. This program's own .deps.json names the runtime
/// alone, so nothing of Passenger's is within a helper's reach. The runtime's own settings (the collector's kind, the
/// thread pool's) are the ones it started with: a helper is given those through its environment.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args is not [{ Length: > 0 } named, ..])
        {
            Console.Error.WriteLine("passenger-helper: name a helper's assembly, then its arguments");
            return 2;
        }
        string assembly = Path.GetFullPath(named);
        if (!File.Exists(assembly))
        {
            Console.Error.WriteLine($"passenger-helper: {assembly} does not exist");
            return 2;
        }
        string folder = Path.GetDirectoryName(assembly)!;
        string name = Path.GetFileNameWithoutExtension(assembly);
        string runtimeConfig = Path.ChangeExtension(assembly, ".runtimeconfig.json");
        if (File.Exists(runtimeConfig))
        {
            if (RuntimeConfig.Read(assembly) is not { } config)
            {
                Console.Error.WriteLine($"passenger-helper: {runtimeConfig} does not read as a runtime configuration");
                return 2;
            }
            // As its host gives them to the runtime, which AppContext's switches and data read.
            foreach ((string property, string value) in config.Properties)
            {
                AppContext.SetData(property, value);
            }
        }
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", folder + Path.DirectorySeparatorChar);
        string deps = Path.Combine(folder, name + ".deps.json");
        AppContext.SetData("APP_CONTEXT_DEPS_FILES", File.Exists(deps) ? deps : string.Empty);
        AssemblyDependencyResolver resolver = new(assembly);
        AssemblyLoadContext.Default.Resolving += (context, wanted) =>
            resolver.ResolveAssemblyToPath(wanted) is { } path ? context.LoadFromAssemblyPath(path) : null;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, library) =>
            resolver.ResolveUnmanagedDllToPath(library) is { } path ? NativeLibrary.Load(path) : IntPtr.Zero;
        Assembly helper = AssemblyLoadContext.Default.LoadFromAssemblyPath(assembly);
        Assembly.SetEntryAssembly(helper);
        MethodInfo main = helper.EntryPoint ?? throw new InvalidOperationException($"{assembly} has no entry point");
        object?[]? given = main.GetParameters().Length == 0 ? null : [args[1..]];
        // Not wrapped, so a helper's failure reads as its own host would have it.
        object? ended = main.Invoke(null, BindingFlags.DoNotWrapExceptions, null, given, null);
        return ended is int code ? code : Environment.ExitCode;
    }
}
