using System.Reflection;

namespace Passenger.UpdateHandlers;

/// <summary>One update's type identities, resolved inside the program after its metadata has changed.</summary>
internal sealed class ChangedTypes
{
    private readonly HashSet<(Guid Module, int Token)> pending = [];

    public void Add(string line)
    {
        if (Wire.TryType(line, out Guid module, out int token))
        {
            pending.Add((module, token));
        }
    }

    /// <summary>Resolves every loaded copy of each module. An unresolvable token invalidates all caches (null),
    /// rather than leaving a handler with an incomplete list. Consumes this batch even when resolution fails.</summary>
    public Type[]? Take(IEnumerable<Assembly> assemblies)
    {
        try
        {
            ILookup<Guid, Module> modules = assemblies.Where(a => !a.IsDynamic).SelectMany(a => a.GetModules()).ToLookup(m => m.ModuleVersionId);
            HashSet<Type> types = [];
            foreach ((Guid module, int token) in pending)
            {
                if (!modules.Contains(module))
                {
                    return null;
                }
                foreach (Module loaded in modules[module])
                {
                    types.Add(loaded.ResolveType(token));
                }
            }
            return [.. types];
        }
        catch (Exception error) when (error is ArgumentException or TypeLoadException or FileNotFoundException or BadImageFormatException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            pending.Clear();
        }
    }
}
