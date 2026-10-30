using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>The routes web projects declare in their code (<see cref="RouteReading"/>).</summary>
public sealed class WebRoutes : IExtensionWorkspaceMessageHandler<RoutesRequest, RoutesFound>
{
    public Task<RoutesFound> ExecuteAsync(RoutesRequest message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        RouteReading.ReadAsync(context.Solution, message.Projects, cancellationToken);
}

/// <summary>Reads an ASP.NET Core project's routes from its code as the server holds it, with no build and no running app:
/// minimal APIs (their groups' prefixes followed through locals, chained calls and the callers of the method that maps
/// them, as far as a straight-line reading proves, never guessed), attribute-routed controllers and conventional routes,
/// Razor Pages and their handlers, Blazor's <c>@page</c>, SignalR hubs and gRPC services.</summary>
internal static partial class RouteReading
{
    private const string EndpointExtensions = "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions";
    private const string HubExtensions = "Microsoft.AspNetCore.Builder.HubEndpointRouteBuilderExtensions";
    private const string GrpcExtensions = "Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions";
    private const string ControllerExtensions = "Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions";

    private static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
    };

    private static readonly HashSet<string> MapNames =
    [
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "Map", "MapHub", "MapGrpcService",
        "MapControllerRoute", "MapDefaultControllerRoute", "MapAreaControllerRoute",
    ];

    public static async Task<RoutesFound> ReadAsync(Solution solution, IReadOnlyList<string> asked, CancellationToken cancel)
    {
        List<WebRoute> routes = [];
        List<string> read = [];
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (Project project in solution.Projects)
        {
            cancel.ThrowIfCancellationRequested();
            // A multi-targeted project is several of the server's projects of one file: its routes are read once.
            if (project.Language != LanguageNames.CSharp || project.FilePath is not { } file || !IsWeb(project)
                || (asked.Count > 0 && !asked.Any(path => SamePath(path, file))) || !seen.Add(PathNames.Canonical(file)))
            {
                continue;
            }
            if (await project.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }
            read.Add(file);
            await new ProjectRoutes(project, compilation, routes, cancel).ReadAsync().ConfigureAwait(false);
        }
        return new RoutesFound(routes, read);
    }

    /// <summary>Whether a project references ASP.NET Core (its shared framework, or Blazor's or gRPC's packages).</summary>
    private static bool IsWeb(Project project) => project.MetadataReferences.Any(reference => reference.Display is { } display
        && Path.GetFileName(display) is { } name
        && (name.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Grpc.AspNetCore", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Two templates as one: a group's prefix and its route, a controller's and its action's.</summary>
    internal static string Join(string left, string right) => Rooted(left.TrimEnd('/') + "/" + right.TrimStart('~').TrimStart('/'));

    /// <summary>A template from the app's root, with no slash at its end but the root's own.</summary>
    internal static string Rooted(string template)
    {
        string trimmed = template.TrimStart('~').Trim('/');
        return "/" + trimmed;
    }

    /// <summary>The parameters a template names, as ASP.NET Core's route parser reads them: <c>{name:constraint=default?}</c>
    /// and catch-alls <c>{*rest}</c>, <c>{{</c> being a brace.</summary>
    internal static IEnumerable<RouteParameter> TemplateParameters(string template)
    {
        foreach (Match match in TemplateParameter().Matches(template))
        {
            string inner = match.Groups["inner"].Value.Trim();
            inner = inner.TrimStart('*');
            bool optional = inner.EndsWith('?');
            inner = inner.TrimEnd('?');
            int equals = inner.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                optional = true;
                inner = inner[..equals];
            }
            int colon = inner.IndexOf(':', StringComparison.Ordinal);
            string name = colon < 0 ? inner : inner[..colon];
            string? constraints = colon < 0 ? null : inner[(colon + 1)..];
            if (name.Length > 0)
            {
                yield return new RouteParameter(name, "route", Constraints: constraints, Optional: optional);
            }
        }
    }

    [GeneratedRegex(@"(?<!\{)\{(?!\{)(?<inner>[^{}]+)\}")]
    private static partial Regex TemplateParameter();

    /// <summary>One project's routes.</summary>
    private sealed partial class ProjectRoutes(Project project, Compilation compilation, List<WebRoute> routes, CancellationToken cancel)
    {
        private readonly string projectPath = project.FilePath!;
        private readonly string folder = Path.GetDirectoryName(project.FilePath!)!;
        private readonly Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        private readonly List<(string Pattern, string? Area)> conventional = [];
        private bool? enumsByName;

        public async Task ReadAsync()
        {
            foreach (Document document in project.Documents)
            {
                cancel.ThrowIfCancellationRequested();
                if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root || document.FilePath is null)
                {
                    continue;
                }
                List<InvocationExpressionSyntax> maps = [.. root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => MapNames.Contains(NameOf(i.Expression) ?? string.Empty))];
                if (maps.Count == 0)
                {
                    continue;
                }
                SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
                SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
                foreach (InvocationExpressionSyntax map in maps)
                {
                    await MappedAsync(map, model, text, document.FilePath).ConfigureAwait(false);
                }
            }
            ReadControllers();
            await ReadRazorAsync().ConfigureAwait(false);
        }

        /// <summary>Whether the app reads every enum by name: its own code makes a JsonStringEnumConverter (for its JSON
        /// options) or gives a source-generated context UseStringEnumConverter. Read once, at the first enum of a body.</summary>
        private bool EnumsByName() => enumsByName ??= ReadsEnumsByName();

        private bool ReadsEnumsByName()
        {
            HashSet<string> own = new(project.Documents.Select(d => d.FilePath).OfType<string>(), StringComparer.Ordinal);
            foreach (SyntaxTree tree in compilation.SyntaxTrees.Where(t => own.Contains(t.FilePath)))
            {
                foreach (SyntaxNode node in tree.GetRoot(cancel).DescendantNodes())
                {
                    if (node is ObjectCreationExpressionSyntax { Type: var made } && TypeName(made) == "JsonStringEnumConverter"
                        || node is AttributeArgumentSyntax { NameEquals.Name.Identifier.ValueText: "UseStringEnumConverter", Expression: var given }
                            && given.IsKind(SyntaxKind.TrueLiteralExpression))
                    {
                        return true;
                    }
                }
            }
            return false;

            static string? TypeName(TypeSyntax type) => type switch
            {
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => null,
            };
        }

        private string Hash(string path, SourceText text)
        {
            if (!hashes.TryGetValue(path, out string? hash))
            {
                hash = TextHash.Of(text.ToString());
                hashes[path] = hash;
            }
            return hash;
        }

        private async Task MappedAsync(InvocationExpressionSyntax map, SemanticModel model, SourceText text, string path)
        {
            if (model.GetSymbolInfo(map, cancel).Symbol is not IMethodSymbol method)
            {
                return;
            }
            IMethodSymbol original = method.ReducedFrom ?? method;
            string container = original.ContainingType.ToDisplayString();
            ExpressionSyntax? receiver = Receiver(map, method);
            SimpleNameSyntax name = map.Expression is MemberAccessExpressionSyntax access ? access.Name : (SimpleNameSyntax)map.Expression;
            if (container == ControllerExtensions)
            {
                // Conventional routes, which the controllers with no route of their own take.
                string? pattern = method.Name == "MapDefaultControllerRoute" ? "{controller=Home}/{action=Index}/{id?}"
                    : Constant(Argument(map, method, "pattern"), model, cancel);
                string? area = method.Name == "MapAreaControllerRoute" ? Constant(Argument(map, method, "areaName"), model, cancel) : null;
                if (pattern is not null && (method.Name != "MapAreaControllerRoute" || area is not null))
                {
                    conventional.Add((pattern, area));
                }
                return;
            }
            string? prefix = await PrefixAsync(receiver, model, 0).ConfigureAwait(false);
            if (prefix is null)
            {
                return;
            }
            Place at = PlaceAt(text, name.SpanStart);
            if (container == HubExtensions && method.Name == "MapHub" && method.TypeArguments is [INamedTypeSymbol hub])
            {
                if (Constant(Argument(map, method, "pattern"), model, cancel) is not { } pattern)
                {
                    return;
                }
                ExpressionSyntax written = Argument(map, method, "pattern")!;
                (string? hubPath, Place? hubAt) = DeclarationOf(hub);
                List<string> operations = [.. hub.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic)
                    .Select(m => m.Name).Distinct()];
                routes.Add(new WebRoute(projectPath, RouteKinds.SignalR, [], Join(prefix, pattern), null, [], hub.Name, path, at,
                    PlaceAt(text, written.Span.End), Hash(path, text), hubPath, hubAt, Operations: operations));
                return;
            }
            if (container == GrpcExtensions && method.Name == "MapGrpcService" && method.TypeArguments is [INamedTypeSymbol service])
            {
                GrpcRoutes(service, path, text, at, PlaceAt(text, map.Span.End));
                return;
            }
            if (container != EndpointExtensions)
            {
                return;
            }
            IReadOnlyList<string>? methods = method.Name switch
            {
                "MapMethods" => Methods(Argument(map, method, "httpMethods"), model),
                "Map" => [],
                _ => MapVerbs.TryGetValue(method.Name, out string? verb) ? [verb] : null,
            };
            ExpressionSyntax? patternArgument = Argument(map, method, "pattern");
            if (methods is null || Constant(patternArgument, model, cancel) is not { } template)
            {
                return;
            }
            string whole = Join(prefix, template);
            ExpressionSyntax? handler = Argument(map, method, "handler") ?? Argument(map, method, "requestDelegate");
            IMethodSymbol? handled = handler is null ? null : HandlerOf(handler, model);
            string handlerName = handled is null || handled.MethodKind == MethodKind.AnonymousFunction ? "lambda" : $"{handled.ContainingType.Name}.{handled.Name}";
            (string? handlerPath, Place? handlerAt) = handled is null ? (null, null)
                : handled.MethodKind == MethodKind.AnonymousFunction ? (path, PlaceAt(text, handler!.SpanStart))
                : DeclarationOf(handled);
            bool bodied = !methods.Any(m => m is "GET" or "HEAD" or "DELETE" or "OPTIONS");
            (List<RouteParameter> parameters, ITypeSymbol? body) = Bind(whole, handled?.Parameters ?? [], apiController: true, bodied, minimal: true);
            routes.Add(new WebRoute(projectPath, RouteKinds.Minimal, methods, whole, null, parameters, handlerName, path, at,
                PlaceAt(text, patternArgument!.Span.End), Hash(path, text), handlerPath, handlerAt,
                body?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), body is null ? null : JsonSample.Of(body, EnumsByName)));
        }

        /// <summary>The route group a map call's receiver is, as its prefix; null when it cannot be proven.</summary>
        private async Task<string?> PrefixAsync(ExpressionSyntax? expression, SemanticModel model, int depth)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }
            if (expression is null || depth > 8)
            {
                return null;
            }
            if (model.GetTypeInfo(expression, cancel).Type?.ToDisplayString() == "Microsoft.AspNetCore.Builder.WebApplication")
            {
                return string.Empty;
            }
            switch (expression)
            {
                case InvocationExpressionSyntax invocation when model.GetSymbolInfo(invocation, cancel).Symbol is IMethodSymbol called:
                {
                    IMethodSymbol original = called.ReducedFrom ?? called;
                    ExpressionSyntax? receiver = Receiver(invocation, called);
                    if (called.Name == "MapGroup" && original.ContainingType.ToDisplayString() == EndpointExtensions)
                    {
                        string? before = await PrefixAsync(receiver, model, depth + 1).ConfigureAwait(false);
                        string? prefix = Constant(Argument(invocation, called, "prefix"), model, cancel);
                        return before is null || prefix is null ? null : Join(before, prefix);
                    }
                    // A convention (RequireAuthorization, WithTags, WithOpenApi) gives back the builder it was given.
                    if (original.IsExtensionMethod && original.Parameters.Length > 0 && original.ReturnType is ITypeParameterSymbol returned
                        && SymbolEqualityComparer.Default.Equals(returned, original.Parameters[0].Type)
                        && original.ContainingAssembly.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
                    {
                        return await PrefixAsync(receiver, model, depth + 1).ConfigureAwait(false);
                    }
                    return null;
                }
                case IdentifierNameSyntax identifier:
                    return model.GetSymbolInfo(identifier, cancel).Symbol switch
                    {
                        ILocalSymbol local => await LocalPrefixAsync(local, model, depth).ConfigureAwait(false),
                        IParameterSymbol parameter => await ParameterPrefixAsync(parameter, depth).ConfigureAwait(false),
                        _ => null,
                    };
                default:
                    return null;
            }
        }

        /// <summary>A local's group: its initializer's, when nothing assigns it again.</summary>
        private async Task<string?> LocalPrefixAsync(ILocalSymbol local, SemanticModel model, int depth)
        {
            if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancel) is not VariableDeclaratorSyntax { Initializer.Value: { } value } declarator)
            {
                return null;
            }
            SyntaxNode root = declarator.SyntaxTree.GetRoot(cancel);
            bool assigned = root.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.Left is IdentifierNameSyntax target
                && target.Identifier.ValueText == local.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(target, cancel).Symbol, local));
            return assigned ? null : await PrefixAsync(value, model, depth + 1).ConfigureAwait(false);
        }

        /// <summary>A parameter's group: the one every call of its method gives it, when they all give the same; the
        /// endpoints of <c>UseEndpoints</c>' callback are the app's own.</summary>
        private async Task<string?> ParameterPrefixAsync(IParameterSymbol parameter, int depth)
        {
            if (parameter.ContainingSymbol is not IMethodSymbol owner)
            {
                return null;
            }
            if (owner.MethodKind == MethodKind.AnonymousFunction)
            {
                SyntaxNode? lambda = owner.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancel);
                if (lambda?.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax use }
                    && project.GetDocument(lambda.SyntaxTree) is { } document
                    && await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is { } model
                    && model.GetSymbolInfo(use, cancel).Symbol is IMethodSymbol { Name: "UseEndpoints" })
                {
                    return string.Empty;
                }
                return null;
            }
            List<string?> given = [];
            foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(owner, project.Solution, cancel).ConfigureAwait(false))
            {
                foreach (ReferenceLocation location in referenced.Locations)
                {
                    SyntaxNode? node = (await location.Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))?.FindNode(location.Location.SourceSpan);
                    if (node?.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } call
                        || await location.Document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model
                        || model.GetSymbolInfo(call, cancel).Symbol is not IMethodSymbol called
                        || !SymbolEqualityComparer.Default.Equals(called.ReducedFrom ?? called.OriginalDefinition, owner.OriginalDefinition))
                    {
                        return null;
                    }
                    ExpressionSyntax? argument = owner.IsExtensionMethod && parameter.Ordinal == 0 ? Receiver(call, called)
                        : Argument(call, called, parameter.Name);
                    given.Add(await PrefixAsync(argument, model, depth + 1).ConfigureAwait(false));
                }
            }
            return given.Count > 0 && given.All(g => g is not null && g == given[0]) ? given[0] : null;
        }

        /// <summary>MapMethods' methods: string constants, or HttpMethods' fields.</summary>
        private IReadOnlyList<string>? Methods(ExpressionSyntax? expression, SemanticModel model)
        {
            IEnumerable<ExpressionSyntax>? elements = expression switch
            {
                CollectionExpressionSyntax collection => collection.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression),
                ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions,
                ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
                ObjectCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
                _ => null,
            };
            if (elements is null)
            {
                return null;
            }
            List<string> methods = [];
            foreach (ExpressionSyntax element in elements)
            {
                if (Constant(element, model, cancel) is { } constant)
                {
                    methods.Add(constant.ToUpperInvariant());
                }
                else if (model.GetSymbolInfo(element, cancel).Symbol is IFieldSymbol { ContainingType: { } fields } field
                    && fields.ToDisplayString() == "Microsoft.AspNetCore.Http.HttpMethods")
                {
                    methods.Add(field.Name.ToUpperInvariant());
                }
                else
                {
                    return null;
                }
            }
            return methods;
        }

        private IMethodSymbol? HandlerOf(ExpressionSyntax handler, SemanticModel model)
        {
            SymbolInfo info = model.GetSymbolInfo(handler, cancel);
            return info.Symbol as IMethodSymbol ?? (info.CandidateSymbols is [IMethodSymbol only] ? only : null);
        }

        private (string? Path, Place? At) DeclarationOf(ISymbol symbol)
        {
            if (symbol.Locations.FirstOrDefault(l => l.IsInSource) is not { SourceTree: { } tree } location || tree.FilePath is not { Length: > 0 } path)
            {
                return (null, null);
            }
            LinePosition start = location.GetLineSpan().StartLinePosition;
            return (path, new Place(start.Line, start.Character));
        }

        /// <summary>Where each of a handler's parameters comes from, as ASP.NET Core binds it, and the body parameter's type.
        /// <paramref name="apiController"/> infers a body for a complex type, as minimal APIs and API controllers do.</summary>
        private (List<RouteParameter> Parameters, ITypeSymbol? Body) Bind(string template, IEnumerable<IParameterSymbol> handlerParameters,
            bool apiController, bool bodied, bool minimal)
        {
            List<RouteParameter> fromTemplate = [.. TemplateParameters(template)];
            List<RouteParameter> bound = [];
            ITypeSymbol? body = null;
            foreach (IParameterSymbol parameter in handlerParameters)
            {
                string type = parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                (string? source, string? name) = Attributed(parameter);
                name ??= parameter.Name;
                if (source is null)
                {
                    if (Special(parameter.Type))
                    {
                        continue;
                    }
                    source = IsForm(parameter.Type) ? "form"
                        : fromTemplate.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ? "route"
                        : Simple(parameter.Type) || (parameter.Type is IArrayTypeSymbol array && Simple(array.ElementType)) ? "query"
                        : parameter.Type.TypeKind == TypeKind.Interface || parameter.Type.IsAbstract ? "services"
                        : apiController ? (bodied && body is null ? "body" : minimal ? "services" : "query")
                        : bodied ? "form" : "query";
                }
                if (source == "body")
                {
                    body = parameter.Type;
                }
                RouteParameter? routed = source == "route" ? fromTemplate.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
                bound.Add(new RouteParameter(routed?.Name ?? name, source, type, routed?.Constraints,
                    routed?.Optional ?? (parameter.HasExplicitDefaultValue || parameter.NullableAnnotation == NullableAnnotation.Annotated)));
            }
            // A route value no parameter takes is still the route's.
            List<RouteParameter> parameters = [.. fromTemplate.Where(t => !bound.Any(b => b.Source == "route" && string.Equals(b.Name, t.Name, StringComparison.OrdinalIgnoreCase))), .. bound];
            parameters.Sort((left, right) => Order(left).CompareTo(Order(right)));
            return (parameters, body);

            int Order(RouteParameter parameter) => parameter.Source == "route" ? fromTemplate.FindIndex(t => string.Equals(t.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)) : 1000;
        }

        /// <summary>A parameter's source as its attribute names it ([FromRoute], [FromQuery], ...), with the name it gives.</summary>
        private static (string? Source, string? Name) Attributed(IParameterSymbol parameter)
        {
            foreach (AttributeData attribute in parameter.GetAttributes())
            {
                string? source = attribute.AttributeClass?.Name switch
                {
                    "FromRouteAttribute" => "route",
                    "FromQueryAttribute" => "query",
                    "FromBodyAttribute" => "body",
                    "FromHeaderAttribute" => "header",
                    "FromFormAttribute" => "form",
                    "FromServicesAttribute" or "FromKeyedServicesAttribute" => "services",
                    _ => null,
                };
                if (source is not null)
                {
                    string? name = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Name").Value.Value as string;
                    return (source, name);
                }
            }
            return (null, null);
        }

        /// <summary>What ASP.NET Core gives a handler itself, never from the request's values.</summary>
        private static bool Special(ITypeSymbol type) => type.ToDisplayString() is "Microsoft.AspNetCore.Http.HttpContext" or "Microsoft.AspNetCore.Http.HttpRequest"
            or "Microsoft.AspNetCore.Http.HttpResponse" or "System.Threading.CancellationToken" or "System.Security.Claims.ClaimsPrincipal"
            or "System.IO.Stream" or "System.IO.Pipelines.PipeReader";

        private static bool IsForm(ITypeSymbol type) => type.ToDisplayString() is "Microsoft.AspNetCore.Http.IFormFile" or "Microsoft.AspNetCore.Http.IFormFileCollection"
            or "Microsoft.AspNetCore.Http.IFormCollection";

        /// <summary>A type bound from one string: a primitive, an enum, a date, a Guid, or one with a static TryParse.</summary>
        internal static bool Simple(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var inner] })
            {
                type = inner;
            }
            return type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_String or SpecialType.System_DateTime
                || type.TypeKind == TypeKind.Enum
                || type.ToDisplayString() is "System.Guid" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly" or "System.TimeSpan" or "System.Uri"
                || type.GetMembers("TryParse").OfType<IMethodSymbol>().Any(m => m.IsStatic && m.Parameters.Length is 2 or 3 && m.Parameters[0].Type.SpecialType == SpecialType.System_String);
        }

        // Controllers

        private void ReadControllers()
        {
            INamedTypeSymbol? controllerBase = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase");
            if (controllerBase is null)
            {
                return;
            }
            bool assemblyApi = compilation.Assembly.GetAttributes().Any(a => a.AttributeClass?.Name == "ApiControllerAttribute");
            foreach (INamedTypeSymbol type in SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                cancel.ThrowIfCancellationRequested();
                if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType || type.DeclaredAccessibility != Accessibility.Public
                    || Chain(type).SelectMany(t => t.GetAttributes()).Any(a => a.AttributeClass?.Name == "NonControllerAttribute")
                    || !(Chain(type).Any(t => SymbolEqualityComparer.Default.Equals(t, controllerBase))
                        || Chain(type).SelectMany(t => t.GetAttributes()).Any(a => a.AttributeClass?.Name is "ControllerAttribute" or "ApiControllerAttribute")))
                {
                    continue;
                }
                Controller(type, assemblyApi);
            }
        }

        private static IEnumerable<INamedTypeSymbol> Chain(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? at = type; at is not null && at.SpecialType != SpecialType.System_Object; at = at.BaseType)
            {
                yield return at;
            }
        }

        private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol space)
        {
            foreach (INamespaceOrTypeSymbol member in space.GetMembers())
            {
                if (member is INamespaceSymbol inner)
                {
                    foreach (INamedTypeSymbol type in SourceTypes(inner))
                    {
                        yield return type;
                    }
                }
                else if (member is INamedTypeSymbol type && type.Locations.Any(l => l.IsInSource))
                {
                    yield return type;
                    foreach (INamedTypeSymbol nested in type.GetTypeMembers().Where(t => t.Locations.Any(l => l.IsInSource)))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private void Controller(INamedTypeSymbol type, bool assemblyApi)
        {
            string name = type.Name.EndsWith("Controller", StringComparison.Ordinal) && type.Name.Length > "Controller".Length ? type.Name[..^"Controller".Length] : type.Name;
            List<AttributeData> classAttributes = [.. Chain(type).SelectMany(t => t.GetAttributes())];
            bool api = assemblyApi || classAttributes.Any(a => a.AttributeClass?.Name == "ApiControllerAttribute");
            string? area = classAttributes.FirstOrDefault(a => a.AttributeClass?.Name == "AreaAttribute")?.ConstructorArguments.FirstOrDefault().Value as string;
            List<string> prefixes = [.. type.GetAttributes().Where(a => a.AttributeClass?.Name == "RouteAttribute")
                .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string).OfType<string>()];
            if (prefixes.Count == 0)
            {
                prefixes = [.. classAttributes.Where(a => a.AttributeClass?.Name == "RouteAttribute")
                    .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string).OfType<string>()];
            }
            HashSet<string> overridden = new(StringComparer.Ordinal);
            foreach (INamedTypeSymbol owner in Chain(type))
            {
                if (owner.ContainingAssembly.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
                {
                    break;
                }
                foreach (IMethodSymbol action in owner.GetMembers().OfType<IMethodSymbol>())
                {
                    if (action.MethodKind != MethodKind.Ordinary || action.DeclaredAccessibility != Accessibility.Public || action.IsStatic || action.IsAbstract
                        || action.IsGenericMethod || action.GetAttributes().Any(a => a.AttributeClass?.Name == "NonActionAttribute")
                        || !overridden.Add(action.Name + "(" + string.Join(",", action.Parameters.Select(p => p.Type.ToDisplayString())) + ")"))
                    {
                        continue;
                    }
                    Action(type, name, area, api, prefixes, action);
                }
            }
        }

        private void Action(INamedTypeSymbol type, string controller, string? area, bool api, List<string> prefixes, IMethodSymbol action)
        {
            string actionName = action.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ActionNameAttribute")?.ConstructorArguments.FirstOrDefault().Value as string
                ?? (action.Name.EndsWith("Async", StringComparison.Ordinal) && action.Name.Length > 5 ? action.Name[..^5] : action.Name);
            List<(AttributeData Attribute, string? Template, IReadOnlyList<string> Verbs)> declared = [];
            foreach (AttributeData attribute in action.GetAttributes())
            {
                if (attribute.AttributeClass is not { } kind)
                {
                    continue;
                }
                if (kind.Name == "RouteAttribute")
                {
                    declared.Add((attribute, attribute.ConstructorArguments.FirstOrDefault().Value as string, []));
                }
                else if (kind.Name == "AcceptVerbsAttribute")
                {
                    List<string> verbs = [.. attribute.ConstructorArguments.SelectMany(c => c.Kind == TypedConstantKind.Array ? c.Values : [c])
                        .Select(v => v.Value as string).OfType<string>().Select(v => v.ToUpperInvariant())];
                    declared.Add((attribute, attribute.NamedArguments.FirstOrDefault(a => a.Key == "Route").Value.Value as string, verbs));
                }
                else if (Chain(kind).Any(t => t.Name == "HttpMethodAttribute") && kind.Name.StartsWith("Http", StringComparison.Ordinal) && kind.Name.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    string verb = kind.Name["Http".Length..^"Attribute".Length].ToUpperInvariant();
                    declared.Add((attribute, attribute.ConstructorArguments.FirstOrDefault().Value as string, [verb]));
                }
            }
            // An attribute with a template is a route of its own; the methods of those without one go to each [Route]'s.
            List<string> loose = [.. declared.Where(d => d.Template is null).SelectMany(d => d.Verbs).Distinct()];
            List<(string? Template, IReadOnlyList<string> Verbs, AttributeData? Attribute)> made = [];
            foreach ((AttributeData attribute, string? template, IReadOnlyList<string> verbs) in declared.Where(d => d.Template is not null))
            {
                made.Add((template, verbs.Count > 0 ? verbs : loose, attribute));
            }
            if (made.Count == 0 && (prefixes.Count > 0 || declared.Count > 0))
            {
                made.Add((null, loose, declared.FirstOrDefault().Attribute));
            }
            List<(string Template, IReadOnlyList<string> Verbs, AttributeData? Attribute)> whole = [];
            if (prefixes.Count > 0 || made.Any(m => m.Template is not null))
            {
                foreach ((string? template, IReadOnlyList<string> verbs, AttributeData? attribute) in made)
                {
                    if (template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)) || prefixes.Count == 0)
                    {
                        whole.Add((Rooted(template ?? string.Empty), verbs, attribute));
                        continue;
                    }
                    foreach (string prefix in prefixes)
                    {
                        whole.Add((template is null ? Rooted(prefix) : Join(prefix, template), verbs, attribute));
                    }
                }
            }
            else
            {
                // No route of its own: the conventional routes' that its area takes.
                foreach ((string pattern, string? patternArea) in conventional)
                {
                    bool areaPattern = patternArea is not null || pattern.Contains("{area", StringComparison.OrdinalIgnoreCase);
                    if ((area is null) == areaPattern || (patternArea is not null && !string.Equals(patternArea, area, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    whole.Add((Rooted(pattern), loose, null));
                }
            }
            foreach ((string template, IReadOnlyList<string> verbs, AttributeData? attribute) in whole)
            {
                string filled = Tokens(Conventional(template, controller, actionName, area), controller, actionName, area);
                SyntaxNode? written = attribute?.ApplicationSyntaxReference?.GetSyntax(cancel);
                Location? location = written?.GetLocation() ?? action.Locations.FirstOrDefault(l => l.IsInSource);
                if (location?.SourceTree is not { } tree || project.GetDocument(tree) is not { FilePath: { } path })
                {
                    continue;
                }
                SourceText text = tree.GetText(cancel);
                TextSpan span = location.SourceSpan;
                bool bodied = verbs.Count == 0 || !verbs.Any(m => m is "GET" or "HEAD" or "DELETE" or "OPTIONS");
                (List<RouteParameter> parameters, ITypeSymbol? body) = Bind(filled, action.Parameters, api, bodied, minimal: false);
                (string? handlerPath, Place? handlerAt) = DeclarationOf(action);
                routes.Add(new WebRoute(projectPath, RouteKinds.Controller, verbs, filled, null, parameters, $"{type.Name}.{action.Name}", path,
                    PlaceAt(text, span.Start), PlaceAt(text, span.End), Hash(path, text), handlerPath, handlerAt,
                    body?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), body is null ? null : JsonSample.Of(body, EnumsByName)));
            }
        }

        /// <summary>A conventional route's controller, action and area filled in, its other values left as they are.</summary>
        private static string Conventional(string template, string controller, string action, string? area) =>
            TemplateParameter().Replace(template, match =>
            {
                string inner = match.Groups["inner"].Value;
                string name = inner.Split(':', '=', '?')[0].Trim();
                return name.ToLowerInvariant() switch
                {
                    "controller" => controller,
                    "action" => action,
                    "area" when area is not null => area,
                    _ => match.Value,
                };
            });

        /// <summary>A template's [controller], [action] and [area] tokens filled in.</summary>
        private static string Tokens(string template, string controller, string action, string? area)
        {
            string filled = Regex.Replace(template, @"\[controller\]", controller, RegexOptions.IgnoreCase);
            filled = Regex.Replace(filled, @"\[action\]", action, RegexOptions.IgnoreCase);
            return area is null ? filled : Regex.Replace(filled, @"\[area\]", area, RegexOptions.IgnoreCase);
        }

        // Razor Pages and Blazor

        private async Task ReadRazorAsync()
        {
            foreach (TextDocument document in project.AdditionalDocuments)
            {
                cancel.ThrowIfCancellationRequested();
                if (document.FilePath is not { } path)
                {
                    continue;
                }
                if (path.EndsWith(RazorFiles.Component, StringComparison.OrdinalIgnoreCase))
                {
                    SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
                    foreach ((int line, string template) in Directives(text, page: true))
                    {
                        string whole = Rooted(template);
                        TextLine row = text.Lines[line];
                        routes.Add(new WebRoute(projectPath, RouteKinds.Blazor, ["GET"], whole, null, [.. TemplateParameters(whole)],
                            Path.GetFileNameWithoutExtension(path), path, PlaceAt(text, row.Start), PlaceAt(text, row.End), Hash(path, text), path, PlaceAt(text, row.Start)));
                    }
                }
                else if (path.EndsWith(RazorFiles.Page, StringComparison.OrdinalIgnoreCase))
                {
                    await RazorPageAsync(document, path).ConfigureAwait(false);
                }
            }
        }

        /// <summary>The routes a Razor file's <c>@page</c> directives give (and a component's <c>@attribute [Route]</c>):
        /// each line with its template, empty where the directive names none.</summary>
        private static IEnumerable<(int Line, string Template)> Directives(SourceText text, bool page)
        {
            foreach (TextLine line in text.Lines)
            {
                string row = line.ToString().Trim();
                Match directive = PageDirective().Match(row);
                if (directive.Success)
                {
                    yield return (line.LineNumber, directive.Groups["template"].Value);
                }
                else if (page && RouteDirective().Match(row) is { Success: true } attribute)
                {
                    yield return (line.LineNumber, attribute.Groups["template"].Value);
                }
            }
        }

        [GeneratedRegex(@"^@page(?:\s+""(?<template>[^""]*)"")?\s*$")]
        private static partial Regex PageDirective();

        [GeneratedRegex(@"^@attribute\s+\[(?:Microsoft\.AspNetCore\.Components\.)?Route\(\s*""(?<template>[^""]*)""\s*\)\]\s*$")]
        private static partial Regex RouteDirective();

        private async Task RazorPageAsync(TextDocument page, string path)
        {
            SourceText text = await page.GetTextAsync(cancel).ConfigureAwait(false);
            if (Directives(text, page: false).Take(1).ToList() is not [var directive])
            {
                return;
            }
            // A page's route is its path under Pages (an area's under Areas/X/Pages), Index being its folder's.
            string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            string? area = null;
            string under;
            if (relative.StartsWith("Pages/", StringComparison.OrdinalIgnoreCase))
            {
                under = relative["Pages/".Length..];
            }
            else if (AreaPage().Match(relative) is { Success: true } areaPage)
            {
                area = areaPage.Groups["area"].Value;
                under = areaPage.Groups["rest"].Value;
            }
            else
            {
                return;
            }
            string name = under[..^RazorFiles.Page.Length];
            if (name.Equals("Index", StringComparison.OrdinalIgnoreCase))
            {
                name = string.Empty;
            }
            else if (name.EndsWith("/Index", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^"/Index".Length];
            }
            string route = Rooted((area is null ? string.Empty : area + "/") + name);
            string template = directive.Template.Length == 0 ? route
                : directive.Template.StartsWith('/') || directive.Template.StartsWith("~/", StringComparison.Ordinal) ? Rooted(directive.Template)
                : Join(route, directive.Template);
            TextLine row = text.Lines[directive.Line];
            Place at = PlaceAt(text, row.Start);
            Place end = PlaceAt(text, row.End);
            string pageName = Path.GetFileName(path);
            List<(IMethodSymbol Handler, string Verb, string Named)> handlers = [];
            if (project.Documents.FirstOrDefault(d => SamePath(d.FilePath, path + ".cs")) is { } behind
                && await behind.GetSemanticModelAsync(cancel).ConfigureAwait(false) is { } model
                && await behind.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is { } root)
            {
                foreach (ClassDeclarationSyntax declared in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declared, cancel) is not INamedTypeSymbol type
                        || !Chain(type).Any(t => t.ToDisplayString() == "Microsoft.AspNetCore.Mvc.RazorPages.PageModel"))
                    {
                        continue;
                    }
                    foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (method.DeclaredAccessibility == Accessibility.Public && !method.IsStatic && PageHandler().Match(method.Name) is { Success: true } handler)
                        {
                            handlers.Add((method, handler.Groups["verb"].Value.ToUpperInvariant(), handler.Groups["name"].Value));
                        }
                    }
                }
            }
            if (!handlers.Any(h => h.Verb == "GET" && h.Named.Length == 0))
            {
                routes.Add(new WebRoute(projectPath, RouteKinds.RazorPage, ["GET"], template, null, [.. TemplateParameters(template)], pageName, path, at, end,
                    Hash(path, text), path, at));
            }
            foreach ((IMethodSymbol handler, string verb, string named) in handlers)
            {
                if (handler.Locations.FirstOrDefault(l => l.IsInSource) is not { SourceTree: { } tree } location)
                {
                    continue;
                }
                SourceText behindText = tree.GetText(cancel);
                bool bodied = verb is not ("GET" or "HEAD" or "DELETE" or "OPTIONS");
                (List<RouteParameter> parameters, _) = Bind(template, handler.Parameters, apiController: false, bodied, minimal: false);
                routes.Add(new WebRoute(projectPath, RouteKinds.RazorPage, [verb], template, named.Length > 0 ? "handler=" + named : null, parameters,
                    $"{handler.ContainingType.Name}.{handler.Name}", tree.FilePath, PlaceAt(behindText, location.SourceSpan.Start),
                    PlaceAt(behindText, location.SourceSpan.End), Hash(tree.FilePath, behindText), tree.FilePath, PlaceAt(behindText, location.SourceSpan.Start)));
            }
        }

        [GeneratedRegex(@"^Areas/(?<area>[^/]+)/Pages/(?<rest>.+)$", RegexOptions.IgnoreCase)]
        private static partial Regex AreaPage();

        [GeneratedRegex(@"^On(?<verb>Get|Post|Put|Delete|Patch|Head|Options)(?<name>[A-Za-z0-9_]*?)(?:Async)?$")]
        private static partial Regex PageHandler();

        // gRPC

        /// <summary>A gRPC service's methods, each a POST to /package.Service/Method: the service's name read from its
        /// generated base's <c>__ServiceName</c>, else from the project's .proto file that declares a service of the base's
        /// class's name, alone.</summary>
        private void GrpcRoutes(INamedTypeSymbol service, string path, SourceText text, Place at, Place end)
        {
            INamedTypeSymbol? generatedBase = service.BaseType;
            if (generatedBase?.ContainingType is not { } generated)
            {
                return;
            }
            string? serviceName = null;
            if (generated.GetMembers("__ServiceName").OfType<IFieldSymbol>().FirstOrDefault()?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancel)
                is VariableDeclaratorSyntax { Initializer.Value: LiteralExpressionSyntax literal })
            {
                serviceName = literal.Token.ValueText;
            }
            serviceName ??= ProtoService(generated.Name);
            if (serviceName is null)
            {
                return;
            }
            foreach (IMethodSymbol method in service.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride && m.MethodKind == MethodKind.Ordinary))
            {
                if (method.Locations.FirstOrDefault(l => l.IsInSource) is not { SourceTree: { } tree } location)
                {
                    continue;
                }
                SourceText methodText = tree.GetText(cancel);
                routes.Add(new WebRoute(projectPath, RouteKinds.Grpc, ["POST"], $"/{serviceName}/{method.Name}", null, [], $"{service.Name}.{method.Name}",
                    tree.FilePath, PlaceAt(methodText, location.SourceSpan.Start), PlaceAt(methodText, location.SourceSpan.End), Hash(tree.FilePath, methodText),
                    tree.FilePath, PlaceAt(methodText, location.SourceSpan.Start),
                    method.Parameters.FirstOrDefault()?.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }

        /// <summary>The full name of the one service of that name the project's .proto files declare.</summary>
        private string? ProtoService(string name)
        {
            List<string> found = [];
            foreach (string proto in Directory.EnumerateFiles(folder, "*.proto", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, proto).Replace('\\', '/');
                if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string written = File.ReadAllText(proto);
                if (Regex.IsMatch(written, $@"\bservice\s+{Regex.Escape(name)}\s*\{{"))
                {
                    string package = ProtoPackage().Match(written) is { Success: true } declared ? declared.Groups["package"].Value + "." : string.Empty;
                    found.Add(package + name);
                }
            }
            return found.Count == 1 ? found[0] : null;
        }

        [GeneratedRegex(@"^\s*package\s+(?<package>[A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline)]
        private static partial Regex ProtoPackage();
    }
}

/// <summary>A JSON body shaped from a type, as a person would start writing one: its properties by System.Text.Json's web
/// names (camelCase, or [JsonPropertyName]'s), nested objects, a collection as one element, an enum as its first member's
/// number, or its name where the app, the enum or the property reads it by name (a JsonStringEnumConverter), and a value of
/// each kind that reads as a placeholder.</summary>
internal static class JsonSample
{
    /// <summary>The body for a type, <paramref name="enumsByName"/> asked at its first enum whether the app reads every enum
    /// by name.</summary>
    public static string Of(ITypeSymbol type, Func<bool> enumsByName)
    {
        StringBuilder written = new();
        Write(written, type, 0, [], enumsByName, byName: false);
        return written.ToString();
    }

    private static void Write(StringBuilder written, ITypeSymbol type, int depth, HashSet<ITypeSymbol> open, Func<bool> enumsByName, bool byName)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var inner] })
        {
            type = inner;
        }
        string name = type.ToDisplayString();
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                written.Append("\"\"");
                return;
            case SpecialType.System_Char:
                written.Append("\"a\"");
                return;
            case SpecialType.System_Boolean:
                written.Append("false");
                return;
            case >= SpecialType.System_SByte and <= SpecialType.System_Double:
                written.Append('0');
                return;
            case SpecialType.System_DateTime:
                written.Append("\"2000-01-01T00:00:00Z\"");
                return;
            case SpecialType.System_Object:
                written.Append("{}");
                return;
        }
        if (type.TypeKind == TypeKind.Enum)
        {
            IFieldSymbol? first = type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue);
            written.Append(first is null ? "0"
                : byName || ReadByName(type) || enumsByName() ? $"\"{EnumName(first)}\""
                : Convert.ToString(first.ConstantValue, CultureInfo.InvariantCulture));
            return;
        }
        switch (name)
        {
            case "System.Guid":
                written.Append("\"00000000-0000-0000-0000-000000000000\"");
                return;
            case "System.DateTimeOffset":
                written.Append("\"2000-01-01T00:00:00+00:00\"");
                return;
            case "System.DateOnly":
                written.Append("\"2000-01-01\"");
                return;
            case "System.TimeOnly" or "System.TimeSpan":
                written.Append("\"00:00:00\"");
                return;
            case "System.Uri":
                written.Append("\"\"");
                return;
        }
        string indent = new(' ', (depth + 1) * 2);
        string closing = new(' ', depth * 2);
        ITypeSymbol? values = Dictionary(type);
        ITypeSymbol? element = values is null ? Element(type) : null;
        // A type inside itself (a tree's children of its own type, a list of itself) stops, empty, where it comes again.
        if (depth > 6 || !open.Add(type))
        {
            written.Append(values is not null ? "{}" : element is not null ? "[]" : "null");
            return;
        }
        List<IPropertySymbol> properties = values is null && element is null ? [.. Properties(type)] : [];
        if (values is not null)
        {
            written.Append("{\n").Append(indent).Append("\"key\": ");
            Write(written, values, depth + 1, open, enumsByName, byName: false);
            written.Append('\n').Append(closing).Append('}');
        }
        else if (element is not null)
        {
            written.Append("[\n").Append(indent);
            Write(written, element, depth + 1, open, enumsByName, byName: false);
            written.Append('\n').Append(closing).Append(']');
        }
        else if (properties.Count == 0)
        {
            written.Append("{}");
        }
        else
        {
            written.Append("{\n");
            for (int i = 0; i < properties.Count; i++)
            {
                IPropertySymbol property = properties[i];
                written.Append(indent).Append('"').Append(JsonName(property)).Append("\": ");
                Write(written, property.Type, depth + 1, open, enumsByName, ReadByName(property));
                written.Append(i < properties.Count - 1 ? ",\n" : "\n");
            }
            written.Append(closing).Append('}');
        }
        open.Remove(type);
    }

    /// <summary>The properties System.Text.Json writes and reads: public, of the instance, not [JsonIgnore]d, base types' first.</summary>
    private static IEnumerable<IPropertySymbol> Properties(ITypeSymbol type)
    {
        List<ITypeSymbol> chain = [];
        for (ITypeSymbol? at = type; at is not null && at.SpecialType != SpecialType.System_Object && at.SpecialType != SpecialType.System_ValueType; at = at.BaseType)
        {
            chain.Insert(0, at);
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ITypeSymbol owner in chain)
        {
            foreach (IPropertySymbol property in owner.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.DeclaredAccessibility == Accessibility.Public && !property.IsStatic && !property.IsIndexer && property.GetMethod is not null
                    && !property.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute") && seen.Add(property.Name)
                    && !(owner.IsRecord && property.Name == "EqualityContract"))
                {
                    yield return property;
                }
            }
        }
    }

    /// <summary>Whether an enum, or a property of one, is read by name: [JsonConverter(typeof(JsonStringEnumConverter))]
    /// on it, or the generic converter's.</summary>
    private static bool ReadByName(ISymbol symbol) => symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonConverterAttribute"
        && a.ConstructorArguments is [{ Value: INamedTypeSymbol { Name: "JsonStringEnumConverter" } }]);

    /// <summary>An enum member's name as System.Text.Json writes it: [JsonStringEnumMemberName]'s, else its own.</summary>
    private static string EnumName(IFieldSymbol member) =>
        member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "JsonStringEnumMemberNameAttribute")?.ConstructorArguments.FirstOrDefault().Value as string
        ?? member.Name;

    private static string JsonName(IPropertySymbol property) =>
        property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "JsonPropertyNameAttribute")?.ConstructorArguments.FirstOrDefault().Value as string
        ?? (property.Name.Length > 0 ? char.ToLowerInvariant(property.Name[0]) + property.Name[1..] : property.Name);

    private static ITypeSymbol? Element(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }
        if (type.SpecialType == SpecialType.System_String)
        {
            return null;
        }
        INamedTypeSymbol? enumerable = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } self ? self
            : type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        return enumerable?.TypeArguments[0];
    }

    private static ITypeSymbol? Dictionary(ITypeSymbol type)
    {
        INamedTypeSymbol? dictionary = (type as INamedTypeSymbol) is { Name: "IDictionary" or "IReadOnlyDictionary", TypeArguments.Length: 2 } self ? self
            : type.AllInterfaces.FirstOrDefault(i => i.Name is "IDictionary" or "IReadOnlyDictionary" && i.TypeArguments.Length == 2
                && i.ContainingNamespace.ToDisplayString() == "System.Collections.Generic");
        return dictionary?.TypeArguments[1];
    }
}
