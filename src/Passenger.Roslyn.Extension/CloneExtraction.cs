using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>A group of clones made to share one method: Extract Method over the first clone, its parameters the variables
/// whose values flow in and the literals the clones write otherwise, its result the one variable whose value flows out to
/// the code after any of them; then every clone, the first too, replaced by a call of it with its own variables and
/// literals. Refused, saying why, where the clones differ in what one method can't take.</summary>
public sealed class ExtractClones : IExtensionWorkspaceMessageHandler<ClonesExtraction, Refactored>
{
    public Task<Refactored> ExecuteAsync(ClonesExtraction message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken) =>
        CloneExtraction.ExtractAsync(context.Solution, message, cancellationToken);
}

internal static class CloneExtraction
{
    /// <summary>A clone read for its extraction: its statements, their tokens, what flows through them, and the member and
    /// type holding them.</summary>
    private sealed record Region(Document Document, SemanticModel Model, List<StatementSyntax> Statements, SyntaxToken[] Tokens, DataFlowAnalysis Flow,
        MemberDeclarationSyntax? Member, INamedTypeSymbol? Type, int Line)
    {
        public TextSpan Span => TextSpan.FromBounds(Statements[0].SpanStart, Statements[^1].Span.End);

        /// <summary>The clone's file and first line, as a refusal names it.</summary>
        public string Where => $"{Path.GetFileName(Document.FilePath)}:{Line}";

        public bool Declares(ISymbol symbol) => symbol.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == Statements[0].SyntaxTree && Span.Contains(r.Span));

        /// <summary>Whether a symbol is a variable of the member holding the clone: a local, or a parameter of it or of a
        /// lambda or local function in it, never a parameter a call names (<c>Add(count: 1)</c>) or the type's own.</summary>
        public bool HasVariable(ISymbol symbol) => symbol is ILocalSymbol || symbol is IParameterSymbol { IsThis: false } parameter && Member is { } member
            && parameter.ContainingSymbol.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == Statements[0].SyntaxTree && member.Span.Contains(r.Span));
    }

    /// <summary>A parameter of the method made: its name, its type as written, and the argument each clone gives it.</summary>
    private sealed record Parameter(string Name, string Type, string[] Arguments);

    public static async Task<Refactored> ExtractAsync(Solution solution, ClonesExtraction asked, CancellationToken cancel)
    {
        if (asked.Clones.Count < 2)
        {
            return Changes.Refused("A group of clones has two or more");
        }
        List<Region> regions = [];
        foreach (CloneRun run in asked.Clones)
        {
            if (await RegionAsync(solution, run, cancel).ConfigureAwait(false) is not { } region)
            {
                return Changes.Refused($"{Path.GetFileName(run.Path)}:{run.Start.Line + 1} no longer holds the clone's statements: find the clones again");
            }
            regions.Add(region);
        }
        try
        {
            return await ExtractAsync(solution, regions, asked.Name, asked.Apply, cancel).ConfigureAwait(false);
        }
        catch (RefusedException refused)
        {
            return Changes.Refused(refused.Message);
        }
    }

    private static async Task<Region?> RegionAsync(Solution solution, CloneRun run, CancellationToken cancel)
    {
        if (DocumentIdsOf(solution, run.Path).Select(solution.GetDocument).OfType<Document>().FirstOrDefault() is not { } document
            || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
            || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return null;
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        TextSpan span = TextSpan.FromBounds(WrittenPlaces.Offset(text, run.Start), WrittenPlaces.Offset(text, run.End));
        List<StatementSyntax> statements = CodeClones.StatementRun(root, span, chosen: false);
        if (statements.Count == 0)
        {
            return null;
        }
        MemberDeclarationSyntax? member = statements[0].Ancestors().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax and not GlobalStatementSyntax);
        INamedTypeSymbol? type = member?.Parent is TypeDeclarationSyntax holder ? model.GetDeclaredSymbol(holder, cancel) : null;
        if (model.AnalyzeDataFlow(statements[0], statements[^1]) is not { } flow)
        {
            return null;
        }
        return new Region(document, model, statements, [.. statements.SelectMany(s => s.DescendantTokens()).Where(t => t.Span.Length > 0)],
            flow, member, type, text.Lines.GetLineFromPosition(statements[0].SpanStart).LineNumber + 1);
    }

    private static async Task<Refactored> ExtractAsync(Solution solution, List<Region> regions, string name, bool apply, CancellationToken cancel)
    {
        Region first = regions[0];
        if (regions.Any(r => r.Tokens.Length != first.Tokens.Length || r.Tokens.Where((t, i) => CodeClones.CodesOf(t).Renamed != CodeClones.CodesOf(first.Tokens[i]).Renamed).Any()))
        {
            throw new RefusedException("The clones are no longer alike: find the clones again");
        }
        if (regions.FirstOrDefault(r => r.Type is null || r.Member is null) is { } loose)
        {
            throw new RefusedException($"The clone at {loose.Where} is in no type's member, where no method can be added or called from");
        }
        if (regions.FirstOrDefault(r => !SamePath(r.Document.Project.FilePath, first.Document.Project.FilePath)) is { } other)
        {
            throw new RefusedException($"The clones are in two projects, {first.Document.Project.Name} and {other.Document.Project.Name}: one method can't serve both");
        }
        IMethodSymbol? function = CheckFlow(regions, cancel);
        Dictionary<ISymbol, ISymbol>[] maps = [.. regions.Select(_ => new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default))];
        List<int> literals = [];
        Match(regions, maps, literals, cancel);
        HashSet<ISymbol> outer = new(maps[0].Keys.Where(s => !first.Declares(s)), SymbolEqualityComparer.Default);
        List<ISymbol> flowsIn = [.. first.Flow.DataFlowsIn.Where(outer.Contains).OrderBy(s => FirstUse(first, s))];
        ISymbol? returned = Returned(regions, maps);
        if (function is not null && returned is not null)
        {
            throw new RefusedException($"The clones return from their method and set {returned.Name}, which it passes out too; one method returns one");
        }
        List<ISymbol> declared = [.. first.Flow.WrittenInside.Where(s => outer.Contains(s) && !flowsIn.Contains(s, SymbolEqualityComparer.Default)).OrderBy(s => FirstUse(first, s))];
        if (first.Flow.CapturedInside.FirstOrDefault(s => outer.Contains(s) && first.Flow.WrittenInside.Contains(s)) is { } captured)
        {
            throw new RefusedException($"A lambda in the clones writes {captured.Name}, which a method would take as a copy");
        }
        bool sameType = regions.All(r => SymbolEqualityComparer.Default.Equals(r.Type, first.Type));
        bool instance = UsesInstance(first, cancel) && !first.Member!.Modifiers.Any(SyntaxKind.StaticKeyword);
        if (instance && regions.FirstOrDefault(r => r.Member!.Modifiers.Any(SyntaxKind.StaticKeyword)) is { } still)
        {
            throw new RefusedException($"The clones use {first.Type!.Name}'s own members, which the clone at {still.Where}, in a static member, can't reach");
        }
        if (!sameType)
        {
            Region apart = regions.First(r => !SymbolEqualityComparer.Default.Equals(r.Type, first.Type));
            if (instance)
            {
                throw new RefusedException($"The clones use {first.Type!.Name}'s own members, which the clone in {apart.Type!.Name} can't reach through one method");
            }
            if (first.Type!.IsGenericType || first.Type.ContainingType is { IsGenericType: true })
            {
                throw new RefusedException($"The clones are in two types, and {first.Type.Name} is generic: a method of it can't be called from {apart.Type!.Name} without its type arguments");
            }
            if (regions.FirstOrDefault(r => !r.Model.IsAccessible(r.Span.Start, first.Type)) is { } unreached)
            {
                throw new RefusedException($"{first.Type.Name} can't be reached from the clone in {unreached.Type!.Name}");
            }
        }
        bool awaits = first.Statements.Any(s => Inside(s).Any(n => n is AwaitExpressionSyntax
            || n is CommonForEachStatementSyntax { AwaitKeyword.RawKind: not 0 } || n is UsingStatementSyntax { AwaitKeyword.RawKind: not 0 }
            || n is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 }));
        int at = first.Member!.Span.End;
        List<Parameter> parameters = [];
        foreach (ISymbol symbol in flowsIn)
        {
            parameters.Add(new Parameter(Escaped(symbol.Name), TypeShown(first, TypeOf(symbol), at), [.. maps.Select(m => Escaped(m[symbol].Name))]));
        }
        Dictionary<int, string> literalNames = Literals(regions, literals, parameters, at, cancel);
        if (!apply)
        {
            return new Refactored(null, []);
        }
        string named = Unique(first, regions, name);
        string? returnType = returned is null ? null : TypeShown(first, TypeOf(returned), at);
        string task = first.Model.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task") is { } taskType ? taskType.ToMinimalDisplayString(first.Model, at) : "System.Threading.Tasks.Task";
        // Clones ending their function's body return as it does, the method's result returned at each call.
        bool valued = function is not null && first.Statements.Any(s => Inside(s).Any(n => n is ReturnStatementSyntax { Expression: not null }));
        awaits = function?.IsAsync ?? awaits;
        string returns = function is not null ? (function.IsAsync && function.ReturnsVoid ? task : TypeShown(first, function.ReturnType, at))
            : awaits ? (returnType is null ? task : $"{task}<{returnType}>") : returnType ?? "void";
        string modifiers = (sameType ? "private" : "internal") + (instance ? string.Empty : " static") + (awaits ? " async" : string.Empty);
        List<StatementSyntax> body = [.. declared.Select(s => SyntaxFactory.ParseStatement($"{TypeShown(first, TypeOf(s), at)} {Escaped(s.Name)};\n"))];
        Dictionary<int, string> byStart = literalNames.ToDictionary(n => first.Tokens[n.Key].SpanStart, n => n.Value);
        body.AddRange(first.Statements.Select((s, i) =>
        {
            StatementSyntax copy = s.ReplaceNodes(s.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => byStart.ContainsKey(l.Token.SpanStart)),
                (old, _) => SyntaxFactory.IdentifierName(byStart[old.Token.SpanStart]).WithTriviaFrom(old));
            return i == 0 ? copy.WithLeadingTrivia(SyntaxFactory.ElasticMarker) : copy;
        }));
        if (returned is not null)
        {
            body.Add(SyntaxFactory.ParseStatement($"return {Escaped(returned.Name)};\n"));
        }
        MethodDeclarationSyntax method = ((MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
                $"{modifiers} {returns} {named}({string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}"))})\n{{\n}}\n")!)
            .WithBody(SyntaxFactory.Block(body))
            .WithLeadingTrivia(SyntaxFactory.EndOfLine("\n"))
            .WithTrailingTrivia(first.Member.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);
        Dictionary<DocumentId, DocumentEditor> editors = [];
        async Task<DocumentEditor> EditorOf(Document document)
        {
            if (!editors.TryGetValue(document.Id, out DocumentEditor? editor))
            {
                editors[document.Id] = editor = await DocumentEditor.CreateAsync(document, cancel).ConfigureAwait(false);
            }
            return editor;
        }
        (await EditorOf(first.Document).ConfigureAwait(false)).InsertAfter(first.Member, method);
        for (int k = 0; k < regions.Count; k++)
        {
            Region region = regions[k];
            string target = SymbolEqualityComparer.Default.Equals(region.Type, first.Type) ? named : $"{first.Type!.ToMinimalDisplayString(region.Model, region.Span.Start)}.{named}";
            string call = $"{(awaits ? "await " : string.Empty)}{target}({string.Join(", ", parameters.Select(p => p.Arguments[k]))})";
            string statement = valued ? $"return {call};"
                : returned is null || !Leaves(region, maps[k][returned]) ? $"{call};"
                : region.Declares(maps[k][returned]) ? $"{DeclaredAs(region, maps[k][returned])} {Escaped(maps[k][returned].Name)} = {call};"
                : $"{Escaped(maps[k][returned].Name)} = {call};";
            DocumentEditor editor = await EditorOf(region.Document).ConfigureAwait(false);
            editor.ReplaceNode(region.Statements[0], SyntaxFactory.ParseStatement(statement)
                .WithLeadingTrivia(region.Statements[0].GetLeadingTrivia()).WithTrailingTrivia(region.Statements[^1].GetTrailingTrivia()));
            foreach (StatementSyntax rest in region.Statements.Skip(1))
            {
                editor.RemoveNode(rest, SyntaxRemoveOptions.KeepNoTrivia);
            }
        }
        Solution changed = solution;
        foreach ((DocumentId id, DocumentEditor editor) in editors)
        {
            changed = changed.WithDocumentSyntaxRoot(id, editor.GetChangedRoot());
        }
        foreach (DocumentId id in editors.Keys)
        {
            changed = (await Formatter.FormatAsync(changed.GetDocument(id)!, Formatter.Annotation, cancellationToken: cancel).ConfigureAwait(false)).Project.Solution;
        }
        return await Changes.BetweenAsync(solution, changed, cancel).ConfigureAwait(false);
    }

    /// <summary>Refuses code whose flow one method can't hold: a break, continue or goto out of it, a return where it does
    /// not end its function's body, a yield, or unsafe code. Where the clones return, the function they return from.</summary>
    private static IMethodSymbol? CheckFlow(List<Region> regions, CancellationToken cancel)
    {
        Region first = regions[0];
        if (first.Model.AnalyzeControlFlow(first.Statements[0], first.Statements[^1]) is not { Succeeded: true } control || !first.Flow.Succeeded)
        {
            throw new RefusedException("The C# language server could not tell how the clones' code flows");
        }
        IMethodSymbol? function = null;
        if (control.ReturnStatements.Length > 0)
        {
            if (regions.FirstOrDefault(r => !EndsBody(r)) is { } partway)
            {
                throw new RefusedException($"The clone at {partway.Where} returns from its method partway, which a method of its code can't do for it");
            }
            function = first.Model.GetEnclosingSymbol(first.Span.Start, cancel) as IMethodSymbol
                ?? throw new RefusedException("The C# language server could not tell what the clones return from");
            if (regions.Skip(1).Select(r => r.Model.GetEnclosingSymbol(r.Span.Start, cancel) as IMethodSymbol)
                .FirstOrDefault(f => f is null || f.IsAsync != function.IsAsync || !SymbolEqualityComparer.Default.Equals(f.ReturnType, function.ReturnType)) is { } other)
            {
                throw new RefusedException($"The clones return from {function.Name} and {other.Name}, which return differently");
            }
        }
        if (control.ExitPoints.Any(e => e is not ReturnStatementSyntax))
        {
            throw new RefusedException("The clones break, continue or go to a label outside them, which a method of their code can't do for them");
        }
        if (first.Statements.Any(s => Inside(s).Any(n => n is YieldStatementSyntax)))
        {
            throw new RefusedException("The clones yield their method's values, which a method of their code can't do for them");
        }
        if (first.Statements.Any(s => s.DescendantNodesAndSelf().Any(n => n is UnsafeStatementSyntax or FixedStatementSyntax or PointerTypeSyntax)))
        {
            throw new RefusedException("The clones hold unsafe code, which Passenger moves into no method");
        }
        return function;
    }

    /// <summary>Whether a clone's statements end the body of the function holding them: a method's, an accessor's, a local
    /// function's or a lambda's.</summary>
    private static bool EndsBody(Region region) =>
        region.Statements[^1].Parent is BlockSyntax { Parent: BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax } body
        && body.Statements[^1] == region.Statements[^1];

    /// <summary>Lines every clone's names up with the first's, token by token: a variable of the clone's own with one of the
    /// other's own, one from outside with one from outside of the same type, the same one throughout; anything else named
    /// the same in every clone. A literal written otherwise in any is noted, to be a parameter.</summary>
    private static void Match(List<Region> regions, Dictionary<ISymbol, ISymbol>[] maps, List<int> literals, CancellationToken cancel)
    {
        Region first = regions[0];
        for (int p = 0; p < first.Tokens.Length; p++)
        {
            SyntaxToken token = first.Tokens[p];
            if (!CodeClones.Renamable(token))
            {
                continue;
            }
            if (!token.IsKind(SyntaxKind.IdentifierToken))
            {
                if (regions.Any(r => r.Tokens[p].Text != token.Text))
                {
                    literals.Add(p);
                }
                continue;
            }
            ISymbol? symbol = SymbolOf(first.Model, token, cancel);
            if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } local && !first.Declares(local))
            {
                throw new RefusedException($"The clones call {local.Name}, a local function only their method sees");
            }
            if (symbol is IRangeVariableSymbol)
            {
                throw new RefusedException($"The clones use {symbol.Name}, a query's variable, which no method takes");
            }
            for (int k = 0; k < regions.Count; k++)
            {
                Region region = regions[k];
                SyntaxToken theirs = region.Tokens[p];
                ISymbol? matched = SymbolOf(region.Model, theirs, cancel);
                if (symbol is not null && first.HasVariable(symbol))
                {
                    bool own = first.Declares(symbol);
                    if (matched is null || !region.HasVariable(matched) || region.Declares(matched) != own)
                    {
                        throw new RefusedException($"{token.ValueText} at {first.Where} is a variable {(own ? "the clone declares" : "from outside it")}, but {theirs.ValueText} at {region.Where} is not");
                    }
                    if (maps[k].TryGetValue(symbol, out ISymbol? was) && !SymbolEqualityComparer.Default.Equals(was, matched))
                    {
                        throw new RefusedException($"{token.ValueText} at {first.Where} stands for both {was.Name} and {matched.Name} at {region.Where}");
                    }
                    if (!own && !SymbolEqualityComparer.Default.Equals(TypeOf(symbol), TypeOf(matched)))
                    {
                        throw new RefusedException($"{token.ValueText} at {first.Where} is {TypeOf(symbol).Name} where {theirs.ValueText} at {region.Where} is {TypeOf(matched).Name}");
                    }
                    if (!own && (symbol is ILocalSymbol { RefKind: not RefKind.None } || matched is ILocalSymbol { RefKind: not RefKind.None }))
                    {
                        throw new RefusedException($"{token.ValueText} at {first.Where} is a ref local, which a method takes only as a copy");
                    }
                    maps[k][symbol] = matched;
                }
                else if (symbol is null ? theirs.ValueText != token.ValueText : !SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, matched?.OriginalDefinition))
                {
                    throw new RefusedException($"The clones name different things: {token.ValueText} at {first.Where}, {theirs.ValueText} at {region.Where}; one method can't take both");
                }
            }
        }
    }

    /// <summary>The variable whose value flows out of the clones to the code after any of them, as the first names it; none
    /// where nothing does.</summary>
    private static ISymbol? Returned(List<Region> regions, Dictionary<ISymbol, ISymbol>[] maps)
    {
        HashSet<ISymbol> leaving = new(SymbolEqualityComparer.Default);
        for (int k = 0; k < regions.Count; k++)
        {
            Dictionary<ISymbol, List<ISymbol>> back = new(SymbolEqualityComparer.Default);
            foreach ((ISymbol mine, ISymbol theirs) in maps[k])
            {
                if (!back.TryGetValue(theirs, out List<ISymbol>? of))
                {
                    back[theirs] = of = [];
                }
                of.Add(mine);
            }
            foreach (ISymbol theirs in Leaving(regions[k]))
            {
                if (back.TryGetValue(theirs, out List<ISymbol>? mine))
                {
                    if (mine.Count > 1)
                    {
                        throw new RefusedException($"{theirs.Name} at {regions[k].Where} stands for {Plural.Listed([.. mine.Select(s => s.Name)])} at {regions[0].Where}, and its value flows on");
                    }
                    leaving.Add(mine[0]);
                }
            }
        }
        if (leaving.Count > 1)
        {
            throw new RefusedException($"The clones leave {Plural.Listed([.. leaving.Select(s => s.Name)])} to the code after them, and one method returns one");
        }
        if (leaving.FirstOrDefault() is { } one && (TypeOf(one).IsAnonymousType || TypeOf(one).IsRefLikeType && one is ILocalSymbol))
        {
            throw new RefusedException($"{one.Name}'s value, which flows on, is of a type a method can't return");
        }
        return leaving.FirstOrDefault();
    }

    /// <summary>The variables a clone sets that the code after it reads: those whose values flow out, and those it declares
    /// that are used after it.</summary>
    private static IEnumerable<ISymbol> Leaving(Region region) =>
        region.Flow.DataFlowsOut.Concat(region.Flow.VariablesDeclared.Where(v => region.Flow.ReadOutside.Contains(v) || region.Flow.WrittenOutside.Contains(v)))
            .Where(s => s is not IParameterSymbol { IsThis: true }).Distinct(SymbolEqualityComparer.Default);

    /// <summary>Whether a clone's code after it reads a variable it sets.</summary>
    private static bool Leaves(Region region, ISymbol variable) => Leaving(region).Contains(variable, SymbolEqualityComparer.Default);

    /// <summary>The literals the clones write otherwise, each a parameter (one for those written alike throughout), named
    /// for its kind, by their places in the first clone's tokens.</summary>
    private static Dictionary<int, string> Literals(List<Region> regions, List<int> places, List<Parameter> parameters, int at, CancellationToken cancel)
    {
        Region first = regions[0];
        Dictionary<string, string> byValues = new(StringComparer.Ordinal);
        Dictionary<int, string> named = [];
        HashSet<string> taken = new(first.Tokens.Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText), StringComparer.Ordinal);
        taken.UnionWith(parameters.Select(p => p.Name));
        foreach (int place in places)
        {
            SyntaxToken token = first.Tokens[place];
            if (token.Parent is not LiteralExpressionSyntax literal)
            {
                throw new RefusedException($"The clones' strings differ inside an interpolated string, at {first.Where}, which a parameter can't stand for");
            }
            if (literal.Ancestors().TakeWhile(a => a is not BlockSyntax).Any(a => a is PatternSyntax or CaseSwitchLabelSyntax or AttributeArgumentSyntax or ParameterSyntax
                || a is LocalDeclarationStatementSyntax { IsConst: true }))
            {
                throw new RefusedException($"{token.Text} at {first.Where} is a constant, where another clone writes {regions.First(r => r.Tokens[place].Text != token.Text).Tokens[place].Text}; a parameter can't stand for it");
            }
            ITypeSymbol? type = first.Model.GetTypeInfo(literal, cancel).ConvertedType;
            foreach (Region region in regions.Skip(1))
            {
                ITypeSymbol? theirs = region.Tokens[place].Parent is LiteralExpressionSyntax other ? region.Model.GetTypeInfo(other, cancel).ConvertedType : null;
                if (type is null || !SymbolEqualityComparer.Default.Equals(type, theirs))
                {
                    throw new RefusedException($"{token.Text} at {first.Where} and {region.Tokens[place].Text} at {region.Where} are of different types, which one parameter can't take");
                }
            }
            string shown = TypeShown(first, type!, at);
            string[] values = [.. regions.Select(r => r.Tokens[place].Text)];
            string key = shown + "\0" + string.Join("\0", values);
            if (!byValues.TryGetValue(key, out string? name))
            {
                string kind = type!.SpecialType switch
                {
                    SpecialType.System_String => "text",
                    SpecialType.System_Boolean => "flag",
                    SpecialType.System_Char => "character",
                    _ => "value",
                };
                name = kind;
                for (int n = 2; !taken.Add(name); n++)
                {
                    name = kind + n;
                }
                byValues[key] = name;
                parameters.Add(new Parameter(name, shown, values));
            }
            named[place] = name;
        }
        return named;
    }

    /// <summary>A name for the method the first clone's type and the clones' places see nothing else by, the one asked if it can.</summary>
    private static string Unique(Region first, List<Region> regions, string asked)
    {
        string name = SyntaxFacts.IsValidIdentifier(asked) ? asked : "NewMethod";
        string chosen = name;
        for (int n = 2; first.Type!.GetMembers(chosen).Length > 0 || regions.Any(r => r.Model.LookupSymbols(r.Span.Start, name: chosen).Length > 0); n++)
        {
            chosen = name + n;
        }
        return chosen;
    }

    /// <summary>Whether the first clone's code uses its instance: this or base, or a member of its type's own reached by name.</summary>
    private static bool UsesInstance(Region first, CancellationToken cancel) => first.Statements.Any(s => s.DescendantNodesAndSelf().Any(n =>
        n is ThisExpressionSyntax or BaseExpressionSyntax || n is SimpleNameSyntax name && ReachedByName(first, name, cancel)));

    /// <summary>Whether a name, not after a dot, names an instance member of the clone's type or a type it derives from.</summary>
    private static bool ReachedByName(Region first, SimpleNameSyntax name, CancellationToken cancel)
    {
        if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name || name.Parent is MemberBindingExpressionSyntax
            || name.Parent is QualifiedNameSyntax qualified && qualified.Right == name
            || name.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assigned && assigned.Left == name)
        {
            return false;
        }
        ISymbol? symbol = first.Model.GetSymbolInfo(name, cancel).Symbol;
        // A primary constructor's parameter is the instance's, as its fields are.
        if (symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } } && !first.HasVariable(symbol))
        {
            return true;
        }
        if (symbol is not (IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }) || symbol.IsStatic)
        {
            return false;
        }
        for (INamedTypeSymbol? type = first.Type; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, symbol.ContainingType?.OriginalDefinition) || SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, symbol.ContainingType?.OriginalDefinition))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A node's own code, what a lambda or local function in it holds left out.</summary>
    private static IEnumerable<SyntaxNode> Inside(SyntaxNode node) =>
        node.DescendantNodesAndSelf(n => n == node || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    /// <summary>What a name in a clone names: the symbol it binds to, or the one it declares.</summary>
    private static ISymbol? SymbolOf(SemanticModel model, SyntaxToken token, CancellationToken cancel)
    {
        if (token.Parent is SimpleNameSyntax name)
        {
            SymbolInfo info = model.GetSymbolInfo(name, cancel);
            return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        }
        return token.Parent is null ? null : model.GetDeclaredSymbol(token.Parent, cancel);
    }

    private static ITypeSymbol TypeOf(ISymbol variable) => variable switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        _ => throw new RefusedException($"{variable.Name} is no variable a method can take"),
    };

    /// <summary>A type as the code where the method goes names it, refused where no method can take it.</summary>
    private static string TypeShown(Region first, ITypeSymbol type, int at)
    {
        if (type.IsAnonymousType || type is IPointerTypeSymbol or IFunctionPointerTypeSymbol || MentionsMethodTypeParameter(type))
        {
            throw new RefusedException($"The clones' code uses a value of {type.ToDisplayString()}, a type the method could not name");
        }
        return type.ToMinimalDisplayString(first.Model, at, SymbolDisplayFormat.MinimallyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
    }

    private static bool MentionsMethodTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol parameter => parameter.TypeParameterKind == TypeParameterKind.Method,
        IArrayTypeSymbol array => MentionsMethodTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(MentionsMethodTypeParameter),
        _ => false,
    };

    /// <summary>How a clone's declaration of a variable whose value flows on names its type: as written, var where it wrote var.</summary>
    private static string DeclaredAs(Region region, ISymbol variable) =>
        variable.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault() is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration }
            ? declaration.Type.ToString()
            : "var";

    /// <summary>Where a variable is first named in the first clone, so parameters come in the order the code reads them.</summary>
    private static int FirstUse(Region first, ISymbol variable) =>
        Array.FindIndex(first.Tokens, t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == variable.Name) is var at and >= 0 ? at : int.MaxValue;

    private static string Escaped(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>Why the clones can't share one method, thrown from where it is found.</summary>
    private sealed class RefusedException(string why) : Exception(why);
}
