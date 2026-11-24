using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Passenger.Roslyn.Extension;

/// <summary>Test doubles: a fake of an interface, or of a class with members a fake can override, that a test project sees,
/// written into its Fakes folder with every member the compiler binds recording its calls and answering through a hook a
/// test sets, else a default (a virtual member its class's own answer); and, for a sealed or static class of the solution's
/// own or one no derived class can make, the word for why none can be made, which a seam answers.</summary>
internal static partial class TestDoubles
{
    public static async Task<DoublesOffered> OfferedAsync(Document document, DoublesAsked asked, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, asked.At, cancel).ConfigureAwait(false);
        IReadOnlyList<string> libraries = await ToFillAsync(document, offset, cancel).ConfigureAwait(false) is null ? []
            : Libraries((await document.Project.GetCompilationAsync(cancel).ConfigureAwait(false))!);
        if (await TypeNamedAsync(document, offset, cancel).ConfigureAwait(false) is not { } type)
        {
            return new DoublesOffered(null, null, false, [], libraries);
        }
        string? seam = type.Locations.Any(l => l.IsInSource) ? SeamOf(type) : null;
        bool fakeable = seam is null && CanFake(type);
        if (!fakeable && seam is null)
        {
            return new DoublesOffered(null, null, false, [], libraries);
        }
        List<FakePlace> places = [];
        foreach (Project test in TestProjects(document.Project.Solution, asked.TestProjects))
        {
            if (await test.GetCompilationAsync(cancel).ConfigureAwait(false) is { } compilation && SeenFrom(compilation, type) is not null)
            {
                string file = FakeFile(test, type);
                places.Add(new FakePlace(test.FilePath!, Path.GetFileNameWithoutExtension(test.FilePath!), file, Exists(test.Solution, file)));
            }
        }
        return new DoublesOffered(Shown(type), seam, fakeable && KeepsOwn(type), places, libraries);
    }

    /// <summary>The call a place is in, its missing arguments filled: a double of the library named for each parameter one
    /// can stand in for (an interface, an abstract class or a delegate), else the parameter's type's default, the library's
    /// namespace imported.</summary>
    public static async Task<Refactored> FillAsync(Document document, ArgumentsAsked asked, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, asked.At, cancel).ConfigureAwait(false);
        Compilation compilation = (await document.Project.GetCompilationAsync(cancel).ConfigureAwait(false))!;
        if (!Libraries(compilation).Contains(asked.Library) || await ToFillAsync(document, offset, cancel).ConfigureAwait(false) is not { } found)
        {
            return Changes.Refused($"Fill Arguments with Doubles fills the missing arguments of the call the caret is in with {asked.Library}'s doubles, where its project references it");
        }
        (ArgumentListSyntax list, IReadOnlyList<IParameterSymbol> missing) = found;
        SyntaxGenerator generator = SyntaxGenerator.GetGenerator(document);
        INamedTypeSymbol maker = compilation.GetTypeByMetadataName(Makers[asked.Library].Type)!;
        IEnumerable<ArgumentSyntax> filled = missing.Select(p => Argument(Doubled(p.Type)
            ? (ExpressionSyntax)generator.InvocationExpression(generator.MemberAccessExpression(generator.TypeExpression(maker),
                generator.GenericName(Makers[asked.Library].Method, generator.TypeExpression(p.Type))))
            : LiteralExpression(SyntaxKind.DefaultLiteralExpression)));
        ArgumentListSyntax made = Imported(list.WithArguments(SeparatedList([.. list.Arguments, .. filled]))).WithAdditionalAnnotations(Formatter.Annotation);
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        Document changed = document.WithSyntaxRoot(root.ReplaceNode(list, made));
        changed = await ImportAdder.AddImportsAsync(changed, Simplifier.AddImportsAnnotation, cancellationToken: cancel).ConfigureAwait(false);
        changed = await Simplifier.ReduceAsync(changed, Simplifier.Annotation, cancellationToken: cancel).ConfigureAwait(false);
        changed = await Formatter.FormatAsync(changed, Formatter.Annotation, cancellationToken: cancel).ConfigureAwait(false);
        return await Changes.BetweenAsync(document.Project.Solution, changed.Project.Solution, cancel).ConfigureAwait(false);
    }

    /// <summary>Each library's type and method that make a double of a type.</summary>
    private static readonly Dictionary<string, (string Type, string Method)> Makers = new(StringComparer.Ordinal)
    {
        [MockingLibraries.NSubstitute] = ("NSubstitute.Substitute", "For"),
        [MockingLibraries.Moq] = ("Moq.Mock", "Of"),
        [MockingLibraries.FakeItEasy] = ("FakeItEasy.A", "Fake"),
    };

    /// <summary>The mocking libraries a compilation references, by their assemblies, each with the type that makes its doubles.</summary>
    public static IReadOnlyList<string> Libraries(Compilation compilation) =>
        [.. Makers.Where(m => compilation.ReferencedAssemblyNames.Any(a => a.Name == m.Key) && compilation.GetTypeByMetadataName(m.Value.Type) is not null).Select(m => m.Key)];

    /// <summary>Whether a double of a library stands in for a parameter of a type: an interface, an abstract class or a delegate.</summary>
    private static bool Doubled(ITypeSymbol type) =>
        type.TypeKind is TypeKind.Interface or TypeKind.Delegate || type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: true };

    /// <summary>The innermost call or object creation an offset is in that binds to nothing as written, its arguments all
    /// given by place, and the parameters the overload they fit best leaves without one (none of them ref or out), one of
    /// them a double's; none for anything else.</summary>
    public static async Task<(ArgumentListSyntax List, IReadOnlyList<IParameterSymbol> Missing)?> ToFillAsync(Document document, int offset, CancellationToken cancel)
    {
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        foreach (SyntaxNode node in Changes.TokenAt(root, offset).Parent?.AncestorsAndSelf() ?? [])
        {
            if (node is not (BaseObjectCreationExpressionSyntax { ArgumentList: { } } or InvocationExpressionSyntax))
            {
                continue;
            }
            ArgumentListSyntax list = node is InvocationExpressionSyntax call ? call.ArgumentList : ((BaseObjectCreationExpressionSyntax)node).ArgumentList!;
            SymbolInfo info = model.GetSymbolInfo(node, cancel);
            if (info.Symbol is not null || list.Arguments.Any(a => a.NameColon is not null || !a.RefKindKeyword.IsKind(SyntaxKind.None)))
            {
                return null;
            }
            int given = list.Arguments.Count;
            List<IParameterSymbol>? best = null;
            foreach (IMethodSymbol candidate in info.CandidateSymbols.OfType<IMethodSymbol>())
            {
                List<IParameterSymbol> missing = [.. candidate.Parameters.Skip(given).TakeWhile(p => !p.IsOptional && !p.IsParams)];
                bool fits = candidate.Parameters.Length >= given && Enumerable.Range(0, given).All(i => model.ClassifyConversion(list.Arguments[i].Expression, candidate.Parameters[i].Type).IsImplicit);
                if (fits && missing.Count > (best?.Count ?? 0) && missing.All(p => p.RefKind is RefKind.None or RefKind.In) && missing.Any(p => Doubled(p.Type)))
                {
                    best = missing;
                }
            }
            return best is null ? null : (list, best);
        }
        return null;
    }

    public static async Task<Refactored> CreateAsync(Document document, FakeAsked asked, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, asked.At, cancel).ConfigureAwait(false);
        if (await TypeNamedAsync(document, offset, cancel).ConfigureAwait(false) is not { } type || !CanFake(type))
        {
            return Changes.Refused("Create Fake makes a fake of an interface, or of a class with members a fake can override: put the caret on its name");
        }
        string name = Path.GetFileNameWithoutExtension(asked.Project);
        if (TestProjects(document.Project.Solution, [asked.Project]).FirstOrDefault() is not { } test
            || await test.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compilation || SeenFrom(compilation, type) is not { } seen)
        {
            return Changes.Refused($"{name} does not see {Shown(type)}");
        }
        string file = FakeFile(test, type);
        if (Exists(test.Solution, file))
        {
            return Changes.Refused($"{Path.GetFileName(file)} is already in {name}");
        }
        Document made = await FakeAsync(test, compilation, seen, file, cancel).ConfigureAwait(false);
        return await Changes.BetweenAsync(test.Solution, made.Project.Solution, cancel).ConfigureAwait(false);
    }

    /// <summary>The type a name at an offset names, as declared (a generic type's definition): one declared there, or used
    /// there as a type or made there; none for anything else, attributes and exceptions among it.</summary>
    public static async Task<INamedTypeSymbol?> TypeNamedAsync(Document document, int offset, CancellationToken cancel)
    {
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        SyntaxToken token = Changes.TokenAt(root, offset);
        if (!token.IsKind(SyntaxKind.IdentifierToken))
        {
            return null;
        }
        ISymbol? named = token.Parent switch
        {
            BaseTypeDeclarationSyntax declaration when declaration.Identifier == token => model.GetDeclaredSymbol(declaration, cancel),
            SimpleNameSyntax name => model.GetSymbolInfo(name, cancel) is var info && info.Symbol is { } bound ? bound
                : info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null,
            _ => null,
        };
        INamedTypeSymbol? type = (named is IMethodSymbol { MethodKind: MethodKind.Constructor } made ? made.ContainingType : named as INamedTypeSymbol)?.OriginalDefinition;
        return type is null || Derives(type, "System.Attribute") || Derives(type, "System.Exception") ? null : type;
    }

    /// <summary>Why no fake can stand in for a class (<see cref="FakeSeams"/>); null for any other type, or a class a fake can
    /// derive from.</summary>
    public static string? SeamOf(INamedTypeSymbol type) =>
        type.TypeKind != TypeKind.Class || type.IsRecord ? null
        : type.IsStatic ? FakeSeams.Static
        : type.IsSealed ? FakeSeams.Sealed
        : type.InstanceConstructors.Any(Callable) ? null : FakeSeams.Closed;

    /// <summary>Whether a fake can stand in for a type: an interface with no static abstract members, or a class a fake can
    /// derive from whose abstract members it can all override and that has a member to override.</summary>
    public static bool CanFake(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => !Interfaces(type).SelectMany(i => i.GetMembers()).Any(m => m.IsStatic && m.IsAbstract),
        TypeKind.Class => !type.IsRecord && SeamOf(type) is null && Overridable(type).Any()
            && !Chain(type).SelectMany(t => t.GetMembers()).Any(m => m.IsAbstract && !Reachable(m.DeclaredAccessibility) && !IsOverriddenBelow(type, m)),
        _ => false,
    };

    /// <summary>Whether a fake of a class leaves members a test cannot set its own: a public instance member that is not
    /// virtual.</summary>
    public static bool KeepsOwn(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Class && Chain(type).SelectMany(t => t.GetMembers()).Any(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic
            && !m.IsImplicitlyDeclared && !m.IsVirtual && !m.IsAbstract && !m.IsOverride && m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol);

    /// <summary>A fake's name: Fake, then the type's own name without an interface's leading I.</summary>
    public static string FakeName(INamedTypeSymbol type) =>
        "Fake" + (type.TypeKind == TypeKind.Interface && type.Name.Length > 1 && type.Name[0] == 'I' && char.IsUpper(type.Name[1]) ? type.Name[1..] : type.Name);

    /// <summary>The fake's file in a test project: in its Fakes folder, named for the fake.</summary>
    public static string FakeFile(Project test, INamedTypeSymbol type) => Path.Combine(Path.GetDirectoryName(test.FilePath!)!, "Fakes", FakeName(type) + ".cs");

    /// <summary>A fake of a type written as a new document of a test project, at a file: in the project's root namespace's
    /// Fakes, declared as its other files declare their namespaces, its types simplified and imported, and formatted.</summary>
    public static async Task<Document> FakeAsync(Project test, Compilation compilation, INamedTypeSymbol type, string file, CancellationToken cancel)
    {
        LanguageVersion version = (test.ParseOptions as CSharpParseOptions)?.LanguageVersion ?? LanguageVersion.Latest;
        SyntaxGenerator generator = SyntaxGenerator.GetGenerator(test);
        MemberDeclarationSyntax fake = new Fake(generator, compilation, version).Of(type);
        string space = (test.DefaultNamespace is { Length: > 0 } root ? root : test.AssemblyName) + ".Fakes";
        bool fileScoped = version >= LanguageVersion.CSharp10 && await FileScopedAsync(test, cancel).ConfigureAwait(false);
        CompilationUnitSyntax unit = CompilationUnit().AddMembers(fileScoped
            ? FileScopedNamespaceDeclaration(ParseName(space)).AddMembers(fake)
            : NamespaceDeclaration(ParseName(space)).AddMembers(fake));
        if (compilation.Options.NullableContextOptions == NullableContextOptions.Disable)
        {
            // Its hooks are nullable: the file says so where its project does not.
            unit = unit.WithLeadingTrivia(ParseLeadingTrivia("#nullable enable\n\n"));
        }
        unit = Imported(unit);
        Document document = test.AddDocument(Path.GetFileName(file), unit, ["Fakes"], file);
        document = await ImportAdder.AddImportsAsync(document, Simplifier.AddImportsAnnotation, cancellationToken: cancel).ConfigureAwait(false);
        document = await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancel).ConfigureAwait(false);
        document = await Formatter.FormatAsync(document, cancellationToken: cancel).ConfigureAwait(false);
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        document = document.WithText(SourceText.From(BlankLines().Replace(text.ToString(), "$1$1").TrimStart('\r', '\n'), text.Encoding));
        return await Changes.EndingInLineBreakAsync(document, cancel).ConfigureAwait(false);
    }

    /// <summary>Code with each type named in full marked to be shortened as the file's usings, added as needed, allow.</summary>
    private static T Imported<T>(T code) where T : SyntaxNode =>
        code.ReplaceNodes(code.DescendantNodes().OfType<NameSyntax>().Where(n => n is QualifiedNameSyntax or AliasQualifiedNameSyntax
                && n.Parent is not (QualifiedNameSyntax or AliasQualifiedNameSyntax or BaseNamespaceDeclarationSyntax)),
            (_, name) => name.WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation));

    /// <summary>Two or more blank lines, by the line break they are made of.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(\r?\n)(?:[ \t]*\r?\n){2,}")]
    private static partial System.Text.RegularExpressions.Regex BlankLines();

    /// <summary>The C# projects of these files, one for each (a project built for two frameworks is one).</summary>
    private static IEnumerable<Project> TestProjects(Solution solution, IReadOnlyList<string> files) =>
        files.Select(f => solution.Projects.FirstOrDefault(p => p.Language == LanguageNames.CSharp && SyntaxReads.SamePath(p.FilePath, f))).OfType<Project>();

    /// <summary>A type as a compilation sees it, where it can use it: the type of the same assembly and name.</summary>
    private static INamedTypeSymbol? SeenFrom(Compilation compilation, INamedTypeSymbol type) =>
        compilation.GetTypesByMetadataName(MetadataName(type)).FirstOrDefault(t => t.ContainingAssembly.Identity.Name == type.ContainingAssembly.Identity.Name
            && compilation.IsSymbolAccessibleWithin(t, compilation.Assembly));

    private static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingType is { } outer ? MetadataName(outer) + "+" + type.MetadataName
        : type.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() + "." + type.MetadataName
        : type.MetadataName;

    private static string Shown(INamedTypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static bool Exists(Solution solution, string file) =>
        File.Exists(file) || solution.Projects.SelectMany(p => p.Documents).Any(d => SyntaxReads.SamePath(d.FilePath, file));

    private static async Task<bool> FileScopedAsync(Project test, CancellationToken cancel)
    {
        foreach (Document document in test.Documents.Where(d => !d.Name.EndsWith(".g.cs", StringComparison.Ordinal)).Take(8))
        {
            if ((await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))?.ChildNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } space)
            {
                return space is FileScopedNamespaceDeclarationSyntax;
            }
        }
        return true;
    }

    private static bool Derives(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? at = type; at is not null; at = at.BaseType)
        {
            if (at.ToDisplayString() == name)
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> Interfaces(INamedTypeSymbol type) => [type, .. type.AllInterfaces];

    /// <summary>A class and the classes it derives from, short of object.</summary>
    private static IEnumerable<INamedTypeSymbol> Chain(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? at = type; at is not null && at.SpecialType != SpecialType.System_Object; at = at.BaseType)
        {
            yield return at;
        }
    }

    /// <summary>Whether a class outside the assembly can call or override a member of this accessibility.</summary>
    private static bool Reachable(Accessibility access) => access is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;

    private static bool Callable(IMethodSymbol constructor) => Reachable(constructor.DeclaredAccessibility) && !constructor.IsStatic;

    private static bool IsOverriddenBelow(INamedTypeSymbol type, ISymbol member) =>
        Chain(type).TakeWhile(t => !SymbolEqualityComparer.Default.Equals(t, member.ContainingType)).SelectMany(t => t.GetMembers())
            .Any(m => Overridden(m) is { } over && SymbolEqualityComparer.Default.Equals(over, member));

    private static ISymbol? Overridden(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    /// <summary>The members of a class a fake overrides, the most derived of each: its abstract members and its virtual
    /// methods a class outside can reach, short of object's own.</summary>
    private static List<ISymbol> Overridable(INamedTypeSymbol type)
    {
        HashSet<ISymbol> settled = new(SymbolEqualityComparer.Default);
        List<ISymbol> found = [];
        foreach (INamedTypeSymbol at in Chain(type))
        {
            foreach (ISymbol member in at.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared || !settled.Add(member))
                {
                    continue;
                }
                ISymbol? root = member;
                for (ISymbol? over = Overridden(member); over is not null; over = Overridden(over))
                {
                    settled.Add(over);
                    root = over;
                }
                bool overridable = !member.IsSealed && (member.IsAbstract || member.IsVirtual || member.IsOverride) && Reachable(member.DeclaredAccessibility)
                    && root.ContainingType.SpecialType != SpecialType.System_Object;
                if (overridable && (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } || member.IsAbstract && member is IPropertySymbol or IEventSymbol))
                {
                    found.Add(member);
                }
            }
        }
        return found;
    }

    /// <summary>A fake's declaration as it is written: for each member it stands in for, a hook a test sets and the calls it
    /// heard, then the member; its names apart from those of what it stands in for.</summary>
    private sealed class Fake(SyntaxGenerator generator, Compilation compilation, LanguageVersion version)
    {
        private readonly HashSet<string> taken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> overloads = new(StringComparer.Ordinal);
        private readonly List<MemberDeclarationSyntax> members = [];

        public MemberDeclarationSyntax Of(INamedTypeSymbol type)
        {
            string name = FakeName(type);
            taken.Add(name);
            IEnumerable<INamedTypeSymbol> holders = type.TypeKind == TypeKind.Interface ? Interfaces(type) : Chain(type);
            taken.UnionWith(holders.SelectMany(t => t.MemberNames));
            if (type.TypeKind == TypeKind.Interface)
            {
                Implement(type);
            }
            else
            {
                Derive(type, name);
            }
            // Each member a line or two from the one before, as the formatter leaves them; the file's blank lines are made one after.
            SyntaxNode declared = generator.ClassDeclaration(name, type.TypeParameters.Select(t => t.Name), Accessibility.Internal, DeclarationModifiers.Sealed,
                type.TypeKind == TypeKind.Class ? Type(type) : null, type.TypeKind == TypeKind.Interface ? [Type(type)] : null,
                members.Select((m, i) => i == 0 ? m : m.WithLeadingTrivia(m.GetLeadingTrivia().InsertRange(0, [EndOfLine("\n"), EndOfLine("\n")]))));
            foreach (ITypeParameterSymbol parameter in type.TypeParameters)
            {
                SpecialTypeConstraintKind kinds = (parameter.HasReferenceTypeConstraint ? SpecialTypeConstraintKind.ReferenceType : 0)
                    | (parameter.HasValueTypeConstraint ? SpecialTypeConstraintKind.ValueType : 0)
                    | (parameter.HasConstructorConstraint ? SpecialTypeConstraintKind.Constructor : 0);
                if (kinds != SpecialTypeConstraintKind.None || parameter.ConstraintTypes.Length > 0)
                {
                    declared = generator.WithTypeConstraint(declared, parameter.Name, kinds, parameter.ConstraintTypes.Select(Type));
                }
            }
            string own = type.TypeKind == TypeKind.Class ? $", else by {type.Name}'s own where it has one" : string.Empty;
            return ((MemberDeclarationSyntax)declared).WithLeadingTrivia(ParseLeadingTrivia(
                $"/// <summary>A fake of {type.Name} for tests: each call is recorded in its member's Calls and answered by its On hook where a test\n"
                + $"/// sets one{own}, else by a default.</summary>\n"));
        }

        /// <summary>Each abstract member of an interface and those it extends, once for each signature; a second of a
        /// signature that answers with another type is implemented apart, for its interface alone.</summary>
        private void Implement(INamedTypeSymbol type)
        {
            Dictionary<string, ITypeSymbol> signatures = new(StringComparer.Ordinal);
            foreach (INamedTypeSymbol holder in Interfaces(type))
            {
                foreach (ISymbol member in holder.GetMembers().Where(m => m.IsAbstract && !m.IsStatic && m is not IMethodSymbol { MethodKind: not MethodKind.Ordinary }))
                {
                    ITypeSymbol answers = member switch { IMethodSymbol m => m.ReturnType, IPropertySymbol p => p.Type, IEventSymbol e => e.Type, _ => compilation.ObjectType };
                    string signature = Signature(member);
                    if (signatures.TryGetValue(signature, out ITypeSymbol? before))
                    {
                        if (!SymbolEqualityComparer.Default.Equals(before, answers))
                        {
                            Apart(member, holder);
                        }
                        continue;
                    }
                    signatures[signature] = answers;
                    Write(member, overriding: false);
                }
            }
        }

        private void Derive(INamedTypeSymbol type, string name)
        {
            List<IMethodSymbol> constructors = [.. type.InstanceConstructors.Where(Callable)];
            if (constructors is not [{ Parameters.Length: 0 }])
            {
                foreach (IMethodSymbol constructor in constructors)
                {
                    members.Add((MemberDeclarationSyntax)generator.ConstructorDeclaration(name, constructor.Parameters.Select(p => generator.ParameterDeclaration(p)),
                        Accessibility.Public, baseConstructorArguments: constructor.Parameters.Select(p => generator.Argument(p.RefKind, IdentifierName(Named(p.Name))))));
                }
            }
            foreach (ISymbol member in Overridable(type))
            {
                Write(member, overriding: true);
            }
        }

        private void Write(ISymbol member, bool overriding)
        {
            switch (member)
            {
                case IMethodSymbol method:
                    Method(method, overriding);
                    break;
                case IPropertySymbol { IsIndexer: true } indexer:
                    Indexer(indexer, overriding);
                    break;
                case IPropertySymbol property:
                    Property(property, overriding);
                    break;
                case IEventSymbol @event:
                    Event(@event, overriding);
                    break;
            }
        }

        /// <summary>A method that records each call and answers through its hook where it can have one (no type
        /// parameters of its own, no ref or out parameter, at most 16), else by its default: a virtual method's base.</summary>
        private void Method(IMethodSymbol method, bool overriding)
        {
            string name = Overload(method.Name);
            bool fallsBack = overriding && !method.IsAbstract;
            List<IParameterSymbol> heard = [.. method.Parameters.Where(p => p.RefKind != RefKind.Out)];
            bool hooked = Hookable(method);
            bool listed = !method.IsGenericMethod && heard.Count > 0 && heard.All(p => Holdable(p.Type));
            string calls = Taken(name + "Calls");
            string? hook = hooked ? Taken("On" + name) : null;
            if (hook is not null)
            {
                members.Add(Settable(Hook(method.Parameters.Select(p => p.Type), method.ReturnsVoid ? null : method.ReturnType), hook));
            }
            members.Add(listed ? Kept(Heard(heard), calls) : Counted(calls));

            List<StatementSyntax> body = [.. method.Parameters.Where(p => p.RefKind == RefKind.Out).Select(p => Statement($"{Named(p.Name)} = {(p.Type.IsValueType ? "default" : "default!")};"))];
            body.Add(listed ? Statement($"{calls}.Add({Tuple(heard)});") : Statement($"{calls}++;"));
            string passed = string.Join(", ", method.Parameters.Select(p => p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", _ => string.Empty } + Named(p.Name)));
            ExpressionSyntax? fallback = fallsBack ? ParseExpression($"base.{Named(method.Name)}{TypeArguments(method)}({passed})") : null;
            if (method.ReturnsByRef || method.ReturnsByRefReadonly)
            {
                body.Add((StatementSyntax)generator.ThrowStatement(generator.ObjectCreationExpression(Type(compilation.GetTypeByMetadataName("System.NotSupportedException")!))));
            }
            else if (method.ReturnsVoid)
            {
                body.Add(hook is null ? (fallback is null ? EmptyStatement() : (StatementSyntax)ExpressionStatement(fallback))
                    : fallback is null ? Statement($"{hook}?.Invoke({passed});")
                    : IfStatement(ParseExpression($"{hook} != null"), Statement($"{hook}({passed});"), ElseClause(ExpressionStatement(fallback))));
            }
            else
            {
                ExpressionSyntax answer = fallback ?? Default(method.ReturnType);
                body.Add(ReturnStatement(hook is null ? answer
                    : ConditionalExpression(ParseExpression($"{hook} != null"), ParseExpression($"{hook}({passed})"), answer)));
            }
            members.Add(Overriding(generator.MethodDeclaration(method), method, overriding, statements: body.Where(s => !s.IsKind(SyntaxKind.EmptyStatement))));
        }

        /// <summary>A property: one an interface asks for settable by a test (init-only where it is), one a class leaves
        /// abstract kept in a property of the fake's beside it.</summary>
        private void Property(IPropertySymbol property, bool overriding)
        {
            if (property.ReturnsByRef || property.ReturnsByRefReadonly)
            {
                members.Add(Overriding(generator.PropertyDeclaration(property), property, overriding,
                    get: [generator.ThrowStatement(generator.ObjectCreationExpression(Type(compilation.GetTypeByMetadataName("System.NotSupportedException")!)))]));
                return;
            }
            bool init = property.SetMethod is { IsInitOnly: true };
            if (!overriding || property.GetMethod is not null && property.SetMethod is not null)
            {
                members.Add(Overriding(Auto(Type(property.Type), Named(property.Name), init ? SyntaxKind.InitAccessorDeclaration : SyntaxKind.SetAccessorDeclaration,
                    Initial(property.Type)), property, overriding));
                return;
            }
            string value = Taken(property.Name + "Value");
            members.Add(Auto(Type(property.Type), value, SyntaxKind.SetAccessorDeclaration, Initial(property.Type)));
            members.Add(Overriding(generator.PropertyDeclaration(property), property, overriding,
                get: property.GetMethod is null ? null : [ReturnStatement(IdentifierName(value))],
                set: property.SetMethod is null ? null : [Statement($"{value} = value;")]));
        }

        /// <summary>An indexer answering through its hook, else a default, and recording what is set where it can be.</summary>
        private void Indexer(IPropertySymbol indexer, bool overriding)
        {
            string hook = Taken("OnItem");
            string sets = Taken("ItemSets");
            string passed = string.Join(", ", indexer.Parameters.Select(p => Named(p.Name)));
            bool hooked = indexer.Parameters.Length < 16 && indexer.Parameters.All(p => p.RefKind is RefKind.None or RefKind.In && Holdable(p.Type)) && Holdable(indexer.Type);
            if (hooked && indexer.GetMethod is not null)
            {
                members.Add(Settable(Hook(indexer.Parameters.Select(p => p.Type), indexer.Type), hook));
            }
            if (hooked && indexer.SetMethod is not null)
            {
                members.Add(Kept([.. indexer.Parameters.Select(p => (p.Type, Named(p.Name))), (indexer.Type, "value")], sets));
            }
            IEnumerable<SyntaxNode>? get = indexer.GetMethod is null ? null
                : [ReturnStatement(hooked ? ConditionalExpression(ParseExpression($"{hook} != null"), ParseExpression($"{hook}({passed})"), Default(indexer.Type)) : Default(indexer.Type))];
            IEnumerable<SyntaxNode>? set = indexer.SetMethod is null ? null : hooked ? [Statement($"{sets}.Add(({passed}, value));")] : [];
            members.Add(Overriding(generator.IndexerDeclaration(indexer), indexer, overriding, get: get, set: set));
        }

        /// <summary>An event that is never null, and a method a test raises it by.</summary>
        private void Event(IEventSymbol @event, bool overriding)
        {
            IMethodSymbol? invoke = (@event.Type as INamedTypeSymbol)?.DelegateInvokeMethod;
            bool quiet = invoke is { ReturnsVoid: true } && invoke.Parameters.All(p => p.RefKind != RefKind.Out);
            EventFieldDeclarationSyntax field = EventFieldDeclaration(VariableDeclaration(Type(@event.Type))
                .AddVariables(VariableDeclarator(Named(@event.Name)).WithInitializer(EqualsValueClause(quiet ? AnonymousMethodExpression() : Default(@event.Type)))))
                .AddModifiers(Token(overriding ? Visibility(@event.DeclaredAccessibility) : SyntaxKind.PublicKeyword));
            members.Add(overriding ? field.AddModifiers(Token(SyntaxKind.OverrideKeyword)) : field);
            if (invoke is null)
            {
                return;
            }
            string passed = string.Join(", ", invoke.Parameters.Select(p => p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", _ => string.Empty } + Named(p.Name)));
            ExpressionSyntax raised = ParseExpression($"{Named(@event.Name)}({passed})");
            members.Add((MemberDeclarationSyntax)generator.MethodDeclaration(Taken("Raise" + @event.Name), invoke.Parameters.Select(p => generator.ParameterDeclaration(p)),
                returnType: invoke.ReturnsVoid ? null : Type(invoke.ReturnType), accessibility: Accessibility.Public,
                statements: [invoke.ReturnsVoid ? ExpressionStatement(raised) : ReturnStatement(raised)]));
        }

        /// <summary>A member of one interface whose signature another's has with another type, implemented for it alone with
        /// its default.</summary>
        private void Apart(ISymbol member, INamedTypeSymbol holder)
        {
            SyntaxNode declared = member switch
            {
                IMethodSymbol method => Overriding(generator.MethodDeclaration(method), method, false, statements: method.ReturnsVoid ? [] : [ReturnStatement(Default(method.ReturnType))]),
                IPropertySymbol { IsIndexer: true } indexer => Overriding(generator.IndexerDeclaration(indexer), indexer, false,
                    get: indexer.GetMethod is null ? null : [ReturnStatement(Default(indexer.Type))], set: indexer.SetMethod is null ? null : []),
                IPropertySymbol property => Overriding(generator.PropertyDeclaration(property), property, false,
                    get: property.GetMethod is null ? null : [ReturnStatement(Default(property.Type))], set: property.SetMethod is null ? null : []),
                IEventSymbol @event => generator.CustomEventDeclaration(@event.Name, Type(@event.Type), addAccessorStatements: [], removeAccessorStatements: []),
                _ => throw new InvalidOperationException($"{member.Name} is no member a fake implements"),
            };
            members.Add(generator.AsPrivateInterfaceImplementation(declared, Type(holder)) as MemberDeclarationSyntax
                ?? throw new InvalidOperationException($"{member.Name} cannot be implemented for {holder.Name} alone"));
        }

        /// <summary>A member made public and plain where it implements an interface, or an override of its class's member at
        /// that member's accessibility without the constraints its overridden member gives it, then given its bodies.</summary>
        private MemberDeclarationSyntax Overriding(SyntaxNode declared, ISymbol member, bool overriding, IEnumerable<SyntaxNode>? statements = null,
            IEnumerable<SyntaxNode>? get = null, IEnumerable<SyntaxNode>? set = null)
        {
            declared = generator.WithModifiers(declared, overriding ? DeclarationModifiers.Override : DeclarationModifiers.None);
            declared = generator.WithAccessibility(declared, overriding ? Accessible(member.DeclaredAccessibility) : Accessibility.Public);
            if (overriding && declared is MethodDeclarationSyntax method)
            {
                declared = method.WithConstraintClauses(default);
            }
            if (statements is not null)
            {
                declared = generator.WithStatements(declared, statements);
            }
            if (get is not null)
            {
                declared = generator.WithGetAccessorStatements(declared, get);
            }
            if (set is not null)
            {
                declared = generator.WithSetAccessorStatements(declared, set);
            }
            return (MemberDeclarationSyntax)declared;
        }

        private static Accessibility Accessible(Accessibility access) => access == Accessibility.ProtectedOrInternal ? Accessibility.Protected : access;

        private static SyntaxKind Visibility(Accessibility access) => access == Accessibility.Public ? SyntaxKind.PublicKeyword : SyntaxKind.ProtectedKeyword;

        /// <summary>Whether a method can answer through a Func or an Action: no type parameters of its own, no ref or out
        /// parameter, at most 16 parameters, none of a type a delegate's type arguments cannot be.</summary>
        private static bool Hookable(IMethodSymbol method) =>
            !method.IsGenericMethod && method.Parameters.Length <= 16 && !method.ReturnsByRef && !method.ReturnsByRefReadonly
            && method.Parameters.All(p => p.RefKind is RefKind.None or RefKind.In && Holdable(p.Type)) && (method.ReturnsVoid || Holdable(method.ReturnType));

        /// <summary>Whether a value of a type can be held in a list or passed as a type argument: no ref struct or pointer.</summary>
        private static bool Holdable(ITypeSymbol type) => !type.IsRefLikeType && type is not (IPointerTypeSymbol or IFunctionPointerTypeSymbol);

        /// <summary>A member's signature as two interfaces' members are told apart: its name, type parameters and
        /// parameters' kinds and types.</summary>
        private static string Signature(ISymbol member) => member switch
        {
            IMethodSymbol method => $"{method.Name}`{method.Arity}({string.Join(",", method.Parameters.Select(p => $"{p.RefKind} {p.Type.ToDisplayString()}"))})",
            IPropertySymbol property => $"{property.Name}[{string.Join(",", property.Parameters.Select(p => p.Type.ToDisplayString()))}]",
            _ => member.Name,
        };

        /// <summary>A name for an overload: the method's for the first, then numbered.</summary>
        private string Overload(string name)
        {
            int count = overloads.GetValueOrDefault(name) + 1;
            overloads[name] = count;
            return count == 1 ? name : name + count;
        }

        /// <summary>A name of the fake's own: the one wanted, numbered where the type has it already.</summary>
        private string Taken(string wanted)
        {
            string name = wanted;
            for (int n = 2; !taken.Add(name); n++)
            {
                name = wanted + n;
            }
            return name;
        }

        /// <summary>The calls heard as list items: each argument heard alone, or a tuple of them named for the parameters.</summary>
        private List<(ITypeSymbol Type, string Name)> Heard(IReadOnlyList<IParameterSymbol> heard) => [.. heard.Select(p => (p.Type, Named(p.Name)))];

        private static string Tuple(IReadOnlyList<IParameterSymbol> heard) =>
            heard.Count == 1 ? Named(heard[0].Name) : "(" + string.Join(", ", heard.Select(p => Named(p.Name))) + ")";

        /// <summary>A list a test reads the calls in: of each argument's type for one, else of named tuples.</summary>
        private MemberDeclarationSyntax Kept(IReadOnlyList<(ITypeSymbol Type, string Name)> items, string name)
        {
            System.Collections.Immutable.ImmutableArray<string?> names = Plain(items) ? [.. items.Select(i => i.Name.TrimStart('@'))] : default;
            ITypeSymbol item = items.Count == 1 ? items[0].Type : compilation.CreateTupleTypeSymbol([.. items.Select(i => i.Type)], names);
            INamedTypeSymbol list = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!.Construct(item);
            ExpressionSyntax made = version >= LanguageVersion.CSharp12 ? CollectionExpression()
                : version >= LanguageVersion.CSharp9 ? ImplicitObjectCreationExpression() : ObjectCreationExpression(Type(list)).WithArgumentList(ArgumentList());
            return PropertyDeclaration(Type(list), Identifier(name)).AddModifiers(Token(SyntaxKind.PublicKeyword))
                .WithAccessorList(AccessorList(SingletonList(Accessor(SyntaxKind.GetAccessorDeclaration))))
                .WithInitializer(EqualsValueClause(made)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        }

        /// <summary>Whether tuple elements can take the parameters' names: none a tuple keeps for itself.</summary>
        private static bool Plain(IReadOnlyList<(ITypeSymbol Type, string Name)> items) =>
            items.All(i => !(i.Name is "Rest" or "ToString" or "GetHashCode" or "Equals" or "GetType" || i.Name.StartsWith("Item", StringComparison.Ordinal)));

        /// <summary>A count of the calls, for a method whose arguments a list cannot hold.</summary>
        private static MemberDeclarationSyntax Counted(string name) =>
            PropertyDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)), Identifier(name)).AddModifiers(Token(SyntaxKind.PublicKeyword))
                .WithAccessorList(AccessorList(List([Accessor(SyntaxKind.GetAccessorDeclaration),
                    Accessor(SyntaxKind.SetAccessorDeclaration).AddModifiers(Token(SyntaxKind.PrivateKeyword))])));

        /// <summary>A Func of the parameters' types and the answer's, or an Action where there is none, that a test may leave
        /// unset.</summary>
        private INamedTypeSymbol Hook(IEnumerable<ITypeSymbol> parameters, ITypeSymbol? answer)
        {
            List<ITypeSymbol> arguments = [.. parameters, .. answer is null ? [] : new[] { answer }];
            INamedTypeSymbol hook = answer is null && arguments.Count == 0 ? compilation.GetTypeByMetadataName("System.Action")!
                : compilation.GetTypeByMetadataName(answer is null ? $"System.Action`{arguments.Count}" : $"System.Func`{arguments.Count}")!
                    .Construct([.. arguments], [.. arguments.Select(a => a.NullableAnnotation)]);
            return (INamedTypeSymbol)hook.WithNullableAnnotation(NullableAnnotation.Annotated);
        }

        private MemberDeclarationSyntax Settable(INamedTypeSymbol type, string name) => Auto(Type(type), name, SyntaxKind.SetAccessorDeclaration, null);

        private static PropertyDeclarationSyntax Auto(TypeSyntax type, string name, SyntaxKind setter, ExpressionSyntax? initial)
        {
            PropertyDeclarationSyntax property = PropertyDeclaration(type, Identifier(name)).AddModifiers(Token(SyntaxKind.PublicKeyword))
                .WithAccessorList(AccessorList(List([Accessor(SyntaxKind.GetAccessorDeclaration), Accessor(setter)])));
            return initial is null ? property : property.WithInitializer(EqualsValueClause(initial)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        }

        private static AccessorDeclarationSyntax Accessor(SyntaxKind kind) => AccessorDeclaration(kind).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        /// <summary>What a property starts as: its default where a value type's, else one a test reads without a null.</summary>
        private ExpressionSyntax? Initial(ITypeSymbol type) => type.IsValueType && type is not ITypeParameterSymbol ? null : Default(type);

        /// <summary>The answer a member gives with no hook set: a completed task (of its result's default), an empty string or
        /// collection, else the type's default.</summary>
        private ExpressionSyntax Default(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { ContainingNamespace: { } space } named && space.ToDisplayString() == "System.Threading.Tasks")
            {
                switch (named.Name, named.Arity)
                {
                    case ("Task", 0):
                        return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, Type(named), IdentifierName("CompletedTask"));
                    case ("Task", 1):
                        return InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                Type(compilation.GetTypeByMetadataName("System.Threading.Tasks.Task")!), GenericName("FromResult").AddTypeArgumentListArguments(Type(named.TypeArguments[0]))))
                            .AddArgumentListArguments(Argument(Default(named.TypeArguments[0])));
                    case ("ValueTask", 0):
                        return LiteralExpression(SyntaxKind.DefaultLiteralExpression);
                    case ("ValueTask", 1):
                        return ObjectCreationExpression(Type(named)).AddArgumentListArguments(Argument(Default(named.TypeArguments[0])));
                }
            }
            if (type.SpecialType == SpecialType.System_String && type.NullableAnnotation != NullableAnnotation.Annotated)
            {
                return LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(string.Empty));
            }
            if (version >= LanguageVersion.CSharp12 && type.NullableAnnotation != NullableAnnotation.Annotated && Collection(type))
            {
                return CollectionExpression();
            }
            ExpressionSyntax nothing = LiteralExpression(SyntaxKind.DefaultLiteralExpression);
            return type.IsValueType || type.NullableAnnotation == NullableAnnotation.Annotated ? nothing
                : PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, nothing);
        }

        /// <summary>Whether a collection expression makes a value of a type: an array, a list, or an interface a list is.</summary>
        private static bool Collection(ITypeSymbol type) =>
            type is IArrayTypeSymbol { Rank: 1 }
            || type is INamedTypeSymbol { Arity: 1, ContainingNamespace: { } space } named && space.ToDisplayString() == "System.Collections.Generic"
                && named.Name is "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "IList" or "ICollection" or "List";

        private TypeSyntax Type(ITypeSymbol type) => (TypeSyntax)generator.TypeExpression(type);

        private static string TypeArguments(IMethodSymbol method) =>
            method.IsGenericMethod ? "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">" : string.Empty;
    }

    /// <summary>A statement as written, on a line of its own once formatted.</summary>
    private static StatementSyntax Statement(string text) => ParseStatement(text).WithTrailingTrivia(ElasticCarriageReturnLineFeed);

    /// <summary>A name as code may write it: a keyword's with an @.</summary>
    private static string Named(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
}
