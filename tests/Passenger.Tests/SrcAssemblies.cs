using System.Text.RegularExpressions;

namespace Passenger.Tests;

/// <summary>The assemblies src's projects build, as the Debug build writes them under artifacts/bin (named as a project's
/// AssemblyName gives it), each with its project's name, read where a rule holds what the IL says; a project not built is
/// passed over.</summary>
internal static class SrcAssemblies
{
    public static IEnumerable<(string Project, string Assembly)> Built()
    {
        string root = Debugging.Helpers.Debuggees.Root;
        foreach (string project in Directory.GetDirectories(Path.Combine(root, "src")).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(project);
            string projectFile = Path.Combine(project, $"{name}.csproj");
            if (!File.Exists(projectFile))
            {
                continue;
            }
            Match named = Regex.Match(File.ReadAllText(projectFile), "<AssemblyName>([^<]+)</AssemblyName>");
            string assembly = Path.Combine(root, "artifacts", "bin", name, "debug", $"{(named.Success ? named.Groups[1].Value : name)}.dll");
            if (File.Exists(assembly))
            {
                yield return (name, assembly);
            }
        }
    }
}
