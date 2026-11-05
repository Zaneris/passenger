using System.Text;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>One script's edits a refactoring makes, by its res:// path.</summary>
public sealed record GDScriptEdits(string Script, IReadOnlyList<TextEdit> Edits);

/// <summary>A place in a script, or beside the scripts (a scene's connection, a C# string), and what is there.</summary>
public readonly record struct GDScriptPlace(string Script, int Start, int End, string What);

/// <summary>What a refactoring made: each script's edits; or why it made none, with the places that stopped it.</summary>
public sealed record GDScriptRefactored(IReadOnlyList<GDScriptEdits> Scripts, string? Refused, IReadOnlyList<GDScriptPlace> Places)
{
    /// <summary>Whether it was refused only for the places it names, uses Passenger can't see, which the change made anyway
    /// leaves as they are.</summary>
    public bool Unseen { get; init; }

    public static GDScriptRefactored No(string why, IReadOnlyList<GDScriptPlace>? places = null) => new([], why, places ?? []);

    public static GDScriptRefactored Of(string script, IReadOnlyList<TextEdit> edits) => new([new GDScriptEdits(script, [.. edits.OrderBy(e => e.Start)])], null, []);
}

/// <summary>A script of the project as a refactoring reads it: its analysis over the text it was made of.</summary>
public sealed record GDScriptRead(string Script, GDScriptAnalysis Analysis)
{
    public GDScriptTree Tree => Analysis.Tree!;

    public string Text => Tree.Text;
}

/// <summary>A parameter of a function as Change Signature leaves it: the old one at <paramref name="From"/> (-1 for a new
/// one), its name, type and default as written (empty for none), and what each call passes a new one.</summary>
public sealed record GDScriptParameterChange(int From, string Name, string Type, string Default, string CallValue);

/// <summary>GDScript's refactorings over Passenger's reading of the scripts (<see cref="GDScriptAnalyzer"/>), each the edits of
/// the scripts it changes, or refused with why and the places where it cannot tell what a change would do: Extract Variable
/// and Extract Function, Inline Variable and Inline Function, Change Signature, Safe Delete, Introduce Parameter, Move and
/// Rename, and a name's places for Find Usages. A
/// member's uses are the names the analyzer resolves to it in every script; a call by a string (<c>call("f")</c>) on an
/// object that may run its class is one too, and one whose name is built at run time, or a call on a value of a type it
/// cannot read, refuses. Pure.</summary>
public static partial class GDScriptRefactorings
{
    /// <summary>The functions that call or take a method by its name as a string, and which of their arguments names it.</summary>
    private static readonly Dictionary<string, int> NamingCalls = new(StringComparer.Ordinal)
    {
        ["call"] = 0, ["call_deferred"] = 0, ["callv"] = 0, ["has_method"] = 0, ["rpc"] = 0, ["rpc_id"] = 1, ["Callable"] = 1,
        ["get_method_argument_count"] = 0,
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "elif", "else", "for", "while", "break", "continue", "pass", "return", "match", "when", "and", "or", "not", "in", "is", "as", "self",
        "super", "signal", "func", "static", "const", "enum", "var", "breakpoint", "preload", "await", "yield", "assert", "void", "class", "class_name",
        "extends", "true", "false", "null", "PI", "TAU", "INF", "NAN", "namespace", "trait",
    };

    /// <summary>Why a name cannot be one GDScript declares; null where it can.</summary>
    /// <summary>Why no change across these scripts can be made: one is read only in part, so a use after where reading stopped
    /// would go unseen; null where each is read whole.</summary>
    public static string? WhyNotChanged(IEnumerable<GDScriptRead> scripts) =>
        scripts.FirstOrDefault(s => s.Tree.Stopped is not null) is { } unread ? $"{Path.GetFileName(unread.Script)} is nested too deeply for Passenger to read whole, so a use of what this changes could go unseen" : null;

    public static string? WhyNotName(string name) =>
        name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_') ? $"'{name}' is not a GDScript name"
        : Keywords.Contains(name) ? $"'{name}' is a GDScript keyword"
        : null;

    /// <summary>A function of a script (its own, or an inner class's) and the offset its positions count from.</summary>
    private readonly record struct Located(GDScriptFunction Function, int Offset);

    /// <summary>The function an offset lies in, a lambda's own aside.</summary>
    private static Located? FunctionAt(GDScriptTree tree, int offset)
    {
        foreach (GDScriptMember member in tree.Members)
        {
            if (offset < member.Start || offset > member.End)
            {
                continue;
            }
            switch (member.Node)
            {
                case GDScriptFunction function:
                    return new Located(function, member.Offset);
                case GDScriptClass inner:
                    foreach (GDScriptFunction function in inner.Members.OfType<GDScriptFunction>())
                    {
                        if (offset >= member.Offset + function.Start && offset <= member.Offset + function.End)
                        {
                            return new Located(function, member.Offset);
                        }
                    }
                    break;
            }
        }
        return null;
    }

    /// <summary>Each node under a root with the node it sits in.</summary>
    private static Dictionary<GDScriptNode, GDScriptNode?> Parents(GDScriptNode root)
    {
        Dictionary<GDScriptNode, GDScriptNode?> parents = [];
        Stack<(GDScriptNode Node, GDScriptNode? Parent)> left = new([(root, null)]);
        while (left.Count > 0)
        {
            (GDScriptNode node, GDScriptNode? parent) = left.Pop();
            parents[node] = parent;
            foreach (GDScriptNode child in node.Children)
            {
                left.Push((child, node));
            }
        }
        return parents;
    }

    internal static IEnumerable<GDScriptNode> Below(GDScriptNode root)
    {
        Stack<GDScriptNode> left = new([root]);
        while (left.Count > 0)
        {
            GDScriptNode node = left.Pop();
            yield return node;
            foreach (GDScriptNode child in node.Children)
            {
                left.Push(child);
            }
        }
    }

    /// <summary>The expression lying exactly over a range of the text (written in parentheses or not), the outermost such.</summary>
    private static GDScriptExpression? ExpressionOver(GDScriptNode root, int offset, string text, TextRange range)
    {
        (int start, int end) = Trimmed(text, range.Start, range.End);
        while (end - start >= 2 && text[start] == '(' && text[end - 1] == ')')
        {
            (start, end) = Trimmed(text, start + 1, end - 1);
        }
        GDScriptExpression? found = null;
        foreach (GDScriptNode node in Below(root))
        {
            if (node is GDScriptExpression expression && offset + node.Start == start && offset + node.End == end
                && (found is null || node.End - node.Start >= found.End - found.Start))
            {
                found = expression;
            }
        }
        return found;
    }

    private static (int Start, int End) Trimmed(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        return (start, end);
    }

    private static int LineStart(string text, int offset) => offset <= 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    /// <summary>Where the line after an offset's starts (the text's end for the last line).</summary>
    private static int NextLine(string text, int offset)
    {
        int newline = text.IndexOf('\n', offset);
        return newline < 0 ? text.Length : newline + 1;
    }

    private static string IndentOf(string text, int lineStart)
    {
        int at = lineStart;
        while (at < text.Length && text[at] is ' ' or '\t')
        {
            at++;
        }
        return text[lineStart..at];
    }

    /// <summary>The script's indentation unit, as its first indented line shows it: a tab unless spaces.</summary>
    private static string Unit(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.Length > 0 && line[0] == '\t')
            {
                return "\t";
            }
            if (line.Length > 0 && line[0] == ' ' && line.TrimStart().Length > 0)
            {
                return new string(' ', line.Length - line.TrimStart(' ').Length);
            }
        }
        return "\t";
    }

    /// <summary>Whether an expression must be put in parentheses where it takes another's place inside a larger one.</summary>
    private static bool Compound(GDScriptExpression expression) =>
        expression is GDScriptBinary or GDScriptTernary or GDScriptUnary or GDScriptCast or GDScriptTypeTest or GDScriptAwait or GDScriptAssignment or GDScriptLambda;

    /// <summary>Whether a place an expression goes stands alone, so a compound one needs no parentheses there: a statement,
    /// an argument, an element, a value assigned, returned or given a variable.</summary>
    private static bool StandsAlone(GDScriptNode place, GDScriptNode? parent) => parent switch
    {
        null or GDScriptSuite or GDScriptArray or GDScriptDictionary or GDScriptAssignable or GDScriptReturn => true,
        GDScriptCall call => call.Callee != place,
        GDScriptAssignment assignment => assignment.Value == place,
        _ => false,
    };

    private static string Placed(string replacement, GDScriptExpression replacing, bool compound, Dictionary<GDScriptNode, GDScriptNode?> parents) =>
        compound && !StandsAlone(replacing, parents.GetValueOrDefault(replacing)) ? $"({replacement})" : replacement;

    /// <summary>The statement of a block an expression lies in, the block, and the statement's place in it.</summary>
    private static (GDScriptNode Statement, GDScriptSuite Suite)? StatementOf(GDScriptNode node, Dictionary<GDScriptNode, GDScriptNode?> parents)
    {
        GDScriptNode at = node;
        while (parents.GetValueOrDefault(at) is { } parent)
        {
            if (parent is GDScriptSuite suite)
            {
                return (at, suite);
            }
            at = parent;
        }
        return null;
    }

    /// <summary>Whether a node lies where it may not run every time the statement does, or runs more than once: a
    /// condition's right side, a conditional's branch, a loop's condition, a lambda.</summary>
    private static string? Conditional(GDScriptNode node, GDScriptNode statement, Dictionary<GDScriptNode, GDScriptNode?> parents)
    {
        GDScriptNode at = node;
        while (at != statement && parents.GetValueOrDefault(at) is { } parent)
        {
            switch (parent)
            {
                case GDScriptBinary { Operator: GDScriptTokenKind.And or GDScriptTokenKind.Or or GDScriptTokenKind.AmpersandAmpersand or GDScriptTokenKind.PipePipe } binary when binary.Right == at:
                    return "it runs only as the condition before it says";
                case GDScriptTernary ternary when ternary.Condition != at:
                    return "it runs only as the conditional's condition says";
                case GDScriptLambda:
                    return "it lies in a lambda";
                case GDScriptWhile loop when loop.Condition == at:
                    return "the loop's condition runs every time round";
                case GDScriptMatchBranch:
                    return "it lies in a match pattern";
            }
            at = parent;
        }
        return null;
    }

    /// <summary>Whether a block of a function declares, or has around it, a local or parameter of a name before an offset;
    /// or the script declares a member so named.</summary>
    private static string? Taken(GDScriptAnalysis analysis, int offset, string name)
    {
        if (analysis.LocalsAt(offset).Any(l => l.Name == name))
        {
            return $"'{name}' is already a local there";
        }
        if (analysis.Tree?.Members.Any(m => NameOf(m.Node) == name) == true)
        {
            return $"the script already has a member '{name}'";
        }
        return null;
    }

    private static string? NameOf(GDScriptNode node) => node switch
    {
        GDScriptFunction function => function.Identifier?.Name,
        GDScriptAssignable assignable => assignable.Identifier?.Name,
        GDScriptSignal signal => signal.Identifier?.Name,
        GDScriptEnum enumerated => enumerated.Identifier?.Name,
        GDScriptClass inner => inner.Identifier?.Name,
        _ => null,
    };

    /// <summary>Extract Variable: the expression selected given a local of a name, declared before the statement it is in
    /// (<c>:=</c> where its type is known), the expression replaced by the name; refused where it can't run ahead of what
    /// runs before it in the statement.</summary>
    public static GDScriptRefactored ExtractVariable(GDScriptAnalysis analysis, TextRange selection, string name)
    {
        if (analysis.Tree is not { } tree)
        {
            return GDScriptRefactored.No("the script does not read");
        }
        if (WhyNotName(name) is { } badName)
        {
            return GDScriptRefactored.No(badName);
        }
        string text = tree.Text;
        if (FunctionAt(tree, selection.Start) is not { Function.Body: { } body } located || located.Function.IsLambda)
        {
            return GDScriptRefactored.No("select an expression in a function's body");
        }
        if (ExpressionOver(body, located.Offset, text, selection) is not { } expression)
        {
            return GDScriptRefactored.No("select a whole expression");
        }
        Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(body);
        if (StatementOf(expression, parents) is not ({ } statement, _) || statement == expression && expression is not GDScriptCall)
        {
            return GDScriptRefactored.No("select an expression inside a statement");
        }
        if (parents.GetValueOrDefault(expression) is GDScriptAssignment { Assignee: var target } && target == expression)
        {
            return GDScriptRefactored.No("what is assigned to can't become a variable");
        }
        if (Conditional(expression, statement, parents) is { } runs)
        {
            return GDScriptRefactored.No($"the expression can't move before its statement: {runs}");
        }
        GDScriptEffects effects = analysis.Effects;
        if (effects.RunsFirst(expression, statement, effects.Of(expression), effects.MayBeLate(expression)) is { } first)
        {
            string earlier = text[(located.Offset + first.Start)..(located.Offset + first.End)];
            return GDScriptRefactored.No($"the expression can't move before its statement: '{earlier}' runs first",
                [new GDScriptPlace(analysis.Path, located.Offset + first.Start, located.Offset + first.End, "runs first")]);
        }
        if (Taken(analysis, located.Offset + statement.Start, name) is { } taken)
        {
            return GDScriptRefactored.No(taken);
        }
        int lineStart = LineStart(text, located.Offset + statement.Start);
        string written = text[(located.Offset + expression.Start)..(located.Offset + expression.End)];
        GDScriptType type = analysis.TypeOf(expression);
        string declared = $"{IndentOf(text, lineStart)}var {name} {(type.IsVariant || type.IsVoid ? "=" : ":=")} {written}\n";
        return GDScriptRefactored.Of(analysis.Path,
        [
            new TextEdit(lineStart, 0, declared),
            new TextEdit(located.Offset + expression.Start, expression.End - expression.Start, name),
        ]);
    }

    /// <summary>Where a use of a local stands that its value, read there, would run other than once right after its
    /// declaration: "a loop" (its body, or a while's condition), "a branch" (an if's or a match's block, a conditional's
    /// branch, the right of and/or) or "a lambda"; null where it runs as the declaration did.</summary>
    private static string? Unsettled(GDScriptNode use, GDScriptNode declaration, Dictionary<GDScriptNode, GDScriptNode?> parents)
    {
        GDScriptNode? stop = parents.GetValueOrDefault(declaration);
        GDScriptNode below = use;
        for (GDScriptNode? at = parents.GetValueOrDefault(use); at is not null && at != stop; below = at, at = parents.GetValueOrDefault(at))
        {
            string? why = at switch
            {
                GDScriptLambda => "a lambda",
                GDScriptFor { Loop: { } loop } when loop == below => "a loop",
                GDScriptWhile => "a loop",
                GDScriptTernary { Condition: { } condition } when condition != below => "a branch",
                GDScriptBinary { Operator: GDScriptTokenKind.And or GDScriptTokenKind.Or or GDScriptTokenKind.AmpersandAmpersand or GDScriptTokenKind.PipePipe, Right: { } right }
                    when right == below => "a branch",
                GDScriptSuite when parents.GetValueOrDefault(at) is GDScriptFunction => "a lambda",
                GDScriptSuite when parents.GetValueOrDefault(at) is not GDScriptFor and not GDScriptWhile => "a branch",
                _ => null,
            };
            if (why is not null)
            {
                return why;
            }
        }
        return null;
    }

    /// <summary>Inline Variable: a local's value put where each use reads it, its declaration taken out; refused where it is
    /// assigned again, where what its value reads changes before a use, where a value that calls would run more often, and
    /// where a value read anew would run in a loop, under a branch or after a call it did not run after.</summary>
    public static GDScriptRefactored InlineVariable(GDScriptAnalysis analysis, int offset)
    {
        if (analysis.Tree is not { } tree || FunctionAt(tree, offset) is not { Function.Body: { } body } located)
        {
            return GDScriptRefactored.No("put the caret on a local variable");
        }
        string text = tree.Text;
        GDScriptIdentifier? at = Below(body).OfType<GDScriptIdentifier>().FirstOrDefault(i => offset >= located.Offset + i.Start && offset <= located.Offset + i.End);
        if (at is null || analysis.DeclarationOf(at) is not { } declaration || declaration.Script != analysis.Path)
        {
            return GDScriptRefactored.No("put the caret on a local variable");
        }
        Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(body);
        GDScriptVariable? variable = Below(body).OfType<GDScriptVariable>().FirstOrDefault(v => v.Identifier is { } n && located.Offset + n.Start == declaration.Start);
        if (variable is not { Identifier: { } own, Initializer: { } value } || StatementOf(variable, parents) is not ({ } statement, _) || statement != variable)
        {
            return GDScriptRefactored.No(variable is null ? "only a local variable inlines" : $"'{declaration.Name}' has no value to inline");
        }
        List<GDScriptIdentifier> uses = [.. Below(body).OfType<GDScriptIdentifier>().Where(i => i != own && analysis.DeclarationOf(i) is { } d && d.Script == declaration.Script && d.Start == declaration.Start)
            .OrderBy(i => i.Start)];
        foreach (GDScriptIdentifier use in uses)
        {
            if (parents.GetValueOrDefault(use) is GDScriptAssignment assignment && assignment.Assignee == use)
            {
                return GDScriptRefactored.No($"'{own.Name}' is assigned again", [new GDScriptPlace(analysis.Path, located.Offset + use.Start, located.Offset + use.End, "assigned")]);
            }
        }
        // What the value reads must stand as it was until the last use.
        HashSet<(string?, int)> read = [.. Below(value).OfType<GDScriptIdentifier>().Select(analysis.DeclarationOf).OfType<GDScriptDeclaration>().Select(d => (d.Script, d.Start))];
        int last = uses.Count > 0 ? uses[^1].End : variable.End;
        foreach (GDScriptAssignment assignment in Below(body).OfType<GDScriptAssignment>().Where(a => a.Start > variable.End && a.Start < last))
        {
            if (assignment.Assignee is GDScriptIdentifier target && analysis.DeclarationOf(target) is { } changed && read.Contains((changed.Script, changed.Start)))
            {
                return GDScriptRefactored.No($"'{changed.Name}', which its value reads, is assigned before a use",
                    [new GDScriptPlace(analysis.Path, located.Offset + assignment.Start, located.Offset + assignment.End, "assigned")]);
            }
        }
        bool calls = Below(value).Any(n => n is GDScriptCall or GDScriptAwait);
        if (calls && uses.Count != 1)
        {
            return GDScriptRefactored.No(uses.Count == 0 ? $"'{own.Name}' is never read, and its value calls" : $"its value calls, and would be called {uses.Count} times");
        }
        // A value read anew may give another answer: each time in a loop, sometimes under a branch, changed by a call before.
        bool steady = Below(value).OfType<GDScriptIdentifier>().All(i => analysis.TypeOf(i).Meta
            || analysis.DeclarationOf(i) is { } d && d.Script == analysis.Path && d.Start >= located.Offset + located.Function.Start && d.Start <= located.Offset + located.Function.End);
        foreach (GDScriptIdentifier use in uses)
        {
            if (Unsettled(use, variable, parents) is { } why && (calls || !steady || why == "a loop"))
            {
                return GDScriptRefactored.No($"its value would be read anew in {why}, where it may give another answer",
                    [new GDScriptPlace(analysis.Path, located.Offset + use.Start, located.Offset + use.End, why)]);
            }
            if ((calls || !steady) && Below(body).FirstOrDefault(n => n is GDScriptCall or GDScriptAwait && n.Start > variable.End && n.Start < use.Start && n.End < use.End) is { } between)
            {
                return GDScriptRefactored.No("its value would be read after a call that may change what it reads",
                    [new GDScriptPlace(analysis.Path, located.Offset + between.Start, located.Offset + between.End, "called first")]);
            }
        }
        string written = text[(located.Offset + value.Start)..(located.Offset + value.End)];
        List<TextEdit> edits = [.. uses.Select(u => new TextEdit(located.Offset + u.Start, u.End - u.Start, Placed(written, u, Compound(value), parents)))];
        int start = LineStart(text, located.Offset + variable.Start);
        edits.Add(new TextEdit(start, NextLine(text, located.Offset + variable.End) - start, string.Empty));
        return GDScriptRefactored.Of(analysis.Path, edits);
    }

    /// <summary>Introduce Parameter: the expression selected made a parameter of its function with it as its default, after
    /// the others, so every call passes what it did; refused for an expression that reads a local, a variable or what a
    /// call gives, which a default would read at another time.</summary>
    public static GDScriptRefactored IntroduceParameter(GDScriptAnalysis analysis, TextRange selection, string name)
    {
        if (analysis.Tree is not { } tree)
        {
            return GDScriptRefactored.No("the script does not read");
        }
        if (WhyNotName(name) is { } badName)
        {
            return GDScriptRefactored.No(badName);
        }
        string text = tree.Text;
        if (FunctionAt(tree, selection.Start) is not { Function: { Body: { } body, ParametersEnd: > 0 } function } located || function.IsLambda)
        {
            return GDScriptRefactored.No("select an expression in a function's body");
        }
        if (ExpressionOver(body, located.Offset, text, selection) is not { } expression)
        {
            return GDScriptRefactored.No("select a whole expression");
        }
        if (function.Rest is not null)
        {
            return GDScriptRefactored.No("the function takes the rest of its arguments, after which no parameter goes");
        }
        foreach (GDScriptNode node in Below(expression))
        {
            if (node is GDScriptCall { Callee: not GDScriptIdentifier { Name: [>= 'A' and <= 'Z', ..] } } or GDScriptAwait or GDScriptSelf or GDScriptGetNode or GDScriptLambda)
            {
                return GDScriptRefactored.No("its value would be read as the function is called: choose an expression of constants and literals");
            }
            if (node is GDScriptIdentifier identifier && analysis.DeclarationOf(identifier) is { } read && read.Script == analysis.Path
                && Below(tree.Members.First(m => m.Offset == located.Offset).Node).OfType<GDScriptAssignable>().FirstOrDefault(a => a.Identifier is { } n && located.Offset + n.Start == read.Start) is
                    GDScriptVariable or GDScriptParameter)
            {
                return GDScriptRefactored.No($"it reads '{identifier.Name}', which a default can't");
            }
        }
        if (function.Parameters.Any(p => p.Identifier?.Name == name) || Taken(analysis, located.Offset + expression.Start, name) is { } taken && !taken.StartsWith("the script", StringComparison.Ordinal))
        {
            return GDScriptRefactored.No($"'{name}' is already a parameter or local there");
        }
        GDScriptType type = analysis.TypeOf(expression);
        string written = text[(located.Offset + expression.Start)..(located.Offset + expression.End)];
        string parameter = $"{name} {(type.IsVariant || type.IsVoid ? "=" : ":=")} {written}";
        int insertAt = located.Offset + function.ParametersEnd - 1;
        string inserted = function.Parameters.Count == 0 ? parameter : $", {parameter}";
        return GDScriptRefactored.Of(analysis.Path,
        [
            new TextEdit(insertAt, 0, inserted),
            new TextEdit(located.Offset + expression.Start, expression.End - expression.Start, name),
        ]);
    }

    /// <summary>Extract Function: the whole statements selected moved into a new function after theirs, called where they
    /// were with the locals they read before them as arguments, the one local they set that is read after them returned
    /// and assigned; refused where they return, leave a loop around them, call <c>super()</c>, or set more than one.</summary>
    public static GDScriptRefactored ExtractFunction(GDScriptAnalysis analysis, TextRange selection, string name)
    {
        if (analysis.Tree is not { } tree)
        {
            return GDScriptRefactored.No("the script does not read");
        }
        if (WhyNotName(name) is { } badName)
        {
            return GDScriptRefactored.No(badName);
        }
        string text = tree.Text;
        if (FunctionAt(tree, selection.Start) is not { Function: { Body: { } body } function } located || function.IsLambda)
        {
            return GDScriptRefactored.No("select statements in a function's body");
        }
        if (tree.Members.Any(m => NameOf(m.Node) == name))
        {
            return GDScriptRefactored.No($"the script already has a member '{name}'");
        }
        int offset = located.Offset;
        (int from, int to) = Trimmed(text, selection.Start, selection.End);
        Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(body);
        GDScriptSuite? suite = Below(body).OfType<GDScriptSuite>()
            .Where(s => s.Statements.Any(st => offset + st.Start >= from && offset + st.End <= to))
            .OrderBy(s => s.End - s.Start).FirstOrDefault();
        if (suite is null)
        {
            return GDScriptRefactored.No("select whole statements");
        }
        List<GDScriptNode> chosen = [.. suite.Statements.Where(st => offset + st.Start < to && offset + st.End > from)];
        if (chosen.Count == 0 || offset + chosen[0].Start < from || offset + chosen[^1].End > to)
        {
            return GDScriptRefactored.No("select whole statements");
        }
        int firstStart = offset + chosen[0].Start;
        int lastEnd = offset + chosen[^1].End;
        foreach (GDScriptNode node in chosen.SelectMany(Below))
        {
            switch (node)
            {
                case GDScriptReturn:
                    return GDScriptRefactored.No("the statements return", [new GDScriptPlace(analysis.Path, offset + node.Start, offset + node.End, "return")]);
                case GDScriptBreak or GDScriptContinue when !InsideLoop(node, chosen, parents):
                    return GDScriptRefactored.No("the statements leave a loop around them", [new GDScriptPlace(analysis.Path, offset + node.Start, offset + node.End, "loop")]);
                case GDScriptCall { IsSuper: true, Callee: null }:
                    return GDScriptRefactored.No("the statements call super()");
            }
        }
        // The locals and parameters declared before the statements that they read, in order of first reading.
        List<(string Name, GDScriptDeclaration Declaration)> inputs = [];
        HashSet<int> inside = [];
        foreach (GDScriptNode node in chosen.SelectMany(Below))
        {
            if (node is GDScriptAssignable { Identifier: { } declaring })
            {
                inside.Add(offset + declaring.Start);
            }
            if (node is GDScriptFor { Variable: { } iterator })
            {
                inside.Add(offset + iterator.Start);
            }
        }
        List<GDScriptIdentifier> reads = [.. chosen.SelectMany(Below).OfType<GDScriptIdentifier>().OrderBy(i => i.Start)];
        HashSet<int> locals = [.. Below(function).OfType<GDScriptAssignable>().Where(a => a is GDScriptVariable or GDScriptConstant or GDScriptParameter).Select(a => a.Identifier is { } n ? offset + n.Start : -1),
            .. Below(function).OfType<GDScriptFor>().Select(f => f.Variable is { } v ? offset + v.Start : -1),
            .. Below(function).OfType<GDScriptPattern>().Select(p => p.Bind is { } b ? offset + b.Start : -1)];
        locals.Remove(-1);
        foreach (GDScriptIdentifier read in reads)
        {
            if (analysis.DeclarationOf(read) is { } declared && declared.Script == analysis.Path && locals.Contains(declared.Start) && !inside.Contains(declared.Start)
                && declared.Start < firstStart && !inputs.Any(i => i.Declaration.Start == declared.Start))
            {
                inputs.Add((declared.Name, declared));
            }
        }
        // What they set that is read after them: a local of their own, or one from before they assign.
        List<GDScriptDeclaration> outputs = [];
        foreach (GDScriptIdentifier later in Below(body).OfType<GDScriptIdentifier>().Where(i => offset + i.Start > lastEnd))
        {
            if (analysis.DeclarationOf(later) is not { } declared || declared.Script != analysis.Path || !locals.Contains(declared.Start) || outputs.Any(o => o.Start == declared.Start))
            {
                continue;
            }
            bool assignedInside = reads.Any(r => parents.GetValueOrDefault(r) is GDScriptAssignment a && a.Assignee == r && analysis.DeclarationOf(r)?.Start == declared.Start);
            if (inside.Contains(declared.Start) || assignedInside)
            {
                outputs.Add(declared);
            }
        }
        if (outputs.Count > 1)
        {
            return GDScriptRefactored.No($"the statements set {Plural.Listed([.. outputs.Select(o => $"'{o.Name}'")])}, which are read after them: one function returns one value");
        }
        bool awaits = chosen.SelectMany(Below).Any(n => n is GDScriptAwait);
        string unit = Unit(text);
        int lineFrom = LineStart(text, firstStart);
        int lineTo = NextLine(text, lastEnd);
        string indent = IndentOf(text, lineFrom);
        string functionIndent = IndentOf(text, LineStart(text, offset + function.Start));
        StringBuilder made = new();
        foreach (string line in text[lineFrom..lineTo].TrimEnd('\n').Split('\n'))
        {
            made.Append(functionIndent).Append(unit).Append(line.StartsWith(indent, StringComparison.Ordinal) ? line[indent.Length..] : line.TrimStart()).Append('\n');
        }
        GDScriptDeclaration? output = outputs.Count == 1 ? outputs[0] : null;
        if (output is not null)
        {
            made.Append(functionIndent).Append(unit).Append("return ").Append(output.Name).Append('\n');
        }
        string Typed(GDScriptType type) => type.Hard && !type.IsVariant && !type.IsVoid ? $": {type}" : string.Empty;
        string parameters = string.Join(", ", inputs.Select(i => $"{i.Name}{Typed(i.Declaration.Type)}"));
        // The type it returns as the analyzer reads the local's, written or inferred from its value.
        string returns = output is null ? " -> void" : !output.Type.IsVariant && !output.Type.IsVoid ? $" -> {output.Type with { Hard = true }}" : string.Empty;
        string header = $"{functionIndent}{(function.IsStatic ? "static " : string.Empty)}func {name}({parameters}){returns}:\n";
        string call = $"{(awaits ? "await " : string.Empty)}{name}({string.Join(", ", inputs.Select(i => i.Name))})";
        string callLine = output is null ? call : inside.Contains(output.Start) ? $"var {output.Name} = {call}" : $"{output.Name} = {call}";
        int functionEnd = NextLine(text, offset + function.End);
        string gap = functionIndent.Length > 0 ? "\n" : "\n\n";
        string after = functionEnd < text.Length || text.EndsWith('\n') ? string.Empty : "\n";
        return GDScriptRefactored.Of(analysis.Path,
        [
            new TextEdit(lineFrom, lineTo - lineFrom, $"{indent}{callLine}\n"),
            new TextEdit(functionEnd, 0, $"{after}{gap}{header}{made}"),
        ]);
    }

    /// <summary>Whether a break or continue's loop lies inside the statements chosen.</summary>
    private static bool InsideLoop(GDScriptNode node, List<GDScriptNode> chosen, Dictionary<GDScriptNode, GDScriptNode?> parents)
    {
        for (GDScriptNode? at = parents.GetValueOrDefault(node); at is not null; at = parents.GetValueOrDefault(at))
        {
            if (at is GDScriptFor or GDScriptWhile)
            {
                return chosen.Any(c => Below(c).Contains(at));
            }
        }
        return false;
    }
}
