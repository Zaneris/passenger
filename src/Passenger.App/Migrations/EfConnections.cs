using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.App.Migrations;

/// <summary>The connection a context connects with, by its name in configuration (ConnectionStrings:NAME): the one its
/// registration in code reads (<c>AddDbContext&lt;ShopContext&gt;(… GetConnectionString("Shop") …)</c>, through a local
/// it is read into too, or an Aspire integration's connection name), else the one the context reads itself, else the one
/// configured under the context's own name ("ShopContext" or "Shop"). Never the first one configured: where none of these
/// tells, there is no name.</summary>
public static class EfConnections
{
    /// <summary>The connection's name, and where it was told; null where nothing tells it.</summary>
    public static (string Name, string Told)? NameOf(string context, IEnumerable<(string Path, string Text)> code, CSharpParseOptions options, IReadOnlyList<string> configured)
    {
        List<SyntaxNode> roots = [.. code.Select(c => CSharpSyntaxTree.ParseText(c.Text, options, c.Path).GetRoot())];
        foreach (SyntaxNode root in roots)
        {
            foreach (InvocationExpressionSyntax registration in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Registers(i, context)))
            {
                if ((Read(registration, root) ?? Named(registration)) is { } name)
                {
                    return (name, $"its registration in {Path.GetFileName(root.SyntaxTree.FilePath)}");
                }
            }
        }
        foreach (SyntaxNode root in roots)
        {
            foreach (ClassDeclarationSyntax declared in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.ValueText == context))
            {
                if (Read(declared, root) is { } name)
                {
                    return (name, $"{context} itself");
                }
            }
        }
        string bare = Assumed(context);
        return configured.FirstOrDefault(c => c.Equals(context, StringComparison.OrdinalIgnoreCase) || c.Equals(bare, StringComparison.OrdinalIgnoreCase)) is { } own
            ? (own, "the configuration's name for it")
            : null;
    }

    /// <summary>The name a context's connection takes where nothing tells it: the context's name without "Context".</summary>
    public static string Assumed(string context) =>
        context.EndsWith("DbContext", StringComparison.Ordinal) && context.Length > 9 ? context[..^9]
        : context.EndsWith("Context", StringComparison.Ordinal) && context.Length > 7 ? context[..^7] : context;

    /// <summary>The names under ConnectionStrings in a project's appsettings.json and appsettings.Development.json, in order.</summary>
    public static IReadOnlyList<string> Configured(string folder)
    {
        List<string> names = [];
        foreach (string file in (string[])["appsettings.json", "appsettings.Development.json"])
        {
            try
            {
                if (FileText.Load(Path.Combine(folder, file)) is not { } settings)
                {
                    continue;
                }
                using JsonDocument read = JsonDocument.Parse(settings.Text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (read.RootElement.ValueKind == JsonValueKind.Object && read.RootElement.TryGetProperty("ConnectionStrings", out JsonElement strings)
                    && strings.ValueKind == JsonValueKind.Object)
                {
                    names.AddRange(strings.EnumerateObject().Select(s => s.Name).Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // A settings file that cannot be read names no connection.
            }
        }
        return names;
    }

    /// <summary>Whether a call registers the context: an Add… method given it as its one type argument.</summary>
    private static bool Registers(InvocationExpressionSyntax call, string context) =>
        (call.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax named } => named,
            GenericNameSyntax named => named,
            _ => null,
        }) is { Identifier.ValueText: { } method, TypeArgumentList.Arguments: [var type] }
        && method.StartsWith("Add", StringComparison.Ordinal)
        && (type is QualifiedNameSyntax { Right.Identifier.ValueText: var right } ? right : (type as IdentifierNameSyntax)?.Identifier.ValueText) == context;

    /// <summary>The name a node's GetConnectionString("NAME") reads, directly or through a local or field read into it.</summary>
    private static string? Read(SyntaxNode node, SyntaxNode root)
    {
        if (node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Select(Literal).FirstOrDefault(n => n is not null) is { } direct)
        {
            return direct;
        }
        HashSet<string> used = [.. node.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.ValueText)];
        return root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => used.Contains(v.Identifier.ValueText) && v.Initializer is not null)
            .SelectMany(v => v.Initializer!.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Select(Literal).FirstOrDefault(n => n is not null);
    }

    /// <summary>The literal name a GetConnectionString call reads; null for another call.</summary>
    private static string? Literal(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetConnectionString" } or IdentifierNameSyntax { Identifier.ValueText: "GetConnectionString" }
        && call.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } name }]
            ? name.Token.ValueText : null;

    /// <summary>The connection name an Aspire integration's registration gives (AddNpgsqlDbContext&lt;T&gt;("shop")).</summary>
    private static string? Named(InvocationExpressionSyntax registration) =>
        registration.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method }
        && method.EndsWith("DbContext", StringComparison.Ordinal) && method != "AddDbContext"
        && registration.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } name
            ? name.Token.ValueText : null;
}
