using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A property Passenger gives a dotnet or MSBuild command line reaches MSBuild as the text given, whatever it holds
/// (a folder of "Smith, John", a path with a semicolon), since every one is written by <see cref="PropertySwitch"/>: no
/// string of Passenger's code begins a <c>-p:</c>, <c>-property:</c> or <c>--property:</c> switch of its own.</summary>
[Collection(Beside.Name)]
public sealed class PropertySwitchTests
{
    /// <summary>The switches that give a property, as MSBuild and the .NET CLI read them, whatever their letters' case.</summary>
    private static readonly string[] Switches = ["-p:", "/p:", "-property:", "/property:", "--property:", "-property", "--property"];

    [Fact]
    public async Task A_value_holding_msbuilds_separators_escape_mark_and_quote_reads_back_whole_from_msbuilds_evaluation()
    {
        string folder = TestFolders.Create("property-switch-");
        try
        {
            string project = TestFolders.Write(folder, "Given.proj", "<Project>\n  <Target Name=\"Build\" />\n</Project>\n");
            const string Sdk = "/Users/Smith, John/Library/Android/sdk;2 %3B 100% \"quoted\" $(Not) @(Items) = x ";
            const string Other = "a,b;c";
            // As the Android workload's installer is given its SDK's folder, the project's own globals beside it.
            MSBuildQuestion question = new([])
            {
                Properties = [MSBuildNames.AndroidSdkDirectory, MSBuildNames.Configuration],
                Given = new Dictionary<string, string> { [MSBuildNames.AndroidSdkDirectory] = Sdk },
            };
            MSBuildAnswer answer = await Waiting.Apart(() => MSBuildQuestions.AskAsync(project, null,
                new Dictionary<string, string> { [MSBuildNames.Configuration] = Other }, question, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.True(answer.Answered, $"MSBuild gave no answer: {answer.Said.Error}{answer.Said.Output}");
            Assert.Equal(Sdk, answer.Property(MSBuildNames.AndroidSdkDirectory));
            Assert.Equal(Other, answer.Property(MSBuildNames.Configuration));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [Fact]
    public void No_property_switch_is_written_but_by_PropertySwitch()
    {
        string root = Debugging.Helpers.Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative is "src/Passenger.Core/PropertySwitch.cs" || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            found.AddRange(Written(File.ReadAllText(file)).Select(at => $"{relative}:{at.Line}: {at.Code}"));
        }
        Assert.True(found.Count == 0, $"{found.Count} property switches are written by hand, so a value holding a ';' or ',' (a folder of \"Smith, John\") "
            + $"splits into another property and a '%' or '\"' is read otherwise: write each with PropertySwitch.Of:\n{string.Join("\n", found)}");
    }

    [Fact]
    public void The_check_finds_a_property_switch_written_each_way_and_passes_the_other_switches()
    {
        const string Code = """
            class Switches
            {
                string[] Arguments(string value, string name, bool watched) =>
                [
                    "-p:Configuration=Release",
                    $"-p:{name}={value}",
                    $"-property:{name}={value}",
                    @"/p:Platform=x64",
                    $@"--property:{name}={value}",
                    "-P:Name=Value",
                    watched ? "--property" : "-p",
                    PropertySwitch.Of(name, value, watched),
                    "-p",
                    $"-getProperty:{name}",
                    "-t:Build",
                    "compose -p name",
                    "RUN dotnet publish /p:UseAppHost=false",
                ];
            }
            """;
        Assert.Equal([5, 6, 7, 8, 9, 10, 11], Written(Code).Select(at => at.Line));
    }

    /// <summary>The strings of <paramref name="code"/> that begin a property switch, by line (from 1) and the code writing it.</summary>
    private static IEnumerable<(int Line, string Code)> Written(string code)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(code, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            string? start = node switch
            {
                LiteralExpressionSyntax plain when plain.IsKind(SyntaxKind.StringLiteralExpression) => plain.Token.ValueText,
                InterpolatedStringExpressionSyntax { Contents: [InterpolatedStringTextSyntax first, ..] } => first.TextToken.ValueText,
                _ => null,
            };
            if (start is not null && Switches.Any(s => s.EndsWith(':') ? start.StartsWith(s, StringComparison.OrdinalIgnoreCase) : start.Equals(s, StringComparison.OrdinalIgnoreCase)))
            {
                yield return (node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, node.ToString());
            }
        }
    }
}
