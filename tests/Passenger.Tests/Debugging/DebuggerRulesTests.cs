using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>The debugger's code says each of its own rows by one name and takes each failure of the runtime's interfaces
/// one way: no string outside <see cref="DapRows"/> is one of its rows' names, and no catch lists the runtime's failures
/// by hand rather than through Failures.OfRuntime.</summary>
[Collection(Beside.Name)]
public sealed class DebuggerRulesTests
{
    [Fact]
    public void A_row_the_debugger_adds_is_named_only_in_DapRows()
    {
        HashSet<string> names = [.. typeof(DapRows).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!)];
        List<string> found = Written(["src"], "src/Passenger.Dap/DapNames.cs", node => node switch
        {
            LiteralExpressionSyntax plain when plain.IsKind(SyntaxKind.StringLiteralExpression) && names.Contains(plain.Token.ValueText) => plain.Token.ValueText,
            // A name numbered after its row's ($ReturnValue1 on), made by a hole after the name.
            InterpolatedStringExpressionSyntax { Contents: [InterpolatedStringTextSyntax text, InterpolationSyntax, ..] } when names.Contains(text.TextToken.ValueText) =>
                text.TextToken.ValueText + "{}",
            _ => null,
        });
        Assert.True(found.Count == 0, $"{found.Count} strings write a row's name outside DapRows, so the debugger and the window can come to name one "
            + $"row two ways and the window no longer tells it apart (Set Value offered on Raw View, the exception or a value returned not marked "
            + $"as one): name each through DapRows:\n{string.Join("\n", found)}");
    }

    [Fact]
    public void A_catch_around_the_runtime_takes_all_its_failures_through_Failures_OfRuntime()
    {
        List<string> found = Written(["src", "Passenger.Debugger"], "src/Passenger.Debugger/Failures.cs", node =>
            node is CatchFilterClauseSyntax filter && !filter.FilterExpression.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name => name.Identifier.ValueText == "HResult")
                && filter.FilterExpression.DescendantNodesAndSelf().Any(test => test is TypeSyntax type && type.ToString().EndsWith("COMException", StringComparison.Ordinal))
                ? filter.ToString()
                : null);
        Assert.True(found.Count == 0, $"{found.Count} catches list the runtime's failures by hand, so the ones they leave out (an E_INVALIDARG's "
            + $"ArgumentException, an E_NOTIMPL's NotImplementedException) escape the read they guard and fail the request around it: take them "
            + $"through Failures.OfRuntime:\n{string.Join("\n", found)}");
    }

    /// <summary>Each C# node under the folder (from the repository's root) that <paramref name="written"/> names, by file
    /// and line, <paramref name="home"/> and the build's output passed over.</summary>
    private static List<string> Written(string[] folder, string home, Func<SyntaxNode, string?> written)
    {
        string root = Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine([root, .. folder]), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == home || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), cancellationToken: TestContext.Current.CancellationToken);
            foreach (SyntaxNode node in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            {
                if (written(node) is { } said)
                {
                    found.Add($"{relative}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {said}");
                }
            }
        }
        return found;
    }
}
