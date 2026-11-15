using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Tests;

/// <summary>A wait Passenger's code bounds, and any time it measures from or to a moment of its own, is on Stopwatch's clock
/// (Core's Deadline, Stopwatch.GetElapsedTime), which stops while the machine sleeps: never on the wall clock
/// (DateTime.UtcNow), which runs on through a sleep and is set back and forth, nor on Environment.TickCount64. The wall clock
/// is read for a moment shown or kept, or set against a file's or another program's time. A time is measured where one of
/// the code's own moments of the wall clock is set against the clock read now; a moment is followed by what holds it in the
/// same file (a local, a field, a pattern's, an out or a lambda's variable, a list's item or a dictionary's value), the way
/// the code's waits are written.</summary>
[Collection(Beside.Name)]
public sealed class WallClockTests
{
    /// <summary>The reads of the tick count that are another program's time, by file: the input events the WPF inspector
    /// raises are stamped as WPF stamps its own.</summary>
    private static readonly Dictionary<string, string> TheirTicks = new(StringComparer.Ordinal)
    {
        ["src/Passenger.Inspector.Wpf/Driver.cs"] = "Environment.TickCount",
    };

    [Fact]
    public void No_wait_or_time_between_Passengers_own_moments_is_measured_on_the_wall_clock()
    {
        string root = Debugging.Helpers.Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            found.AddRange(Measured(File.ReadAllText(file)).Where(at => TheirTicks.GetValueOrDefault(relative) != at.Code).Select(at => $"{relative}:{at.Line}: {at.Code}"));
        }
        Assert.True(found.Count == 0, $"{found.Count} times between Passenger's own moments are measured on the wall clock, so a wait under way as the "
            + "machine sleeps gives up at once as it wakes, and one the clock is set back under lasts that much longer: measure each on Stopwatch's "
            + $"clock (Deadline, Stopwatch.GetElapsedTime):\n{string.Join("\n", found)}");
    }

    [Fact]
    public void The_check_finds_each_way_a_wait_is_written_on_the_wall_clock_and_passes_a_moment_set_against_a_files_time()
    {
        const string Code = """
            class Waits
            {
                private readonly DateTime until = DateTime.UtcNow.AddSeconds(10);
                private readonly Dictionary<string, DateTime> saved = new();
                private DateTime? followed;

                async Task Wait(TimeSpan limit, string path)
                {
                    DateTime ends = DateTime.UtcNow + limit;
                    while (DateTime.UtcNow < ends) { await Task.Delay(50); }
                    TimeSpan left = ends - DateTime.UtcNow;
                    DateTime first = DateTime.UtcNow;
                    bool longer = DateTime.UtcNow - first > limit;
                    bool past = DateTime.UtcNow >= until;
                    bool again = followed is { } at && DateTime.UtcNow - at < limit;
                    saved[path] = DateTime.UtcNow;
                    bool own = saved.TryGetValue(path, out DateTime when) && DateTime.UtcNow - when < limit;
                    long tick = Environment.TickCount64;
                    DateTime since = DateTime.UtcNow - limit;
                    bool written = File.GetLastWriteTimeUtc(path) >= since;
                    TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                    string shown = $"{DateTime.Now:T}";
                    Deadline kept = Deadline.In(limit);
                    while (!kept.Passed) { await Task.Delay(50); }
                    followed = DateTime.UtcNow;
                    DateTime now = DateTime.UtcNow;
                    bool quiet = now - first > limit;
                    DateTime later = until > ends ? until : ends;
                    List<DateTime> restarts = [];
                    restarts.Add(now);
                    restarts.RemoveAll(then => now - then > limit);
                    foreach (DateTime restart in restarts) { bool recent = DateTime.UtcNow - restart < limit; }
                    List<DateTime> writes = [];
                    writes.Add(File.GetLastWriteTimeUtc(path));
                    bool fresh = writes.Exists(w => DateTime.UtcNow - w < limit);
                }
            }
            """;
        Assert.Equal([10, 11, 13, 14, 15, 17, 18, 27, 31, 32], Measured(Code).Select(at => at.Line));
    }

    /// <summary>Where <paramref name="code"/> sets one of its own moments of the wall clock against the clock read now, or
    /// reads Environment.TickCount, by line (from 1) and the code doing it.</summary>
    private static IEnumerable<(int Line, string Code)> Measured(string code)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(code, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        HashSet<string> moments = Followed(root);
        HashSet<string> nows = [.. root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().SelectMany(local => local.Declaration.Variables)
            .Where(local => local.Initializer?.Value is { } value && IsClock(value)).Select(local => local.Identifier.ValueText)];
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            bool measured = node switch
            {
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TickCount" or "TickCount64" } access => access.Expression.ToString() is "Environment" or "System.Environment",
                BinaryExpressionSyntax binary => binary.Kind() is SyntaxKind.SubtractExpression or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                    or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression && Measures(binary.Left, binary.Right, moments, nows),
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Subtract" or "CompareTo" } of, ArgumentList.Arguments: [{ } other] } =>
                    Measures(of.Expression, other.Expression, moments, nows),
                _ => false,
            };
            if (measured)
            {
                yield return (node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, node.ToString());
            }
        }
    }

    /// <summary>The names in the code that hold a moment of the wall clock read in it, followed through each other and the
    /// lists and dictionaries that hold one, their items read by a loop, a lambda or an out variable, until no more come.</summary>
    private static HashSet<string> Followed(SyntaxNode root)
    {
        HashSet<string> moments = new(StringComparer.Ordinal);
        HashSet<string> stores = new(StringComparer.Ordinal);
        int known;
        do
        {
            known = moments.Count + stores.Count;
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax { Initializer.Value: { } value } declared when IsMoment(value, moments):
                        moments.Add(declared.Identifier.ValueText);
                        break;
                    case PropertyDeclarationSyntax { Initializer.Value: { } value } property when IsMoment(value, moments):
                        moments.Add(property.Identifier.ValueText);
                        break;
                    case AssignmentExpressionSyntax assigned when assigned.IsKind(SyntaxKind.SimpleAssignmentExpression) && IsMoment(assigned.Right, moments):
                        if (assigned.Left is ElementAccessExpressionSyntax { Expression: { } store } && NameOf(store) is { } held)
                        {
                            stores.Add(held);
                        }
                        else if (NameOf(assigned.Left) is { } name)
                        {
                            moments.Add(name);
                        }
                        break;
                    case IsPatternExpressionSyntax matched when IsMoment(matched.Expression, moments):
                        moments.UnionWith(matched.Pattern.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>().Select(v => v.Identifier.ValueText));
                        break;
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryGetValue" or "Remove" or "TryRemove" } call } asked
                        when NameOf(call.Expression) is { } dictionary && stores.Contains(dictionary):
                        moments.UnionWith(asked.ArgumentList.Arguments.Select(a => a.Expression).OfType<DeclarationExpressionSyntax>()
                            .SelectMany(d => d.Designation.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>()).Select(v => v.Identifier.ValueText));
                        break;
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" or "Insert" or "Enqueue" or "Push" } call, ArgumentList.Arguments: [.., { } item] }
                        when IsMoment(item.Expression, moments) && NameOf(call.Expression) is { } list:
                        stores.Add(list);
                        break;
                    case ForEachVariableStatementSyntax each when NameOf(each.Expression) is { } dictionary && stores.Contains(dictionary):
                        moments.UnionWith(each.Variable.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>().Select(v => v.Identifier.ValueText));
                        break;
                    case ForEachStatementSyntax each when NameOf(each.Expression) is { } list && stores.Contains(list):
                        moments.Add(each.Identifier.ValueText);
                        break;
                    case SimpleLambdaExpressionSyntax lambda when lambda.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax read } }
                        && NameOf(read.Expression) is { } list && stores.Contains(list):
                        moments.Add(lambda.Parameter.Identifier.ValueText);
                        break;
                }
            }
        }
        while (moments.Count + stores.Count > known);
        return moments;
    }

    /// <summary>Whether two moments of the code's own wall clock are set against each other, one of them the clock read now
    /// (or now moved by a time): a time measured from or to the other.</summary>
    private static bool Measures(ExpressionSyntax one, ExpressionSyntax other, HashSet<string> moments, HashSet<string> nows) =>
        IsMoment(one, moments) && IsMoment(other, moments) && (IsNow(one, nows) || IsNow(other, nows));

    /// <summary>Whether an expression is the wall clock read now: the clock itself, a local that read it as it was declared,
    /// or either moved by a time.</summary>
    private static bool IsNow(ExpressionSyntax expression, HashSet<string> nows) => expression switch
    {
        ParenthesizedExpressionSyntax inner => IsNow(inner.Expression, nows),
        BinaryExpressionSyntax moved when moved.IsKind(SyntaxKind.AddExpression) || moved.IsKind(SyntaxKind.SubtractExpression) =>
            IsNow(moved.Left, nows) || (moved.IsKind(SyntaxKind.AddExpression) && IsNow(moved.Right, nows)),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: { } method } moved } when method.StartsWith("Add", StringComparison.Ordinal) =>
            IsNow(moved.Expression, nows),
        _ => IsClock(expression) || (NameOf(expression) is { } name && nows.Contains(name)),
    };

    /// <summary>Whether an expression reads the wall clock: DateTime's or DateTimeOffset's Now or UtcNow.</summary>
    private static bool IsClock(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax inner => IsClock(inner.Expression),
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "UtcNow" or "Now" } clock => clock.Expression.ToString() is "DateTime" or "DateTimeOffset"
            or "System.DateTime" or "System.DateTimeOffset",
        _ => false,
    };

    /// <summary>Whether an expression is a moment of the wall clock read in the code: the clock itself, a name holding one,
    /// or one moved by a time (a moment less a moment is a time, no moment).</summary>
    private static bool IsMoment(ExpressionSyntax expression, HashSet<string> moments) => expression switch
    {
        ParenthesizedExpressionSyntax inner => IsMoment(inner.Expression, moments),
        _ when IsClock(expression) => true,
        BinaryExpressionSyntax added when added.IsKind(SyntaxKind.AddExpression) || added.IsKind(SyntaxKind.CoalesceExpression) =>
            IsMoment(added.Left, moments) || IsMoment(added.Right, moments),
        BinaryExpressionSyntax taken when taken.IsKind(SyntaxKind.SubtractExpression) => IsMoment(taken.Left, moments) && !IsMoment(taken.Right, moments),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: { } method } moved } when method.StartsWith("Add", StringComparison.Ordinal) =>
            IsMoment(moved.Expression, moments),
        ConditionalExpressionSyntax either => IsMoment(either.WhenTrue, moments) || IsMoment(either.WhenFalse, moments),
        _ => NameOf(expression) is { } name && moments.Contains(name),
    };

    /// <summary>The name an expression reads, a field's through <c>this</c> too; null for anything else.</summary>
    private static string? NameOf(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: { } member } => member.Identifier.ValueText,
        _ => null,
    };
}
