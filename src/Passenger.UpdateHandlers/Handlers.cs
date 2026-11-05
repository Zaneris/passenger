using System.Reflection;
using System.Reflection.Metadata;

namespace Passenger.UpdateHandlers;

/// <summary>The program's MetadataUpdateHandlers, called as the runtime's own Hot Reload agent calls them after an update:
/// every ClearCache, then every UpdateApplication, each given no types (any may have changed), the handlers of an assembly
/// another references before that other's, so a framework's caches are cleared before an app's handler reads them.</summary>
internal static class Handlers
{
    /// <summary>Calls every handler, answering each failure in words: its type, its method and what it threw.</summary>
    public static IReadOnlyList<string> Run()
    {
        List<string> failures = [];
        List<Type> handlers = [];
        foreach (Assembly assembly in Ordered(AppDomain.CurrentDomain.GetAssemblies()))
        {
            try
            {
                handlers.AddRange(assembly.GetCustomAttributes<MetadataUpdateHandlerAttribute>().Select(a => a.HandlerType));
            }
            catch (Exception error) when (error is TypeLoadException or FileNotFoundException or BadImageFormatException or CustomAttributeFormatException)
            {
                failures.Add($"{assembly.GetName().Name}'s handlers could not be read: {error.Message}");
            }
        }
        foreach (string method in (string[])["ClearCache", "UpdateApplication"])
        {
            foreach (Type handler in handlers)
            {
                if (handler.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type[])]) is not { } called)
                {
                    continue;
                }
                try
                {
                    called.Invoke(null, [null]);
                }
                catch (TargetInvocationException error)
                {
                    failures.Add($"{handler.FullName}.{method} threw {error.InnerException?.GetType().Name}: {error.InnerException?.Message}");
                }
            }
        }
        return failures;
    }

    /// <summary>The assemblies loaded, each after those of them it references.</summary>
    private static List<Assembly> Ordered(Assembly[] assemblies)
    {
        Dictionary<string, Assembly> byName = [];
        foreach (Assembly assembly in assemblies.Where(a => !a.IsDynamic))
        {
            byName.TryAdd(assembly.GetName().Name ?? string.Empty, assembly);
        }
        List<Assembly> ordered = [];
        HashSet<Assembly> visited = [];
        foreach (Assembly assembly in byName.Values)
        {
            Visit(assembly);
        }
        return ordered;

        void Visit(Assembly assembly)
        {
            if (!visited.Add(assembly))
            {
                return;
            }
            foreach (AssemblyName referenced in assembly.GetReferencedAssemblies())
            {
                if (referenced.Name is { } name && byName.TryGetValue(name, out Assembly? loaded))
                {
                    Visit(loaded);
                }
            }
            ordered.Add(assembly);
        }
    }
}
