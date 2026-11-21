using System.Text.RegularExpressions;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>What the tools' recipes share is written once: a fetch of another project's file, and the Godot commit the
/// GDScript port's tables are read at, in tools/fetched.py; a failure said, the checkout's root and a Release publish in
/// tools/release-common.sh. Read from the scripts as written, comments passed over.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed partial class ToolsRulesTests
{
    /// <summary>No Python recipe but fetched.py opens an address or names Godot's commit: one fetching by itself keeps a
    /// file cut short as whole, and one naming the commit again reads its table from another Godot once the port moves.</summary>
    [Fact]
    public void Every_recipe_fetches_through_fetched_and_names_Godots_commit_there_alone()
    {
        string tools = Path.Combine(Debuggees.Root, "tools");
        string commit = GodotCommit().Match(File.ReadAllText(Path.Combine(tools, "fetched.py"))).Groups[1].Value;
        Assert.True(commit.Length == 40, "tools/fetched.py names no GODOT_COMMIT, which the Godot recipes read their sources at");
        List<string> found = Lines(tools, "*.py", "fetched.py", "#", line => line.Contains("urlopen(", StringComparison.Ordinal) || line.Contains(commit, StringComparison.Ordinal));
        Assert.True(found.Count == 0, "A recipe fetches by itself or names Godot's commit again, so a fetch cut short is kept as whole, or one table "
            + $"is read from another Godot than the rest: fetch through fetched.cached or fetched.godot, and read fetched.GODOT_COMMIT:\n{string.Join('\n', found)}");
    }

    /// <summary>No shell script of the tools' own folder but release-common.sh says a failure or publishes a project by its
    /// own words, and none sourcing it finds the checkout's root by hand: a publish of its own restores what no lock file
    /// pins and leaves MSBuild's nodes running. tools/linux/install.sh, which goes out alone in Linux's archive, keeps its own.</summary>
    [Fact]
    public void Every_release_script_fails_finds_its_root_and_publishes_through_release_common()
    {
        string tools = Path.Combine(Debuggees.Root, "tools");
        List<string> found = Lines(tools, "*.sh", "release-common.sh", "#", line => FailDeclared().IsMatch(line) || line.Contains("dotnet publish", StringComparison.Ordinal));
        foreach (string script in Directory.EnumerateFiles(tools, "*.sh").Order(StringComparer.Ordinal))
        {
            string[] lines = File.ReadAllLines(script);
            if (lines.Any(line => line.Contains("release-common.sh\"", StringComparison.Ordinal) && line.TrimStart().StartsWith('.')))
            {
                found.AddRange(lines.Index().Where(line => line.Item.Contains("$(dirname \"$0\")/..", StringComparison.Ordinal))
                    .Select(line => $"tools/{Path.GetFileName(script)}:{line.Index + 1}: {line.Item.Trim()}"));
            }
        }
        Assert.True(found.Count == 0, "A release script says a failure, finds the checkout or publishes by its own words, so it fails unlike the rest, "
            + $"or its publish restores unlocked and leaves MSBuild's nodes running: take fail, checkout_root and publish_project from release-common.sh:\n{string.Join('\n', found)}");
    }

    /// <summary>Each line of the tools' own folder's files of a pattern but one, outside comments, that matches.</summary>
    private static List<string> Lines(string tools, string pattern, string home, string comment, Func<string, bool> matches) =>
        [.. Directory.EnumerateFiles(tools, pattern).Where(file => Path.GetFileName(file) != home).Order(StringComparer.Ordinal)
            .SelectMany(file => File.ReadAllLines(file).Index()
                .Where(line => !line.Item.TrimStart().StartsWith(comment, StringComparison.Ordinal) && matches(line.Item))
                .Select(line => $"tools/{Path.GetFileName(file)}:{line.Index + 1}: {line.Item.Trim()}"))];

    [GeneratedRegex(@"^GODOT_COMMIT = ""([0-9a-f]+)""", RegexOptions.Multiline)]
    private static partial Regex GodotCommit();

    [GeneratedRegex(@"^\s*fail\s*\(\s*\)")]
    private static partial Regex FailDeclared();
}
