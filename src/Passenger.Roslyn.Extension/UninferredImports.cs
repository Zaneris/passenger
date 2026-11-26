using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.AddImport;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SymbolSearch;

namespace Passenger.Roslyn.Extension;

/// <summary>The usings a member's name is fixed by where it binds only to extension methods of another receiver, their type
/// arguments inferred from nothing (CS0411): under System.Linq's using, .NET 10's AsyncEnumerable takes EF Core's
/// FirstOrDefaultAsync, ToListAsync and the like on an IQueryable so. Such a name is searched as one no member answers
/// (CS1061), so a namespace is offered only where an extension method of it takes the receiver.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UninferredImports))]
[Shared]
internal sealed class UninferredImports : CodeFixProvider
{
    private const string Uninferred = "CS0411";
    private const string NoSuchMember = "CS1061";

    /// <summary>The most namespaces offered for one name.</summary>
    private const int MostOffered = 5;

    [ImportingConstructor]
    public UninferredImports()
    {
    }

    public override ImmutableArray<string> FixableDiagnosticIds => [Uninferred];

    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Document document = context.Document;
        CancellationToken cancel = context.CancellationToken;
        IAddImportFeatureService imports = document.GetRequiredLanguageService<IAddImportFeatureService>();
        // No package is added here, only a using for what the project already references.
        SymbolSearchOptions search = await document.GetSymbolSearchOptionsAsync(cancel).ConfigureAwait(false) with { SearchNuGetPackages = false };
        AddImportOptions options = await document.GetAddImportOptionsAsync(search, cleanupDocument: true, cancel).ConfigureAwait(false);
        ISymbolSearchService symbols = document.Project.Solution.Services.GetRequiredService<ISymbolSearchService>();
        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            ImmutableArray<AddImportFixData> fixes = await imports.GetFixesAsync(document, diagnostic.Location.SourceSpan, NoSuchMember, MostOffered,
                symbols, options, [], cancel).ConfigureAwait(false);
            ImmutableArray<CodeAction> actions = imports.GetCodeActionsForFixes(document, fixes, installerService: null, MostOffered);
            context.RegisterFixes(actions, diagnostic);
        }
    }
}
