using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>A context whose model has changes no migration covers, at its class's declaration where it was found.</summary>
public sealed record EfPending(string Context, string? File, TextPosition Position);

/// <summary>A type a C# file declares, as dotnet ef's projects are read: its name and where it is, the last part of the
/// name of the class it derives from (none for a struct, an interface or a class deriving from none), and whether dotnet
/// ef could make it, being neither abstract, static nor generic.</summary>
public sealed record EfDeclaredType(string Name, string? Base, bool Made, TextPosition At)
{
    /// <summary>The context its <c>[DbContext(typeof(…))]</c> attribute names, the last part of the name as written; null
    /// without one.</summary>
    public string? ContextOf { get; init; }

    /// <summary>The migration's id its <c>[Migration("…")]</c> attribute gives; null without one.</summary>
    public string? MigrationId { get; init; }
}

/// <summary>A migration or model snapshot a project holds, by the context its <c>[DbContext]</c> attribute names (the last
/// part of the name as written): the migration's id (null for the snapshot), and the file declaring it.</summary>
public sealed record EfMigrationFile(string Context, string? Id, string File);

/// <summary>What a C# file gives dotnet ef's projects: the types it declares, and what its MigrationsAssembly calls name,
/// an assembly by its name or by a type of it (<c>typeof(Marker).Assembly</c>).</summary>
public sealed record EfFileRead(IReadOnlyList<EfDeclaredType> Types, IReadOnlyList<string> Assemblies, IReadOnlyList<string> AssemblyTypes);

/// <summary>A project EF Core reaches, as dotnet ef takes it for <c>--project</c>: the contexts it declares, each at its
/// name; whether it holds migrations (a model snapshot); and whether a MigrationsAssembly call names it, so the migrations
/// of the contexts it configures are made there.</summary>
public sealed record EfProjectRead(string Path, IReadOnlyList<EfPending> Contexts, bool HoldsMigrations, bool NamedForMigrations)
{
    /// <summary>Its migrations and model snapshots, each with its context and file, wherever in the project they are.</summary>
    public IReadOnlyList<EfMigrationFile> Migrations { get; init; } = [];

    /// <summary>A context's migrations and model snapshots, by its name as dotnet ef lists it, short or full.</summary>
    public IEnumerable<EfMigrationFile> MigrationsOf(string context) => Migrations.Where(m => m.Context == EfTargets.ContextName(context));

    /// <summary>Whether dotnet ef makes migrations in it: it declares a context, holds migrations or is named for them.</summary>
    public bool IsTarget => Contexts.Count > 0 || HoldsMigrations || NamedForMigrations;

    public bool SameAs(EfProjectRead other) =>
        PathNames.Same(Path, other.Path) && Contexts.SequenceEqual(other.Contexts) && HoldsMigrations == other.HoldsMigrations && NamedForMigrations == other.NamedForMigrations
        && Migrations.SequenceEqual(other.Migrations);
}

/// <summary>The projects the Migrations window offers, by dotnet ef's own rules: for <c>--project</c>, those EF Core reaches
/// that declare a context or hold its migrations, or that a MigrationsAssembly call names; for <c>--startup-project</c>,
/// those that load the chosen one (it, or a project referencing it, directly or through others) and run on .NET, as dotnet
/// ef refuses a .NET Standard or .NET Framework one. A context is a class whose base's name ends in DbContext (an
/// interface's aside), or is a context's, through any of the projects read, as a type's name alone tells them.</summary>
public static class EfTargets
{
    /// <summary>The class a model snapshot derives from, which every project holding migrations declares one of.</summary>
    private const string Snapshot = "ModelSnapshot";

    /// <summary>The call naming the assembly a context's migrations are made in.</summary>
    private const string MigrationsAssembly = "MigrationsAssembly";

    /// <summary>The attribute naming the context of a migration or model snapshot.</summary>
    private const string DbContextAttribute = "DbContext";

    /// <summary>The attribute giving a migration its id.</summary>
    private const string MigrationAttribute = "Migration";

    /// <summary>A context's name as an <see cref="EfMigrationFile"/> keeps it, from the name dotnet ef lists, short or full:
    /// its last part.</summary>
    public static string ContextName(string listed) => listed[(listed.LastIndexOf('.') + 1)..];

    /// <summary>Whether a project references EF Core, or a provider of it, itself.</summary>
    public static bool UsesEf(ProjectModel project) =>
        project.Packages.Any(p => p.Name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether EF Core reaches a project through its evaluation: it, or a project among <paramref name="all"/> it
    /// references, directly or through others, references EF Core.</summary>
    public static bool ReachesEf(ProjectModel project, IReadOnlyList<ProjectModel> all) => project.WithReferenced(all).Any(UsesEf);

    /// <summary>Whether a project references another among <paramref name="all"/>, directly or through others.</summary>
    public static bool References(ProjectModel from, ProjectModel to, IReadOnlyList<ProjectModel> all) => Referencing(to, all).Contains(from.Path);

    /// <summary>The paths of the projects among <paramref name="all"/> referencing a project, directly or through others.</summary>
    private static HashSet<string> Referencing(ProjectModel project, IReadOnlyList<ProjectModel> all)
    {
        ILookup<string, ProjectModel> referrers = all.SelectMany(p => p.ProjectReferences.Select(r => (Reference: r, Referrer: p)))
            .ToLookup(r => r.Reference, r => r.Referrer, PathNames.Comparer);
        HashSet<string> found = new(PathNames.Comparer) { project.Path };
        Queue<string> next = new([project.Path]);
        while (next.TryDequeue(out string? at))
        {
            foreach (ProjectModel referrer in referrers[at].Where(p => found.Add(p.Path)))
            {
                next.Enqueue(referrer.Path);
            }
        }
        found.Remove(project.Path);
        return found;
    }

    /// <summary>A C# file's types and MigrationsAssembly calls, from its text and its tree.</summary>
    public static EfFileRead ReadOf(string text, SyntaxNode root)
    {
        List<EfDeclaredType> types = [];
        TextSnapshot? lines = null;
        foreach (BaseTypeDeclarationSyntax declared in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
            .OfType<BaseTypeDeclarationSyntax>())
        {
            string? baseName = declared is ClassDeclarationSyntax { BaseList.Types: [var first, ..] } ? Last(first.Type) : null;
            bool made = declared is ClassDeclarationSyntax own && !own.Modifiers.Any(SyntaxKind.AbstractKeyword) && !own.Modifiers.Any(SyntaxKind.StaticKeyword)
                && !declared.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Any(t => t.TypeParameterList is not null);
            TextPosition at = baseName is null ? default : (lines ??= TextSnapshot.From(text)).GetPosition(declared.Identifier.SpanStart);
            (string? contextOf, string? migrationId) = declared.AttributeLists.Count > 0 ? Attributed(declared) : (null, null);
            types.Add(new EfDeclaredType(declared.Identifier.ValueText, baseName, made, at) { ContextOf = contextOf, MigrationId = migrationId });
        }
        List<string> assemblies = [];
        List<string> assemblyTypes = [];
        if (text.Contains(MigrationsAssembly, StringComparison.Ordinal))
        {
            foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: MigrationsAssembly } && call.ArgumentList.Arguments is [var argument])
                {
                    if (argument.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
                    {
                        assemblies.Add(literal.Token.ValueText);
                    }
                    else if (argument.Expression.DescendantNodesAndSelf().OfType<TypeOfExpressionSyntax>().FirstOrDefault() is { } type && Last(type.Type) is { } marker)
                    {
                        assemblyTypes.Add(marker);
                    }
                }
            }
        }
        return new EfFileRead(types, assemblies, assemblyTypes);
    }

    /// <summary>The context a type's <c>[DbContext(typeof(…))]</c> names and the id its <c>[Migration("…")]</c> gives, as
    /// dotnet ef writes both on a migration and the context on its model snapshot.</summary>
    private static (string? Context, string? Id) Attributed(BaseTypeDeclarationSyntax declared)
    {
        string? context = null;
        string? id = null;
        foreach (AttributeSyntax attribute in declared.AttributeLists.SelectMany(l => l.Attributes))
        {
            if (attribute.ArgumentList?.Arguments is not [var argument])
            {
                continue;
            }
            switch (Last(attribute.Name))
            {
                case DbContextAttribute or DbContextAttribute + "Attribute" when argument.Expression is TypeOfExpressionSyntax type:
                    context = Last(type.Type);
                    break;
                case MigrationAttribute or MigrationAttribute + "Attribute"
                    when argument.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal:
                    id = literal.Token.ValueText;
                    break;
            }
        }
        return (context, id);
    }

    /// <summary>The last part of a type's name as written, without its type arguments: DbContext of
    /// <c>Microsoft.EntityFrameworkCore.DbContext</c>, IdentityDbContext of <c>IdentityDbContext&lt;User&gt;</c>.</summary>
    private static string? Last(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => Last(qualified.Right),
        AliasQualifiedNameSyntax aliased => Last(aliased.Name),
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    /// <summary>What each project EF Core reaches gives dotnet ef, from its C# files' reads, a context deriving from one
    /// another project declares found too.</summary>
    public static IReadOnlyList<EfProjectRead> Resolve(IReadOnlyList<(ProjectModel Project, IReadOnlyList<(string File, EfFileRead Read)> Files)> projects)
    {
        IEnumerable<EfDeclaredType> every = projects.SelectMany(p => p.Files).SelectMany(f => f.Read.Types);
        HashSet<string> contexts = Contexts(every);
        HashSet<string> assemblies = new(projects.SelectMany(p => p.Files).SelectMany(f => f.Read.Assemblies), StringComparer.OrdinalIgnoreCase);
        HashSet<string> markers = new(projects.SelectMany(p => p.Files).SelectMany(f => f.Read.AssemblyTypes), StringComparer.Ordinal);
        List<EfProjectRead> read = [];
        foreach ((ProjectModel project, IReadOnlyList<(string File, EfFileRead Read)> files) in projects)
        {
            EfPending[] declared = [.. files.SelectMany(f => f.Read.Types.Where(t => t.Made && contexts.Contains(t.Name)).Select(t => new EfPending(t.Name, f.File, t.At)))];
            bool holds = files.Any(f => f.Read.Types.Any(t => t.Base == Snapshot));
            bool named = assemblies.Contains(AssemblyOf(project)) || files.Any(f => f.Read.Types.Any(t => markers.Contains(t.Name)));
            EfMigrationFile[] migrations = [.. files.SelectMany(f => f.Read.Types
                .Where(t => t.ContextOf is not null && (t.MigrationId is not null || t.Base == Snapshot))
                .Select(t => new EfMigrationFile(t.ContextOf!, t.MigrationId, f.File)))];
            read.Add(new EfProjectRead(project.Path, declared, holds, named) { Migrations = migrations });
        }
        return read;
    }

    /// <summary>The names of the classes that are contexts: those whose base's name ends in DbContext and is no interface's
    /// (IAppDbContext, as .NET names one), then those deriving from one of them, until no more are found.</summary>
    private static HashSet<string> Contexts(IEnumerable<EfDeclaredType> types)
    {
        List<EfDeclaredType> derived = [.. types.Where(t => t.Base is not null)];
        HashSet<string> found = new(StringComparer.Ordinal);
        bool more = true;
        while (more)
        {
            more = false;
            foreach (EfDeclaredType type in derived)
            {
                if ((type.Base!.EndsWith("DbContext", StringComparison.Ordinal) && !IsInterface(type.Base) || found.Contains(type.Base)) && found.Add(type.Name))
                {
                    more = true;
                }
            }
        }
        return found;
    }

    /// <summary>Whether a type's name is an interface's, as .NET names one: an I, then a capital.</summary>
    private static bool IsInterface(string name) => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);

    /// <summary>A project's assembly's name, as MigrationsAssembly names it.</summary>
    private static string AssemblyOf(ProjectModel project) =>
        project.Evaluated?.AssemblyName is { Length: > 0 } named ? named : System.IO.Path.GetFileNameWithoutExtension(project.Path);

    /// <summary>The projects offered for <c>--project</c>: those dotnet ef makes migrations in (<see cref="EfProjectRead.IsTarget"/>)
    /// in the solution's order, one a MigrationsAssembly call names first, then one holding migrations, as dotnet ef makes a
    /// context's migrations where those are; else, before the read or where none declares a context yet, those referencing
    /// EF Core themselves.</summary>
    public static IReadOnlyList<ProjectModel> Projects(IReadOnlyList<ProjectModel> all, IReadOnlyList<EfProjectRead>? read)
    {
        (ProjectModel Project, EfProjectRead Read)[] targets = read is null ? []
            : [.. all.Select(p => (Project: p, Read: read.FirstOrDefault(r => PathNames.Same(r.Path, p.Path))))
                .Where(t => t.Read is { IsTarget: true })
                .Select(t => (t.Project, t.Read!))];
        return targets.Length > 0
            ? [.. targets.OrderBy(t => t.Read.NamedForMigrations ? 0 : t.Read.HoldsMigrations ? 1 : 2).Select(t => t.Project)]
            : [.. all.Where(UsesEf)];
    }

    /// <summary>The projects offered for <c>--startup-project</c> of a project: those referencing it, directly or through
    /// others, that run (those with dotnet ef's Design package first), then itself, then those that don't run (a library, a
    /// test project, which dotnet ef also starts with the Design package); each only where it targets .NET, as dotnet ef
    /// refuses to start a .NET Standard or .NET Framework one. One not yet evaluated, whose frameworks are not known, stays.</summary>
    public static IReadOnlyList<ProjectModel> Startups(ProjectModel project, IReadOnlyList<ProjectModel> all)
    {
        HashSet<string> referrers = Referencing(project, all);
        List<ProjectModel> referencing = [.. all.Where(p => referrers.Contains(p.Path))];
        IEnumerable<ProjectModel> running = referencing.Where(p => p.IsRunnable).OrderBy(p => p.Packages.Any(Designs) ? 0 : 1);
        return [.. running.Append(project).Concat(referencing.Where(p => !p.IsRunnable)).Where(RunsOnNet)];
    }

    /// <summary>Whether a package gives a startup project what dotnet ef loads from it: the Design package, or the Tools one,
    /// which brings it.</summary>
    private static bool Designs((string Name, string? Version) package) =>
        package.Name.Equals(EfRestore.DesignPackage, StringComparison.OrdinalIgnoreCase) || package.Name.Equals(EfRestore.ToolsPackage, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a project targets .NET, in one of its frameworks at least; true while they are not known.</summary>
    private static bool RunsOnNet(ProjectModel project) =>
        project.TargetFrameworks.Count == 0 || project.TargetFrameworks.Any(f => FrameworkNames.VersionOf(f) is not null);
}
