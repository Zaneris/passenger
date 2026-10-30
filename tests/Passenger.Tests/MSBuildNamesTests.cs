using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Host;

namespace Passenger.Tests;

/// <summary>MSBuild's names that Passenger gives a build, asks an evaluation for or writes into a project come from one home,
/// <see cref="MSBuildNames"/> or the evaluation's <see cref="CarriedProperties"/>: no string the IDE's code writes is one of
/// them again, whole or in a command line's switch or a $(macro). The MSBuild file editor's words (the App's MSBuild/), which
/// offer a person the names to write, are its own.</summary>
[Collection(Beside.Name)]
public sealed partial class MSBuildNamesTests
{
    /// <summary>The projects whose code talks to MSBuild.</summary>
    private static readonly string[] Projects = ["Passenger.App", "Passenger.Workspace", "Passenger.Lsp", "Passenger.Core", "Passenger.Packages"];

    /// <summary>Strings that are such a name for something other than MSBuild, by file.</summary>
    private static readonly (string File, string Text)[] Others =
    [
        // A launch profile's key for its container's docker run words, named as the project's property is.
        ("src/Passenger.App/Run/LaunchProfile.cs", MSBuildNames.DockerfileRunArguments),
    ];

    [Fact]
    public void MSBuild_names_are_written_only_in_their_homes()
    {
        HashSet<string> names = [.. ((Type[])[typeof(MSBuildNames), typeof(CarriedProperties)])
            .SelectMany(home => home.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!)];
        string root = Debugging.Helpers.Debuggees.Root;
        List<string> found = [];
        foreach (string file in Projects.SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative is "src/Passenger.Core/MSBuildNames.cs" || relative.StartsWith("src/Passenger.App/MSBuild/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), cancellationToken: TestContext.Current.CancellationToken);
            foreach (SyntaxNode node in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            {
                string? written = node switch
                {
                    LiteralExpressionSyntax plain when plain.IsKind(SyntaxKind.StringLiteralExpression) => plain.Token.ValueText,
                    InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(part => part is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : "{}")),
                    _ => null,
                };
                if (written is not null && Names(written, names) is { } name && !Others.Contains((relative, name)))
                {
                    found.Add($"{relative}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: \"{written}\"");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} strings write an MSBuild name outside its home, so what a build is given, an evaluation is asked "
            + $"for or a project is written can drift from what is read back, one copy changed and another not: name each through MSBuildNames or "
            + $"CarriedProperties:\n{string.Join("\n", found)}");
    }

    /// <summary>The name a string writes: one of more than a word whole ("TargetPath"; a word alone, "Configuration", is an
    /// ordinary one), or any after a switch (-p:, -property:, -getProperty:, -t:, -target:) or in a macro; else null.</summary>
    private static string? Names(string written, HashSet<string> names)
    {
        if (names.Contains(written) && (written.Contains('_') || written.Skip(1).Any(char.IsUpper)))
        {
            return written;
        }
        return Named().Matches(written).Select(match => match.Groups["name"].Value).FirstOrDefault(names.Contains);
    }

    [GeneratedRegex(@"(?:(?<![\w])[-/](?:p|property|getProperty|t|target):|\$\()(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Named();
}
