using System.Reflection;
using System.Reflection.Metadata;

namespace Passenger.UpdateHandlers;

/// <summary>The program's MetadataUpdateHandlers, called as the runtime's own Hot Reload agent calls them after an update:
/// every ClearCache, then every UpdateApplication, each given the changed types, the handlers of an assembly
/// another references before that other's, so a framework's caches are cleared before an app's handler reads them.</summary>
internal static class Handlers
{
    /// <summary>Calls every handler, answering each failure in words: its type, its method and what it threw.</summary>
    public static IReadOnlyList<string> Run(Type[]? updatedTypes) => Run(AppDomain.CurrentDomain.GetAssemblies(), updatedTypes);

    /// <summary>Calls the handlers of these assemblies, as <see cref="Run(Type[])"/> does those of every one loaded.</summary>
    public static IReadOnlyList<string> Run(IEnumerable<Assembly> assemblies, Type[]? updatedTypes = null)
    {
        List<string> failures = [];
        List<Type> handlers = [];
        foreach (Assembly assembly in Ordered(assemblies))
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
                    called.Invoke(null, [updatedTypes]);
                }
                catch (TargetInvocationException error)
                {
                    failures.Add($"{handler.FullName}.{method} threw {error.InnerException?.GetType().Name}: {error.InnerException?.Message}");
                }
            }
        }
        return failures;
    }

    /// <summary>The assemblies given, each after those of them it references, every load of a name among them: one assembly
    /// in two load contexts holds two sets of statics, so each has its handlers called.</summary>
    public static List<Assembly> Ordered(IEnumerable<Assembly> assemblies)
    {
        ILookup<string, Assembly> byName = assemblies.Where(a => !a.IsDynamic).ToLookup(a => a.GetName().Name ?? string.Empty);
        List<Assembly> ordered = [];
        HashSet<Assembly> visited = [];
        foreach (Assembly assembly in byName.SelectMany(named => named))
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
                foreach (Assembly loaded in byName[referenced.Name ?? string.Empty])
                {
                    Visit(loaded);
                }
            }
            ordered.Add(assembly);
        }
    }
}
