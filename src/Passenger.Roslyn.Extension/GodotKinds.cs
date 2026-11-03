using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>The strings of a kind naming one name in a Godot project's C# (<see cref="GodotKinds.UsesAsync"/>), which its rename edits.</summary>
public sealed class GodotKindStrings : IExtensionWorkspaceMessageHandler<GodotKindAsked, GodotKindUses>
{
    public Task<GodotKindUses> ExecuteAsync(GodotKindAsked message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        GodotKinds.UsesAsync(context.Solution, message, cancellationToken);
}

/// <summary>The strings Godot's calls take naming what a Godot project defines outside its code (<see cref="GodotStringKind"/>),
/// as Roslyn binds them against GodotSharp: a string's kind read from the class of the method it is given to and the name of
/// its parameter (<c>Input</c>'s <c>action</c>, <c>Node</c>'s <c>group</c>, <c>ShaderMaterial</c>'s <c>param</c>, a
/// <c>parameters/…</c> property's an AnimationTree's), and the object the call is made on where the code tells it: an
/// animation player, a tree or a material's node by its path from the class's node (<c>GetNode&lt;AnimationPlayer&gt;("Anim")</c>,
/// a field or local only ever given one such path) or by the <c>[Export]</c> member a scene sets to it. A GetNode path's
/// <c>/root/Name</c> names an autoload; a constant a <c>layerNumber</c> takes names a layer.</summary>
internal static class GodotKinds
{
    private const string Root = "/root/";

    /// <summary>The prefix of the property an AnimationTree takes each of its tree's parameters by.</summary>
    internal const string Parameters = "parameters/";

    /// <summary>The string literals each document was last read to give, by its text's version, so a scan of the projects
    /// for a name reads again only what changed.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DocumentId, (VersionStamp Version, HashSet<string> Strings)> Written = new();

    /// <summary>How far a field's or local's values are followed to the node or material they hold.</summary>
    private const int Deepest = 3;

    /// <summary>A control's or window's call taking a theme item's name, its kind the second group.</summary>
    private static readonly System.Text.RegularExpressions.Regex ThemeCall = new("^(Get|Has|Add|Remove)Theme(Color|Constant|FontSize|Font|Icon|Stylebox)(Override)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A Theme resource's call setting, reading or asking for an item of a kind, its kind the second group.</summary>
    private static readonly System.Text.RegularExpressions.Regex ThemeSet = new("^(Set|Get|Has|Clear)(Color|Constant|FontSize|Font|Icon|Stylebox)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The file's strings of a kind, each with the class it is written in and the object its call is made on where
    /// told; and its layer numbers, each the constant a <c>layerNumber</c> is given.</summary>
    public static IReadOnlyList<GodotKindRead> Read(Solution solution, SyntaxNode root, SemanticModel model, IReadOnlyList<INamedTypeSymbol> declared, SourceText text, CancellationToken cancel)
    {
        List<GodotKindRead> reads = [];
        foreach (SyntaxToken token in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)))
        {
            if (Of(token, model, cancel) is not (var kind, var adds, _))
            {
                continue;
            }
            int index = ClassOf(declared, token, model, cancel);
            (string? node, string? export, string? member) = Told(solution, kind, token, model, cancel);
            bool exact = GodotGenerated.Exact(token);
            reads.Add(new GodotKindRead(kind, token.ValueText, PlaceAt(text, exact ? token.SpanStart + 1 : token.SpanStart), PlaceAt(text, exact ? token.Span.End - 1 : token.Span.End),
                exact, adds, index, node, export, member));
        }
        foreach (ArgumentSyntax argument in root.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (LayerOf(argument, model, cancel) is ({ } layers, int number))
            {
                reads.Add(new GodotKindRead(GodotStringKind.Layer, number.ToString(System.Globalization.CultureInfo.InvariantCulture), PlaceAt(text, argument.Expression.SpanStart),
                    PlaceAt(text, argument.Expression.Span.End), true, false, ClassOf(declared, argument.Expression.GetFirstToken(), model, cancel), null, null, layers));
            }
        }
        return reads;
    }

    /// <summary>The layers an argument names one of by its number, and that number: the constant given to a GodotSharp
    /// method's <c>layerNumber</c>, among the layers its class and name say (<see cref="LayersOf"/>); null for any other.</summary>
    internal static (string Layers, int Number)? LayerOf(ArgumentSyntax argument, SemanticModel model, CancellationToken cancel)
    {
        if (argument.Parent?.Parent is not InvocationExpressionSyntax call || model.GetOperation(call, cancel) is not IInvocationOperation { TargetMethod: var method } invocation
            || !OfGodot(method) || invocation.Arguments.FirstOrDefault(a => a.Syntax == argument)?.Parameter is not { Name: "layerNumber" }
            || LayersOf(method) is not { } layers || model.GetConstantValue(argument.Expression, cancel) is not { HasValue: true, Value: int number })
        {
            return null;
        }
        return (layers, number);
    }

    /// <summary>The layers of project.godot's [layer_names] a GodotSharp method's <c>layerNumber</c> numbers one of, by what its
    /// name sets (collision, navigation, avoidance, a 3D render mask) and whether its class is of 2D or 3D; null for none known.</summary>
    private static string? LayersOf(IMethodSymbol method)
    {
        string? dimension = null;
        for (INamedTypeSymbol? type = method.ContainingType; type is not null && dimension is null; type = type.BaseType)
        {
            dimension = type.Name switch
            {
                "NavigationPolygon" or "TileSet" => "2d",
                "NavigationMesh" or "GridMap" => "3d",
                _ when type.Name.EndsWith("2D", StringComparison.Ordinal) => "2d",
                _ when type.Name.EndsWith("3D", StringComparison.Ordinal) => "3d",
                _ => null,
            };
        }
        string name = method.Name;
        return name.Contains("Avoidance", StringComparison.Ordinal) ? "avoidance"
            : dimension is null ? null
            : name.Contains("Navigation", StringComparison.Ordinal) ? $"{dimension}_navigation"
            : name.Contains("Collision", StringComparison.Ordinal) ? $"{dimension}_physics"
            : dimension == "3d" && (name.Contains("LayerMask", StringComparison.Ordinal) || name.Contains("CullMask", StringComparison.Ordinal) || name.Contains("BakeMask", StringComparison.Ordinal)
                || name.Contains("HeightfieldMask", StringComparison.Ordinal)) ? "3d_render"
            : null;
    }

    /// <summary>The string literals a document's text gives, read again only once its text changes.</summary>
    private static async Task<HashSet<string>> StringsOfAsync(Document document, CancellationToken cancel)
    {
        VersionStamp version = await document.GetTextVersionAsync(cancel).ConfigureAwait(false);
        if (Written.TryGetValue(document.Id, out (VersionStamp Version, HashSet<string> Strings) kept) && kept.Version == version)
        {
            return kept.Strings;
        }
        HashSet<string> strings = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is { } root
            ? [.. root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)).Select(t => t.ValueText)] : [];
        Written[document.Id] = (version, strings);
        return strings;
    }

    /// <summary>Whether a document's text gives a string literal that one of some names is, or starts with (<paramref name="prefix"/>).</summary>
    internal static async Task<bool> WritesAsync(Document document, IReadOnlySet<string> names, bool prefix, CancellationToken cancel)
    {
        HashSet<string> strings = await StringsOfAsync(document, cancel).ConfigureAwait(false);
        return prefix ? strings.Any(s => names.Any(n => s.StartsWith(n, StringComparison.Ordinal))) : strings.Overlaps(names);
    }

    /// <summary>The names of the file's strings that a call elsewhere in the code of its project, or of one near it, adds
    /// (<c>AddToGroup("enemies")</c>), so they are defined though no scene says them.</summary>
    public static async Task<IReadOnlyList<GodotKindAdded>> AddedAsync(Solution solution, Project project, IReadOnlyList<GodotKindRead> reads, CancellationToken cancel)
    {
        HashSet<(GodotStringKind, string)> wanted = [.. reads.Where(r => !r.Adds).Select(r => (r.Kind, r.Name))];
        HashSet<string> names = [.. wanted.Select(w => w.Item2)];
        HashSet<GodotKindAdded> added = [];
        if (names.Count == 0)
        {
            return [];
        }
        ProjectDependencyGraph graph = solution.GetProjectDependencyGraph();
        IEnumerable<ProjectId> near = [project.Id, .. graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id), .. graph.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id)];
        foreach (Project each in near.Distinct().Select(solution.GetProject).OfType<Project>())
        {
            foreach (Document document in each.Documents)
            {
                if (!await WritesAsync(document, names, prefix: false, cancel).ConfigureAwait(false) || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
                {
                    continue;
                }
                List<SyntaxToken> strings = [.. root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) && names.Contains(t.ValueText))];
                if (strings.Count == 0 || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
                {
                    continue;
                }
                foreach (SyntaxToken token in strings)
                {
                    if (Of(token, model, cancel) is (var kind, true, _) && wanted.Contains((kind, token.ValueText)))
                    {
                        added.Add(new GodotKindAdded(kind, token.ValueText));
                    }
                }
            }
        }
        return [.. added];
    }

    /// <summary>Every string of a kind naming a name in the C# of the projects in a Godot project's folder, each once though a
    /// project builds it for several frameworks, told as <see cref="Read"/> tells it: for an autoload, the name in each node
    /// path through <c>/root/Name</c>; for a layer, each constant a <c>layerNumber</c> is given of its number, its layers
    /// as its member.</summary>
    public static async Task<GodotKindUses> UsesAsync(Solution solution, GodotKindAsked asked, CancellationToken cancel)
    {
        List<GodotKindUse> uses = [];
        HashSet<(string, int)> seen = [];
        HashSet<string> names = [asked.Kind == GodotStringKind.Autoload ? Root + asked.Name : asked.Name];
        foreach (Project project in solution.Projects.Where(p => System.IO.Path.GetDirectoryName(p.FilePath) is { } home
            && (SamePath(home, asked.Folder) || Passenger.Core.PathNames.IsUnder(home, asked.Folder))))
        {
            foreach (Document document in project.Documents)
            {
                if (document.FilePath is not { Length: > 0 } path)
                {
                    continue;
                }
                if (asked.Kind == GodotStringKind.Layer)
                {
                    uses.AddRange(await LayerUsesAsync(document, path, asked.Name, seen, cancel).ConfigureAwait(false));
                    continue;
                }
                if (!await WritesAsync(document, names, prefix: asked.Kind == GodotStringKind.Autoload, cancel).ConfigureAwait(false)
                    || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
                {
                    continue;
                }
                List<SyntaxToken> strings = [.. root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) && GodotGenerated.Exact(t)
                    && (asked.Kind == GodotStringKind.Autoload ? AutoloadOf(t.ValueText) == asked.Name : t.ValueText == asked.Name))];
                if (strings.Count == 0 || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
                {
                    continue;
                }
                SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
                foreach (SyntaxToken token in strings)
                {
                    int start = token.SpanStart + 1 + (asked.Kind == GodotStringKind.Autoload ? Root.Length : 0);
                    bool named = asked.Kind == GodotStringKind.Autoload ? IsNodePath(token, model, cancel) : Of(token, model, cancel) is var (kind, _, _) && kind == asked.Kind;
                    if (!named || !seen.Add((path, start)))
                    {
                        continue;
                    }
                    (string? node, string? export, string? member) = Told(solution, asked.Kind, token, model, cancel);
                    IReadOnlyList<string> scripts = model.GetEnclosingSymbol(token.SpanStart, cancel)?.ContainingType is { } inside
                        ? await ScriptsOfAsync(inside, solution, cancel).ConfigureAwait(false) : [];
                    uses.Add(new GodotKindUse(path, PlaceAt(text, start), PlaceAt(text, start + asked.Name.Length), scripts, node, export, member));
                }
            }
        }
        return new GodotKindUses(uses);
    }

    /// <summary>A document's constants given to a <c>layerNumber</c> that are a number, each with the layers it is among.</summary>
    private static async Task<IReadOnlyList<GodotKindUse>> LayerUsesAsync(Document document, string path, string number, HashSet<(string, int)> seen, CancellationToken cancel)
    {
        if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
        {
            return [];
        }
        List<ArgumentSyntax> arguments = [.. root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => NameOf(c.Expression)?.EndsWith("Value", StringComparison.Ordinal) == true).SelectMany(c => c.ArgumentList.Arguments)];
        if (arguments.Count == 0 || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return [];
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        List<GodotKindUse> uses = [];
        foreach (ArgumentSyntax argument in arguments)
        {
            if (LayerOf(argument, model, cancel) is ({ } layers, int at) && at.ToString(System.Globalization.CultureInfo.InvariantCulture) == number
                && seen.Add((path, argument.Expression.SpanStart)))
            {
                uses.Add(new GodotKindUse(path, PlaceAt(text, argument.Expression.SpanStart), PlaceAt(text, argument.Expression.Span.End), [], null, null, layers));
            }
        }
        return uses;
    }

    /// <summary>The object a string's call is made on, as the code tells it (<see cref="NodeOf"/>, <see cref="MaterialOf"/>).</summary>
    private static (string? Node, string? Export, string? Member) Told(Solution solution, GodotStringKind kind, SyntaxToken token, SemanticModel model, CancellationToken cancel) =>
        Of(token, model, cancel) is (_, _, var instance) ? kind switch
        {
            GodotStringKind.Animation or GodotStringKind.TreeParameter => NodeOf(solution, instance, model, Deepest, cancel) is var (n, e) ? (n, e, null) : (null, null, null),
            GodotStringKind.ShaderParameter => MaterialOf(solution, instance, model, Deepest, cancel) ?? (null, null, null),
            >= GodotStringKind.ThemeColor and <= GodotStringKind.ThemeStyle => ThemeTold(solution, token, instance, model, cancel),
            _ => (null, null, null),
        } : (null, null, null);

    /// <summary>The theme type a theme item's call looks its name up in, as its member: the type it names (<c>themeType</c>),
    /// else the engine class of the control or window it is made on; and as its node "." where that is the class's own, else
    /// the node the code tells it is (<see cref="NodeOf"/>), whose scenes may give it a type variation.</summary>
    private static (string? Node, string? Export, string? Member) ThemeTold(Solution solution, SyntaxToken token, IOperation? instance, SemanticModel model, CancellationToken cancel)
    {
        if (token.Parent?.Parent?.Parent?.Parent is not InvocationExpressionSyntax call || model.GetOperation(call, cancel) is not IInvocationOperation invocation)
        {
            return (null, null, null);
        }
        string? given = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "themeType") is { ArgumentKind: not ArgumentKind.DefaultValue } typed ? Constant(typed.Value) : null;
        instance = Unwrapped(instance);
        bool own = instance is null or IInstanceReferenceOperation;
        if (given is { Length: > 0 } || invocation.TargetMethod.ContainingType.Name == "Theme")
        {
            return (null, null, given is { Length: > 0 } ? given : null);
        }
        ITypeSymbol? receiver = own ? model.GetEnclosingSymbol(token.SpanStart, cancel)?.ContainingType : instance!.Type;
        for (ITypeSymbol? type = receiver; type is not null; type = type.BaseType)
        {
            if (OfGodot(type))
            {
                (string? node, string? export) = own ? (".", null) : NodeOf(solution, instance, model, Deepest, cancel) ?? (null, null);
                return (node, export, type.Name == "GodotObject" ? "Object" : type.Name);
            }
        }
        return (null, null, null);
    }

    /// <summary>The theme item kind a control's, window's or Theme's method takes the name of; null for any other.</summary>
    private static GodotStringKind? ThemeKindOf(IMethodSymbol method) =>
        (method.ContainingType.Name is "Control" or "Window" ? ThemeCall.Match(method.Name) is { Success: true } call ? call.Groups[2].Value : null
            : method.ContainingType.Name == "Theme" && ThemeSet.Match(method.Name) is { Success: true } set ? set.Groups[2].Value : null) switch
        {
            "Color" => GodotStringKind.ThemeColor,
            "Constant" => GodotStringKind.ThemeConstant,
            "Font" => GodotStringKind.ThemeFont,
            "FontSize" => GodotStringKind.ThemeFontSize,
            "Icon" => GodotStringKind.ThemeIcon,
            "Stylebox" => GodotStringKind.ThemeStyle,
            _ => null,
        };

    /// <summary>The scripts a class's code runs as: its own <c>[ScriptPath]</c>'s, and those of the classes deriving from it.</summary>
    internal static async Task<IReadOnlyList<string>> ScriptsOfAsync(INamedTypeSymbol type, Solution solution, CancellationToken cancel)
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

    private static string? ScriptPath(INamedTypeSymbol type) =>
        type.GetAttributes().FirstOrDefault(a => a.AttributeClass is { Name: "ScriptPathAttribute", ContainingNamespace.Name: "Godot" })?.ConstructorArguments
            is [{ Value: string path }] ? path : null;

    /// <summary>The autoload a node path names by <c>/root/Name</c>; null for any other path.</summary>
    internal static string? AutoloadOf(string path)
    {
        if (!path.StartsWith(Root, StringComparison.Ordinal))
        {
            return null;
        }
        int end = path.IndexOfAny(['/', ':'], Root.Length);
        string name = end < 0 ? path[Root.Length..] : path[Root.Length..end];
        return name.Length == 0 ? null : name;
    }

    /// <summary>Whether a string literal is given to GodotSharp as a node path: a NodePath parameter's, or a NodePath made of it.</summary>
    private static bool IsNodePath(SyntaxToken token, SemanticModel model, CancellationToken cancel)
    {
        if (token.Parent is not LiteralExpressionSyntax { Parent: ArgumentSyntax argument } || argument.Parent?.Parent is not { } call)
        {
            return false;
        }
        IReadOnlyList<IArgumentOperation> arguments = model.GetOperation(call, cancel) switch
        {
            IInvocationOperation invocation when OfGodot(invocation.TargetMethod) => invocation.Arguments,
            IObjectCreationOperation { Constructor: { } made } creation when OfGodot(made) => creation.Arguments,
            _ => [],
        };
        IParameterSymbol? parameter = arguments.FirstOrDefault(a => a.Syntax == argument)?.Parameter;
        return parameter?.Type is INamedTypeSymbol { Name: "NodePath", ContainingNamespace.Name: "Godot" }
            || parameter?.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType.Name: "NodePath" };
    }

    /// <summary>What a string literal names as Godot's call takes it: its kind, whether the call adds it, and the object the
    /// call is made on; null for any other string. A property's assignment counts as its setter's call
    /// (<c>player.CurrentAnimation = "run"</c>).</summary>
    internal static (GodotStringKind Kind, bool Adds, IOperation? Instance)? Of(SyntaxToken token, SemanticModel model, CancellationToken cancel)
    {
        if (token.Parent is not LiteralExpressionSyntax literal)
        {
            return null;
        }
        if (literal.Parent is AssignmentExpressionSyntax assignment && assignment.Right == literal)
        {
            return model.GetOperation(assignment, cancel) is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property: var property } target } && OfGodot(property)
                && (property.ContainingType.Name, property.Name) is ("AnimationPlayer", "CurrentAnimation" or "AssignedAnimation" or "Autoplay")
                    or ("AnimatedSprite2D" or "AnimatedSprite3D", "Animation" or "Autoplay")
                ? (GodotStringKind.Animation, false, target.Instance) : null;
        }
        if (literal.Parent is not ArgumentSyntax argument || argument.Parent?.Parent is not { } call
            || model.GetOperation(call, cancel) is not IInvocationOperation { TargetMethod: var method } invocation || !OfGodot(method)
            || invocation.Arguments.FirstOrDefault(a => a.Syntax == argument)?.Parameter is not { } parameter)
        {
            return null;
        }
        if (parameter.Name == "property" && method.ContainingType.Name == "GodotObject" && token.ValueText.StartsWith(Parameters, StringComparison.Ordinal))
        {
            return (GodotStringKind.TreeParameter, false, invocation.Instance);
        }
        return KindOf(method, parameter.Name) is var (kind, adds) ? (kind, adds, invocation.Instance) : null;
    }

    /// <summary>The kind of name a parameter of GodotSharp's API takes, by its method's class and its own name, and whether
    /// the method adds it; null for a parameter of none.</summary>
    private static (GodotStringKind Kind, bool Adds)? KindOf(IMethodSymbol method, string parameter) => (method.ContainingType.Name, parameter) switch
    {
        ("Input", "action" or "negativeAction" or "positiveAction" or "negativeX" or "positiveX" or "negativeY" or "positiveY") => (GodotStringKind.Action, false),
        ("InputEvent", "action") => (GodotStringKind.Action, false),
        ("InputMap", "action") => (GodotStringKind.Action, method.Name == "AddAction"),
        ("Node", "group") => (GodotStringKind.Group, method.Name == "AddToGroup"),
        ("SceneTree", "group") => (GodotStringKind.Group, false),
        ("SceneTree", "name") when method.Name == "HasGroup" => (GodotStringKind.Group, false),
        ("AnimationMixer", "name") when method.Name is "HasAnimation" or "GetAnimation" or "Capture" => (GodotStringKind.Animation, false),
        ("AnimationPlayer", "name" or "animation" or "animationFrom" or "animationTo") => (GodotStringKind.Animation, false),
        ("AnimatedSprite2D" or "AnimatedSprite3D", "name") => (GodotStringKind.Animation, false),
        ("AnimationLibrary", "name") when method.Name == "AddAnimation" => (GodotStringKind.Animation, true),
        ("ShaderMaterial", "param") => (GodotStringKind.ShaderParameter, false),
        ("GodotObject" or "TranslationServer", "message") => (GodotStringKind.Translation, false),
        ("Translation", "srcMessage") when method.Name is "AddMessage" or "AddPluralMessage" => (GodotStringKind.Translation, true),
        ("RenderingServer", "name") when method.Name.StartsWith("GlobalShaderParameter", StringComparison.Ordinal) => (GodotStringKind.ShaderGlobal, method.Name == "GlobalShaderParameterAdd"),
        ("Control" or "Window" or "Theme", "name") when ThemeKindOf(method) is { } theme => (theme, method.Name.StartsWith("Add", StringComparison.Ordinal) || method.Name.StartsWith("Set", StringComparison.Ordinal)),
        _ => null,
    };

    /// <summary>The node an object is, as the code tells it (<see cref="NodeOf(Solution, IOperation?, SemanticModel, int, CancellationToken)"/>).</summary>
    internal static (string? Node, string? Export)? NodeOf(Solution solution, IOperation? value, SemanticModel model, CancellationToken cancel) =>
        NodeOf(solution, value, model, Deepest, cancel);

    /// <summary>The index among a file's classes Godot runs of the one a token is written in (or a class nested in it); -1 outside them.</summary>
    internal static int ClassOf(IReadOnlyList<INamedTypeSymbol> declared, SyntaxToken token, SemanticModel model, CancellationToken cancel)
    {
        INamedTypeSymbol? inside = model.GetEnclosingSymbol(token.SpanStart, cancel)?.ContainingType;
        int index = -1;
        for (int i = 0; i < declared.Count && inside is not null; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(declared[i], inside) || SymbolEqualityComparer.Default.Equals(declared[i], inside.ContainingType))
            {
                index = i;
            }
        }
        return index;
    }

    /// <summary>The node an object is, as the code tells it: the class's own (<c>this</c>), a GetNode path from it, or the
    /// <c>[Export]</c> member a scene sets to it; through a field, property or local whose every value is one of those
    /// alike. Null where the code does not tell.</summary>
    private static (string? Node, string? Export)? NodeOf(Solution solution, IOperation? value, SemanticModel model, int depth, CancellationToken cancel)
    {
        value = Unwrapped(value);
        switch (value)
        {
            case null:
                return null;
            case IInstanceReferenceOperation:
                return (".", null);
            case IInvocationOperation { TargetMethod: { Name: "GetNode" or "GetNodeOrNull" } method, Instance: null or IInstanceReferenceOperation } call
                when method.OriginalDefinition.ContainingType is { Name: "Node", ContainingNamespace.Name: "Godot" } && call.Arguments.Length > 0:
                return Constant(call.Arguments[0].Value) is { } path ? (path, null) : null;
        }
        if (depth == 0 || Held(value) is not { } held)
        {
            return null;
        }
        if (held is IFieldSymbol or IPropertySymbol && held.GetAttributes().Any(a => a.AttributeClass is { Name: "ExportAttribute", ContainingNamespace.Name: "Godot" }))
        {
            return (null, held.Name);
        }
        (string? Node, string? Export)? told = null;
        foreach (IOperation given in Values(solution, held, model, cancel))
        {
            if (NodeOf(solution, given, model, depth - 1, cancel) is not { } one || told is { } before && before != one)
            {
                return null;
            }
            told = one;
        }
        return told;
    }

    /// <summary>The node property a material is read from, as the code tells it (<c>Material as ShaderMaterial</c>,
    /// <c>GetNode&lt;Sprite2D&gt;("Sprite").Material</c>), with the node (<see cref="NodeOf"/>); null where it does not tell.</summary>
    private static (string? Node, string? Export, string? Member)? MaterialOf(Solution solution, IOperation? value, SemanticModel model, int depth, CancellationToken cancel)
    {
        value = Unwrapped(value);
        if (value is IPropertyReferenceOperation { Property: { Name: "Material" or "MaterialOverride" or "MaterialOverlay" } property } read && OfGodot(property))
        {
            string member = property.Name switch { "Material" => "material", "MaterialOverride" => "material_override", _ => "material_overlay" };
            return NodeOf(solution, read.Instance, model, depth, cancel) is var (node, export) ? (node, export, member) : null;
        }
        if (depth == 0 || value is null || Held(value) is not { } held)
        {
            return null;
        }
        (string? Node, string? Export, string? Member)? told = null;
        foreach (IOperation given in Values(solution, held, model, cancel))
        {
            if (MaterialOf(solution, given, model, depth - 1, cancel) is not { } one || told is { } before && before != one)
            {
                return null;
            }
            told = one;
        }
        return told;
    }

    /// <summary>A value read from a field or property of the code's (on its own class) or a local; null for any other.</summary>
    private static ISymbol? Held(IOperation value) => value switch
    {
        ILocalReferenceOperation local => local.Local,
        IFieldReferenceOperation { Instance: null or IInstanceReferenceOperation, Field: var field } when field.Locations.Any(l => l.IsInSource) => field,
        IPropertyReferenceOperation { Instance: null or IInstanceReferenceOperation, Property: var property } when property.Locations.Any(l => l.IsInSource) => property,
        _ => null,
    };

    /// <summary>Every value the code gives a field, property or local: its initializer and each assignment to it, in the
    /// files declaring its class (a local's, in its method); a source generator's (Godot's, which sets every field as a
    /// script's state is put back) left out.</summary>
    private static List<IOperation> Values(Solution solution, ISymbol held, SemanticModel model, CancellationToken cancel)
    {
        List<IOperation> values = [];
        IEnumerable<SyntaxNode> scopes = held is ILocalSymbol
            ? held.DeclaringSyntaxReferences.Select(r => r.GetSyntax(cancel).FirstAncestorOrSelf<MemberDeclarationSyntax>()).OfType<SyntaxNode>()
            : held.ContainingType.DeclaringSyntaxReferences.Where(r => solution.GetDocument(r.SyntaxTree) is not SourceGeneratedDocument).Select(r => r.GetSyntax(cancel));
        foreach (SyntaxNode scope in scopes)
        {
            SemanticModel there = scope.SyntaxTree == model.SyntaxTree ? model : model.Compilation.GetSemanticModel(scope.SyntaxTree);
            foreach (SyntaxNode node in scope.DescendantNodes())
            {
                ExpressionSyntax? given = node switch
                {
                    VariableDeclaratorSyntax { Initializer.Value: var initial } declarator when SymbolEqualityComparer.Default.Equals(there.GetDeclaredSymbol(declarator, cancel), held) => initial,
                    PropertyDeclarationSyntax { Initializer.Value: var initial } property when SymbolEqualityComparer.Default.Equals(there.GetDeclaredSymbol(property, cancel), held) => initial,
                    AssignmentExpressionSyntax { Left: var left, Right: var right } when SymbolEqualityComparer.Default.Equals(there.GetSymbolInfo(left, cancel).Symbol, held) => right,
                    _ => null,
                };
                if (given is not null && there.GetOperation(given, cancel) is { } operation)
                {
                    values.Add(operation);
                }
            }
        }
        return values;
    }

    /// <summary>A value as written, before the conversions and casts around it (<c>Material as ShaderMaterial</c>).</summary>
    private static IOperation? Unwrapped(IOperation? value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }
        return value;
    }

    /// <summary>A constant string a value is, or the one a NodePath or StringName is made of.</summary>
    private static string? Constant(IOperation value)
    {
        value = Unwrapped(value)!;
        if (value is IObjectCreationOperation { Type.Name: "NodePath" or "StringName", Arguments: [{ Value: var made }] })
        {
            value = Unwrapped(made)!;
        }
        return value.ConstantValue is { HasValue: true, Value: string path } ? path : null;
    }

    private static bool OfGodot(ISymbol symbol) => symbol.ContainingAssembly?.Name == "GodotSharp";
}
