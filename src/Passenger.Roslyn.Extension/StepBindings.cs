using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>The step definitions, hooks and step argument transformations of the Reqnroll and SpecFlow projects
/// (<see cref="StepBindingReading"/>).</summary>
public sealed class StepBindings : IExtensionWorkspaceMessageHandler<StepsRequest, StepsFound>
{
    public Task<StepsFound> ExecuteAsync(StepsRequest message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken) =>
        StepBindingReading.ReadAsync(context.Solution, message, cancellationToken);
}

/// <summary>Reads a Reqnroll (or SpecFlow) project's bindings from its compilation as the server holds it, with no build, as
/// Reqnroll's runtime discovers them: every method of a class marked <c>[Binding]</c> (its base classes' too) in the
/// project's code, the projects it references and the binding assemblies its configuration names, whose attributes derive
/// from the step definition, hook or transformation attributes; a <c>[Scope]</c> of the class and of the method each an
/// alternative. A project is Reqnroll's or SpecFlow's where its compilation knows that one's <c>BindingAttribute</c>.</summary>
internal static class StepBindingReading
{
    private static readonly string[] Namespaces = ["Reqnroll", "TechTalk.SpecFlow"];

    public static async Task<StepsFound> ReadAsync(Solution solution, StepsRequest asked, CancellationToken cancel)
    {
        List<ProjectStepsFound> found = [];
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (Project project in solution.Projects)
        {
            cancel.ThrowIfCancellationRequested();
            // A multi-targeted project is several of the server's projects of one file: its bindings are read once.
            if (project.Language != LanguageNames.CSharp || project.FilePath is not { } file
                || (asked.Projects.Count > 0 && !asked.Projects.Any(path => SamePath(path, file))) || !seen.Add(PathNames.Canonical(file))
                || await project.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }
            string? ns = Namespaces.FirstOrDefault(n => compilation.GetTypeByMetadataName(n + ".BindingAttribute") is not null);
            // A project not restored or still loading binds no attribute: its bindings are unknown, never none.
            if (ns is null || compilation.GetSpecialType(SpecialType.System_Object).TypeKind == TypeKind.Error)
            {
                continue;
            }
            IReadOnlyList<string> named = asked.BindingAssemblies?.FirstOrDefault(p => SamePath(p.Key, file)).Value ?? [];
            ProjectReading reading = new(compilation, ns, cancel);
            await reading.ReadAsync(AssembliesOf(solution, compilation, named)).ConfigureAwait(false);
            found.Add(new ProjectStepsFound(PathNames.Canonical(file), ns != "Reqnroll", reading.Steps, reading.Hooks, reading.Transformations));
        }
        return new StepsFound(found);
    }

    /// <summary>The assemblies a project's bindings may be in: its own, each project it references, and each assembly its
    /// configuration names; with the project file each is the code of, where it is a project's.</summary>
    private static IEnumerable<(IAssemblySymbol Assembly, string? Project)> AssembliesOf(Solution solution, Compilation compilation, IReadOnlyList<string> named)
    {
        Project? owner = solution.Projects.FirstOrDefault(p => p.AssemblyName == compilation.AssemblyName);
        yield return (compilation.Assembly, owner?.FilePath);
        foreach (MetadataReference reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                continue;
            }
            if (reference is CompilationReference referenced)
            {
                yield return (assembly, solution.Projects.FirstOrDefault(p => p.AssemblyName == referenced.Compilation.AssemblyName)?.FilePath);
            }
            else if (named.Contains(assembly.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return (assembly, null);
            }
        }
    }

    private sealed class ProjectReading(Compilation compilation, string ns, CancellationToken cancel)
    {
        private readonly INamedTypeSymbol? binding = compilation.GetTypeByMetadataName(ns + ".BindingAttribute");
        private readonly INamedTypeSymbol? stepBase = compilation.GetTypeByMetadataName(ns + ".StepDefinitionBaseAttribute");
        private readonly INamedTypeSymbol? transformation = compilation.GetTypeByMetadataName(ns + ".StepArgumentTransformationAttribute");
        private readonly INamedTypeSymbol? scope = compilation.GetTypeByMetadataName(ns + ".ScopeAttribute");

        public List<ProjectStep> Steps { get; } = [];

        public List<StepHookFound> Hooks { get; } = [];

        public List<StepTransformationFound> Transformations { get; } = [];

        public async Task ReadAsync(IEnumerable<(IAssemblySymbol Assembly, string? Project)> assemblies)
        {
            foreach ((IAssemblySymbol assembly, string? project) in assemblies)
            {
                foreach (INamedTypeSymbol type in TypesOf(assembly.GlobalNamespace))
                {
                    cancel.ThrowIfCancellationRequested();
                    if (type.TypeKind == TypeKind.Class && IsBinding(type))
                    {
                        await ReadTypeAsync(type, project).ConfigureAwait(false);
                    }
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> TypesOf(INamespaceSymbol space)
        {
            foreach (INamespaceOrTypeSymbol member in space.GetMembers())
            {
                if (member is INamespaceSymbol inner)
                {
                    foreach (INamedTypeSymbol type in TypesOf(inner))
                    {
                        yield return type;
                    }
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (INamedTypeSymbol nested in type.GetTypeMembers().SelectMany(Nested))
                    {
                        yield return nested;
                    }
                }
            }

            static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => [type, .. type.GetTypeMembers().SelectMany(Nested)];
        }

        private bool IsBinding(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? at = type; at is not null; at = at.BaseType)
            {
                if (at.GetAttributes().Any(a => Derives(a.AttributeClass, binding)))
                {
                    return true;
                }
            }
            return false;
        }

        private async Task ReadTypeAsync(INamedTypeSymbol type, string? project)
        {
            List<StepScopeFound> typeScopes = ScopesOf(type.GetAttributes());
            for (INamedTypeSymbol? at = type; at is not null && at.SpecialType != SpecialType.System_Object; at = at.BaseType)
            {
                foreach (IMethodSymbol method in at.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
                {
                    if (!ReferenceEquals(at, type) && method.DeclaredAccessibility != Accessibility.Public)
                    {
                        continue;
                    }
                    await ReadMethodAsync(type, method, typeScopes, project).ConfigureAwait(false);
                }
            }
        }

        private async Task ReadMethodAsync(INamedTypeSymbol type, IMethodSymbol method, List<StepScopeFound> typeScopes, string? project)
        {
            IReadOnlyList<AttributeData> attributes = method.GetAttributes();
            if (attributes.Count == 0)
            {
                return;
            }
            List<StepScopeFound> scopes = [.. typeScopes, .. ScopesOf(attributes)];
            StepPlace? methodAt = await PlaceOfAsync(method.Locations.FirstOrDefault(l => l.IsInSource)).ConfigureAwait(false);
            string typeName = FullName(type);
            foreach (AttributeData attribute in attributes)
            {
                if (attribute.AttributeClass is not { } kind)
                {
                    continue;
                }
                if (Derives(kind, stepBase))
                {
                    string? expression = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? Named(attribute, "Expression") as string;
                    string? expressionType = Named(attribute, "ExpressionType") switch
                    {
                        1 => "CucumberExpression",
                        2 => "RegularExpression",
                        _ => null,
                    };
                    StepPlace? expressionAt = await LiteralPlaceAsync(attribute).ConfigureAwait(false);
                    Steps.Add(new ProjectStep(new StepDefinitionFound(StepKindOf(kind), expression, expressionType, method.Name, typeName,
                        [.. method.Parameters.Select(ParameterOf)], scopes, ErrorOf(method, type), methodAt, expressionAt), project));
                }
                else if (Derives(kind, transformation))
                {
                    string? regex = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? Named(attribute, "Regex") as string;
                    Transformations.Add(new StepTransformationFound(regex, Named(attribute, "Name") as string, FullName(method.ReturnType), methodAt));
                }
                else if (HookKind(kind) is { } hook)
                {
                    IEnumerable<StepScopeFound> tags = attribute.ConstructorArguments.Where(a => a.Kind == TypedConstantKind.Array)
                        .SelectMany(a => a.Values).Select(v => v.Value).OfType<string>().Select(t => new StepScopeFound(t, null, null));
                    int order = Named(attribute, "Order") is int named ? named : 10000;
                    Hooks.Add(new StepHookFound(hook, method.Name, typeName, [.. scopes, .. tags], order, methodAt));
                }
            }
        }

        /// <summary>Why Reqnroll would refuse a step definition: one it cannot call or call as a step.</summary>
        private static string? ErrorOf(IMethodSymbol method, INamedTypeSymbol type)
        {
            if (method.DeclaredAccessibility != Accessibility.Public)
            {
                return "A step definition must be public.";
            }
            if (method.IsAsync && method.ReturnsVoid)
            {
                return "A step definition must not be async void: return a Task.";
            }
            if (method.Parameters.Any(p => p.RefKind is RefKind.Out or RefKind.Ref))
            {
                return "A step definition's parameters cannot be out or ref.";
            }
            return type.IsAbstract && !method.IsStatic ? "A step definition of an abstract class must be static." : null;
        }

        private string StepKindOf(INamedTypeSymbol kind)
        {
            for (INamedTypeSymbol? at = kind; at is not null; at = at.BaseType)
            {
                if (at.ContainingNamespace?.ToDisplayString() == ns && at.Name is "GivenAttribute" or "WhenAttribute" or "ThenAttribute")
                {
                    return at.Name[..^"Attribute".Length];
                }
            }
            return "StepDefinition";
        }

        private string? HookKind(INamedTypeSymbol kind)
        {
            for (INamedTypeSymbol? at = kind; at is not null; at = at.BaseType)
            {
                if (at.ContainingNamespace?.ToDisplayString() == ns && at.Name.EndsWith("Attribute", StringComparison.Ordinal)
                    && (at.Name.StartsWith("Before", StringComparison.Ordinal) || at.Name.StartsWith("After", StringComparison.Ordinal)))
                {
                    string name = at.Name[..^"Attribute".Length];
                    return name switch
                    {
                        "Before" => "BeforeScenario",
                        "After" => "AfterScenario",
                        _ => name,
                    };
                }
            }
            return null;
        }

        private List<StepScopeFound> ScopesOf(IEnumerable<AttributeData> attributes) =>
            [.. attributes.Where(a => Derives(a.AttributeClass, scope))
                .Select(a => new StepScopeFound(Named(a, "Tag") as string, Named(a, "Feature") as string, Named(a, "Scenario") as string))];

        private static object? Named(AttributeData attribute, string name) =>
            attribute.NamedArguments.FirstOrDefault(a => a.Key == name) is { Key: not null } named ? named.Value.Value : null;

        private static bool Derives(INamedTypeSymbol? type, INamedTypeSymbol? of)
        {
            for (INamedTypeSymbol? at = type; at is not null && of is not null; at = at.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(at.OriginalDefinition, of))
                {
                    return true;
                }
            }
            return false;
        }

        private static StepParameterFound ParameterOf(IParameterSymbol parameter)
        {
            ITypeSymbol type = parameter.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : parameter.Type;
            IReadOnlyList<string>? members = type.TypeKind == TypeKind.Enum
                ? [.. type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name)]
                : null;
            return new StepParameterFound(parameter.Name, FullName(type), members);
        }

        /// <summary>A type's full metadata name, a nested type's after a <c>+</c> ("Shop.Basket+Color").</summary>
        private static string FullName(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
            {
                return FullName(array.ElementType) + "[]";
            }
            string name = type.MetadataName;
            for (INamedTypeSymbol? outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            {
                name = outer.MetadataName + "+" + name;
            }
            return type.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() + "." + name : name;
        }

        private async Task<StepPlace?> PlaceOfAsync(Location? location)
        {
            if (location?.SourceTree is not { } tree || tree.FilePath is not { Length: > 0 } path)
            {
                return null;
            }
            SourceText text = await tree.GetTextAsync(cancel).ConfigureAwait(false);
            return new StepPlace(PathNames.Canonical(path), PlaceAt(text, location.SourceSpan.Start), location.SourceSpan.Length);
        }

        /// <summary>Where an attribute's expression is written: its first argument's string literal.</summary>
        private async Task<StepPlace?> LiteralPlaceAsync(AttributeData attribute)
        {
            if (attribute.ApplicationSyntaxReference is not { } reference
                || await reference.GetSyntaxAsync(cancel).ConfigureAwait(false) is not AttributeSyntax syntax
                || syntax.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals is null || a.NameEquals.Name.Identifier.ValueText == "Expression")?.Expression is not LiteralExpressionSyntax literal)
            {
                return null;
            }
            return await PlaceOfAsync(literal.GetLocation()).ConfigureAwait(false);
        }
    }
}
