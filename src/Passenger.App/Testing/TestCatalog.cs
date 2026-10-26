using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>The tests a runner found, each told as the IDE's (<see cref="TestItem"/>) and kept by its key with what the
/// runner runs it by (VSTest's test case, a Testing Platform node). An assembly looked at again replaces what it had; a
/// test it names twice by its id is told once; two whose keys are one (an MSTest data row named as its method) are told
/// as rows named by the order they came in. Touched only by one discovery or run at a time, as the controller asks.</summary>
public sealed class TestCatalog<THandle>
    where THandle : class
{
    private readonly Dictionary<string, THandle> handles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> byAssembly = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> canonical = new(StringComparer.Ordinal);

    /// <summary>What runs a test told; null for a key no look found.</summary>
    public THandle? Of(string key) => handles.GetValueOrDefault(key);

    /// <summary>Tells what a look at some assemblies found, in place of what each had: every test with its assembly, its
    /// id, what it is told as and what runs it, in the order they came.</summary>
    public IReadOnlyList<TestItem> Tell<T>(IEnumerable<TestAssembly> looked, IEnumerable<(TestAssembly Assembly, T Test)> found, Func<T, string> id,
        Func<TestAssembly, T, TestItem> item, Func<TestAssembly, T, THandle> handle)
    {
        canonical.Clear();
        foreach (TestAssembly assembly in looked)
        {
            foreach (string key in byAssembly.GetValueOrDefault(assembly.Path) ?? [])
            {
                handles.Remove(key);
            }
            byAssembly[assembly.Path] = [];
        }
        HashSet<(string, string)> ids = [];
        List<TestItem> told = [];
        foreach ((TestAssembly assembly, T test) in found)
        {
            if (!ids.Add((assembly.Path, id(test))))
            {
                continue;
            }
            TestItem made = item(assembly, test);
            string row = made.Row ?? made.DisplayName;
            for (int again = 2; handles.ContainsKey(made.Key); again++)
            {
                made = made with { Row = $"{row} ({again})" };
            }
            handles[made.Key] = handle(assembly, test);
            byAssembly[assembly.Path].Add(made.Key);
            told.Add(made);
        }
        return told;
    }

    /// <summary>A path as a runner names it (as it was given, or as the PDB or the compiler had it), canonical as the
    /// tree's are.</summary>
    public string Canonical(string path)
    {
        if (!canonical.TryGetValue(path, out string? made))
        {
            canonical[path] = made = PathNames.Canonical(path);
        }
        return made;
    }
}
