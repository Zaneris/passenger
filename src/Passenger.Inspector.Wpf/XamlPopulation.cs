using System.Reflection;
using System.Windows;

namespace Passenger.Inspector.Wpf;

/// <summary>The callback appended by Passenger's shared build import after compiled LoadComponent and before the user
/// constructor continues. It never replaces the compiled BAML loader, connection IDs, namescope or user initialization.</summary>
internal sealed class XamlPopulation(Action<string, DependencyObject, bool> created)
{
    private const string Slot = "Passenger.Wpf.Populated.1";
    private readonly Dictionary<Type, string> types = [];
    private Action<object, Uri>? callback;

    public string? Prepare(Type type, string file)
    {
        object? current = AppContext.GetData(Slot);
        if (callback is not null ? !ReferenceEquals(current, callback) : current is not null)
        {
            return "The app owns or changed the WPF population callback; restart to load the edit";
        }
        CustomAttributeData? marker = type.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType.FullName == "Passenger.Generated.WpfPopulationAttribute");
        if (marker?.ConstructorArguments is not [{ ArgumentType: var argument, Value: 1 }] || argument != typeof(int))
        {
            return Environment.GetEnvironmentVariable(Wire.WpfPopulationProblemVariable)
                ?? $"{type.Name} was built without a supported WPF population hook; rebuild and start it from Passenger to update newly created views";
        }
        if (CompiledPopulation.Source(type) is not { } uri || Sources.FileOf(new Uri(uri, UriKind.Relative)) is not { } source || !SameFile(source, file))
        {
            return $"{type.Name}'s compiled WPF population no longer matches its supported loader and source; restart to load the edit";
        }
        return null;
    }

    public void Install(IReadOnlyDictionary<Type, string> prepared)
    {
        foreach ((Type type, string file) in prepared)
        {
            if (Prepare(type, file) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
        }
        if (callback is null)
        {
            callback = Populated;
            AppContext.SetData(Slot, callback);
        }
        foreach ((Type type, string file) in prepared)
        {
            types[type] = file;
        }
    }

    public void Remove(IEnumerable<Type> added)
    {
        foreach (Type type in added)
        {
            types.Remove(type);
        }
        if (types.Count == 0)
        {
            Stop();
        }
    }

    public bool Contains(Type type) => types.ContainsKey(type);

    public void Stop()
    {
        if (callback is not null && ReferenceEquals(AppContext.GetData(Slot), callback))
        {
            AppContext.SetData(Slot, null);
        }
        callback = null;
        types.Clear();
    }

    private void Populated(object instance, Uri uri)
    {
        if (instance is DependencyObject element && types.TryGetValue(instance.GetType(), out string? file))
        {
            // A class loaded by another component URI must not receive this source's update. Created reports a
            // source mismatch as a runtime failure, rather than silently mutating the unrelated component.
            created(file, element, Sources.FileOf(uri) is { } source && SameFile(source, file));
        }
    }

    private static bool SameFile(string first, string second) => Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
