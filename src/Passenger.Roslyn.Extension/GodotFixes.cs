using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Formatting;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>A fix of a Godot check made in a script's code (<see cref="GodotFixRequest"/>).</summary>
public sealed class GodotFix : IExtensionWorkspaceMessageHandler<GodotFixRequest, Refactored>
{
    public Task<Refactored> ExecuteAsync(GodotFixRequest message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken) =>
        GodotSource.FixAsync(context.Solution, message, cancellationToken);
}

/// <summary>The fixes of Godot's checks as the solution's edits: a signal's handler, an exported property, a GetNode call's
/// type, each type written as the code at its place names it, so the file builds as it did with no import added.</summary>
internal static partial class GodotSource
{
    public static async Task<Refactored> FixAsync(Solution solution, GodotFixRequest asked, CancellationToken cancel)
    {
        IReadOnlyList<(Document Document, Project Project)> documents = await LocalizationSource.DocumentsOfAsync(solution, asked.Path, false, cancel).ConfigureAwait(false);
        if (documents.Count == 0 || await documents[0].Project.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compilation
            || GodotSharp.ObjectIn(compilation) is not { } godotObject)
        {
            return Changes.Refused($"{System.IO.Path.GetFileName(asked.Path)} is in no Godot project the C# language server has loaded");
        }
        Engine engine = Engine.Of(godotObject.ContainingAssembly);
        if (asked.Kind == GodotFixKind.NodeType)
        {
            return await NodeTypeAsync(solution, documents[0].Document, compilation, engine, asked, cancel).ConfigureAwait(false);
        }
        if (ScriptClass(compilation, asked.Script, cancel) is not { } type)
        {
            return Changes.Refused($"No class of the code is the script {asked.Script}");
        }
        // The class's part in the file asked of, else its first in a file of the code.
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences.OrderByDescending(r => SamePath(r.SyntaxTree.FilePath, asked.Path)))
        {
            if (await reference.GetSyntaxAsync(cancel).ConfigureAwait(false) is ClassDeclarationSyntax declaration && File.Exists(reference.SyntaxTree.FilePath)
                && solution.GetDocument(reference.SyntaxTree) is { } holder && await holder.GetSemanticModelAsync(cancel).ConfigureAwait(false) is { } model)
            {
                return asked.Kind == GodotFixKind.Handler
                    ? await HandlerAsync(solution, holder, model, declaration, type, compilation, engine, asked, cancel).ConfigureAwait(false)
                    : await ExportAsync(solution, holder, model, declaration, type, compilation, engine, asked, cancel).ConfigureAwait(false);
            }
        }
        return Changes.Refused($"No file of the code declares {type.Name}");
    }

    /// <summary>A handler of a signal added to a class, its parameters the signal's (less those the connection unbinds) and
    /// each value it binds, after the last of its other handlers, else its last method.</summary>
    private static async Task<Refactored> HandlerAsync(Solution solution, Document holder, SemanticModel model, ClassDeclarationSyntax declaration, INamedTypeSymbol type,
        Compilation compilation, Engine engine, GodotFixRequest asked, CancellationToken cancel)
    {
        if (!Changes.IsName(asked.Name))
        {
            return Changes.Refused($"'{asked.Name}' is not a name C# takes for a method");
        }
        for (INamedTypeSymbol? at = type; at is not null && !engine.Owns(at); at = at.BaseType)
        {
            if (at.GetMembers(asked.Name).Length > 0)
            {
                return Changes.Refused($"{type.Name} has a member {asked.Name} already");
            }
        }
        int position = declaration.OpenBraceToken.Span.End;
        HashSet<string> names = new(StringComparer.Ordinal);
        List<string> parameters = [];
        if (asked.Signal is { } signal && SignalOf(engine, signal.Script is null ? null : ScriptClass(compilation, signal.Script, cancel), signal.Engine, signal.Name) is ({ } invoke, _))
        {
            foreach (IParameterSymbol parameter in invoke.Parameters.Take(Math.Max(0, invoke.Parameters.Length - asked.Unbinds)))
            {
                parameters.Add($"{parameter.Type.ToMinimalDisplayString(model, position)} {Unique(names, Written(parameter.Name))}");
            }
        }
        foreach (string bound in asked.Binds)
        {
            parameters.Add($"{Shown(TypeOf(compilation, engine, bound, cancel), compilation, model, position)} {Unique(names, "bound")}");
        }
        MemberDeclarationSyntax made = Member($"private void {asked.Name}({string.Join(", ", parameters)})\n{{\n}}\n");
        MemberDeclarationSyntax? after = declaration.Members.OfType<MethodDeclarationSyntax>().LastOrDefault(m => asked.Beside.Contains(m.Identifier.ValueText))
            ?? declaration.Members.OfType<MethodDeclarationSyntax>().LastOrDefault() ?? declaration.Members.LastOrDefault();
        return await AddedAsync(solution, holder, declaration, made, after, before: null, cancel).ConfigureAwait(false);
    }

    /// <summary>An exported property of a type added to a class, after its last exported member, else before its first.</summary>
    private static async Task<Refactored> ExportAsync(Solution solution, Document holder, SemanticModel model, ClassDeclarationSyntax declaration, INamedTypeSymbol type,
        Compilation compilation, Engine engine, GodotFixRequest asked, CancellationToken cancel)
    {
        if (!Changes.IsName(asked.Name))
        {
            return Changes.Refused($"'{asked.Name}' is not a name C# takes for a property");
        }
        if (Property(engine, type, null, asked.Name) is not null)
        {
            return Changes.Refused($"{type.Name} has a property {asked.Name} already");
        }
        int position = declaration.OpenBraceToken.Span.End;
        string attribute = GodotSharp.Type(compilation, GodotSharp.Export) is { } export ? export.ToMinimalDisplayString(model, position) : $"{GodotSharp.Namespace}.{GodotSharp.Export}";
        attribute = attribute.EndsWith("Attribute", StringComparison.Ordinal) ? attribute[..^"Attribute".Length] : attribute;
        MemberDeclarationSyntax made = Member($"[{attribute}]\npublic {Shown(TypeOf(compilation, engine, asked.Type, cancel), compilation, model, position)} {asked.Name} {{ get; set; }}\n");
        MemberDeclarationSyntax? after = declaration.Members.LastOrDefault(m => m is PropertyDeclarationSyntax or FieldDeclarationSyntax
            && m.AttributeLists.SelectMany(l => l.Attributes).Any(a => model.GetTypeInfo(a, cancel).Type is { Name: GodotSharp.Export, ContainingNamespace.Name: GodotSharp.Namespace }));
        return await AddedAsync(solution, holder, declaration, made, after, after is null ? declaration.Members.FirstOrDefault() : null, cancel).ConfigureAwait(false);
    }

    /// <summary>The type a GetNode call asks for made a node's own (an engine class by Godot's name, else a script's class by
    /// its res://), and with it the type of a local the call declares by name.</summary>
    private static async Task<Refactored> NodeTypeAsync(Solution solution, Document document, Compilation compilation, Engine engine, GodotFixRequest asked, CancellationToken cancel)
    {
        if (asked.At is not { } at || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
            || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return Changes.Refused("The GetNode call was not found");
        }
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        InvocationExpressionSyntax? call = root.FindToken(offset).Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(c => NameOf(c.Expression) is "GetNode" or "GetNodeOrNull");
        GenericNameSyntax? generic = call?.Expression switch
        {
            GenericNameSyntax named => named,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax named } => named,
            _ => null,
        };
        if (generic is not { TypeArgumentList.Arguments: [{ } argument] })
        {
            return Changes.Refused("The GetNode call no longer asks for a type there");
        }
        if (TypeOf(compilation, engine, asked.Type, cancel) is not { } type)
        {
            return Changes.Refused($"No type of the code is {asked.Type}");
        }
        ITypeSymbol? was = model.GetTypeInfo(argument, cancel).Type;
        DocumentEditor editor = await DocumentEditor.CreateAsync(document, cancel).ConfigureAwait(false);
        editor.ReplaceNode(argument, SyntaxFactory.ParseTypeName(type.ToMinimalDisplayString(model, argument.SpanStart)).WithTriviaFrom(argument));
        // A local the call's value is declared as by the type it asked for takes the node's.
        if (call!.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Type: { IsVar: false } local } } }
            && SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(local, cancel).Type, was))
        {
            editor.ReplaceNode(local, SyntaxFactory.ParseTypeName(type.ToMinimalDisplayString(model, local.SpanStart)).WithTriviaFrom(local));
        }
        return await Changes.BetweenAsync(solution, editor.GetChangedDocument().Project.Solution, cancel).ConfigureAwait(false);
    }

    /// <summary>A member put in a class after a member (a blank line before it), else before one (a blank line after it), else
    /// as its last, indented as the file indents.</summary>
    private static async Task<Refactored> AddedAsync(Solution solution, Document holder, ClassDeclarationSyntax declaration, MemberDeclarationSyntax made,
        MemberDeclarationSyntax? after, MemberDeclarationSyntax? before, CancellationToken cancel)
    {
        DocumentEditor editor = await DocumentEditor.CreateAsync(holder, cancel).ConfigureAwait(false);
        SyntaxTrivia line = SyntaxFactory.EndOfLine("\n");
        if (after is not null)
        {
            editor.InsertAfter(after, made.WithLeadingTrivia(line));
        }
        else if (before is not null)
        {
            editor.InsertBefore(before, made.WithTrailingTrivia(line, line));
        }
        else
        {
            editor.AddMember(declaration, made);
        }
        Document changed = await Formatter.FormatAsync(editor.GetChangedDocument(), Formatter.Annotation, cancellationToken: cancel).ConfigureAwait(false);
        return await Changes.BetweenAsync(solution, changed.Project.Solution, cancel).ConfigureAwait(false);
    }

    private static MemberDeclarationSyntax Member(string text) => SyntaxFactory.ParseMemberDeclaration(text)!.WithAdditionalAnnotations(Formatter.Annotation);

    /// <summary>A type as the code at a place names it; Godot's Variant for one not known.</summary>
    private static string Shown(ITypeSymbol? type, Compilation compilation, SemanticModel model, int position) =>
        (type ?? GodotSharp.Type(compilation, "Variant"))?.ToMinimalDisplayString(model, position) ?? $"{GodotSharp.Namespace}.Variant";

    /// <summary>The C# type of a Godot type as a scene's value names it: a variant's (bool, int, float, String, Vector2, a
    /// packed array's element's array), an engine class by Godot's name, a script's class by its res://; null for none.</summary>
    private static ITypeSymbol? TypeOf(Compilation compilation, Engine engine, string? godot, CancellationToken cancel)
    {
        if (godot is null)
        {
            return null;
        }
        if (godot.StartsWith("res://", StringComparison.Ordinal))
        {
            return ScriptClass(compilation, godot, cancel);
        }
        SpecialType special = godot switch
        {
            "bool" => SpecialType.System_Boolean,
            "int" => SpecialType.System_Int32,
            "float" => SpecialType.System_Single,
            "String" => SpecialType.System_String,
            _ => SpecialType.None,
        };
        if (special != SpecialType.None)
        {
            return compilation.GetSpecialType(special);
        }
        if (godot.StartsWith("Packed", StringComparison.Ordinal) && godot.EndsWith("Array", StringComparison.Ordinal) && godot.Length > "PackedArray".Length)
        {
            string element = godot["Packed".Length..^"Array".Length];
            ITypeSymbol? of = element switch
            {
                "Byte" => compilation.GetSpecialType(SpecialType.System_Byte),
                "Int32" => compilation.GetSpecialType(SpecialType.System_Int32),
                "Int64" => compilation.GetSpecialType(SpecialType.System_Int64),
                "Float32" => compilation.GetSpecialType(SpecialType.System_Single),
                "Float64" => compilation.GetSpecialType(SpecialType.System_Double),
                "String" => compilation.GetSpecialType(SpecialType.System_String),
                _ => GodotSharp.Type(compilation, element),
            };
            return of is null ? null : compilation.CreateArrayTypeSymbol(of);
        }
        if (godot is "Array" or "Dictionary")
        {
            return GodotSharp.Type(compilation, "Collections." + godot);
        }
        return engine.Type(godot) ?? GodotSharp.Type(compilation, godot switch
        {
            "AABB" => "Aabb",
            "RID" => "Rid",
            _ => godot,
        });
    }

    /// <summary>A name as C# writes it: a keyword's with its @.</summary>
    private static string Written(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>A name not yet taken, numbered after the first.</summary>
    private static string Unique(HashSet<string> taken, string name)
    {
        string unique = name;
        for (int i = 2; !taken.Add(unique); i++)
        {
            unique = name + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return unique;
    }
}
