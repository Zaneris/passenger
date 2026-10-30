using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace Passenger.Roslyn.Extension;

/// <summary>Pull Members Up and Push Members Down: members of a class moved into a base class (or declared in an
/// interface it implements) or into every derived class chosen, each one made abstract where it stays when asked, its
/// overrides then marked so; an override pulled up takes the place of the base's abstract declaration, and a chosen
/// class's override of a member pushed down becomes its own, virtual. The files they go to get the usings of the file
/// they came from that they need.</summary>
internal static class MemberMoves
{
    public static async Task<Hierarchy> DescribeAsync(Document document, Place at, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        (INamedTypeSymbol? type, _) = await Changes.TypeAtAsync(document, offset, cancel).ConfigureAwait(false);
        if (type is null || type.TypeKind != TypeKind.Class || type.IsRecord)
        {
            return new Hierarchy("Pull Members Up and Push Members Down move a class's members: put the caret on one", string.Empty, [], [], []);
        }
        List<Member> bases = [];
        for (INamedTypeSymbol? up = type.BaseType; up is not null && InSource(up); up = up.BaseType)
        {
            bases.Add(TypeOf(up));
        }
        bases.AddRange(type.AllInterfaces.Where(InSource).Select(TypeOf));
        IEnumerable<INamedTypeSymbol> derived = await SymbolFinder.FindDerivedClassesAsync(type, document.Project.Solution, transitive: false, cancellationToken: cancel).ConfigureAwait(false);
        return new Hierarchy(null, type.Name, [.. Movable(type).Select(MemberOf)], bases, [.. derived.Where(InSource).Select(TypeOf)]);
    }

    public static async Task<Refactored> PullUpAsync(Document document, MembersMove move, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, string? problem) = await SourceAsync(document, move, cancel).ConfigureAwait(false);
        if (type is null)
        {
            return Changes.Refused(problem!);
        }
        Compilation compilation = (await document.Project.GetCompilationAsync(cancel).ConfigureAwait(false))!;
        if (Bases(type).Concat(type.AllInterfaces).FirstOrDefault(b => InSource(b) && TypeId(b) == move.Target) is not { } target)
        {
            return Changes.Refused($"Choose a base type of '{type.Name}' in this solution to pull members up to");
        }
        INamedTypeSymbol declaring = target.OriginalDefinition;
        Dictionary<ITypeParameterSymbol, ITypeParameterSymbol> named = NamedBy(type, target);
        List<ISymbol> members = Chosen(type, move.Members);
        if (members.Count == 0)
        {
            return Changes.Refused("Select the members to pull up");
        }
        Solution solution = document.Project.Solution;
        Editors editors = new(solution, cancel);
        TypeDeclarationSyntax into = await DeclarationAsync(declaring, cancel).ConfigureAwait(false);
        DocumentEditor targetEditor = await editors.OfAsync(into).ConfigureAwait(false);
        HashSet<Document> sources = [];
        bool needsAbstract = false;
        HashSet<INamedTypeSymbol> below = new(SymbolEqualityComparer.Default) { type.OriginalDefinition };
        if (target.TypeKind != TypeKind.Interface)
        {
            below.UnionWith(Bases(type).TakeWhile(b => !Same(b, target)).Select(b => b.OriginalDefinition));
        }
        HashSet<ISymbol> moving = new(members.Select(m => m.OriginalDefinition), SymbolEqualityComparer.Default);
        foreach (ISymbol member in members)
        {
            SyntaxNode declared = await DeclarationAsync(member, cancel).ConfigureAwait(false);
            sources.Add(solution.GetDocument(declared.SyntaxTree)!);
            DocumentEditor sourceEditor = await editors.OfAsync(declared).ConfigureAwait(false);
            bool makeAbstract = move.Abstract.Contains(Id(member));
            ISymbol? replaced = target.TypeKind != TypeKind.Interface && !makeAbstract && Overridden(member) is { IsAbstract: true } overridden && Same(overridden.ContainingType, target)
                ? overridden
                : null;
            SemanticModel model = compilation.GetSemanticModel(declared.SyntaxTree);
            if (PullProblem(model, member, declared, type, target, below, moving, named, makeAbstract, replaced is not null, cancel) is { } wrong)
            {
                return Changes.Refused(wrong);
            }
            if (replaced is not null && NamesOtherwise(replaced, target, named))
            {
                return Changes.Refused($"'{member.Name}' overrides {Interfaces.Text(replaced.OriginalDefinition)} as '{Shown(target)}' gives it, so it cannot take the place of that declaration in '{target.Name}'");
            }
            if (target.TypeKind != TypeKind.Interface && (makeAbstract || member.IsAbstract)
                && await UnimplementedAsync(solution, member, type, declaring, makeAbstract, cancel).ConfigureAwait(false) is { } lacking)
            {
                return Changes.Refused(lacking);
            }
            if (target.TypeKind == TypeKind.Interface)
            {
                editors.AddMember(targetEditor, into, RenamedAsGenerated(Interfaces.Declare(targetEditor.Generator, member), member, named));
                continue;
            }
            SyntaxNode Written(SyntaxNode node) => Renamed(model, node, named, cancel);
            if (makeAbstract)
            {
                needsAbstract = true;
                editors.AddMember(targetEditor, into, Abstract(targetEditor.Generator, Written(declared), member.IsOverride));
                sourceEditor.ReplaceNode(declared, (current, generator) => generator.WithModifiers(current, generator.GetModifiers(current).WithIsVirtual(false).WithIsOverride(true)));
                continue;
            }
            needsAbstract |= member.IsAbstract;
            SyntaxNode moved = Alone(declared, Written);
            moved = member.DeclaredAccessibility == Accessibility.Private ? targetEditor.Generator.WithAccessibility(moved, Accessibility.Protected) : moved;
            if (replaced is not null)
            {
                SyntaxNode stated = await DeclarationAsync(replaced, cancel).ConfigureAwait(false);
                (await editors.OfAsync(stated).ConfigureAwait(false)).ReplaceNode(stated, (current, generator) => InPlaceOf(generator, moved, current, replaced.IsOverride));
            }
            else
            {
                editors.AddMember(targetEditor, into, moved);
            }
            Remove(sourceEditor, declared);
        }
        if (needsAbstract && !target.IsAbstract)
        {
            if (await ConstructedAsync(solution, declaring, cancel).ConfigureAwait(false) is { } made)
            {
                return Changes.Refused(made);
            }
            targetEditor.ReplaceNode(into, (current, generator) => generator.WithModifiers(current, generator.GetModifiers(current).WithIsAbstract(true)));
        }
        Solution changed = await editors.ChangedAsync(sources).ConfigureAwait(false);
        if (target.TypeKind == TypeKind.Interface && await UnimplementedInterfaceAsync(solution, changed, declaring, cancel).ConfigureAwait(false) is { } unimplemented)
        {
            return Changes.Refused(unimplemented);
        }
        return await Changes.BetweenAsync(solution, changed, cancel).ConfigureAwait(false);
    }

    public static async Task<Refactored> PushDownAsync(Document document, MembersMove move, CancellationToken cancel)
    {
        (INamedTypeSymbol? type, string? problem) = await SourceAsync(document, move, cancel).ConfigureAwait(false);
        if (type is null)
        {
            return Changes.Refused(problem!);
        }
        Solution solution = document.Project.Solution;
        IEnumerable<INamedTypeSymbol> derived = await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: false, cancellationToken: cancel).ConfigureAwait(false);
        HashSet<string> wanted = [.. move.Target.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        List<INamedTypeSymbol> targets = [.. derived.Where(d => InSource(d) && wanted.Contains(TypeId(d)))];
        if (targets.Count == 0)
        {
            return Changes.Refused($"Choose the classes derived from '{type.Name}' to push members down to");
        }
        List<ISymbol> members = Chosen(type, move.Members);
        if (members.Count == 0)
        {
            return Changes.Refused("Select the members to push down");
        }
        Editors editors = new(solution, cancel);
        TypeDeclarationSyntax from = await DeclarationAsync(type, cancel).ConfigureAwait(false);
        DocumentEditor sourceEditor = await editors.OfAsync(from).ConfigureAwait(false);
        bool needsAbstract = false;
        List<TypeDeclarationSyntax> intos = [];
        foreach (INamedTypeSymbol target in targets)
        {
            intos.Add(await DeclarationAsync(target, cancel).ConfigureAwait(false));
        }
        List<SyntaxNode> pushed = [];
        foreach (ISymbol member in members)
        {
            pushed.Add(await DeclarationAsync(member, cancel).ConfigureAwait(false));
        }
        foreach (ISymbol member in members)
        {
            bool keepAbstract = move.Abstract.Contains(Id(member));
            if (keepAbstract && (member is IFieldSymbol || member.IsStatic || Explicit(member)))
            {
                return Changes.Refused($"'{member.Name}' cannot be made abstract");
            }
            if (await PushProblemAsync(solution, member, type, targets, pushed, keepAbstract, cancel).ConfigureAwait(false) is { } wrong)
            {
                return Changes.Refused(wrong);
            }
        }
        foreach ((ISymbol member, SyntaxNode declared) in members.Zip(pushed))
        {
            bool keepAbstract = move.Abstract.Contains(Id(member));
            if (keepAbstract && member.IsAbstract)
            {
                continue;
            }
            foreach ((INamedTypeSymbol target, TypeDeclarationSyntax into) in targets.Zip(intos))
            {
                if (Declares(target, member))
                {
                    if (!keepAbstract && !member.IsOverride && OverrideIn(target, member) is { } own)
                    {
                        SyntaxNode stated = await DeclarationAsync(own, cancel).ConfigureAwait(false);
                        (await editors.OfAsync(stated).ConfigureAwait(false)).ReplaceNode(stated, (current, generator) =>
                            generator.WithModifiers(current, generator.GetModifiers(current).WithIsOverride(false).WithIsSealed(false).WithIsVirtual(!own.IsSealed)));
                    }
                    continue;
                }
                DocumentEditor targetEditor = await editors.OfAsync(into).ConfigureAwait(false);
                SyntaxNode copy = keepAbstract
                    ? targetEditor.Generator.WithModifiers(declared, targetEditor.Generator.GetModifiers(declared).WithIsAbstract(false).WithIsVirtual(false).WithIsOverride(true))
                    : Alone(declared);
                editors.AddMember(targetEditor, into, copy);
            }
            if (keepAbstract)
            {
                needsAbstract = true;
                sourceEditor.ReplaceNode(declared, (current, generator) => Abstract(generator, current, member.IsOverride).WithTriviaFrom(current));
            }
            else
            {
                Remove(sourceEditor, declared);
            }
        }
        if (needsAbstract && !type.IsAbstract)
        {
            if (await ConstructedAsync(solution, type, cancel).ConfigureAwait(false) is { } made)
            {
                return Changes.Refused(made);
            }
            sourceEditor.ReplaceNode(from, (current, generator) => generator.WithModifiers(current, generator.GetModifiers(current).WithIsAbstract(true)));
        }
        Solution changed = await editors.ChangedAsync([solution.GetDocument(from.SyntaxTree)!]).ConfigureAwait(false);
        return await Changes.BetweenAsync(solution, changed, cancel).ConfigureAwait(false);
    }

    /// <summary>The class at the caret, when it is one whose members can move.</summary>
    private static async Task<(INamedTypeSymbol? Type, string? Problem)> SourceAsync(Document document, MembersMove move, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, move.At, cancel).ConfigureAwait(false);
        (INamedTypeSymbol? type, _) = await Changes.TypeAtAsync(document, offset, cancel).ConfigureAwait(false);
        return type is null || type.TypeKind != TypeKind.Class || type.IsRecord
            ? (null, "Pull Members Up and Push Members Down move a class's members: put the caret on one")
            : (type, null);
    }

    /// <summary>The members of a class that can move: its methods, properties, indexers, events and fields, declared in
    /// source, in their order.</summary>
    private static IEnumerable<ISymbol> Movable(INamedTypeSymbol type) => type.GetMembers().Where(m =>
        !m.IsImplicitlyDeclared && m.Locations.Any(l => l.IsInSource) && m switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol or IEventSymbol => true,
            IFieldSymbol field => field.AssociatedSymbol is null,
            _ => false,
        });

    private static List<ISymbol> Chosen(INamedTypeSymbol type, IReadOnlyList<string> ids) => [.. Movable(type).Where(m => ids.Contains(Id(m)))];

    private static IEnumerable<INamedTypeSymbol> Bases(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? up = type.BaseType; up is not null; up = up.BaseType)
        {
            yield return up;
        }
    }

    private static bool InSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    private static string Id(ISymbol symbol) => DocumentationCommentId.CreateDeclarationId(symbol) ?? symbol.Name;

    /// <summary>A type's id as the hierarchy names it: its documentation comment id, a constructed generic one's with its type
    /// arguments (T:Shop.IShape{System.Int32}), so two constructions of one type are told apart.</summary>
    private static string TypeId(INamedTypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition) ? Id(type) : "T:" + DocumentationCommentId.CreateReferenceId(type);

    private static Member TypeOf(INamedTypeSymbol type) => new(TypeId(type), Shown(type), type.TypeKind == TypeKind.Interface ? "interface" : "class");

    /// <summary>A type as the class names it: IShape&lt;int&gt;.</summary>
    private static string Shown(INamedTypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static Member MemberOf(ISymbol member) => new(Id(member), Interfaces.Text(member), member switch
    {
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => "field",
    });

    /// <summary>A member's declaration: a field's whole declaration when it declares that field alone.</summary>
    private static async Task<SyntaxNode> DeclarationAsync(ISymbol member, CancellationToken cancel)
    {
        SyntaxNode node = await member.DeclaringSyntaxReferences[0].GetSyntaxAsync(cancel).ConfigureAwait(false);
        return node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } && field.Declaration.Variables.Count == 1 ? field : node;
    }

    /// <summary>A member's declaration as it goes into another type, written there as <paramref name="written"/> writes the
    /// code it is in: one variable of a field declaring several as a field declaration of its own, with the field's
    /// modifiers, attributes and comment.</summary>
    private static SyntaxNode Alone(SyntaxNode declared, Func<SyntaxNode, SyntaxNode>? written = null)
    {
        written ??= node => node;
        if (declared is not VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } list } variable)
        {
            return written(declared);
        }
        int at = list.Variables.IndexOf(variable);
        BaseFieldDeclarationSyntax whole = (BaseFieldDeclarationSyntax)written(field);
        return whole.WithDeclaration(whole.Declaration.WithVariables(SyntaxFactory.SingletonSeparatedList(whole.Declaration.Variables[at].WithoutTrivia())));
    }

    /// <summary>Whether a type already declares a member as the one moved would be declared there: a method or indexer
    /// of the same parameters, anything else of the same name and kind.</summary>
    private static bool Declares(INamedTypeSymbol type, ISymbol member) => type.GetMembers(member.Name).Any(m => m.Kind == member.Kind && !m.IsImplicitlyDeclared && (m, member) switch
    {
        (IMethodSymbol theirs, IMethodSymbol ours) => theirs.Arity == ours.Arity
            && SameParameters((theirs.Arity == 0 ? theirs : theirs.Construct([.. ours.TypeParameters])).Parameters, ours.Parameters),
        (IPropertySymbol theirs, IPropertySymbol ours) => SameParameters(theirs.Parameters, ours.Parameters),
        _ => true,
    });

    /// <summary>Whether two lists of parameters overload as one: the same types, each passed by reference or by value
    /// alike.</summary>
    private static bool SameParameters(IReadOnlyList<IParameterSymbol> theirs, IReadOnlyList<IParameterSymbol> ours) =>
        theirs.Count == ours.Count && theirs.Zip(ours).All(p => SymbolEqualityComparer.Default.Equals(p.First.Type, p.Second.Type)
            && (p.First.RefKind == RefKind.None) == (p.Second.RefKind == RefKind.None));

    /// <summary>Why a member cannot be pushed down into the targets; null when it can. Code that would still reach it where
    /// it is gone (the base's own, a class not chosen, anything through the base's type or a base. call), an override in a
    /// class not chosen, or an interface the base or a class not chosen implements with it would not build; kept abstract
    /// in the base, a base. call or a class left without it would not. What an override overrides stays, reached by all
    /// but a base. call; where that is abstract, the base, unless abstract, or a class left without it would not build.</summary>
    private static async Task<string?> PushProblemAsync(Solution solution, ISymbol member, INamedTypeSymbol type, IReadOnlyList<INamedTypeSymbol> targets,
        IReadOnlyList<SyntaxNode> pushed, bool keepAbstract, CancellationToken cancel)
    {
        bool Gets(ITypeSymbol? holder) => holder switch
        {
            ITypeParameterSymbol parameter => parameter.ConstraintTypes.Any(Gets),
            INamedTypeSymbol named => targets.Any(t => named.OriginalDefinition.Equals(t.OriginalDefinition, SymbolEqualityComparer.Default)) || Gets(named.BaseType),
            _ => false,
        };
        string text = Interfaces.Text(member);
        string Hint(IEnumerable<INamedTypeSymbol> lacking)
        {
            INamedTypeSymbol[] those = [.. lacking.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)];
            return those.Length > 0 && those.All(l => l.BaseType is { } up && Same(up, type))
                ? $": push it down to {string.Join(" and ", those.Select(l => $"'{l.Name}'"))} too"
                : string.Empty;
        }
        List<INamedTypeSymbol> overriding = [];
        foreach (ISymbol over in await SymbolFinder.FindOverridesAsync(member, solution, cancellationToken: cancel).ConfigureAwait(false))
        {
            overriding.Add(over.ContainingType);
            if (!keepAbstract && !member.IsOverride && !Gets(over.ContainingType))
            {
                return $"'{over.ContainingType.Name}' overrides {text}, which it would no longer have{Hint([over.ContainingType])}";
            }
        }
        IEnumerable<INamedTypeSymbol> below = await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancel).ConfigureAwait(false);
        if (!keepAbstract && !member.IsOverride)
        {
            foreach (INamedTypeSymbol holder in below.Where(d => !Gets(d)).Prepend(type))
            {
                if (Implementing(holder, member) is { } implemented)
                {
                    string hint = !Same(holder, type) ? Hint([holder])
                        : member.IsStatic || Explicit(member) ? string.Empty
                        : $": keep it abstract in '{type.Name}'";
                    return $"'{holder.Name}' implements {implemented.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} with {text}, which it would no longer have{hint}";
                }
            }
        }
        ISymbol? above = !keepAbstract && Overridden(member) is { IsAbstract: true } overridden ? overridden : null;
        if (above is not null && !type.IsAbstract)
        {
            return $"'{type.Name}' would not implement {text}, abstract in '{above.ContainingType.Name}': keep it abstract in '{type.Name}'";
        }
        if ((keepAbstract && !member.IsAbstract) || above is not null)
        {
            string abstractIn = above is not null ? $"abstract in '{above.ContainingType.Name}'" : $"kept abstract in '{type.Name}'";
            foreach (INamedTypeSymbol derived in below)
            {
                if (!derived.IsAbstract && !Gets(derived) && !Bases(derived).Prepend(derived).TakeWhile(b => !Same(b, type)).Any(b => overriding.Any(o => Same(o, b))))
                {
                    return $"'{derived.Name}' would not implement {text}, {abstractIn}{Hint([derived])}";
                }
            }
        }
        List<(string Who, INamedTypeSymbol? Through)> left = [];
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(member, solution, cancel).ConfigureAwait(false))
        {
            if (!SymbolEqualityComparer.Default.Equals(referenced.Definition.OriginalDefinition, member.OriginalDefinition))
            {
                continue;
            }
            foreach (ReferenceLocation location in referenced.Locations.Where(l => !l.IsCandidateLocation))
            {
                SyntaxNode root = (await location.Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
                SyntaxNode node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node.AncestorsAndSelf().Any(n => n is CrefSyntax))
                {
                    continue;
                }
                SemanticModel model = (await location.Document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
                bool moves = pushed.Any(d => d.SyntaxTree == node.SyntaxTree && d.Span.Contains(node.Span));
                (Reach reach, ITypeSymbol? through) = location.IsImplicit ? (Reach.Typed, null) : ReachOf(model, node, member.ContainingType, member is IPropertySymbol { IsIndexer: true }, cancel);
                bool breaks = reach switch
                {
                    Reach.Base => moves || !Gets(through),
                    _ when member.IsOverride => false,
                    Reach.Own => !moves && !keepAbstract && !Gets(through),
                    _ => !keepAbstract && !Gets(through),
                };
                if (breaks)
                {
                    left.Add(($"{User(model, node, cancel)} in {Path.GetFileName(location.Document.FilePath ?? location.Document.Name)}", through as INamedTypeSymbol));
                }
            }
        }
        if (left.Count == 0)
        {
            return null;
        }
        string Item((string Who, INamedTypeSymbol? Through) use) => use.Through is { } via ? $"{use.Who}, through '{via.Name}'" : use.Who;
        string uses = string.Join("; ", left.Take(3).Select(Item)) + (left.Count > 3 ? $"; and {left.Count - 3} more" : string.Empty);
        return $"'{member.Name}' would be gone from code still using it: {uses}"
            + (left.All(l => l.Through is not null) ? Hint(left.Select(l => l.Through!)) : string.Empty);
    }

    /// <summary>How a name reaches its member: as the code's own (alone, or through this), through base, or through a
    /// value, a type's name or an object made.</summary>
    private enum Reach
    {
        Own,
        Base,
        Typed,
    }

    /// <summary>How a reference reaches its member, and the type it is reached through, where known: for the code's own,
    /// the type the code is in that has the member.</summary>
    private static (Reach Reach, ITypeSymbol? Through) ReachOf(SemanticModel model, SyntaxNode node, INamedTypeSymbol holder, bool indexer, CancellationToken cancel)
    {
        INamedTypeSymbol? Enclosing()
        {
            for (INamedTypeSymbol? type = model.GetEnclosingSymbol(node.SpanStart, cancel)?.ContainingType ?? model.GetEnclosingSymbol(node.SpanStart, cancel) as INamedTypeSymbol;
                type is not null; type = type.ContainingType)
            {
                for (INamedTypeSymbol? up = type; up is not null; up = up.BaseType)
                {
                    if (Same(up, holder))
                    {
                        return type;
                    }
                }
            }
            return holder;
        }
        ITypeSymbol? TypeOf(SyntaxNode expression) => model.GetTypeInfo(expression, cancel).Type;
        ITypeSymbol? Conditional(SyntaxNode binding) => binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional ? TypeOf(conditional.Expression) : null;
        if (indexer)
        {
            return node.FirstAncestorOrSelf<ExpressionSyntax>(e => e is ElementAccessExpressionSyntax or ElementBindingExpressionSyntax) switch
            {
                ElementAccessExpressionSyntax { Expression: BaseExpressionSyntax } => (Reach.Base, Enclosing()?.BaseType),
                ElementAccessExpressionSyntax { Expression: ThisExpressionSyntax } => (Reach.Own, Enclosing()),
                ElementAccessExpressionSyntax access => (Reach.Typed, TypeOf(access.Expression)),
                ElementBindingExpressionSyntax binding => (Reach.Typed, Conditional(binding)),
                _ => (Reach.Typed, null),
            };
        }
        SyntaxNode name = node is SimpleNameSyntax ? node : node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault() ?? node;
        return name.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == name => access.Expression switch
            {
                BaseExpressionSyntax => (Reach.Base, Enclosing()?.BaseType),
                ThisExpressionSyntax => (Reach.Own, Enclosing()),
                _ => (Reach.Typed, TypeOf(access.Expression)),
            },
            MemberBindingExpressionSyntax binding => (Reach.Typed, Conditional(binding)),
            NameColonSyntax { Parent: SubpatternSyntax { Parent: PropertyPatternClauseSyntax { Parent: RecursivePatternSyntax pattern } } } =>
                (Reach.Typed, model.GetTypeInfo(pattern, cancel).ConvertedType),
            QualifiedNameSyntax qualified when qualified.Right == name => (Reach.Typed, model.GetSymbolInfo(qualified.Left, cancel).Symbol as ITypeSymbol),
            AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax { Parent: { } made } } assignment when assignment.Left == name => (Reach.Typed, TypeOf(made)),
            _ => (Reach.Own, Enclosing()),
        };
    }

    /// <summary>The member whose code a place is in, as Type.Member, or its type's name outside any member.</summary>
    private static string User(SemanticModel model, SyntaxNode node, CancellationToken cancel)
    {
        ISymbol? symbol = model.GetEnclosingSymbol(node.SpanStart, cancel);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }
        symbol = symbol is IMethodSymbol { AssociatedSymbol: { } associated } ? associated : symbol;
        return symbol switch
        {
            INamedTypeSymbol type => type.Name,
            { ContainingType: { } type } => $"{type.Name}.{symbol.Name}",
            _ => symbol?.Name ?? "code",
        };
    }

    /// <summary>An interface a type implements with a member, where it does.</summary>
    private static INamedTypeSymbol? Implementing(INamedTypeSymbol type, ISymbol member) => type.AllInterfaces.FirstOrDefault(i => i.GetMembers().Any(m =>
        type.FindImplementationForInterfaceMember(m) is { } found && SymbolEqualityComparer.Default.Equals(found.OriginalDefinition, member.OriginalDefinition)));

    /// <summary>Whether a member is an explicit implementation of an interface's member.</summary>
    private static bool Explicit(ISymbol member) => member switch
    {
        IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0,
        IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
        IEventSymbol @event => @event.ExplicitInterfaceImplementations.Length > 0,
        _ => false,
    };

    /// <summary>A type's own override of a member, where it declares one.</summary>
    private static ISymbol? OverrideIn(INamedTypeSymbol type, ISymbol member) =>
        type.GetMembers(member.Name).FirstOrDefault(m => Overridden(m) is { } over && SymbolEqualityComparer.Default.Equals(over.OriginalDefinition, member.OriginalDefinition));

    /// <summary>Why a member declared abstract in the target class, kept abstract or abstract already, would leave classes
    /// below it that are not abstract without it, naming each whose bases up to the target pass neither the class it comes
    /// from nor one overriding what it overrides; null when none would.</summary>
    private static async Task<string?> UnimplementedAsync(Solution solution, ISymbol member, INamedTypeSymbol type, INamedTypeSymbol target, bool keptAbstract,
        CancellationToken cancel)
    {
        List<INamedTypeSymbol> overriding = [];
        if (Overridden(member) is { } above)
        {
            overriding.AddRange((await SymbolFinder.FindOverridesAsync(above, solution, cancellationToken: cancel).ConfigureAwait(false)).Select(o => o.ContainingType));
        }
        IEnumerable<INamedTypeSymbol> below = await SymbolFinder.FindDerivedClassesAsync(target, solution, transitive: true, cancellationToken: cancel).ConfigureAwait(false);
        string[] lacking = [.. below.Where(d => !d.IsAbstract && !Bases(d).Prepend(d).TakeWhile(b => !Same(b, target)).Any(b => Same(b, type) || overriding.Any(o => Same(o, b))))
            .Select(d => $"'{d.Name}'").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return lacking.Length == 0 ? null : $"{Names(lacking)} would not implement {Interfaces.Text(member)}, {(keptAbstract ? "kept abstract" : "abstract")} in '{target.Name}'";
    }

    /// <summary>Why members a pull declared in an interface would leave types implementing it without them, naming each
    /// class or struct listing it, or an interface below it, that neither declares nor inherits a member implementing one,
    /// an abstract member counting; null when none would.</summary>
    private static async Task<string?> UnimplementedInterfaceAsync(Solution solution, Solution changed, INamedTypeSymbol contract, CancellationToken cancel)
    {
        async Task<Compilation?> NowAsync(ISymbol symbol) => solution.GetProject(symbol.ContainingAssembly, cancel) is { } project && changed.GetProject(project.Id) is { } now
            ? await now.GetCompilationAsync(cancel).ConfigureAwait(false)
            : null;
        HashSet<string> had = [.. contract.GetMembers().Select(Id)];
        if (await NowAsync(contract).ConfigureAwait(false) is not { } declaring || DocumentationCommentId.GetFirstSymbolForDeclarationId(Id(contract), declaring) is not INamedTypeSymbol declared)
        {
            return null;
        }
        List<(INamedTypeSymbol Type, Compilation Compilation)> listing = [];
        foreach (INamedTypeSymbol implementer in await SymbolFinder.FindImplementationsAsync(contract, solution, transitive: true, cancellationToken: cancel).ConfigureAwait(false))
        {
            if (InSource(implementer) && implementer.Interfaces.Any(i => Same(i, contract) || i.AllInterfaces.Any(a => Same(a, contract)))
                && await NowAsync(implementer).ConfigureAwait(false) is { } compilation
                && DocumentationCommentId.GetFirstSymbolForDeclarationId(Id(implementer), compilation) is INamedTypeSymbol type)
            {
                listing.Add((type, compilation));
            }
        }
        foreach (ISymbol member in declared.GetMembers().Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol && !had.Contains(Id(m))))
        {
            string[] lacking = [.. listing.Where(l => !Implements(l.Type, DocumentationCommentId.GetFirstSymbolForDeclarationId(Id(member), l.Compilation)))
                .Select(l => $"'{l.Type.Name}'").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            if (lacking.Length > 0)
            {
                return $"{Names(lacking)} would not implement {Interfaces.Text(member)}, declared in '{contract.Name}'";
            }
        }
        return null;
    }

    /// <summary>Whether a type implements an interface's member, each accessor of a property, under every construction of
    /// that interface it has.</summary>
    private static bool Implements(INamedTypeSymbol type, ISymbol? member) => member is null
        || type.AllInterfaces.SelectMany(i => i.GetMembers(member.Name)).Where(m => SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, member))
            .SelectMany(m => m is IPropertySymbol property ? [property.GetMethod, property.SetMethod] : new ISymbol?[] { m })
            .All(m => m is null || type.FindImplementationForInterfaceMember(m) is not null);

    /// <summary>Why a class made abstract would leave code that makes one failing, naming each member that does, by its
    /// file: an object made of it, or it given as a type argument that must be constructed; null where nothing makes one.</summary>
    private static async Task<string?> ConstructedAsync(Solution solution, INamedTypeSymbol type, CancellationToken cancel)
    {
        SortedSet<string> places = new(StringComparer.Ordinal);
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(type, solution, cancel).ConfigureAwait(false))
        {
            bool made = referenced.Definition is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor && Same(constructor.ContainingType, type);
            if (!made && !(referenced.Definition is INamedTypeSymbol named && Same(named, type)))
            {
                continue;
            }
            foreach (ReferenceLocation location in referenced.Locations.Where(l => !l.IsCandidateLocation))
            {
                SyntaxNode root = (await location.Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
                SyntaxNode node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                SemanticModel model = (await location.Document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
                if (!node.AncestorsAndSelf().Any(n => n is CrefSyntax) && (made ? Creates(model, node, type, cancel) : Constructed(model, node, cancel)))
                {
                    places.Add($"{User(model, node, cancel)} in {Path.GetFileName(location.Document.FilePath ?? location.Document.Name)}");
                }
            }
        }
        return places.Count == 0 ? null : $"'{type.Name}' would be made abstract, but code makes one: {string.Join("; ", places)}";
    }

    /// <summary>Whether a reference to a constructor is an object made of a type.</summary>
    private static bool Creates(SemanticModel model, SyntaxNode node, INamedTypeSymbol type, CancellationToken cancel) =>
        node.FirstAncestorOrSelf<BaseObjectCreationExpressionSyntax>() is { } creation
        && model.GetSymbolInfo(creation, cancel).Symbol is IMethodSymbol { ContainingType: { } made } && Same(made, type);

    /// <summary>Whether a type's name is given as a type argument whose parameter must be constructed (new()).</summary>
    private static bool Constructed(SemanticModel model, SyntaxNode node, CancellationToken cancel)
    {
        while ((node.Parent is QualifiedNameSyntax qualified && qualified.Right == node) || (node.Parent is AliasQualifiedNameSyntax alias && alias.Name == node))
        {
            node = node.Parent;
        }
        if (node is not TypeSyntax argument || node.Parent is not TypeArgumentListSyntax { Parent: GenericNameSyntax generic } list)
        {
            return false;
        }
        ImmutableArray<ITypeParameterSymbol> parameters = model.GetSymbolInfo(generic, cancel).Symbol switch
        {
            IMethodSymbol method => method.TypeParameters,
            INamedTypeSymbol named => named.TypeParameters,
            _ => [],
        };
        int at = list.Arguments.IndexOf(argument);
        return at >= 0 && at < parameters.Length && parameters[at].HasConstructorConstraint;
    }

    /// <summary>Names quoted, joined as a list is read.</summary>
    private static string Names(string[] names) => names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";

    /// <summary>Why a member cannot be pulled up into a target; null when it can. The target or a class between already
    /// declaring it (but an abstract declaration the member overrides, which it replaces), code that goes with it using
    /// what the target would not reach, or a type parameter of its own named as one it renames (<paramref name="named"/>),
    /// each would leave code that does not build.</summary>
    private static string? PullProblem(SemanticModel model, ISymbol member, SyntaxNode declared, INamedTypeSymbol type, INamedTypeSymbol target,
        IReadOnlySet<INamedTypeSymbol> below, IReadOnlySet<ISymbol> moving, IReadOnlyDictionary<ITypeParameterSymbol, ITypeParameterSymbol> named,
        bool makeAbstract, bool replaces, CancellationToken cancel)
    {
        bool contract = target.TypeKind == TypeKind.Interface;
        if (contract && member is IFieldSymbol)
        {
            return $"'{member.Name}' is a field, which an interface cannot declare";
        }
        if (contract && (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic))
        {
            return $"'{member.Name}' is not a public instance member, which an interface could declare";
        }
        if (makeAbstract && (member is IFieldSymbol || member.IsStatic))
        {
            return $"'{member.Name}' cannot be made abstract";
        }
        if (below.FirstOrDefault(b => !Same(b, type) && Declares(b, member)) is { } between)
        {
            return $"'{between.Name}', between '{type.Name}' and '{target.Name}', also declares {Interfaces.Text(member)}";
        }
        if (!replaces && Declares(target, member))
        {
            return $"'{target.Name}' already declares {Interfaces.Text(member)}";
        }
        if (member is IMethodSymbol method && method.TypeParameters.FirstOrDefault(p => named.Any(n => n.Key.Name != n.Value.Name && (n.Key.Name == p.Name || n.Value.Name == p.Name))) is { } clash)
        {
            return $"'{member.Name}' declares its own {clash.Name}, which '{target.Name}' would read as another type parameter";
        }
        (List<ISymbol> left, ISymbol? unmoved, ISymbol? based) = Reaches(model, contract || makeAbstract ? Signature(declared) : Code(declared), target, below, moving, named, cancel);
        if (unmoved is not null)
        {
            string whose = unmoved is ITypeParameterSymbol ? $"a type parameter of '{unmoved.ContainingType.Name}'" : $"a parameter of the primary constructor of '{unmoved.ContainingType.Name}'";
            return $"'{member.Name}' uses {unmoved.Name}, {whose}, which '{target.Name}' does not have";
        }
        if (contract)
        {
            return null;
        }
        if (based is not null)
        {
            return $"'{member.Name}' uses base.{based.Name}, which in '{target.Name}' would be another member";
        }
        if (left.Count == 0)
        {
            return null;
        }
        bool one = left.Count == 1;
        string names = one ? $"'{left[0].Name}'" : string.Join(", ", left.SkipLast(1).Select(l => $"'{l.Name}'")) + $" and '{left[^1].Name}'";
        string[] holders = [.. left.Select(l => l.ContainingType.Name).Distinct(StringComparer.Ordinal)];
        bool movable = left.All(l => l is not INamedTypeSymbol && Same(l.ContainingType, type));
        return $"'{member.Name}' uses {names}, which {(one ? "stays" : "stay")} in {(holders.Length == 1 ? $"'{holders[0]}'" : $"the classes below '{target.Name}'")}"
            + (movable ? $": pull {(one ? "it" : "them")} up with '{member.Name}'" : string.Empty);
    }

    /// <summary>What code moved into the target would no longer reach: the members and nested types of the classes below
    /// it that do not move, named alone or through this or base, or where the target may not see them; a type parameter
    /// (but one the target names, <paramref name="named"/>) or primary constructor's parameter of those classes; and a
    /// member of the target itself reached through base.</summary>
    private static (List<ISymbol> Left, ISymbol? Unmoved, ISymbol? Based) Reaches(SemanticModel model, IEnumerable<SyntaxNode> code, INamedTypeSymbol target,
        IReadOnlySet<INamedTypeSymbol> below, IReadOnlySet<ISymbol> moving, IReadOnlyDictionary<ITypeParameterSymbol, ITypeParameterSymbol> named,
        CancellationToken cancel)
    {
        List<ISymbol> left = [];
        ISymbol? unmoved = null;
        ISymbol? based = null;
        foreach (SimpleNameSyntax name in code.SelectMany(n => n.DescendantNodesAndSelf()).OfType<SimpleNameSyntax>())
        {
            ISymbol? used = model.GetSymbolInfo(name, cancel).Symbol?.OriginalDefinition;
            if (used is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Type } or IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } })
            {
                unmoved ??= below.Contains(used.ContainingType.OriginalDefinition) && !(used is ITypeParameterSymbol parameter && named.ContainsKey(parameter)) ? used : null;
                continue;
            }
            if (used is not (IFieldSymbol or IPropertySymbol or IEventSymbol or INamedTypeSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary })
                || used.IsImplicitlyDeclared || used.ContainingType is not { } holder || moving.Contains(used))
            {
                continue;
            }
            bool own = Own(name, out bool throughBase);
            if (throughBase && Same(holder, target))
            {
                based ??= used;
            }
            else if (below.Contains(holder.OriginalDefinition) && !Inherited(used, below) && (own || !model.Compilation.IsSymbolAccessibleWithin(used, target.OriginalDefinition))
                && !left.Contains(used, SymbolEqualityComparer.Default))
            {
                left.Add(used);
            }
        }
        return (left, unmoved, based);
    }

    /// <summary>Each type parameter of a class that its base type is given as a type argument (Box&lt;U&gt; : Base&lt;U&gt;), with
    /// the base's own type parameter it is there (T), as code moved into the base names it.</summary>
    private static Dictionary<ITypeParameterSymbol, ITypeParameterSymbol> NamedBy(INamedTypeSymbol type, INamedTypeSymbol target)
    {
        Dictionary<ITypeParameterSymbol, ITypeParameterSymbol> named = new(SymbolEqualityComparer.Default);
        foreach ((ITypeSymbol given, ITypeParameterSymbol own) in target.TypeArguments.Zip(target.OriginalDefinition.TypeParameters))
        {
            if (given is ITypeParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, type))
            {
                named.TryAdd(parameter, own);
            }
        }
        return named;
    }

    /// <summary>Code of the class's declaration with each of its type parameters the base names (<see cref="NamedBy"/>)
    /// written as the base's own.</summary>
    private static SyntaxNode Renamed(SemanticModel model, SyntaxNode node, IReadOnlyDictionary<ITypeParameterSymbol, ITypeParameterSymbol> named, CancellationToken cancel)
    {
        if (named.Count == 0)
        {
            return node;
        }
        Dictionary<SyntaxNode, string> names = [];
        foreach (IdentifierNameSyntax name in node.DescendantNodesAndSelf(descendIntoTrivia: true).OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(name, cancel).Symbol is ITypeParameterSymbol parameter && named.TryGetValue(parameter, out ITypeParameterSymbol? own))
            {
                names[name] = own.Name;
            }
        }
        return node.ReplaceNodes(names.Keys, (original, _) => SyntaxFactory.IdentifierName(names[original]).WithTriviaFrom(original));
    }

    /// <summary>A declaration written from a member's symbol, each type parameter of its class the base names written as the
    /// base's own, by name: in it a type parameter is a name alone, any other type a qualified one. A method's own type
    /// parameter of that name is the one its code means, and stays.</summary>
    private static SyntaxNode RenamedAsGenerated(SyntaxNode declaration, ISymbol member, IReadOnlyDictionary<ITypeParameterSymbol, ITypeParameterSymbol> named)
    {
        HashSet<string> own = member is IMethodSymbol method ? [.. method.TypeParameters.Select(p => p.Name)] : [];
        Dictionary<string, string> names = named.Where(n => !own.Contains(n.Key.Name)).ToDictionary(n => n.Key.Name, n => n.Value.Name, StringComparer.Ordinal);
        IdentifierNameSyntax[] renamed = [.. declaration.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => names.ContainsKey(n.Identifier.ValueText)
            && n.Parent is not (QualifiedNameSyntax or AliasQualifiedNameSyntax or MemberAccessExpressionSyntax))];
        return renamed.Length == 0 ? declaration : declaration.ReplaceNodes(renamed, (original, _) => SyntaxFactory.IdentifierName(names[original.Identifier.ValueText]).WithTriviaFrom(original));
    }

    /// <summary>Whether a member of a generic base, as its declaration names its types, takes one of the base's type
    /// parameters that the class gives a type argument other than one of its own the base names: an override written as
    /// the class has it would be another member there.</summary>
    private static bool NamesOtherwise(ISymbol stated, INamedTypeSymbol target, IReadOnlyDictionary<ITypeParameterSymbol, ITypeParameterSymbol> named)
    {
        ITypeParameterSymbol[] given = [.. target.TypeArguments.Zip(target.OriginalDefinition.TypeParameters)
            .Where(t => !(t.First is ITypeParameterSymbol parameter && named.ContainsKey(parameter))).Select(t => t.Second)];
        IEnumerable<ITypeSymbol> types = stated.OriginalDefinition switch
        {
            IMethodSymbol method => method.Parameters.Select(p => p.Type).Prepend(method.ReturnType),
            IPropertySymbol property => property.Parameters.Select(p => p.Type).Prepend(property.Type),
            IEventSymbol @event => [@event.Type],
            _ => [],
        };
        return given.Length > 0 && types.Any(t => given.Any(p => Mentions(t, p)));
    }

    /// <summary>Whether a type is a type parameter or is made with it.</summary>
    private static bool Mentions(ITypeSymbol type, ITypeParameterSymbol parameter) => type switch
    {
        ITypeParameterSymbol given => SymbolEqualityComparer.Default.Equals(given, parameter),
        IArrayTypeSymbol array => Mentions(array.ElementType, parameter),
        IPointerTypeSymbol pointer => Mentions(pointer.PointedAtType, parameter),
        INamedTypeSymbol named => named.TypeArguments.Any(a => Mentions(a, parameter)) || (named.ContainingType is { } outer && Mentions(outer, parameter)),
        _ => false,
    };

    /// <summary>Whether a name reaches its member as the code's own: alone, or through this or base, rather than through
    /// another object or a type's name.</summary>
    private static bool Own(SimpleNameSyntax name, out bool throughBase)
    {
        throughBase = name.Parent is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } baseAccess && baseAccess.Name == name;
        return name.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == name => access.Expression is ThisExpressionSyntax or BaseExpressionSyntax,
            QualifiedNameSyntax qualified when qualified.Right == name => false,
            AliasQualifiedNameSyntax alias when alias.Name == name => false,
            MemberBindingExpressionSyntax or NameColonSyntax or ExpressionColonSyntax => false,
            AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assignment when assignment.Left == name => false,
            _ => true,
        };
    }

    /// <summary>Whether a member overrides one declared above the classes below the target, which the target then has.</summary>
    private static bool Inherited(ISymbol member, IReadOnlySet<INamedTypeSymbol> below)
    {
        for (ISymbol? up = Overridden(member); up is not null; up = Overridden(up))
        {
            if (!below.Contains(up.ContainingType.OriginalDefinition))
            {
                return true;
            }
        }
        return false;
    }

    private static ISymbol? Overridden(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    private static bool Same(ITypeSymbol one, ITypeSymbol other) => SymbolEqualityComparer.Default.Equals(one.OriginalDefinition, other.OriginalDefinition);

    /// <summary>What of a member's declaration an interface or an abstract declaration takes: all but its bodies and
    /// initializer.</summary>
    private static IEnumerable<SyntaxNode> Signature(SyntaxNode declared) =>
        declared.ChildNodes().Where(n => n is not (BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or AccessorListSyntax));

    /// <summary>A member's code as it moves: one variable of a field declaring several with the field's type and attributes.</summary>
    private static IEnumerable<SyntaxNode> Code(SyntaxNode declared) =>
        declared is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } list } variable
            ? [.. field.AttributeLists, list.Type, variable]
            : [declared];

    /// <summary>An override pulled up in place of the abstract declaration it overrides: virtual, or still an override where
    /// that declaration overrides one above, with that declaration's comment, or its own where that has none.</summary>
    private static SyntaxNode InPlaceOf(SyntaxGenerator generator, SyntaxNode moved, SyntaxNode stated, bool overrides)
    {
        static bool IsDocumentation(SyntaxTrivia trivia) => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
        SyntaxTriviaList leading = stated.GetLeadingTrivia();
        if (!leading.Any(IsDocumentation))
        {
            leading = leading.AddRange(moved.GetLeadingTrivia().Where(IsDocumentation));
        }
        SyntaxNode made = generator.WithModifiers(moved, generator.GetModifiers(moved).WithIsAbstract(false).WithIsSealed(false).WithIsOverride(overrides).WithIsVirtual(!overrides));
        return made.WithLeadingTrivia(leading).WithTrailingTrivia(stated.GetTrailingTrivia()).WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static async Task<TypeDeclarationSyntax> DeclarationAsync(INamedTypeSymbol type, CancellationToken cancel) =>
        (TypeDeclarationSyntax)await type.DeclaringSyntaxReferences[0].GetSyntaxAsync(cancel).ConfigureAwait(false);

    /// <summary>A member made abstract: its signature kept, its body gone, the line it ended kept, for the formatter to
    /// space; still an override where it overrides one above.</summary>
    private static SyntaxNode Abstract(SyntaxGenerator generator, SyntaxNode declaration, bool overrides)
    {
        SyntaxToken semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(declaration.GetLastToken().TrailingTrivia);
        SyntaxNode bare = declaration switch
        {
            MethodDeclarationSyntax method => Ended(method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)).WithSemicolonToken(semicolon),
            BasePropertyDeclarationSyntax property when property.AccessorList is { } accessors =>
                property.WithAccessorList(accessors.WithAccessors(SyntaxFactory.List(accessors.Accessors.Select(a =>
                    Ended(a.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))),
            PropertyDeclarationSyntax { ExpressionBody: not null } property => Ended(property.WithExpressionBody(null).WithSemicolonToken(default))
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))).WithTrailingTrivia(semicolon.TrailingTrivia)),
            _ => declaration,
        };
        if (bare is PropertyDeclarationSyntax { Initializer: not null } initialized)
        {
            bare = initialized.WithInitializer(null).WithSemicolonToken(default);
        }
        return generator.WithModifiers(bare, generator.GetModifiers(bare).WithIsAbstract(true).WithIsVirtual(false).WithIsSealed(false).WithIsOverride(overrides))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    /// <summary>A node whose last token is followed by nothing, its body having gone.</summary>
    private static T Ended<T>(T node) where T : SyntaxNode => node.ReplaceToken(node.GetLastToken(), node.GetLastToken().WithTrailingTrivia());

    /// <summary>A member taken out, with its comment, and what follows it no longer led by a blank line when it came first.</summary>
    private static void Remove(DocumentEditor editor, SyntaxNode declared)
    {
        if (declared.Parent is TypeDeclarationSyntax holder && holder.Members.FirstOrDefault() == declared && holder.Members.Count > 1)
        {
            editor.ReplaceNode(holder.Members[1], (current, _) => Changes.WithBlankLinesOf(current, declared));
        }
        editor.RemoveNode(declared, SyntaxRemoveOptions.KeepNoTrivia);
    }

    /// <summary>The editors of every file a move changes, and the solution they make, the files members went to given
    /// the usings of the files they came from that they need, and formatted where members went in.</summary>
    private sealed class Editors(Solution solution, CancellationToken cancel)
    {
        private readonly Dictionary<DocumentId, DocumentEditor> editors = [];
        private readonly Dictionary<SyntaxNode, int> added = [];

        /// <summary>A member added at the end of a type, a blank line before it unless it is the type's first, with its
        /// documentation comment and no other trivia, for the formatter to indent.</summary>
        public void AddMember(DocumentEditor editor, TypeDeclarationSyntax into, SyntaxNode member)
        {
            int before = added.GetValueOrDefault(into);
            added[into] = before + 1;
            IEnumerable<SyntaxTrivia> comment = member.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
            SyntaxTriviaList leading = into.Members.Count + before > 0 ? SyntaxFactory.TriviaList(SyntaxFactory.EndOfLine("\n")) : default;
            editor.AddMember(into, member.WithLeadingTrivia(leading.AddRange(comment)).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")).WithAdditionalAnnotations(Formatter.Annotation));
        }

        public async Task<DocumentEditor> OfAsync(SyntaxNode node)
        {
            Document document = solution.GetDocument(node.SyntaxTree)!;
            if (!editors.TryGetValue(document.Id, out DocumentEditor? editor))
            {
                editor = await DocumentEditor.CreateAsync(document, cancel).ConfigureAwait(false);
                editors[document.Id] = editor;
            }
            return editor;
        }

        public async Task<Solution> ChangedAsync(IEnumerable<Document> sources)
        {
            Solution changed = solution;
            foreach ((DocumentId id, DocumentEditor editor) in editors)
            {
                changed = changed.WithDocumentSyntaxRoot(id, editor.GetChangedRoot());
            }
            foreach (DocumentId id in editors.Keys)
            {
                Document document = changed.GetDocument(id)!;
                document = await WithUsingsOfAsync(document, sources.Where(s => s.Id != id).Select(s => solution.GetDocument(s.Id)!), cancel).ConfigureAwait(false);
                document = await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancel).ConfigureAwait(false);
                changed = document.Project.Solution;
            }
            return changed;
        }
    }

    /// <summary>A file given the usings of other files that it lacks, the names a generator wrote in it (an interface's
    /// declarations) shortened by them, less those its code then does not need (CS8019).</summary>
    private static async Task<Document> WithUsingsOfAsync(Document document, IEnumerable<Document> others, CancellationToken cancel)
    {
        CompilationUnitSyntax root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        HashSet<string> had = [.. root.Usings.Select(u => u.ToString())];
        List<UsingDirectiveSyntax> added = [];
        foreach (Document other in others)
        {
            CompilationUnitSyntax theirs = (CompilationUnitSyntax)(await other.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
            foreach (UsingDirectiveSyntax directive in theirs.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None)))
            {
                if (had.Add(directive.ToString()))
                {
                    added.Add(directive.WithoutTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));
                }
            }
        }
        if (added.Count == 0)
        {
            return await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancel).ConfigureAwait(false);
        }
        SyntaxAnnotation mark = new();
        Document widened = document.WithSyntaxRoot(root.AddUsings([.. added.Select(u => u.WithAdditionalAnnotations(mark, Formatter.Annotation))]));
        widened = await Simplifier.ReduceAsync(widened, Simplifier.Annotation, cancellationToken: cancel).ConfigureAwait(false);
        SemanticModel model = (await widened.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        SyntaxNode wide = (await widened.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        HashSet<SyntaxNode> unused = [.. model.GetDiagnostics(cancellationToken: cancel).Where(d => d.Id == "CS8019").Select(d => wide.FindNode(d.Location.SourceSpan))];
        List<SyntaxNode> drop = [.. wide.GetAnnotatedNodes(mark).Where(unused.Contains)];
        return drop.Count == 0 ? widened : widened.WithSyntaxRoot(wide.RemoveNodes(drop, SyntaxRemoveOptions.KeepNoTrivia)!);
    }
}
