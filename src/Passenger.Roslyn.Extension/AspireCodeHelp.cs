using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>Reads a deliberately bounded, straight-line AppHost through Roslyn, never through resource-name heuristics.</summary>
public sealed class AspireCodeHelp : IExtensionDocumentMessageHandler<AspireHelpRequest, AspireHelp>
{
    public Task<AspireHelp> ExecuteAsync(AspireHelpRequest message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        AspireSource.HelpAsync(context.Document(), message.At, message.Explain, cancellationToken);
}

/// <summary>Recomputes a missing edge before applying it, so a previously offered fix cannot add a duplicate reference.</summary>
public sealed class AspireReferenceFix : IExtensionDocumentMessageHandler<AspireFixRequest, Refactored>
{
    public async Task<Refactored> ExecuteAsync(AspireFixRequest message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        AspireHelp help = await AspireSource.HelpAsync(document, null, false, cancellationToken).ConfigureAwait(false);
        return help.Missing.FirstOrDefault(m => m.Service == message.Service && m.Resource == message.Resource
            && Compare(m.Start, message.At) <= 0 && Compare(message.At, m.End) <= 0)?.Fix
            ?? new Refactored("The Aspire reference was not added: the source changed since the fix was offered, and the reference is no longer missing there; request the fix again", []);
    }

    private static int Compare(Place left, Place right) => left.Line == right.Line ? left.Character.CompareTo(right.Character) : left.Line.CompareTo(right.Line);
}

/// <summary>Rename of a resource's name across its AppHost and the services that read it, worked out again as it is made.</summary>
public sealed class AspireRename : IExtensionDocumentMessageHandler<AspireRenameRequest, AspireRenaming>
{
    public Task<AspireRenaming> ExecuteAsync(AspireRenameRequest message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        AspireSource.RenameAsync(context.Document(), message.At, message.NewName, cancellationToken);
}

/// <summary>The parameters an AppHost's file declares that take their value from its configuration alone.</summary>
public sealed class AspireParameters : IExtensionDocumentMessageHandler<AspireParametersRequest, AspireParametersFound>
{
    public Task<AspireParametersFound> ExecuteAsync(AspireParametersRequest message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        AspireSource.ParametersAsync(context.Document(), cancellationToken);
}

/// <summary>The project resources an AppHost proves, each with where it is declared and that file's fingerprint.</summary>
public sealed class AspireRunResources : IExtensionWorkspaceMessageHandler<AspireResourcesRequest, AspireRunResourcesResult>
{
    public Task<AspireRunResourcesResult> ExecuteAsync(AspireResourcesRequest message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken) =>
        AspireSource.RunResourcesAsync(context.Solution, message.AppHost, cancellationToken);
}

/// <summary>The connection strings projects' code reads (<see cref="AspireSource.ConnectionUses"/>) and, asked, every
/// configuration key it reads or binds (<see cref="ConfigurationSource"/>), from one walk of its documents.</summary>
public sealed class ConnectionReads : IExtensionWorkspaceMessageHandler<ConnectionReadsRequest, ConnectionReadsFound>
{
    public Task<ConnectionReadsFound> ExecuteAsync(ConnectionReadsRequest message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken) =>
        AspireSource.ConnectionReadsAsync(context.Solution, message.Projects, cancellationToken, message.Keys, message.File);
}

internal static class AspireSource
{
    private const string Builder = "Aspire.Hosting.ApplicationModel.IResourceBuilder<T>";
    private const string ConnectionResource = "Aspire.Hosting.ApplicationModel.IResourceWithConnectionString";

    private sealed record Resource(ILocalSymbol Symbol, string Name, string? ProjectPath, bool Connection, bool Discovery, VariableDeclaratorSyntax Declaration,
        ExpressionSyntax NameExpression);
    /// <summary>An AppHost's proven graph: its resources, which reads which, and those whose environment it sets by hand
    /// (WithEnvironment), which may give a connection string by a name no resource has.</summary>
    private sealed record Graph(Document Document, SourceText Text, IReadOnlyList<Resource> Resources, HashSet<(string Service, string Target)> Edges,
        HashSet<string> Environed);
    private sealed record Use(ExpressionSyntax Expression, string Name, bool Connection);

    public static async Task<AspireRunResourcesResult> RunResourcesAsync(Solution solution, string appHost, CancellationToken cancel)
    {
        Project? project = solution.Projects.FirstOrDefault(candidate => SamePath(candidate.FilePath, appHost));
        if (project is null)
        {
            return new([], "The AppHost is not loaded in the C# language server");
        }
        List<AspireRunResource> resources = [];
        foreach (Document document in project.Documents)
        {
            cancel.ThrowIfCancellationRequested();
            SyntaxNode? root = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
            SemanticModel? model = await document.GetSemanticModelAsync(cancel).ConfigureAwait(false);
            if (root is null || model is null || document.FilePath is null)
            {
                continue;
            }
            SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
            string hash = TextHash.Of(text.ToString());
            foreach (InvocationExpressionSyntax added in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(added, cancel).Symbol is not IMethodSymbol method || method.Name != "AddProject"
                    || method.ContainingType.ToDisplayString() != "Aspire.Hosting.ProjectResourceBuilderExtensions"
                    || method.ContainingAssembly.Name != "Aspire.Hosting")
                {
                    continue;
                }
                string? name = null;
                string? projectPath = null;
                foreach (ArgumentSyntax argument in added.ArgumentList.Arguments)
                {
                    string? parameter = AspireProjectDeclaration.Parameter(method, added, argument)?.Name;
                    if (Constant(argument.Expression, model, cancel) is not { } value)
                    {
                        continue;
                    }
                    if (parameter == "name")
                    {
                        name = value;
                    }
                    else if (parameter == "projectPath")
                    {
                        projectPath = ResolvePath(value, AppHostDirectory(model.Compilation) ?? Path.GetDirectoryName(project.FilePath));
                    }
                }
                if (method.TypeArguments is [INamedTypeSymbol projectType])
                {
                    projectPath = await AspireProjectDeclaration.MetadataPathAsync(projectType, model.Compilation, cancel).ConfigureAwait(false);
                }
                if (!string.IsNullOrWhiteSpace(name) && projectPath is not null)
                {
                    resources.Add(new(name, projectPath, document.FilePath, PlaceAt(text, added.SpanStart), hash));
                }
            }
        }
        if (resources.GroupBy(resource => resource.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            return new([], "The AppHost declares multiple project resources with the same name");
        }
        return new(resources);
    }

    public static async Task<AspireHelp> HelpAsync(Document document, Place? at, bool explain, CancellationToken cancel)
    {
        List<string>? evidence = explain ? [] : null;
        AspireHelp help = await ReadHelpAsync(document, at, evidence, cancel).ConfigureAwait(false);
        evidence?.Add($"Result: {help.Missing.Count} missing references");
        return help with { Evidence = evidence is null ? null : string.Join("\n", evidence.Take(30)) };
    }

    private static async Task<AspireHelp> ReadHelpAsync(Document document, Place? at, List<string>? evidence, CancellationToken cancel)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancel).ConfigureAwait(false);
        evidence?.Add($"Document: {document.FilePath}; project: {document.Project.FilePath}; syntax errors: {root?.ContainsDiagnostics}");
        if (root is null || model is null || document.Project.FilePath is null)
        {
            return new AspireHelp(null, null, [], []);
        }
        if (evidence is not null)
        {
            evidence.AddRange(model.GetDiagnostics(cancellationToken: cancel).Where(d => d.Severity == DiagnosticSeverity.Error).Take(5).Select(d => d.ToString()));
        }
        if (await HostOfAsync(document, evidence, cancel).ConfigureAwait(false) is not ({ } host, var service))
        {
            return new AspireHelp(null, null, [], []);
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        List<AspireMissingReference> missing = [];
        List<AspireUnknownConnection> unknown = [];
        if (service is not null && !root.ContainsDiagnostics)
        {
            foreach (Use use in Uses(root, model, evidence, cancel))
            {
                evidence?.Add($"Semantic resource use: {use.Name}; connection: {use.Connection}");
                Resource? target = host.Resources.SingleOrDefault(r => string.Equals(r.Name, use.Name, StringComparison.OrdinalIgnoreCase)
                    && (use.Connection ? r.Connection : r.Discovery && !r.Connection));
                if (target is null && use.Connection && !host.Environed.Contains(service.Name)
                    && !host.Resources.Any(r => string.Equals(r.Name, use.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    // A name no resource has, which the service's own settings may give.
                    Read? read = use.Expression is LiteralExpressionSyntax written ? ReadOf(written, model, cancel) : null;
                    TextSpan name = read is { Exact: true } ? read.Name : use.Expression.Span;
                    unknown.Add(new AspireUnknownConnection(PlaceAt(text, name.Start), PlaceAt(text, name.End), use.Name, read is { Exact: true },
                        document.Project.FilePath, [.. host.Resources.Where(r => r.Connection).Select(r => r.Name)]));
                    continue;
                }
                if (target is null || target == service || host.Edges.Contains((service.Name, target.Name)))
                {
                    continue;
                }
                Refactored? fix = ReferenceFix(host, service, target);
                evidence?.Add($"Missing edge: {service.Name} -> {target.Name}; fix available: {fix is not null}");
                if (fix is not null)
                {
                    missing.Add(new AspireMissingReference(PlaceAt(text, use.Expression.SpanStart), PlaceAt(text, use.Expression.Span.End), service.Name, target.Name, fix));
                }
            }
        }
        if (at is null)
        {
            return new AspireHelp(null, null, [], missing, Unknown: unknown);
        }
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        LiteralExpressionSyntax? literal = root.FindToken(Math.Clamp(offset, 0, Math.Max(0, root.FullSpan.End - 1))).Parent?.AncestorsAndSelf()
            .OfType<LiteralExpressionSyntax>().FirstOrDefault();
        if (literal is null || !literal.IsKind(SyntaxKind.StringLiteralExpression) || !literal.Token.Text.StartsWith('"')
            || literal.Token.Text.StartsWith("\"\"\"", StringComparison.Ordinal) || offset <= literal.SpanStart
            || literal.Parent is not ArgumentSyntax argument || argument.Parent?.Parent is not InvocationExpressionSyntax call
            || model.GetSymbolInfo(call, cancel).Symbol is not IMethodSymbol called)
        {
            return new AspireHelp(null, null, [], missing, Unknown: unknown);
        }
        int contentEnd = literal.Token.Text.Length > 1 && literal.Token.Text.EndsWith('"') ? literal.Span.End - 1 : literal.Span.End;
        if (offset > contentEnd)
        {
            return new AspireHelp(null, null, [], missing, Unknown: unknown);
        }
        bool connection = NamesConnection(called, call, argument);
        bool resourceName = called.ContainingType.ToDisplayString() == "Aspire.Hosting.ApplicationModel.ResourceNotificationService"
            && called.ContainingAssembly.Name == "Aspire.Hosting" && called.Name == "WaitForResourceAsync"
            && AspireProjectDeclaration.Parameter(called, call, argument)?.Name == "resourceName";
        string[] names = connection && service is not null
            ? [.. host.Resources.Where(r => r.Connection && host.Edges.Contains((service.Name, r.Name))).Select(r => r.Name).Order(StringComparer.Ordinal)]
            : resourceName ? [.. host.Resources.Select(r => r.Name).Order(StringComparer.Ordinal)] : [];
        return new AspireHelp(PlaceAt(text, literal.SpanStart + 1), PlaceAt(text, contentEnd), names, missing, Unknown: unknown);
    }

    /// <summary>The one AppHost whose proven graph a document's project is in, with the resource the project is (null for the
    /// AppHost's own document); null when no AppHost, or more than one, can be proven to run it.</summary>
    private static async Task<(Graph Host, Resource? Service)?> HostOfAsync(Document document, List<string>? evidence, CancellationToken cancel)
    {
        List<(Graph Graph, Resource? Service)> matches = [];
        foreach (Project project in document.Project.Solution.Projects)
        {
            cancel.ThrowIfCancellationRequested();
            if (!project.MetadataReferences.OfType<PortableExecutableReference>().Any(r => Path.GetFileName(r.FilePath) == "Aspire.Hosting.dll")
                && !project.ProjectReferences.Any(r => project.Solution.GetProject(r.ProjectId)?.AssemblyName == "Aspire.Hosting"))
            {
                continue;
            }
            Compilation? compilation = await project.GetCompilationAsync(cancel).ConfigureAwait(false);
            if (compilation?.GetTypeByMetadataName("Aspire.Hosting.IDistributedApplicationBuilder") is null)
            {
                continue;
            }
            List<Graph> graphs = [];
            bool unsupported = false;
            foreach (Document source in project.Documents)
            {
                SyntaxNode? syntax = await source.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
                SemanticModel? semantic = await source.GetSemanticModelAsync(cancel).ConfigureAwait(false);
                if (syntax is null || semantic is null || !syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(i => semantic.GetSymbolInfo(i, cancel).Symbol is IMethodSymbol method && IsCreateBuilder(method)))
                {
                    if (syntax?.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression switch
                        {
                            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "CreateBuilder",
                            IdentifierNameSyntax name => name.Identifier.ValueText == "CreateBuilder",
                            _ => false,
                        }) == true)
                    {
                        unsupported = true;
                    }
                    continue;
                }
                Graph? graph = await ReadGraphAsync(source, syntax, semantic, cancel).ConfigureAwait(false);
                evidence?.Add($"AppHost: {source.FilePath}; graph proven: {graph is not null}");
                if (graph is null)
                {
                    unsupported = true;
                }
                else
                {
                    graphs.Add(graph);
                }
            }
            if (unsupported || graphs.Count > 1)
            {
                return null;
            }
            if (graphs.Count == 0)
            {
                continue;
            }
            Graph selected = graphs[0];
            Resource[] services = [.. selected.Resources.Where(r => SamePath(r.ProjectPath, document.Project.FilePath))];
            evidence?.Add($"Service matches in {selected.Document.FilePath}: {services.Length}");
            if (evidence is not null)
            {
                Compilation? hostCompilation = await selected.Document.Project.GetCompilationAsync(cancel).ConfigureAwait(false);
                INamedTypeSymbol? discovery = hostCompilation?.GetTypeByMetadataName("Aspire.Hosting.IResourceWithServiceDiscovery");
                evidence.Add($"Discovery capability: {discovery?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}; assembly: {discovery?.ContainingAssembly.Identity}");
                evidence.AddRange(selected.Resources.Take(10).Select(r => $"Resource: {r.Name}; project: {r.ProjectPath}; connection: {r.Connection}; discovery: {r.Discovery}"));
                foreach (Resource resource in selected.Resources.Where(r => r.ProjectPath is not null).Take(3))
                {
                    INamedTypeSymbol? type = ResourceType(resource.Symbol.Type);
                    evidence.Add($"Resource type for {resource.Name}: {type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}; assembly: {type?.ContainingAssembly.Identity}; nullable: {type?.NullableAnnotation}; interfaces: {string.Join(", ", type?.AllInterfaces.Take(16).Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) ?? [])}");
                }
                evidence.AddRange(selected.Edges.Take(10).Select(e => $"Reference: {e.Service} -> {e.Target}"));
            }
            if (services.Length > 1)
            {
                return null;
            }
            if (services.Length == 1)
            {
                matches.Add((selected, services[0]));
            }
            else if (document.Project.Id == project.Id)
            {
                matches.Add((selected, null));
            }
        }
        evidence?.Add($"Unambiguous AppHost/service matches: {matches.Count}");
        return matches.Count == 1 ? matches[0] : null;
    }

    private static async Task<Graph?> ReadGraphAsync(Document document, SyntaxNode root, SemanticModel model, CancellationToken cancel)
    {
        if (root is not CompilationUnitSyntax unit || document.FilePath is null || unit.ContainsDiagnostics || unit.ContainsDirectives
            || model.GetDiagnostics(cancellationToken: cancel).Any(d => d.Severity == DiagnosticSeverity.Error)
            || unit.Members.Any(m => m is not GlobalStatementSyntax)
            || unit.Members.OfType<GlobalStatementSyntax>().Any(g => g.Statement is not LocalDeclarationStatementSyntax and not ExpressionStatementSyntax)
            || root.DescendantNodes().Any(n => n is AnonymousFunctionExpressionSyntax or AssignmentExpressionSyntax
                or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
        {
            return null;
        }
        InvocationExpressionSyntax[] calls = [.. root.DescendantNodes().OfType<InvocationExpressionSyntax>()];
        if (calls.Count(i => model.GetSymbolInfo(i, cancel).Symbol is IMethodSymbol method && IsCreateBuilder(method)) != 1)
        {
            return null;
        }
        foreach (InvocationExpressionSyntax call in calls)
        {
            if (model.GetSymbolInfo(call, cancel).Symbol is not IMethodSymbol method || !Official(method)
                || call.ArgumentList.Arguments.Any(a => model.GetTypeInfo(a.Expression, cancel).Type?.TypeKind == TypeKind.Delegate)
                || IsCreateBuilder(method) && call.ArgumentList.Arguments.Any(a => a.Expression is not IdentifierNameSyntax
                    || AspireProjectDeclaration.Parameter(method, call, a)?.Name != "args"
                    || model.GetTypeInfo(a.Expression, cancel).Type is not IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String }))
            {
                return null;
            }
        }
        List<Resource> resources = [];
        foreach (VariableDeclaratorSyntax declaration in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration, cancel) is not ILocalSymbol local || ResourceType(local.Type) is not { } resourceType)
            {
                continue;
            }
            if (declaration.Initializer?.Value is not InvocationExpressionSyntax initializer)
            {
                return null;
            }
            InvocationExpressionSyntax? add = Chain(initializer).FirstOrDefault(i => model.GetSymbolInfo(i, cancel).Symbol is IMethodSymbol m
                && m.Name.StartsWith("Add", StringComparison.Ordinal) && m.Parameters.Any(p => p.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "Aspire.Hosting.ApplicationModel.ResourceNameAttribute")));
            if (add is null || model.GetSymbolInfo(add, cancel).Symbol is not IMethodSymbol method || !Official(method))
            {
                return null;
            }
            ArgumentSyntax? nameArgument = add.ArgumentList.Arguments.FirstOrDefault(a => AspireProjectDeclaration.Parameter(method, add, a)
                ?.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "Aspire.Hosting.ApplicationModel.ResourceNameAttribute") == true);
            if (nameArgument is null || Constant(nameArgument.Expression, model, cancel) is not { } name)
            {
                return null;
            }
            string? projectPath = null;
            if (method.ContainingType.ToDisplayString() == "Aspire.Hosting.ProjectResourceBuilderExtensions" && method.Name == "AddProject")
            {
                if (method.TypeArguments is [INamedTypeSymbol metadata])
                {
                    projectPath = await AspireProjectDeclaration.MetadataPathAsync(metadata, model.Compilation, cancel).ConfigureAwait(false);
                }
                else
                {
                    ArgumentSyntax? pathArgument = add.ArgumentList.Arguments.FirstOrDefault(a => AspireProjectDeclaration.Parameter(method, add, a)?.Name == "projectPath");
                    if (pathArgument is not null && Constant(pathArgument.Expression, model, cancel) is { } path)
                    {
                        projectPath = ResolvePath(path, AppHostDirectory(model.Compilation));
                    }
                }
                if (projectPath is null)
                {
                    return null;
                }
            }
            resources.Add(new Resource(local, name, projectPath, Implements(resourceType, model.Compilation.GetTypeByMetadataName(ConnectionResource)),
                Implements(resourceType, model.Compilation.GetTypeByMetadataName("Aspire.Hosting.IResourceWithServiceDiscovery")), declaration, nameArgument.Expression));
        }
        if (resources.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
        {
            return null;
        }
        HashSet<(string Service, string Target)> edges = [];
        HashSet<string> environed = new(StringComparer.Ordinal);
        HashSet<string> declaredNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (InvocationExpressionSyntax call in calls)
        {
            IMethodSymbol method = (IMethodSymbol)model.GetSymbolInfo(call, cancel).Symbol!;
            ArgumentSyntax? resourceName = call.ArgumentList.Arguments.FirstOrDefault(a => AspireProjectDeclaration.Parameter(method, call, a)
                ?.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "Aspire.Hosting.ApplicationModel.ResourceNameAttribute") == true);
            if (method.Name.StartsWith("Add", StringComparison.Ordinal) && resourceName is not null)
            {
                if (Constant(resourceName.Expression, model, cancel) is not { } name
                    || !declaredNames.Add(name) || !resources.Any(r => r.Declaration.Span.Contains(call.Span)))
                {
                    return null;
                }
            }
            if (method.Name == "Build" && resources.Any(r => r.Declaration.SpanStart > call.SpanStart))
            {
                return null;
            }
            if (method.Name == "WithReference")
            {
                if (method.ContainingType.ToDisplayString() != "Aspire.Hosting.ResourceBuilderExtensions"
                    || call.ArgumentList.Arguments.Count != 1 || call.Expression is not MemberAccessExpressionSyntax member)
                {
                    return null;
                }
                Resource? source = Receiver(member.Expression, call, resources, model, cancel);
                Resource? target = resources.FirstOrDefault(r => SymbolEqualityComparer.Default.Equals(r.Symbol,
                    model.GetSymbolInfo(call.ArgumentList.Arguments[0].Expression, cancel).Symbol));
                if (source is null || target is null || !SymbolEqualityComparer.Default.Equals(ResourceType(model.GetTypeInfo(member.Expression, cancel).Type), ResourceType(source.Symbol.Type)))
                {
                    return null;
                }
                edges.Add((source.Name, target.Name));
            }
            else if (method.Name is "WaitFor" or "WaitForStart" or "WaitForCompletion" && method.ContainingType.ToDisplayString() == "Aspire.Hosting.ResourceBuilderExtensions")
            {
                // An order of starting, which gives the waiting resource nothing to read: no edge.
            }
            else if (call.ArgumentList.Arguments.Any(a => ResourceType(model.GetTypeInfo(a.Expression, cancel).Type) is not null))
            {
                return null;
            }
            else if (method.Name.StartsWith("WithEnvironment", StringComparison.Ordinal) && call.Expression is MemberAccessExpressionSyntax set
                && Receiver(set.Expression, call, resources, model, cancel) is { } environment)
            {
                environed.Add(environment.Name);
            }
        }
        return new Graph(document, await document.GetTextAsync(cancel).ConfigureAwait(false), resources, edges, environed);
    }

    private static Resource? Receiver(ExpressionSyntax expression, InvocationExpressionSyntax call, IReadOnlyList<Resource> resources, SemanticModel model, CancellationToken cancel)
    {
        while (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member })
        {
            expression = member.Expression;
        }
        if (expression is IdentifierNameSyntax)
        {
            Resource? direct = resources.FirstOrDefault(r => SymbolEqualityComparer.Default.Equals(r.Symbol, model.GetSymbolInfo(expression, cancel).Symbol));
            if (direct is not null)
            {
                return direct;
            }
        }
        return call.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is { } declaration
            ? resources.FirstOrDefault(r => r.Declaration == declaration) : null;
    }

    private static Refactored? ReferenceFix(Graph graph, Resource service, Resource target)
    {
        GlobalStatementSyntax? serviceStatement = service.Declaration.Ancestors().OfType<GlobalStatementSyntax>().FirstOrDefault();
        GlobalStatementSyntax? targetStatement = target.Declaration.Ancestors().OfType<GlobalStatementSyntax>().FirstOrDefault();
        if (serviceStatement is null || targetStatement is null || serviceStatement.Parent != targetStatement.Parent)
        {
            return null;
        }
        GlobalStatementSyntax later = serviceStatement.Span.End >= targetStatement.Span.End ? serviceStatement : targetStatement;
        if (service.Symbol.DeclaringSyntaxReferences.Length != 1 || target.Symbol.DeclaringSyntaxReferences.Length != 1
            || service.Declaration.Parent is not VariableDeclarationSyntax { Variables.Count: 1 }
            || target.Declaration.Parent is not VariableDeclarationSyntax { Variables.Count: 1 })
        {
            return null;
        }
        string newline = graph.Text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        Place insertion = PlaceAt(graph.Text, later.FullSpan.End);
        string prefix = later.FullSpan.End > 0 && graph.Text[later.FullSpan.End - 1] is not ('\r' or '\n') ? newline : string.Empty;
        string change = prefix + service.Declaration.Identifier.Text + ".WithReference(" + target.Declaration.Identifier.Text + ");" + newline;
        return new Refactored(null, [new FileChange(graph.Document.FilePath!, false, [new Edit(insertion, insertion, change)])]);
    }

    /// <summary>Each read of a resource a service's document makes: the connection strings it reads (<see cref="ConnectionUses"/>)
    /// and the services an HttpClient's base address discovers.</summary>
    private static IEnumerable<Use> Uses(SyntaxNode root, SemanticModel model, List<string>? evidence, CancellationToken cancel)
    {
        foreach (Use use in ConnectionUses(root, model, cancel))
        {
            yield return use;
        }
        foreach (AssignmentExpressionSyntax assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (evidence is not null)
            {
                ISymbol? assigned = model.GetSymbolInfo(assignment.Left, cancel).Symbol;
                evidence.Add($"Assignment symbol: {assigned?.ToDisplayString()}; assembly: {assigned?.ContainingAssembly?.Name}; value type: {model.GetTypeInfo(assignment.Right, cancel).Type?.ToDisplayString()}");
            }
            if (model.GetSymbolInfo(assignment.Left, cancel).Symbol is not IPropertySymbol property || property.Name != "BaseAddress"
                || property.ContainingType.ToDisplayString() != "System.Net.Http.HttpClient" || property.ContainingAssembly.Name != "System.Net.Http")
            {
                continue;
            }
            ArgumentListSyntax? arguments = assignment.Right switch
            {
                ObjectCreationExpressionSyntax creation when model.GetTypeInfo(creation, cancel).Type?.ToDisplayString() == "System.Uri" => creation.ArgumentList,
                ImplicitObjectCreationExpressionSyntax creation when model.GetTypeInfo(creation, cancel).Type?.ToDisplayString() == "System.Uri" => creation.ArgumentList,
                _ => null,
            };
            if (arguments?.Arguments.FirstOrDefault()?.Expression is not { } uriExpression
                || Constant(uriExpression, model, cancel) is not { } uri)
            {
                continue;
            }
            int separator = uri.IndexOf("://", StringComparison.Ordinal);
            if (separator <= 0 || uri[..separator].ToLowerInvariant() is not ("http" or "https" or "http+https" or "https+http"))
            {
                continue;
            }
            string authority = uri[(separator + 3)..].Split('/', '?', '#')[0];
            if (authority.Length > 0 && !authority.Contains(':') && !authority.Contains('@'))
            {
                yield return new Use(uriExpression, authority, false);
            }
        }
    }

    /// <summary>The connection strings a document's code reads, each where its name is written: the constant argument of
    /// GetConnectionString or of a client integration's connectionName, and a <c>ConnectionStrings:</c> key in a string.</summary>
    private static IEnumerable<Use> ConnectionUses(SyntaxNode root, SemanticModel model, CancellationToken cancel)
    {
        HashSet<ExpressionSyntax> named = [];
        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(call, cancel).Symbol is not IMethodSymbol method)
            {
                continue;
            }
            ArgumentSyntax? argument = call.ArgumentList.Arguments.FirstOrDefault(a => NamesConnection(method, call, a));
            if (argument is not null && Constant(argument.Expression, model, cancel) is { } name)
            {
                named.Add(argument.Expression);
                yield return new Use(argument.Expression, name, true);
            }
        }
        foreach (LiteralExpressionSyntax literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!named.Contains(literal) && literal.IsKind(SyntaxKind.StringLiteralExpression)
                && literal.Token.ValueText.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
                && ReadOf(literal, model, cancel) is { Connection: true } read)
            {
                yield return new Use(literal, read.Value, true);
            }
        }
    }

    /// <summary>Whether a document can read a connection string, by its syntax alone: a call of GetConnectionString or of an
    /// Add method (a client integration's), or a string that names a <c>ConnectionStrings:</c> key; only such a document's
    /// semantics are read.</summary>
    private static bool MayReadConnections(SyntaxNode root) =>
        root.DescendantNodes().Any(node => node switch
        {
            InvocationExpressionSyntax call => (call.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => string.Empty,
            }) is "GetConnectionString" or ['A', 'd', 'd', ..],
            LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
                && literal.Token.ValueText.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase),
            _ => false,
        });

    /// <summary>What each project's code read, by its version: a project's documents are read again only once one of them,
    /// or a project it references, has changed. The connection strings and the configuration keys come of one walk.</summary>
    private static readonly SolutionKept<ProjectId, (VersionStamp Version, IReadOnlyList<ConnectionRead> Reads, IReadOnlyList<ConfigurationKey> Keys)> ConnectionsRead =
        SolutionKept.ByProject<(VersionStamp, IReadOnlyList<ConnectionRead>, IReadOnlyList<ConfigurationKey>)>();

    /// <summary>The connection strings the projects' code reads (<see cref="ConnectionUses"/>), each where its name is
    /// written, and with <see cref="ConnectionReadsRequest.Keys"/> the configuration keys it reads or binds
    /// (<see cref="ConfigurationSource"/>): every target framework's build of a project, each read once. With
    /// <paramref name="file"/>, only that file's code, read alone: a walk of the projects is kept only until an edit
    /// anywhere in them, so a file's check walking them would walk them all at each key.</summary>
    public static async Task<ConnectionReadsFound> ConnectionReadsAsync(Solution solution, IReadOnlyList<string> projects, CancellationToken cancel, bool keys = false,
        string? file = null)
    {
        List<ConnectionRead> reads = [];
        List<ConfigurationKey> configured = [];
        ConnectionsRead.LetGoOfGone(solution);
        System.Collections.Concurrent.ConcurrentDictionary<ProjectId, (VersionStamp Version, IReadOnlyList<ConnectionRead> Reads, IReadOnlyList<ConfigurationKey> Keys)> read =
            ConnectionsRead.Of(solution);
        foreach (string path in projects)
        {
            HashSet<(string Name, string File, int Line, int Character)> seen = [];
            HashSet<(string Key, string Kind, string? File, int Line, int Character)> keyed = [];
            foreach (Project project in solution.Projects.Where(p => SamePath(p.FilePath, path)))
            {
                (IReadOnlyList<ConnectionRead> Reads, IReadOnlyList<ConfigurationKey> Keys) known;
                if (file is not null)
                {
                    known = await ReadAsync(path, solution.GetDocumentIdsWithFilePath(file).Where(id => id.ProjectId == project.Id).Select(project.GetDocument)
                        .OfType<Document>().Where(d => SamePath(d.FilePath, file)), cancel).ConfigureAwait(false);
                }
                else
                {
                    VersionStamp version = await project.GetDependentVersionAsync(cancel).ConfigureAwait(false);
                    if (!read.TryGetValue(project.Id, out (VersionStamp Version, IReadOnlyList<ConnectionRead> Reads, IReadOnlyList<ConfigurationKey> Keys) kept) || kept.Version != version)
                    {
                        (IReadOnlyList<ConnectionRead> found, IReadOnlyList<ConfigurationKey> named) =
                            await ReadAsync(path, project.Documents.Where(d => d.FilePath is not null), cancel).ConfigureAwait(false);
                        kept = (version, found, named);
                        read[project.Id] = kept;
                    }
                    known = (kept.Reads, kept.Keys);
                }
                reads.AddRange(known.Reads.Where(r => seen.Add((r.Name, r.Path, r.At.Line, r.At.Character))).Select(r => r with { Project = path }));
                if (keys)
                {
                    configured.AddRange(known.Keys.Where(k => keyed.Add((k.Key, k.Kind, k.Path, k.At?.Line ?? -1, k.At?.Character ?? -1))).Select(k => k with { Project = path }));
                }
            }
        }
        return new ConnectionReadsFound(reads, keys ? configured : null);
    }

    /// <summary>The connection strings and configuration keys the documents' code reads, of the project at
    /// <paramref name="project"/>: a document whose syntax can read neither is never bound.</summary>
    private static async Task<(IReadOnlyList<ConnectionRead> Reads, IReadOnlyList<ConfigurationKey> Keys)> ReadAsync(string project, IEnumerable<Document> documents,
        CancellationToken cancel)
    {
        List<ConnectionRead> found = [];
        List<ConfigurationKey> named = [];
        foreach (Document document in documents)
        {
            if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
            {
                continue;
            }
            bool connections = MayReadConnections(root);
            bool configuration = ConfigurationSource.MayRead(root);
            if (!connections && !configuration || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
            {
                continue;
            }
            SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
            if (connections)
            {
                found.AddRange(ConnectionUses(root, model, cancel).Select(use =>
                    new ConnectionRead(project, use.Name, document.FilePath!, PlaceAt(text, use.Expression.SpanStart), PlaceAt(text, use.Expression.Span.End))));
            }
            if (configuration)
            {
                named.AddRange(ConfigurationSource.Read(project, document.FilePath!, root, model, text, cancel));
            }
        }
        return (found, named);
    }

    /// <summary>Whether an argument is the name of a connection string a service reads: GetConnectionString's, or an Aspire
    /// client integration's connectionName (AddNpgsqlDataSource, AddRedisClient, AddSqlServerDbContext and the rest).</summary>
    private static bool NamesConnection(IMethodSymbol method, InvocationExpressionSyntax call, ArgumentSyntax argument) =>
        AspireProjectDeclaration.Parameter(method, call, argument)?.Name is { } parameter
        && (IsConnectionString(method) && parameter == "name"
            || parameter == "connectionName" && method.ContainingAssembly.Name.StartsWith("Aspire.", StringComparison.Ordinal)
                && !method.ContainingAssembly.Name.StartsWith("Aspire.Hosting", StringComparison.Ordinal));

    private static bool IsConnectionString(IMethodSymbol method) => method.Name == "GetConnectionString"
        && method.ContainingType.ToDisplayString() == "Microsoft.Extensions.Configuration.ConfigurationExtensions"
        && method.ContainingAssembly.Name == "Microsoft.Extensions.Configuration.Abstractions";

    private static bool IsCreateBuilder(IMethodSymbol method) => method.Name == "CreateBuilder"
        && method.ContainingType.ToDisplayString() == "Aspire.Hosting.DistributedApplication" && method.ContainingAssembly.Name == "Aspire.Hosting";

    private static bool Official(IMethodSymbol method) => method.ContainingNamespace.ToDisplayString().StartsWith("Aspire.Hosting", StringComparison.Ordinal)
        && (method.ContainingAssembly.Name == "Aspire.Hosting" || method.ContainingAssembly.Name.StartsWith("Aspire.Hosting.", StringComparison.Ordinal));

    private static INamedTypeSymbol? ResourceType(ITypeSymbol? type) => type is INamedTypeSymbol named
        ? new[] { named }.Concat(named.AllInterfaces).FirstOrDefault(t => t.OriginalDefinition.ToDisplayString() == Builder)?.TypeArguments.FirstOrDefault() as INamedTypeSymbol
        : null;

    private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol? capability) => capability is not null
        && (SymbolEqualityComparer.Default.Equals(type, capability) || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, capability)));

    private static IEnumerable<InvocationExpressionSyntax> Chain(InvocationExpressionSyntax invocation)
    {
        yield return invocation;
        if (invocation.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax previous })
        {
            foreach (InvocationExpressionSyntax call in Chain(previous))
            {
                yield return call;
            }
        }
    }

    private static string? ResolvePath(string path, string? directory)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : directory is null ? null : Path.GetFullPath(path, directory);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? AppHostDirectory(Compilation compilation)
    {
        string[] directories = [.. compilation.Assembly.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute")
            .Where(a => a.ConstructorArguments is [{ Value: "AppHostProjectPath" }, { Value: string }])
            .Select(a => (string)a.ConstructorArguments[1].Value!)];
        return directories.Length == 1 ? directories[0] : null;
    }

    /// <summary>A resource's name as a service writes it in a string: the name of a connection string it reads (an argument
    /// of GetConnectionString or a client integration, a <c>ConnectionStrings:</c> key) or of a service it discovers (an
    /// <c>https+http://</c> address's host, a <c>services:</c> key), with the stretch of the literal that is the name;
    /// <paramref name="Exact"/> when that stretch is the name as written, so it can be replaced (no escapes in the literal).</summary>
    private sealed record Read(LiteralExpressionSyntax Literal, TextSpan Name, string Value, bool Connection, bool Exact);

    private static Read? ReadOf(LiteralExpressionSyntax literal, SemanticModel model, CancellationToken cancel)
    {
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return null;
        }
        string value = literal.Token.ValueText;
        bool exact = literal.Token.Text == "\"" + value + "\"";
        Read Named(int at, int length, bool connection) =>
            new(literal, exact ? new TextSpan(literal.SpanStart + 1 + at, length) : literal.Span, value.Substring(at, length), connection, exact);
        if (literal.Parent is ArgumentSyntax argument && argument.Parent?.Parent is InvocationExpressionSyntax call
            && model.GetSymbolInfo(call, cancel).Symbol is IMethodSymbol method && NamesConnection(method, call, argument))
        {
            return value.Length > 0 ? Named(0, value.Length, true) : null;
        }
        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && value[..scheme].ToLowerInvariant() is "http" or "https" or "http+https" or "https+http")
        {
            string authority = value[(scheme + 3)..].Split('/', '?', '#')[0];
            string host = authority.Split(':')[0];
            return host.Length > 0 && !authority.Contains('@', StringComparison.Ordinal) ? Named(scheme + 3, host.Length, false) : null;
        }
        foreach ((string prefix, bool connection) in new[] { ("services:", false), ("ConnectionStrings:", true) })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string name = value[prefix.Length..].Split(':')[0];
                return name.Length > 0 ? Named(prefix.Length, name.Length, connection) : null;
            }
        }
        return null;
    }

    /// <summary>The resource a read names in its AppHost's graph: the one of that name (letter case aside, as configuration
    /// keys are) that gives what is read, else the one of that name.</summary>
    private static Resource? Named(Graph host, Read read)
    {
        Resource[] named = [.. host.Resources.Where(r => string.Equals(r.Name, read.Value, StringComparison.OrdinalIgnoreCase))];
        return named.FirstOrDefault(r => read.Connection ? r.Connection : r.Discovery) ?? (named.Length == 1 ? named[0] : null);
    }

    /// <summary>The string literal at an offset of a document, and what it reads, when it names a resource.</summary>
    private static async Task<(SyntaxNode Root, SemanticModel Model, Read Read)?> ReadAtAsync(Document document, Place at, CancellationToken cancel)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancel).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return null;
        }
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        SyntaxToken token = root.FindToken(Math.Clamp(offset, 0, Math.Max(0, root.FullSpan.End - 1)));
        return token.IsKind(SyntaxKind.StringLiteralToken) && token.Parent is LiteralExpressionSyntax literal
            && literal.SpanStart < offset && offset <= literal.Span.End && ReadOf(literal, model, cancel) is { } read
            ? (root, model, read) : null;
    }

    /// <summary>Where the resource a service names at a place is declared in its AppHost: the AppHost's file and the start of
    /// the name in its Add call; none where the place names no resource of a proven graph.</summary>
    public static async Task<AspireProjectTarget> DeclarationAsync(Document document, Place at, CancellationToken cancel)
    {
        if (await ReadAtAsync(document, at, cancel).ConfigureAwait(false) is not { Read: { } read }
            || await HostOfAsync(document, null, cancel).ConfigureAwait(false) is not ({ } host, _)
            || Named(host, read) is not { } resource)
        {
            return new AspireProjectTarget(null);
        }
        int start = resource.NameExpression is LiteralExpressionSyntax { Token.Text: ['"', ..] } named ? named.SpanStart + 1 : resource.NameExpression.SpanStart;
        return new AspireProjectTarget(host.Document.FilePath, PlaceAt(host.Text, start));
    }

    /// <summary>Rename of the resource whose name is at a place (where a service reads it, or where its AppHost declares it):
    /// its name, the stretch of it there, and with a new name the edits that rename it where the AppHost declares it and
    /// wherever its AppHost's services read it, worked out from the solution as it stands; no name where none is.</summary>
    public static async Task<AspireRenaming> RenameAsync(Document document, Place at, string? newName, CancellationToken cancel)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
        if (root is null || await HostOfAsync(document, null, cancel).ConfigureAwait(false) is not ({ } host, _))
        {
            return new AspireRenaming(null, null, null, null);
        }
        Resource? resource = null;
        TextSpan span = default;
        if (await ReadAtAsync(document, at, cancel).ConfigureAwait(false) is { Read: { } read } && Named(host, read) is { } found)
        {
            (resource, span) = (found, read.Name);
        }
        else if (host.Document.Id == document.Id)
        {
            int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
            resource = host.Resources.FirstOrDefault(r => r.NameExpression is LiteralExpressionSyntax literal && literal.SpanStart < offset && offset <= literal.Span.End);
            span = resource?.NameExpression is LiteralExpressionSyntax named && NameSpan(named) is { } exact ? exact : resource?.NameExpression.Span ?? default;
        }
        if (resource is null)
        {
            return new AspireRenaming(null, null, null, null);
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        AspireRenaming described = new(resource.Name, PlaceAt(text, span.Start), PlaceAt(text, span.End), null);
        if (newName is null)
        {
            return described;
        }
        if (ResourceNameProblem(newName) is { } wrong)
        {
            return described with { Made = new Refactored(wrong, []) };
        }
        if (host.Resources.Any(r => r != resource && string.Equals(r.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            return described with { Made = new Refactored($"The AppHost has a resource named {newName} already", []) };
        }
        if (resource.NameExpression is not LiteralExpressionSyntax declared || NameSpan(declared) is not { } declaredName)
        {
            return described with { Made = new Refactored($"{resource.Name} is not named by a plain string in the AppHost, so it is renamed by hand", []) };
        }
        Dictionary<string, List<Edit>> edits = new(StringComparer.Ordinal) { [host.Document.FilePath!] = [new Edit(PlaceAt(host.Text, declaredName.Start), PlaceAt(host.Text, declaredName.End), newName)] };
        HashSet<string> projects = [.. host.Resources.Select(r => r.ProjectPath).OfType<string>()];
        foreach (Project project in host.Document.Project.Solution.Projects.Where(p => projects.Any(path => SamePath(path, p.FilePath))))
        {
            foreach (Document source in project.Documents)
            {
                SyntaxNode? sourceRoot = await source.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
                SemanticModel? model = await source.GetSemanticModelAsync(cancel).ConfigureAwait(false);
                if (sourceRoot is null || model is null || source.FilePath is null)
                {
                    continue;
                }
                SourceText sourceText = await source.GetTextAsync(cancel).ConfigureAwait(false);
                foreach (LiteralExpressionSyntax literal in sourceRoot.DescendantNodes().OfType<LiteralExpressionSyntax>())
                {
                    if (ReadOf(literal, model, cancel) is not { } use || Named(host, use) != resource)
                    {
                        continue;
                    }
                    if (!use.Exact)
                    {
                        return described with { Made = new Refactored($"{Path.GetFileName(source.FilePath)} reads {resource.Name} in a string written with escapes, so it is renamed by hand", []) };
                    }
                    if (!edits.TryGetValue(source.FilePath, out List<Edit>? file))
                    {
                        edits[source.FilePath] = file = [];
                    }
                    file.Add(new Edit(PlaceAt(sourceText, use.Name.Start), PlaceAt(sourceText, use.Name.End), newName));
                }
            }
        }
        return described with { Made = new Refactored(null, [.. edits.Select(e => new FileChange(e.Key, false, e.Value))]) };
    }

    /// <summary>Each AddParameter of a document that gives no value of its own (only a name, and whether it is a secret), so the
    /// AppHost takes it from its configuration's Parameters:NAME, with where its name is written.</summary>
    public static async Task<AspireParametersFound> ParametersAsync(Document document, CancellationToken cancel)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancel).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return new AspireParametersFound([]);
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        List<AspireParameter> found = [];
        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddParameter" }
                || model.GetSymbolInfo(call, cancel).Symbol is not IMethodSymbol { Name: "AddParameter" } method
                || method.ContainingType.ToDisplayString() != "Aspire.Hosting.ParameterResourceBuilderExtensions" || method.ContainingAssembly.Name != "Aspire.Hosting")
            {
                continue;
            }
            string? name = null;
            ExpressionSyntax? written = null;
            bool secret = false;
            bool valued = false;
            foreach (ArgumentSyntax argument in call.ArgumentList.Arguments)
            {
                Optional<object?> constant = model.GetConstantValue(argument.Expression, cancel);
                switch (AspireProjectDeclaration.Parameter(method, call, argument)?.Name)
                {
                    case "name" when constant is { HasValue: true, Value: string named }:
                        (name, written) = (named, argument.Expression);
                        break;
                    case "secret" when constant is { HasValue: true, Value: bool isSecret }:
                        secret = isSecret;
                        break;
                    default:
                        // A value, a default or a getter of its own: the configuration is not all it has.
                        valued = true;
                        break;
                }
            }
            if (name is not null && written is not null && !valued)
            {
                TextSpan span = written is LiteralExpressionSyntax literal && NameSpan(literal) is { } exact ? exact : written.Span;
                found.Add(new AspireParameter(name, secret, PlaceAt(text, span.Start), PlaceAt(text, span.End)));
            }
        }
        return new AspireParametersFound(found);
    }

    /// <summary>The stretch of a plain string literal that is its text, between its quotes; null for one written otherwise
    /// (escapes, verbatim, raw), whose text is not its characters one for one.</summary>
    private static TextSpan? NameSpan(LiteralExpressionSyntax literal) =>
        literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.Text == "\"" + literal.Token.ValueText + "\""
            ? new TextSpan(literal.SpanStart + 1, literal.Token.ValueText.Length) : null;

    /// <summary>Why a name is no resource's, by Aspire's rule for them: ASCII letters, digits and single dashes, beginning with
    /// a letter and ending with no dash, at most 64 characters; null for one that is.</summary>
    private static string? ResourceNameProblem(string name) =>
        name.Length is 0 or > 64 ? "A resource's name has 1 to 64 characters"
        : !char.IsAsciiLetter(name[0]) ? "A resource's name begins with a letter"
        : name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ? "A resource's name has only letters, digits and dashes"
        : name.EndsWith('-') || name.Contains("--", StringComparison.Ordinal) ? "A resource's name has no dash at its end and no two together"
        : null;

    internal static async Task<string?> ProjectPathAsync(Document document, InvocationExpressionSyntax call, SemanticModel model, CancellationToken cancel)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
        Graph? graph = root is null ? null : await ReadGraphAsync(document, root, model, cancel).ConfigureAwait(false);
        return graph?.Resources.SingleOrDefault(r => r.Declaration.Span.Contains(call.Span))?.ProjectPath;
    }
}
