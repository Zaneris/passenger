using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Passenger.Roslyn.Extension;

/// <summary>GodotSharp, Godot's C# API, as the extension's readers of a Godot project's code know it: its assembly and
/// namespace, GodotObject (Godot's Object), the attributes Godot's source generator reads and writes, an engine class's
/// name in Godot's own words, the scripts a class runs as, and whether a class derives from another.</summary>
internal static class GodotSharp
{
    /// <summary>GodotSharp's assembly.</summary>
    public const string Assembly = "GodotSharp";

    /// <summary>The namespace of GodotSharp's classes and attributes.</summary>
    public const string Namespace = "Godot";

    /// <summary>The class every engine class and script derives from, Godot's Object.</summary>
    public const string GodotObject = "GodotObject";

    /// <summary>The attribute exporting a script's member to Godot's inspector.</summary>
    public const string Export = "ExportAttribute";

    /// <summary>The end of a <c>[Signal]</c> delegate's name, which its signal's name leaves out.</summary>
    public const string EventHandler = "EventHandler";

    private const string Signal = "SignalAttribute";
    private const string ScriptPathAttribute = "ScriptPathAttribute";
    private const string ClassNameAttribute = "GodotClassNameAttribute";

    /// <summary>A type of GodotSharp's in a compilation by its name in the namespace ("Variant", "Collections.Array"); null
    /// where the compilation has none.</summary>
    public static INamedTypeSymbol? Type(Compilation compilation, string name) => compilation.GetTypeByMetadataName($"{Namespace}.{name}");

    /// <summary>GodotObject in a compilation; null for one that references no GodotSharp.</summary>
    public static INamedTypeSymbol? ObjectIn(Compilation compilation) => Type(compilation, GodotObject);

    /// <summary>Whether GodotSharp declares a symbol.</summary>
    public static bool Declares(ISymbol symbol) => symbol.ContainingAssembly?.Name == Assembly;

    /// <summary>Whether a type is GodotObject.</summary>
    public static bool IsObject(ITypeSymbol? type) => type is { Name: GodotObject, ContainingNamespace.Name: Namespace };

    /// <summary>An engine class's name in Godot's own words: its <c>[GodotClassName]</c>'s (CsgBox3D's CSGBox3D), GodotObject's
    /// Object, else its C# name.</summary>
    public static string ClassName(ITypeSymbol type) =>
        Attribute(type, ClassNameAttribute)?.ConstructorArguments is [{ Value: string given }] ? given : IsObject(type) ? "Object" : type.Name;

    /// <summary>A symbol's attribute of GodotSharp's by the attribute class's name; null where it has none.</summary>
    public static AttributeData? Attribute(ISymbol symbol, string attribute) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass is { } type && type.Name == attribute && type.ContainingNamespace.Name == Namespace);

    /// <summary>Whether a symbol has an attribute of GodotSharp's, by the attribute class's name.</summary>
    public static bool Has(ISymbol symbol, string attribute) => Attribute(symbol, attribute) is not null;

    /// <summary>Whether a type is a delegate marked <c>[Signal]</c>, a script's signal.</summary>
    public static bool IsSignal(INamedTypeSymbol type) => type.TypeKind == TypeKind.Delegate && Has(type, Signal);

    /// <summary>The res:// path Godot's source generator gives a class (<c>[ScriptPath("res://Main.cs")]</c>); null for none.</summary>
    public static string? ScriptPath(INamedTypeSymbol type) =>
        Attribute(type, ScriptPathAttribute)?.ConstructorArguments is [{ Value: string path }] ? path : null;

    /// <summary>The scripts a class's code runs as: its own <c>[ScriptPath]</c>'s, and those of the classes deriving from it.</summary>
    public static async Task<IReadOnlyList<string>> ScriptsOfAsync(INamedTypeSymbol type, Solution solution, CancellationToken cancel)
    {
        List<string> scripts = [.. ScriptPath(type) is { } own ? [own] : Array.Empty<string>()];
        foreach (INamedTypeSymbol derived in await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancel).ConfigureAwait(false))
        {
            if (ScriptPath(derived) is { } script && !scripts.Contains(script))
            {
                scripts.Add(script);
            }
        }
        return scripts;
    }

    /// <summary>Whether a type derives from a class, however far below it; never the class itself.</summary>
    public static bool Derives(INamedTypeSymbol type, INamedTypeSymbol from)
    {
        for (INamedTypeSymbol? at = type.BaseType; at is not null; at = at.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(at, from))
            {
                return true;
            }
        }
        return false;
    }
}
