using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using EditAndContinueAnalyzer = Microsoft.CodeAnalysis.EditAndContinue.AbstractEditAndContinueAnalyzer;

namespace Passenger.Roslyn.Extension;

/// <summary>The calls an update would leave on a deleted body: a member whose signature changed is applied as the old one
/// deleted (its body made to throw) and a new one added, and a caller whose own code is as it was is not compiled again,
/// so it still calls the old one. Each member so left is a rude edit (ENC0033's) at its new declaration, so the change
/// waits for a restart.</summary>
internal static class StaleCallers
{
    /// <summary>Roslyn's rude edit for a member deleted, which is what the update does to the old member.</summary>
    private const string DeletingMember = "ENC0033";

    /// <summary>The members of the changed documents that the update deletes while code it leaves as it was still calls them,
    /// in the modules the program has loaded, each as a problem at the member as it is now (or where it was, when gone); of
    /// the program's projects alone (<paramref name="scope"/>, every one when null).</summary>
    public static async Task<List<HotReloadProblem>> FindAsync(Solution before, Solution after, IReadOnlyCollection<Guid> loaded, IReadOnlySet<ProjectId>? scope,
        CancellationToken cancel)
    {
        List<HotReloadProblem> found = [];
        Dictionary<ProjectId, bool> running = [];
        foreach (ProjectChanges project in after.GetChanges(before).GetProjectChanges().Where(p => scope?.Contains(p.ProjectId) != false))
        {
            if (before.GetProject(project.ProjectId) is not { } old || after.GetProject(project.ProjectId) is not { } now
                || await now.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compiled)
            {
                continue;
            }
            foreach (DocumentId id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                if (old.GetDocument(id) is not { } document || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model
                    || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
                {
                    continue;
                }
                foreach (MemberDeclarationSyntax declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(Callable))
                {
                    if (model.GetDeclaredSymbol(declaration, cancel) is not { IsImplicitlyDeclared: false } member || Kept(member, compiled, cancel))
                    {
                        continue;
                    }
                    if (await FirstStaleCallAsync(member, before, after, loaded, running, cancel).ConfigureAwait(false) is { } call)
                    {
                        found.Add(await ProblemAsync(member, declaration, call, after, project.ProjectId, cancel).ConfigureAwait(false));
                    }
                }
            }
        }
        return found;
    }

    /// <summary>Whether the update keeps a member as the runtime knows it: one of the new code of its name and parameters
    /// whose signature the runtime sees as the same, its return, property or event type and ref-kinds too (tuple names,
    /// nullability and dynamic aside, as Edit and Continue sets them). One changed there is deleted, and another added.</summary>
    private static bool Kept(ISymbol member, Compilation compiled, CancellationToken cancel) =>
        SymbolFinder.FindSimilarSymbols(member, compiled, cancel).Any(now => EditAndContinueAnalyzer.MemberOrDelegateSignaturesEquivalent(member, now, exact: false));

    /// <summary>A member a call can reach: a method, constructor, operator, property, indexer or event.</summary>
    private static bool Callable(MemberDeclarationSyntax declaration) =>
        declaration is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax;

    /// <summary>The first place, in a module the program has loaded, that calls the old member from code the update does not
    /// compile again.</summary>
    private static async Task<ReferenceLocation?> FirstStaleCallAsync(ISymbol member, Solution before, Solution after, IReadOnlyCollection<Guid> loaded,
        Dictionary<ProjectId, bool> running, CancellationToken cancel)
    {
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(member, before, cancel).ConfigureAwait(false))
        {
            foreach (ReferenceLocation call in referenced.Locations)
            {
                if (call.IsImplicit || !call.Location.IsInSource || !Runs(call.Document.Project, loaded, running))
                {
                    continue;
                }
                if (!await IsCompiledAgainAsync(call, after, cancel).ConfigureAwait(false))
                {
                    return call;
                }
            }
        }
        return null;
    }

    /// <summary>Whether the update compiles the code a call is in again: the member around it (top-level statements as one)
    /// is still there and its code changed, beyond its whitespace and comments.</summary>
    private static async Task<bool> IsCompiledAgainAsync(ReferenceLocation call, Solution after, CancellationToken cancel)
    {
        if (await call.Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
            || await after.GetDocumentAsync(call.Document.Id, includeSourceGenerated: true, cancel).ConfigureAwait(false) is not { } now
            || await now.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } nowRoot)
        {
            return false;
        }
        SyntaxNode at = root.FindNode(call.Location.SourceSpan, getInnermostNodeForTie: true);
        if (at.FirstAncestorOrSelf<GlobalStatementSyntax>() is not null)
        {
            return !Equivalent(root.ChildNodes().OfType<GlobalStatementSyntax>(), nowRoot.ChildNodes().OfType<GlobalStatementSyntax>());
        }
        if (at.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax) is not { } around
            || await call.Document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model
            || await now.Project.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compiled)
        {
            return false;
        }
        ISymbol? caller = around is BaseFieldDeclarationSyntax field
            ? field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, cancel)).FirstOrDefault()
            : model.GetDeclaredSymbol(around, cancel);
        if (caller is null)
        {
            return false;
        }
        // A caller gone with it is replaced too, and the new one is compiled as it now reads.
        if (SymbolFinder.FindSimilarSymbols(caller, compiled, cancel).FirstOrDefault() is not { } kept)
        {
            return true;
        }
        // Of a partial member's parts, the one in the call's file; one moved to another file is compiled anew.
        foreach (SyntaxReference reference in kept.DeclaringSyntaxReferences.Where(r => r.SyntaxTree.FilePath == now.FilePath))
        {
            SyntaxNode declared = await reference.GetSyntaxAsync(cancel).ConfigureAwait(false);
            if (SyntaxFactory.AreEquivalent(around, declared.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? declared, topLevel: false))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Equivalent(IEnumerable<GlobalStatementSyntax> before, IEnumerable<GlobalStatementSyntax> after)
    {
        List<GlobalStatementSyntax> was = [.. before];
        List<GlobalStatementSyntax> now = [.. after];
        return was.Count == now.Count && was.Zip(now).All(pair => SyntaxFactory.AreEquivalent(pair.First, pair.Second, topLevel: false));
    }

    /// <summary>Whether a project's code runs in the program: its assembly is one of the modules loaded, by its MVID (one whose
    /// assembly cannot be read is taken to run).</summary>
    private static bool Runs(Project project, IReadOnlyCollection<Guid> loaded, Dictionary<ProjectId, bool> running)
    {
        if (!running.TryGetValue(project.Id, out bool runs))
        {
            runs = Mvid(project) is not { } mvid || loaded.Contains(mvid);
            running[project.Id] = runs;
        }
        return runs;
    }

    internal static Guid? Mvid(Project project)
    {
        if ((project.CompilationOutputInfo.AssemblyPath ?? project.OutputFilePath) is not { } path || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using PEReader assembly = new(file);
            MetadataReader metadata = assembly.GetMetadataReader();
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The rude edit, at the member's new declaration where its name is kept, else where the old one was.</summary>
    private static async Task<HotReloadProblem> ProblemAsync(ISymbol member, MemberDeclarationSyntax old, ReferenceLocation call, Solution after,
        ProjectId project, CancellationToken cancel)
    {
        Location place = old.GetLocation();
        if (await after.GetProject(project)!.GetCompilationAsync(cancel).ConfigureAwait(false) is { } compiled)
        {
            INamedTypeSymbol? type = member.ContainingType is { } containing ? SymbolFinder.FindSimilarSymbols(containing, compiled, cancel).FirstOrDefault() : null;
            if (type?.GetMembers(member.Name).FirstOrDefault(m => !m.IsImplicitlyDeclared && m.Locations.Any(l => l.IsInSource)) is { } now)
            {
                place = now.Locations.First(l => l.IsInSource);
            }
        }
        FileLinePositionSpan where = place.GetMappedLineSpan();
        FileLinePositionSpan caller = call.Location.GetMappedLineSpan();
        string shown = member.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
        return new HotReloadProblem(
            DeletingMember,
            $"Changing the signature of {Kind(member)} '{shown}' requires restarting the application: "
                + $"{Path.GetFileName(caller.Path)}:{caller.StartLinePosition.Line + 1} still calls it as it was",
            Error: true,
            RestartRequired: true,
            where.Path,
            where.StartLinePosition.Line,
            where.StartLinePosition.Character,
            where.EndLinePosition.Line,
            where.EndLinePosition.Character,
            after.GetProject(project)?.Name);
    }

    private static string Kind(ISymbol member) => member switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => "method",
    };
}
