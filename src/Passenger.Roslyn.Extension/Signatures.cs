using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>Change Signature: a method's or constructor's parameters added, removed, reordered, renamed or
/// retyped, its return type and name changed, through every declaration in its hierarchy (what it overrides and
/// implements, and what overrides and implements it) and every call, each call's arguments following their parameters
/// and passing a new one's value. Parameters and the method are renamed by Roslyn's Renamer, which resolves conflicts.</summary>
internal static class Signatures
{
    private static readonly string[] Modifiers = ["", "ref", "out", "in", "params", "this", "ref readonly", "scoped", "scoped ref", "this ref", "ref this", "this in", "in this"];

    public static async Task<Signature> DescribeAsync(Document document, Place at, CancellationToken cancel)
    {
        (IMethodSymbol? method, string? problem) = await FindAsync(document, at, cancel).ConfigureAwait(false);
        if (method is not null && (await DeclarationsAsync(method, cancel).ConfigureAwait(false)).FirstOrDefault() is { } declaration)
        {
            return new Signature(null, method.MethodKind == MethodKind.Constructor ? "constructor" : "method", method.ContainingType.Name,
                string.Join(" ", declaration.Modifiers.Select(m => m.Text)),
                method.MethodKind == MethodKind.Constructor ? method.ContainingType.Name : method.Name,
                declaration is MethodDeclarationSyntax named ? named.ReturnType.ToString() : string.Empty,
                [.. declaration.ParameterList.Parameters.Select(ParameterOf)], method.IsExtensionMethod);
        }
        problem ??= $"Unable to change signature of {Describe(method!)}: it is declared as a record's or a type's parameters";
        return new Signature(problem, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, [], false);
    }

    public static async Task<Refactored> ChangeAsync(Document document, SignatureChange change, CancellationToken cancel)
    {
        (IMethodSymbol? method, string? problem) = await FindAsync(document, change.At, cancel).ConfigureAwait(false);
        if (method is null)
        {
            return Changes.Refused(problem!);
        }
        if ((await DeclarationsAsync(method, cancel).ConfigureAwait(false)).FirstOrDefault() is not { } declaration)
        {
            return Changes.Refused($"Unable to change signature of {Describe(method)}: it is declared as a record's or a type's parameters");
        }
        IReadOnlyList<Parameter> original = [.. declaration.ParameterList.Parameters.Select(ParameterOf)];
        if (Problem(method, original, change) is { } wrong)
        {
            return Changes.Refused(wrong);
        }
        Solution before = document.Project.Solution;
        List<IMethodSymbol> family = await FamilyAsync(method, before, cancel).ConfigureAwait(false);
        if (family.FirstOrDefault(m => !m.Locations.Any(l => l.IsInSource)) is { } outside)
        {
            bool implements = outside.ContainingType.TypeKind == TypeKind.Interface;
            return Changes.Refused($"Change Signature is applied to {Describe(method)} that also {(implements ? "implements" : "overrides")} {Describe(outside)} "
                + $"that will not be {(implements ? "converted" : "changed")}. Please resolve the conflict manually.");
        }
        foreach (IMethodSymbol member in family)
        {
            if ((await DeclarationsAsync(member, cancel).ConfigureAwait(false)).Count == 0)
            {
                return Changes.Refused($"Unable to change signature of {Describe(member)}: it is declared as a record's or a type's parameters");
            }
        }
        List<MethodId> members = [.. family.Select(m => Identify(before, m))];
        Solution solution = await RenameParametersAsync(before, members, change, cancel).ConfigureAwait(false);
        SyntaxAnnotation marker = new();
        (solution, IReadOnlyList<UseLeft> left) = await RestructureAsync(before, solution, members, Identify(before, method), original, change, marker, cancel).ConfigureAwait(false);
        if (method.MethodKind == MethodKind.Ordinary && change.Name != method.Name)
        {
            solution = await RenameMethodAsync(solution, marker, change.Name, cancel).ConfigureAwait(false);
        }
        return (await Changes.BetweenAsync(before, solution, cancel).ConfigureAwait(false)) with { Left = left };
    }

    /// <summary>The method or constructor at a place: the one named there, declared or called (a constructor by the type
    /// a new names, or by base and this), else the one whose declaration holds the caret.</summary>
    private static async Task<(IMethodSymbol? Method, string? Problem)> FindAsync(Document document, Place at, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        SyntaxToken token = Changes.TokenAt(root, offset);
        ISymbol? found = null;
        if (token.Parent is { } parent)
        {
            if (Called(parent) is { } call)
            {
                found = model.GetSymbolInfo(call, cancel) is { } info ? info.Symbol ?? info.CandidateSymbols.FirstOrDefault() : null;
            }
            else if (parent is BaseMethodDeclarationSyntax declared)
            {
                found = model.GetDeclaredSymbol(declared, cancel);
            }
        }
        found ??= token.Parent?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() is { } around ? model.GetDeclaredSymbol(around, cancel) : null;
        if (found is not IMethodSymbol method || method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.ReducedExtension))
        {
            return (null, "Change Signature changes a method or a constructor: put the caret on one, or on a call of one");
        }
        method = (method.ReducedFrom ?? method).OriginalDefinition;
        if (!method.Locations.Any(l => l.IsInSource))
        {
            return (null, $"Unable to change signature of {Describe(method)}: it is not declared in this solution's code");
        }
        return (method, null);
    }

    /// <summary>The call a name or keyword is the callee of: an invocation, an object creation (by its type's name or
    /// new), a constructor initializer's base or this, or a primary constructor's base type.</summary>
    private static SyntaxNode? Called(SyntaxNode name)
    {
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node
            || node.Parent is MemberAccessExpressionSyntax access && access.Name == node
            || node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node
            || node.Parent is AliasQualifiedNameSyntax alias && alias.Name == node)
        {
            node = node.Parent;
        }
        return node switch
        {
            BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax => node,
            _ => node.Parent switch
            {
                InvocationExpressionSyntax invocation when invocation.Expression == node => invocation,
                ObjectCreationExpressionSyntax creation when creation.Type == node => creation,
                PrimaryConstructorBaseTypeSyntax primary when primary.Type == node => primary,
                _ => null,
            },
        };
    }

    /// <summary>The method's declarations that carry its parameters (a partial method's two): none for one declared by a
    /// record's or type's own parameters.</summary>
    private static async Task<IReadOnlyList<BaseMethodDeclarationSyntax>> DeclarationsAsync(IMethodSymbol method, CancellationToken cancel)
    {
        List<BaseMethodDeclarationSyntax> declarations = [];
        foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
        {
            if (await reference.GetSyntaxAsync(cancel).ConfigureAwait(false) is BaseMethodDeclarationSyntax declaration)
            {
                declarations.Add(declaration);
            }
        }
        return declarations;
    }

    private static Parameter ParameterOf(ParameterSyntax parameter) => new(
        string.Join(" ", parameter.Modifiers.Select(m => m.Text)),
        parameter.Type?.ToString() ?? string.Empty,
        parameter.Identifier.ValueText,
        parameter.Default?.Value.ToString() ?? string.Empty);

    /// <summary>Why the signature asked for cannot be made; null when it can.</summary>
    private static string? Problem(IMethodSymbol method, IReadOnlyList<Parameter> original, SignatureChange change)
    {
        if (method.MethodKind == MethodKind.Ordinary && !Changes.IsName(change.Name))
        {
            return change.Name.Trim().Length == 0 ? "Method name can not be empty" : $"'{change.Name}' is not a valid method name";
        }
        if (method.MethodKind == MethodKind.Ordinary && !IsReturnType(change.ReturnType))
        {
            return "The return type is invalid";
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<int> kept = [];
        bool optional = false;
        for (int i = 0; i < change.Parameters.Count; i++)
        {
            ParameterChange parameter = change.Parameters[i];
            if (parameter.From >= original.Count || (parameter.From >= 0 && !kept.Add(parameter.From)))
            {
                return "A parameter is listed twice";
            }
            if (parameter.Name.Trim().Length == 0)
            {
                return "Parameter name can not be empty";
            }
            if (!Changes.IsName(parameter.Name))
            {
                return "A parameter has an invalid name";
            }
            if (!names.Add(parameter.Name))
            {
                return "Parameters should have different names";
            }
            if (!IsType(parameter.Type))
            {
                return "A parameter has an invalid type";
            }
            if (!Modifiers.Contains(Normalized(parameter.Modifier)))
            {
                return $"'{parameter.Modifier}' is not a parameter modifier";
            }
            if (!IsExpression(parameter.DefaultValue) || !IsExpression(parameter.CallValue))
            {
                return "Error in default value expression";
            }
            if (Normalized(parameter.Modifier) == "params" && i != change.Parameters.Count - 1)
            {
                return $"'{parameter.Name}' parameter must appear after all other parameters";
            }
            if (parameter.DefaultValue.Trim().Length > 0)
            {
                optional = true;
            }
            else if (optional && Normalized(parameter.Modifier) != "params")
            {
                return "Optional parameters must appear after all regular parameters";
            }
        }
        if (method.IsExtensionMethod && (change.Parameters.Count == 0 || change.Parameters[0].From != 0))
        {
            return $"The first parameter of the extension method '{method.Name}' must stay first";
        }
        return null;
    }

    private static string Normalized(string modifier) => string.Join(" ", modifier.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsType(string text) => text.Trim().Length > 0 && !SyntaxFactory.ParseTypeName(text).ContainsDiagnostics;

    /// <summary>A type a method can return: any type, or void, which a type's parser takes for none.</summary>
    private static bool IsReturnType(string text) => Normalized(text) == "void" || IsType(text);

    private static bool IsExpression(string text) => text.Trim().Length == 0 || !SyntaxFactory.ParseExpression(text).ContainsDiagnostics;

    /// <summary>The method and every method its signature is tied to: what it overrides and implements, and what overrides
    /// and implements those, as far as they go both ways. A constructor stands alone.</summary>
    private static async Task<List<IMethodSymbol>> FamilyAsync(IMethodSymbol method, Solution solution, CancellationToken cancel)
    {
        HashSet<IMethodSymbol> family = new(SymbolEqualityComparer.Default);
        Queue<IMethodSymbol> next = new([method]);
        while (next.TryDequeue(out IMethodSymbol? member))
        {
            member = member.OriginalDefinition;
            if (!family.Add(member) || member.MethodKind != MethodKind.Ordinary)
            {
                continue;
            }
            if (member.OverriddenMethod is { } overridden)
            {
                next.Enqueue(overridden);
            }
            foreach (ISymbol implemented in await SymbolFinder.FindImplementedInterfaceMembersAsync(member, solution, cancellationToken: cancel).ConfigureAwait(false))
            {
                if (implemented is IMethodSymbol each)
                {
                    next.Enqueue(each);
                }
            }
            if (!member.Locations.Any(l => l.IsInSource))
            {
                continue;
            }
            foreach (ISymbol overriding in await SymbolFinder.FindOverridesAsync(member, solution, cancellationToken: cancel).ConfigureAwait(false))
            {
                if (overriding is IMethodSymbol each)
                {
                    next.Enqueue(each);
                }
            }
            if (member.ContainingType.TypeKind == TypeKind.Interface || member.IsAbstract || member.IsVirtual)
            {
                foreach (ISymbol implementation in await SymbolFinder.FindImplementationsAsync(member, solution, cancellationToken: cancel).ConfigureAwait(false))
                {
                    if (implementation is IMethodSymbol each)
                    {
                        next.Enqueue(each);
                    }
                }
            }
        }
        return [.. family];
    }

    /// <summary>A member found again in a later solution: its documentation comment id in the project declaring it,
    /// which renaming its parameters leaves as it was.</summary>
    private sealed record MethodId(ProjectId Project, string Id);

    private static MethodId Identify(Solution solution, IMethodSymbol method) =>
        new(solution.GetDocumentId(method.Locations.First(l => l.IsInSource).SourceTree)!.ProjectId,
            DocumentationCommentId.CreateDeclarationId(method) ?? throw new InvalidOperationException($"{method.Name} has no documentation comment id"));

    private static async Task<IMethodSymbol?> ResolveAsync(Solution solution, MethodId member, CancellationToken cancel) =>
        await solution.GetProject(member.Project)!.GetCompilationAsync(cancel).ConfigureAwait(false) is { } compilation
            ? DocumentationCommentId.GetFirstSymbolForDeclarationId(member.Id, compilation) as IMethodSymbol
            : null;

    /// <summary>Each kept parameter given its new name in every declaration of the hierarchy, its uses and the calls'
    /// named arguments with it: first to a name of its own, so names that trade places never meet.</summary>
    private static async Task<Solution> RenameParametersAsync(Solution solution, IReadOnlyList<MethodId> members, SignatureChange change, CancellationToken cancel)
    {
        List<(MethodId Method, int Ordinal, string Name)> renames = [];
        foreach (MethodId member in members)
        {
            if (await ResolveAsync(solution, member, cancel).ConfigureAwait(false) is not { } method)
            {
                continue;
            }
            foreach (ParameterChange parameter in change.Parameters.Where(p => p.From >= 0 && p.From < method.Parameters.Length))
            {
                if (method.Parameters[parameter.From].Name != parameter.Name)
                {
                    renames.Add((member, parameter.From, parameter.Name));
                }
            }
        }
        SymbolRenameOptions options = new(RenameOverloads: false, RenameInStrings: false, RenameInComments: true, RenameFile: false);
        foreach (bool final in new[] { false, true })
        {
            foreach ((MethodId member, int ordinal, string name) in renames)
            {
                if (await ResolveAsync(solution, member, cancel).ConfigureAwait(false) is not { } method)
                {
                    continue;
                }
                // The same passing name for a parameter in every declaration, so a rename Roslyn carries to another finds it done.
                string to = final ? name : $"passenger__{ordinal}__{name}";
                if (method.Parameters[ordinal].Name != to)
                {
                    solution = await Renamer.RenameSymbolAsync(solution, method.Parameters[ordinal], options, to, cancel).ConfigureAwait(false);
                }
            }
        }
        return solution;
    }

    /// <summary>Every declaration's parameters and return type as asked, every call's arguments in the new order with the
    /// new parameters' values, and every cref's parameter types, each file through one editor of its own. Each use left
    /// as it was that the new signature leaves wrong is named with why, at its place in <paramref name="before"/>: a call
    /// whose arguments do not all bind or pass as their parameters take them, a method group the method no longer fits or
    /// whose delegate's calls would reach other parameters, a call made without the method's name that no longer compiles.</summary>
    private static async Task<(Solution Solution, IReadOnlyList<UseLeft> Left)> RestructureAsync(Solution before, Solution solution, IReadOnlyList<MethodId> members,
        MethodId main, IReadOnlyList<Parameter> original, SignatureChange change, SyntaxAnnotation marker, CancellationToken cancel)
    {
        Dictionary<DocumentId, DocumentEditor> editors = [];
        async Task<DocumentEditor> EditorOf(Document document)
        {
            if (!editors.TryGetValue(document.Id, out DocumentEditor? editor))
            {
                editor = await DocumentEditor.CreateAsync(document, cancel).ConfigureAwait(false);
                editors[document.Id] = editor;
            }
            return editor;
        }

        HashSet<(DocumentId, TextSpan)> seen = [];
        List<(Document Document, BaseMethodDeclarationSyntax Declaration, bool Marked)> declarations = [];
        List<(Document Document, SyntaxNode Call, List<Passed> Plan)> calls = [];
        List<(Document Document, NameMemberCrefSyntax Cref, IMethodSymbol Method)> crefs = [];
        List<Held> held = [];
        List<UseLeft> left = [];
        bool sameTypes = SameTypes(original, change);
        bool samePlaces = change.Parameters.Select((p, i) => p.From < 0 || p.From == i).All(kept => kept);
        bool sameCalls = change.Parameters.Count == original.Count && change.Parameters.Select((p, i) => p.From == i).All(kept => kept);
        IMethodSymbol? primary = await ResolveAsync(solution, main, cancel).ConfigureAwait(false);
        foreach (MethodId member in members)
        {
            if (await ResolveAsync(solution, member, cancel).ConfigureAwait(false) is not { } method)
            {
                continue;
            }
            bool first = SymbolEqualityComparer.Default.Equals(method, primary);
            foreach (BaseMethodDeclarationSyntax declaration in await DeclarationsAsync(method, cancel).ConfigureAwait(false))
            {
                declarations.Add((solution.GetDocument(declaration.SyntaxTree)!, declaration, first));
                first = false;
            }
            foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(method, solution, cancel).ConfigureAwait(false))
            {
                foreach (ReferenceLocation place in referenced.Locations)
                {
                    if (!place.Location.IsInSource || place.Document is SourceGeneratedDocument || !seen.Add((place.Document.Id, place.Location.SourceSpan)))
                    {
                        continue;
                    }
                    SyntaxNode root = (await place.Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
                    SemanticModel model = (await place.Document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
                    int at = place.Location.SourceSpan.Start;
                    if (root.FindToken(at, findInsideTrivia: true).Parent?.AncestorsAndSelf().OfType<NameMemberCrefSyntax>().FirstOrDefault() is { } cref)
                    {
                        if (cref.Parameters is not null && !sameTypes)
                        {
                            crefs.Add((place.Document, cref, method));
                        }
                        continue;
                    }
                    SyntaxNode node = root.FindNode(place.Location.SourceSpan, getInnermostNodeForTie: true);
                    if (place.IsImplicit)
                    {
                        SyntaxNode around = node.AncestorsAndSelf().FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax) ?? node;
                        if (held.Any(h => h.Node == around))
                        {
                            continue;
                        }
                        int errors = Errors(model, around, cancel);
                        held.Add(new Held(place.Document, around, at, (now, it) =>
                            Errors(now, it, cancel) > errors ? $"{Named(method, change)}, called here without its name, which the new signature no longer fits" : null));
                    }
                    else if (Called(node) is { } call)
                    {
                        (List<Passed>? plan, string? unpassed) = Plan(model, call, method, change, cancel);
                        if (plan is not null)
                        {
                            calls.Add((place.Document, call, plan));
                        }
                        else if (unpassed is not null)
                        {
                            left.Add(await LeftAsync(before, place.Document, at, $"a call of {Named(method, change)} {unpassed}", cancel).ConfigureAwait(false));
                        }
                        else if (!sameCalls)
                        {
                            string why = model.GetOperation(call, cancel) is IDynamicInvocationOperation or IDynamicObjectCreationOperation
                                ? $"a call of {Named(method, change)} bound as the program runs, whose arguments are dynamic"
                                : $"a call of {Named(method, change)} whose arguments do not all match its parameters";
                            left.Add(await LeftAsync(before, place.Document, at, why, cancel).ConfigureAwait(false));
                        }
                    }
                    else if (Grouped(node) is var group && !IsNameOf(model, group, cancel))
                    {
                        string made = DelegateOf(model, group, cancel);
                        string what = $"{Named(method, change)} as {made}";
                        held.Add(new Held(place.Document, group, at, (now, it) =>
                            !Binds(now, it, cancel) ? $"{what}, which the new signature no longer fits"
                            : DelegateOf(now, it, cancel) is var remade && remade != made ? $"{what}, which it now makes {remade} instead"
                            : !samePlaces ? $"{what}, whose calls would pass their arguments to other parameters"
                            : null));
                    }
                }
            }
        }

        // Marked first, so a call made after whose argument holds one carries the mark, or drops it with the argument.
        foreach (Held use in held)
        {
            (await EditorOf(use.Document).ConfigureAwait(false)).ReplaceNode(use.Node, (current, _) => current.WithAdditionalAnnotations(use.Mark));
        }
        HashSet<DocumentId> edited = [.. declarations.Select(d => d.Document.Id), .. calls.Select(c => c.Document.Id)];
        foreach ((Document document, NameMemberCrefSyntax cref, IMethodSymbol method) in crefs)
        {
            if (CrefParameters(cref.Parameters!, change) is { } made)
            {
                edited.Add(document.Id);
                (await EditorOf(document).ConfigureAwait(false)).ReplaceNode(cref, (current, _) => ((NameMemberCrefSyntax)current).WithParameters(made));
            }
            else
            {
                left.Add(await LeftAsync(before, document, cref.SpanStart, $"a documentation comment's cref of {Named(method, change)}, whose new parameter types it could not write", cancel).ConfigureAwait(false));
            }
        }
        foreach ((Document document, BaseMethodDeclarationSyntax declaration, bool marked) in declarations)
        {
            (await EditorOf(document).ConfigureAwait(false)).ReplaceNode(declaration, (current, _) =>
            {
                BaseMethodDeclarationSyntax now = (BaseMethodDeclarationSyntax)current;
                now = now.WithParameterList(Parameters(now.ParameterList, original, change));
                if (now is MethodDeclarationSyntax named && Normalized(change.ReturnType) != Normalized(named.ReturnType.ToString()) && change.ReturnType.Trim().Length > 0)
                {
                    now = named.WithReturnType(SyntaxFactory.ParseTypeName(change.ReturnType.Trim()).WithTriviaFrom(named.ReturnType));
                }
                return marked ? now.WithAdditionalAnnotations(marker) : now;
            });
        }
        // The last first, so a call inside another's argument is changed before the argument is moved or dropped.
        foreach ((Document document, SyntaxNode call, List<Passed> plan) in calls.OrderByDescending(c => c.Call.SpanStart))
        {
            (await EditorOf(document).ConfigureAwait(false)).ReplaceNode(call, (current, _) => WithArguments(current, plan));
        }
        // A file only marked is checked as changed but answered as it was, so it is no file the change makes.
        Solution checking = solution;
        foreach ((DocumentId id, DocumentEditor editor) in editors)
        {
            SyntaxNode root = editor.GetChangedRoot();
            checking = checking.WithDocumentSyntaxRoot(id, root);
            solution = edited.Contains(id) ? solution.WithDocumentSyntaxRoot(id, root) : solution;
        }

        foreach (Held use in held)
        {
            Document document = checking.GetDocument(use.Document.Id)!;
            SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
            if (root.GetAnnotatedNodes(use.Mark).FirstOrDefault() is not { } now)
            {
                continue;
            }
            if (use.Why((await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!, now) is { } why)
            {
                left.Add(await LeftAsync(before, use.Document, use.At, why, cancel).ConfigureAwait(false));
            }
        }
        return (solution, [.. left.DistinctBy(u => (u.Path, u.At)).OrderBy(u => u.Path, StringComparer.Ordinal).ThenBy(u => u.At.Line).ThenBy(u => u.At.Character)]);
    }

    /// <summary>A use that is no call to change, marked to be found again once the signature changed, and why the new
    /// signature leaves it wrong there (null when it does not); <paramref name="At"/> is its name's offset.</summary>
    private sealed record Held(Document Document, SyntaxNode Node, int At, Func<SemanticModel, SyntaxNode, string?> Why)
    {
        public SyntaxAnnotation Mark { get; } = new();
    }

    /// <summary>A method of the hierarchy as its words name it, by the name the change gives it.</summary>
    private static string Named(IMethodSymbol method, SignatureChange change) => method.MethodKind == MethodKind.Constructor
        ? $"{method.ContainingType.Name}'s constructor"
        : $"{method.ContainingType.Name}.{(Changes.IsName(change.Name) ? change.Name : method.Name)}";

    /// <summary>The expression a method's name stands in as a method group: the name, or the member access it ends.</summary>
    private static SyntaxNode Grouped(SyntaxNode name)
    {
        SyntaxNode node = name;
        while (node.Parent is MemberAccessExpressionSyntax access && access.Name == node
            || node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node
            || node.Parent is QualifiedNameSyntax qualified && qualified.Right == node)
        {
            node = node.Parent;
        }
        return node;
    }

    private static bool IsNameOf(SemanticModel model, SyntaxNode group, CancellationToken cancel) =>
        group.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation } && model.GetOperation(invocation, cancel) is INameOfOperation;

    /// <summary>The delegate or function pointer a method group is made into, as its words say it ("a Func&lt;int, int&gt;").</summary>
    private static string DelegateOf(SemanticModel model, SyntaxNode group, CancellationToken cancel)
    {
        for (IOperation? operation = model.GetOperation(group, cancel); operation is not null; operation = operation.Parent)
        {
            if (operation.Type is { TypeKind: TypeKind.Delegate or TypeKind.FunctionPointer } type)
            {
                string name = type.IsAnonymousType ? "delegate" : type.ToMinimalDisplayString(model, group.SpanStart);
                return ("AEIOUaeiou".Contains(name[0], StringComparison.Ordinal) ? "an " : "a ") + name;
            }
        }
        return "a method group";
    }

    /// <summary>Whether a method group still makes the delegate it stands for: it names a method, with no error in it.</summary>
    private static bool Binds(SemanticModel model, SyntaxNode group, CancellationToken cancel) =>
        model.GetSymbolInfo(group, cancel).Symbol is IMethodSymbol && !model.GetDiagnostics(group.Span, cancel).Any(d => d.Severity == DiagnosticSeverity.Error);

    private static int Errors(SemanticModel model, SyntaxNode node, CancellationToken cancel) =>
        model.GetDiagnostics(node.Span, cancel).Count(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>Whether the new signature takes the same types, ref kinds and order as the old, so a cref's parameters
    /// still name it.</summary>
    private static bool SameTypes(IReadOnlyList<Parameter> original, SignatureChange change) =>
        change.Parameters.Count == original.Count && change.Parameters.Select((p, i) => p.From == i && Normalized(p.Type) == Normalized(original[i].Type)
            && RefKindOf(p.Modifier) == RefKindOf(original[i].Modifier)).All(same => same);

    /// <summary>A cref's parameters as the new signature's, a generic's type arguments in braces as a cref writes them; null
    /// when they do not parse.</summary>
    private static CrefParameterListSyntax? CrefParameters(CrefParameterListSyntax list, SignatureChange change)
    {
        string types = string.Join(", ", change.Parameters.Select(p => RefKindOf(p.Modifier) + p.Type.Trim().Replace('<', '{').Replace('>', '}')));
        SyntaxTriviaList comment = SyntaxFactory.ParseLeadingTrivia($"/// <see cref=\"M({types})\"/>\n");
        CrefParameterListSyntax? made = comment.Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>()
            .SelectMany(d => d.DescendantNodes().OfType<NameMemberCrefSyntax>()).FirstOrDefault()?.Parameters;
        return made is null || made.ContainsDiagnostics || made.Parameters.Count != change.Parameters.Count ? null : made.WithTriviaFrom(list);
    }

    /// <summary>The words a cref writes before a parameter's type for its modifier: "ref ", "out ", "in ", "ref readonly ".</summary>
    private static string RefKindOf(string modifier)
    {
        string[] words = Normalized(modifier).Split(' ');
        return words.Contains("out") ? "out " : words.Contains("readonly") ? "ref readonly " : words.Contains("ref") ? "ref " : words.Contains("in") ? "in " : string.Empty;
    }

    /// <summary>A use left as it was, at its place in the file as it was before its parameters were renamed.</summary>
    private static async Task<UseLeft> LeftAsync(Solution before, Document renamed, int offset, string why, CancellationToken cancel)
    {
        Document was = before.GetDocument(renamed.Id) ?? renamed;
        int delta = 0;
        foreach (TextChange change in (await renamed.GetTextChangesAsync(was, cancel).ConfigureAwait(false)).OrderBy(c => c.Span.Start))
        {
            int start = change.Span.Start + delta;
            int length = change.NewText?.Length ?? 0;
            if (offset < start)
            {
                break;
            }
            if (offset < start + length)
            {
                offset = start;
                break;
            }
            delta += length - change.Span.Length;
        }
        SourceText text = await was.GetTextAsync(cancel).ConfigureAwait(false);
        LinePosition place = text.Lines.GetLinePosition(Math.Clamp(offset - delta, 0, text.Length));
        return new UseLeft(was.FilePath ?? was.Name, new Place(place.Line, place.Character), why);
    }

    /// <summary>A declaration's parameters as asked: each kept one as it is in this declaration, given the type, modifier
    /// or default value asked for where it differs from the method's own; each new one as written. Each place keeps the
    /// spacing the parameter there had.</summary>
    private static ParameterListSyntax Parameters(ParameterListSyntax list, IReadOnlyList<Parameter> original, SignatureChange change)
    {
        List<ParameterSyntax> parameters = [];
        foreach (ParameterChange asked in change.Parameters)
        {
            ParameterSyntax made;
            if (asked.From >= 0 && asked.From < list.Parameters.Count)
            {
                ParameterSyntax kept = list.Parameters[asked.From];
                Parameter was = original[asked.From];
                bool retyped = Normalized(asked.Type) != Normalized(was.Type) || Normalized(asked.Modifier) != Normalized(was.Modifier)
                    || asked.DefaultValue.Trim() != was.DefaultValue.Trim();
                made = retyped
                    ? Parse(asked.Modifier, asked.Type, kept.Identifier.ValueText, asked.DefaultValue).WithAttributeLists(kept.AttributeLists)
                    : kept;
            }
            else
            {
                made = Parse(asked.Modifier, asked.Type, asked.Name, asked.DefaultValue);
            }
            int at = parameters.Count;
            made = made.WithLeadingTrivia(at < list.Parameters.Count ? list.Parameters[at].GetLeadingTrivia() : default)
                .WithTrailingTrivia(at < list.Parameters.Count ? list.Parameters[at].GetTrailingTrivia() : default);
            parameters.Add(made);
        }
        return list.WithParameters(SyntaxFactory.SeparatedList(parameters, Separators(list.Parameters.GetSeparators().ToList(), parameters.Count)));
    }

    private static ParameterSyntax Parse(string modifier, string type, string name, string defaultValue)
    {
        string text = $"({Normalized(modifier)} {type.Trim()} {name}{(defaultValue.Trim().Length > 0 ? " = " + defaultValue.Trim() : string.Empty)})";
        return SyntaxFactory.ParseParameterList(text).Parameters[0];
    }

    /// <summary>A list's commas: those it had, then new ones followed by a space.</summary>
    private static IEnumerable<SyntaxToken> Separators(List<SyntaxToken> had, int items)
    {
        for (int i = 0; i < items - 1; i++)
        {
            yield return i < had.Count ? had[i] : SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);
        }
    }

    /// <summary>What a call passes in the new signature: for each place, the argument it already passes for that
    /// parameter (by its index in the call, several for a params array or collection spread out) with the keyword its
    /// passing now takes, or the value a new parameter is given; every argument after a parameter left to its default is
    /// named. No plan for a call whose arguments do not all bind, which is left as it is, and named where the calls change;
    /// nor for one passing an argument the parameter can no longer take as it is passed now, said why.</summary>
    private static (List<Passed>? Plan, string? Unpassed) Plan(SemanticModel model, SyntaxNode call, IMethodSymbol method, SignatureChange change, CancellationToken cancel)
    {
        if (ArgumentsOf(call) is not { } list || model.GetOperation(call, cancel) is not { } operation)
        {
            return (null, null);
        }
        ImmutableArray<IArgumentOperation> arguments = operation switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => operation.DescendantsAndSelf().OfType<IInvocationOperation>().FirstOrDefault(i => i.Syntax == call)?.Arguments
                ?? operation.DescendantsAndSelf().OfType<IObjectCreationOperation>().FirstOrDefault()?.Arguments ?? [],
        };
        int[] bound = [.. Enumerable.Repeat(-1, list.Arguments.Count)];
        bool reduced = false;
        foreach (IArgumentOperation argument in arguments)
        {
            int ordinal = argument.Parameter?.Ordinal ?? -1;
            if (argument.Syntax is ArgumentSyntax syntax && list.Arguments.IndexOf(syntax) is >= 0 and int index)
            {
                bound[index] = ordinal;
            }
            else if (argument.ArgumentKind is ArgumentKind.ParamArray or ArgumentKind.ParamCollection)
            {
                // Spread out: each value the call passes is an element of the array or collection it makes, whatever its type.
                foreach (IOperation element in argument.Value.DescendantsAndSelf())
                {
                    if (list.Arguments.IndexOf(a => a.Expression == element.Syntax) is >= 0 and int spread)
                    {
                        bound[spread] = ordinal;
                    }
                }
            }
            else if (argument.ArgumentKind == ArgumentKind.Explicit && ordinal == 0 && method.IsExtensionMethod && argument.Syntax is not ArgumentSyntax)
            {
                reduced = true;
            }
        }
        if (bound.Contains(-1))
        {
            return (null, null);
        }
        List<Passed> plan = [];
        bool named = false;
        foreach (ParameterChange parameter in change.Parameters)
        {
            if (reduced && parameter.From == 0)
            {
                continue;
            }
            if (parameter.From >= 0)
            {
                int[] passed = [.. Enumerable.Range(0, bound.Length).Where(i => bound[i] == parameter.From)];
                if (passed.Length == 0)
                {
                    named = true;
                    continue;
                }
                bool spread = passed.Length > 1 || method.Parameters[parameter.From].IsParams;
                RefKind passing = RefKindNamed(parameter.Modifier);
                string? keyword = null;
                if (!spread && method.Parameters[parameter.From].RefKind != passing)
                {
                    ArgumentSyntax argument = list.Arguments[passed[0]];
                    if (Keyword(model, argument, passing, cancel) is not { } given)
                    {
                        return (null, $"whose argument {argument.Expression} cannot be passed {Passing(passing)} to '{parameter.Name}'");
                    }
                    keyword = given;
                }
                foreach (int index in passed)
                {
                    plan.Add(new Passed(index, null, named && !spread ? parameter.Name : null, keyword));
                }
            }
            else if (parameter.CallValue.Trim().Length > 0)
            {
                plan.Add(new Passed(-1, parameter.CallValue.Trim(), named ? parameter.Name : null));
            }
            else
            {
                named = true;
            }
        }
        return (plan, null);
    }

    /// <summary>One argument of a call in the new signature: the one at an index of the call as it was, or a new value;
    /// named when it must be, and given the keyword its passing takes where that changed (empty for none).</summary>
    private sealed record Passed(int Index, string? Value, string? Name, string? Keyword = null);

    /// <summary>How a parameter of a modifier is passed.</summary>
    private static RefKind RefKindNamed(string modifier) => RefKindOf(modifier) switch
    {
        "out " => RefKind.Out,
        "ref readonly " => RefKind.RefReadOnlyParameter,
        "ref " => RefKind.Ref,
        "in " => RefKind.In,
        _ => RefKind.None,
    };

    private static string Passing(RefKind kind) => kind switch
    {
        RefKind.None => "by value",
        RefKind.Out => "as out",
        RefKind.Ref => "by ref",
        _ => "as in",
    };

    /// <summary>The keyword an argument takes for a parameter passed anew: none by value; out, ref or in before a variable,
    /// a value passed as in taking none; null where the argument cannot be passed so (a value by ref or as out, an out
    /// variable or discard to anything but out).</summary>
    private static string? Keyword(SemanticModel model, ArgumentSyntax argument, RefKind passing, CancellationToken cancel)
    {
        if (argument.Expression is DeclarationExpressionSyntax || model.GetOperation(argument.Expression, cancel) is IDiscardOperation)
        {
            return passing == RefKind.Out ? "out" : null;
        }
        return passing switch
        {
            RefKind.None => string.Empty,
            RefKind.Out => IsVariable(model, argument.Expression, written: true, cancel) ? "out" : null,
            RefKind.Ref => IsVariable(model, argument.Expression, written: true, cancel) ? "ref" : null,
            _ => IsVariable(model, argument.Expression, written: false, cancel) ? "in" : string.Empty,
        };
    }

    /// <summary>Whether an expression is a variable an argument can pass by reference: a local, a parameter, a field, an
    /// array's element, or what a call or property returns by reference; one to be written neither readonly nor a
    /// constant.</summary>
    private static bool IsVariable(SemanticModel model, ExpressionSyntax expression, bool written, CancellationToken cancel) => model.GetOperation(expression, cancel) switch
    {
        ILocalReferenceOperation local => !local.Local.IsConst && !(written && (local.Local.IsUsing || local.Local.IsForEach || local.Local.RefKind == RefKind.RefReadOnly)),
        IParameterReferenceOperation parameter => !(written && parameter.Parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter),
        IFieldReferenceOperation field => !field.Field.IsConst && !(written && field.Field.IsReadOnly),
        IArrayElementReferenceOperation => true,
        IInvocationOperation invocation => invocation.TargetMethod.ReturnsByRef || (!written && invocation.TargetMethod.ReturnsByRefReadonly),
        IPropertyReferenceOperation property => property.Property.ReturnsByRef || (!written && property.Property.ReturnsByRefReadonly),
        _ => false,
    };

    private static BaseArgumentListSyntax? ArgumentsOf(SyntaxNode call) => call switch
    {
        InvocationExpressionSyntax invocation => invocation.ArgumentList,
        BaseObjectCreationExpressionSyntax creation => creation.ArgumentList ?? SyntaxFactory.ArgumentList(),
        ConstructorInitializerSyntax initializer => initializer.ArgumentList,
        PrimaryConstructorBaseTypeSyntax primary => primary.ArgumentList,
        _ => null,
    };

    private static SyntaxNode WithArguments(SyntaxNode call, List<Passed> plan)
    {
        BaseArgumentListSyntax list = ArgumentsOf(call)!;
        List<ArgumentSyntax> arguments = [];
        foreach (Passed passed in plan)
        {
            ArgumentSyntax argument = passed.Value is { } value
                ? SyntaxFactory.Argument(SyntaxFactory.ParseExpression(value))
                : list.Arguments[passed.Index];
            if (passed.Keyword is { } keyword)
            {
                argument = argument.WithRefKindKeyword(keyword.Length == 0 ? default : SyntaxFactory.Token(SyntaxFacts.GetKeywordKind(keyword)).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithExpression(argument.Expression.WithoutLeadingTrivia());
            }
            if (passed.Name is { } name && argument.NameColon is null)
            {
                argument = argument.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(name)).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithExpression(argument.Expression.WithoutLeadingTrivia());
            }
            int at = arguments.Count;
            argument = argument.WithLeadingTrivia(at < list.Arguments.Count ? list.Arguments[at].GetLeadingTrivia() : default)
                .WithTrailingTrivia(at < list.Arguments.Count ? list.Arguments[at].GetTrailingTrivia() : default);
            arguments.Add(argument);
        }
        SeparatedSyntaxList<ArgumentSyntax> made = SyntaxFactory.SeparatedList(arguments, Separators(list.Arguments.GetSeparators().ToList(), arguments.Count));
        return call switch
        {
            InvocationExpressionSyntax invocation => invocation.WithArgumentList(invocation.ArgumentList.WithArguments(made)),
            ObjectCreationExpressionSyntax creation => creation.WithArgumentList((creation.ArgumentList ?? SyntaxFactory.ArgumentList()).WithArguments(made)),
            ImplicitObjectCreationExpressionSyntax creation => creation.WithArgumentList(creation.ArgumentList.WithArguments(made)),
            ConstructorInitializerSyntax initializer => initializer.WithArgumentList(initializer.ArgumentList.WithArguments(made)),
            PrimaryConstructorBaseTypeSyntax primary => primary.WithArgumentList(primary.ArgumentList.WithArguments(made)),
            _ => call,
        };
    }

    /// <summary>The method marked in its first declaration renamed, with its hierarchy and calls, by Roslyn's Renamer.</summary>
    private static async Task<Solution> RenameMethodAsync(Solution solution, SyntaxAnnotation marker, string name, CancellationToken cancel)
    {
        foreach (Document document in solution.Projects.SelectMany(p => p.Documents))
        {
            SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
            if (!root.ContainsAnnotations || root.GetAnnotatedNodes(marker).FirstOrDefault() is not { } declaration)
            {
                continue;
            }
            SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
            if (model.GetDeclaredSymbol(declaration, cancel) is IMethodSymbol method)
            {
                SymbolRenameOptions options = new(RenameOverloads: false, RenameInStrings: false, RenameInComments: true, RenameFile: false);
                return await Renamer.RenameSymbolAsync(solution, method, options, name, cancel).ConfigureAwait(false);
            }
        }
        return solution;
    }

    private static string Describe(IMethodSymbol method) => method.MethodKind == MethodKind.Constructor
        ? $"constructor '{method.ContainingType.Name}'"
        : $"method '{method.ContainingType.Name}.{method.Name}'";
}
