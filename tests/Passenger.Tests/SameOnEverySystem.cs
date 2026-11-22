using System.Reflection;
using Passenger.App;
using Xunit.v3;

namespace Passenger.Tests;

/// <summary>A report class whose subject behaves the same on every system: CI's report lane runs it on one system alone,
/// the others leaving it out by its trait, SameOnEverySystem=true (tools/test-shards.py's ONE_SYSTEM). The C# language
/// server, the projects it and the project host read, the language servers whose answers a test reads and what a test's
/// own thread allocates under the headless platform count as alike everywhere; nothing else another system could do
/// otherwise may run. <see cref="SameOnEverySystemRulesTests"/> holds that from the built tests, and each IDE window a
/// marked test opens is followed: the programs started in its folder (<see cref="Started"/>) and Git as it closes
/// (<see cref="Follow"/>).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SameOnEverySystemAttribute : Attribute, ITraitAttribute
{
    public const string Name = "SameOnEverySystem";

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(Name, "true")];

    /// <summary>Whether a test is of a class marked so.</summary>
    public static bool Marks(MethodInfo test) => test.ReflectedType?.GetCustomAttribute<SameOnEverySystemAttribute>() is not null;

    /// <summary>For a test of a marked class, why what its windows ran breaks the mark; null where they ran nothing so.</summary>
    public static string? Unlike(MethodInfo test, IEnumerable<string> ran)
    {
        string[] said = [.. ran.Distinct()];
        return !Marks(test) || said.Length == 0 ? null
            : $"{test.Name} is of a class marked [{Name}], which runs on one system alone, but its window ran what another system could do otherwise: "
                + $"{string.Join("; ", said)}. Take the mark off, or keep the test to what behaves the same everywhere";
    }

    /// <summary>The programs counted as alike everywhere, by a word of their command lines: the C# language server, the
    /// project host, the .NET CLI's question of the SDK a folder takes and its read of a file-based program, the language
    /// servers carried under web/, and git's look for the folder's repository.</summary>
    private static readonly string[] Alike = ["Microsoft.CodeAnalysis.LanguageServer", "Passenger.Host", "dotnet --version", "dotnet --list-sdks",
        "dotnet run-api", "rev-parse --show-toplevel", $"{Path.DirectorySeparatorChar}web{Path.DirectorySeparatorChar}"];

    /// <summary>What a program the IDE started in a followed window's folder (<see cref="OpenWindows.Following"/>) ran that
    /// another system could do otherwise: null for one of <see cref="Alike"/>.</summary>
    public static string? Started(Passenger.Core.ChildStart start) =>
        Alike.Any(word => start.Command.Contains(word, StringComparison.Ordinal)) ? null : $"a program it started: {start.Command}";

    /// <summary>Follows an IDE window a marked test opened as it closes, telling what it runs then (<see cref="Standing"/>).</summary>
    public static void Follow(MainWindow window, Action<string> ran) =>
        window.Closing += (_, _) =>
        {
            foreach (string git in Standing(window))
            {
                ran(git);
            }
        };

    /// <summary>What a window runs as it stands that another system could do otherwise: Git in its repository.</summary>
    public static IEnumerable<string> Standing(MainWindow window) =>
        window.Vcs.Repository is { } repository ? [$"Git in its repository {repository.Root}"] : [];
}
