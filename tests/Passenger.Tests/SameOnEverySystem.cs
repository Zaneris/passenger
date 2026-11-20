using System.Reflection;
using Passenger.App;
using Xunit.v3;

namespace Passenger.Tests;

/// <summary>A report class whose subject behaves the same on every system: CI's report lane runs it on one system alone,
/// the others leaving it out by its trait, SameOnEverySystem=true (tools/test-shards.py's ONE_SYSTEM). Only a class that
/// runs nothing another system could do otherwise takes it, which <see cref="SameOnEverySystemRulesTests"/> holds from the
/// built tests; every IDE window one of its tests opens is held to it too as it closes or the test ends (<see cref="Ran"/>).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SameOnEverySystemAttribute : Attribute, ITraitAttribute
{
    public const string Name = "SameOnEverySystem";

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(Name, "true")];

    /// <summary>For a test of a marked class, why what its windows ran breaks the mark (<see cref="Ran"/>, read as each
    /// closed or as the test ended); null where they ran nothing so, or the class is not marked.</summary>
    public static string? Unlike(MethodInfo test, IEnumerable<string> ran)
    {
        string[] said = [.. ran.Distinct()];
        return test.ReflectedType?.GetCustomAttribute<SameOnEverySystemAttribute>() is null || said.Length == 0 ? null
            : $"{test.Name} is of a class marked [{Name}], which runs on one system alone, but its window ran what another system could do otherwise: "
                + $"{string.Join("; ", said)}. Take the mark off, or keep the test to what behaves the same everywhere";
    }

    /// <summary>What an IDE window runs that another system could do otherwise: the C# language server, MSBuild on a
    /// project or solution, Git in a repository, a language's own server for a file open.</summary>
    public static IEnumerable<string> Ran(MainWindow window)
    {
        if (window.CSharpService is not null)
        {
            yield return "the C# language server, a program of its own, runs for it";
        }
        if (window.Session.Solution is { } solution)
        {
            yield return $"MSBuild read its solution {Path.GetFileName(solution.Path)}";
        }
        foreach (Passenger.Workspace.ProjectModel project in window.Session.Projects)
        {
            yield return $"MSBuild read its project {Path.GetFileName(project.Path)}";
        }
        if (window.Vcs.Repository is { } repository)
        {
            yield return $"Git runs in its repository {repository.Root}";
        }
        foreach (Passenger.App.Workspace.OpenDocument document in window.EditorArea.Documents.Where(document => Passenger.App.Web.WebLanguages.LanguageOf(document.Path) is not null))
        {
            yield return $"{document.Name} is read by a language server of its own, a program";
        }
    }
}
