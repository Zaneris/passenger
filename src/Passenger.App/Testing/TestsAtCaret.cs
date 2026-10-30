using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App.Testing;

/// <summary>A test method a file declares: its type's full name (nested types after a '+'), its name, the editor's line
/// (from zero) its name is on, and the traits its attributes and its type's give it. Abstract and generic types' tests
/// run only through the types deriving from them.</summary>
public sealed record DeclaredTest(string Type, string Method, int Line, IReadOnlyList<KeyValuePair<string, string>> Traits, bool Abstract)
{
    public bool SameAs(DeclaredTest other) => Line == other.Line && SameTest(other);

    /// <summary>The same test, on whatever line.</summary>
    public bool SameTest(DeclaredTest other) =>
        Type == other.Type && Method == other.Method && Abstract == other.Abstract && Traits.SequenceEqual(other.Traits);
}

/// <summary>What a C# file declares for testing: the types that hold tests and the tests, with the lines their names
/// are on, the name of every method it declares, test or not, by which a test a runner found there and since removed is
/// known to be gone, and the test attributes it declares (classes deriving from one, named as written, less "Attribute").</summary>
public sealed record FileTests(IReadOnlyList<(string Type, int Line)> Types, IReadOnlyList<DeclaredTest> Tests, IReadOnlySet<string> Methods)
{
    public static readonly FileTests None = new([], [], new HashSet<string>(StringComparer.Ordinal));

    public IReadOnlySet<string> Attributes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether a later read says the same: typing inside a method's body changes nothing of it.</summary>
    public bool SameAs(FileTests other) =>
        Types.SequenceEqual(other.Types) && Tests.Count == other.Tests.Count && Tests.Zip(other.Tests).All(p => p.First.SameAs(p.Second)) && Methods.SetEquals(other.Methods)
        && Attributes.SetEquals(other.Attributes);

    /// <summary>Whether a later read says the same but for the lines the types and tests are on: lines put in or taken out
    /// above or among them, which move their rows rather than make the tree again.</summary>
    public bool OnlyMoved(FileTests other)
    {
        if (Types.Count != other.Types.Count || Tests.Count != other.Tests.Count || !Methods.SetEquals(other.Methods) || !Attributes.SetEquals(other.Attributes))
        {
            return false;
        }
        for (int i = 0; i < Types.Count; i++)
        {
            if (Types[i].Type != other.Types[i].Type)
            {
                return false;
            }
        }
        for (int i = 0; i < Tests.Count; i++)
        {
            if (!Tests[i].SameTest(other.Tests[i]))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>A type or a method a file declares: its type's full name (nested types after a '+'), the method's name (null
/// for the type), the editor's line (from zero) its name is on, and the lines it spans from its attributes to its end as
/// the compiler counts them (from zero), where a runner says the tests it declares are.</summary>
public sealed record DeclaredPlace(string Type, string? Method, int Line, int First, int Last)
{
    /// <summary>Whether a line a runner gives a test (from one, as the compiler counts) is inside it.</summary>
    public bool Holds(int line) => line - 1 >= First && line - 1 <= Last;
}

/// <summary>Every type and method a file declares, with where each is (<see cref="DeclaredPlace"/>), outer types first.</summary>
public sealed record FilePlaces(IReadOnlyList<DeclaredPlace> Types, IReadOnlyList<DeclaredPlace> Methods)
{
    public static readonly FilePlaces None = new([], []);
}

/// <summary>What Run Tests runs from the editor: the test method the caret is in, else every test of the
/// class it is in, found from the file's own syntax so it needs no build to know where it is; and the tests a file
/// declares, for the gutter's marks and the Unit Tests window's tree. The syntax is an open file's shared parse, or a
/// closed file's own; lines are the editor's.</summary>
public static class TestsAtCaret
{
    /// <summary>The frameworks' own attributes that make a method a test, as written less "Attribute": xUnit's (and its
    /// culture's, STA's, WPF's and Avalonia's), NUnit's and TUnit's, and MSTest's (its STA and UI threads' too).</summary>
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "CulturedFact", "CulturedTheory", "StaFact", "StaTheory", "WpfFact", "WpfTheory", "UIFact", "UITheory",
        "AvaloniaFact", "AvaloniaTheory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod", "STATestMethod", "UITestMethod",
    };

    /// <summary>The test the caret's line (from zero, the editor's line, which Roslyn may count as several) is in: its
    /// test method, else the nearest type around it holding tests, its own or its nested types' (the method null). A test
    /// is one by its attributes or one a runner found there (<paramref name="found"/>, given a type and a method or null);
    /// null where there is none.</summary>
    public static DeclaredPlace? At(ParsedText syntax, int line, Func<string, string?, bool>? found = null)
    {
        TextSnapshot text = syntax.Text;
        if (line < 0 || line >= text.LineCount)
        {
            return null;
        }
        string content = text.GetLineText(line);
        SyntaxNode root = syntax.Root;
        SyntaxNode? node = root.FindToken(Math.Min(text.GetLineStart(line) + (content.Length - content.TrimStart().Length), root.FullSpan.End)).Parent;
        MethodDeclarationSyntax? method = node?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        TypeDeclarationSyntax? type = (method?.Parent as TypeDeclarationSyntax) ?? node?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null)
        {
            return null;
        }
        if (method is not null && (IsTest(method, NoneOwn) || found?.Invoke(FullName(type), method.Identifier.ValueText) == true))
        {
            return Place(syntax, FullName(type), method.Identifier.ValueText, method.Identifier, method);
        }
        foreach (TypeDeclarationSyntax around in type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>())
        {
            string name = FullName(around);
            if (HoldsTests(around) || found?.Invoke(name, null) == true)
            {
                return Place(syntax, name, null, around.Identifier, around);
            }
        }
        return null;
    }

    /// <summary>Every type and method a file declares and where, read without going into any member's body.</summary>
    public static FilePlaces Places(ParsedText syntax)
    {
        List<DeclaredPlace> types = [];
        List<DeclaredPlace> methods = [];
        foreach (TypeDeclarationSyntax type in Types(syntax.Root))
        {
            string name = FullName(type);
            types.Add(Place(syntax, name, null, type.Identifier, type));
            foreach (MethodDeclarationSyntax method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                methods.Add(Place(syntax, name, method.Identifier.ValueText, method.Identifier, method));
            }
        }
        return new FilePlaces(types, methods);
    }

    /// <summary>A declaration's place: its name's editor line, and its lines as the compiler counts them.</summary>
    private static DeclaredPlace Place(ParsedText syntax, string type, string? method, SyntaxToken name, SyntaxNode declaration)
    {
        FileLinePositionSpan lines = declaration.SyntaxTree.GetLineSpan(declaration.Span);
        return new DeclaredPlace(type, method, syntax.Text.GetLineFromOffset(name.SpanStart), lines.StartLinePosition.Line, lines.EndLinePosition.Line);
    }

    private static readonly IReadOnlySet<string> NoneOwn = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Everything a file declares for testing, read without going into any member's body: a test is a method
    /// with a framework's test attribute, or one of <paramref name="own"/>, the attributes its project derives from them.</summary>
    public static FileTests Read(ParsedText syntax, IReadOnlySet<string>? own = null)
    {
        own ??= NoneOwn;
        List<(string, int)> types = [];
        List<DeclaredTest> tests = [];
        HashSet<string> methods = new(StringComparer.Ordinal);
        HashSet<string> attributes = new(StringComparer.Ordinal);
        foreach (TypeDeclarationSyntax type in Types(syntax.Root))
        {
            string name = FullName(type);
            bool any = false;
            bool inherited = InheritedOnly(type);
            if (type.BaseList?.Types.FirstOrDefault()?.Type is { } baseType && IsTestAttribute(Simple(baseType), own))
            {
                attributes.Add(Unsuffixed(type.Identifier.ValueText));
            }
            IReadOnlyList<KeyValuePair<string, string>>? typeTraits = null;
            foreach (MethodDeclarationSyntax method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                methods.Add(method.Identifier.ValueText);
                if (!IsTest(method, own))
                {
                    continue;
                }
                any = true;
                typeTraits ??= Traits(type.AttributeLists);
                tests.Add(new DeclaredTest(name, method.Identifier.ValueText, syntax.Text.GetLineFromOffset(method.Identifier.SpanStart),
                    [.. typeTraits, .. Traits(method.AttributeLists)], inherited));
            }
            if (any)
            {
                types.Add((name, syntax.Text.GetLineFromOffset(type.Identifier.SpanStart)));
            }
        }
        return new FileTests(types, tests, methods) { Attributes = attributes };
    }

    /// <summary>A file not open in an editor, parsed on its own with its project's symbols (<see cref="CSharpParsing"/>),
    /// its documentation comments passed over: an open one's tests are read from its shared parse.</summary>
    public static FileTests Read(string text, CSharpParseOptions options, IReadOnlySet<string>? own = null) =>
        Read(new ParsedText(CSharpSyntaxTree.ParseText(text, options.WithDocumentationMode(DocumentationMode.None)), TextSnapshot.From(text)), own);

    /// <summary>Whether a test's full name (a theory's rows, NUnit's cases and fixtures' instances included) belongs to a
    /// type and method.</summary>
    public static bool Matches(string fullyQualifiedName, string type, string? method)
    {
        string name = TestItem.MethodOf(fullyQualifiedName);
        string normalized = name.Replace('+', '.');
        string owner = type.Replace('+', '.');
        return method is null ? normalized.StartsWith(owner + ".", StringComparison.Ordinal) : normalized == $"{owner}.{method}";
    }

    /// <summary>The types a file declares, nested ones too, found without going into any member's body.</summary>
    private static IEnumerable<TypeDeclarationSyntax> Types(SyntaxNode root) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax).OfType<TypeDeclarationSyntax>();

    private static bool IsTest(MethodDeclarationSyntax method, IReadOnlySet<string> own) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Any(a => IsTestAttribute(Name(a), own));

    private static bool IsTestAttribute(string name, IReadOnlySet<string> own) => TestAttributes.Contains(name) || own.Contains(name);

    /// <summary>Whether a type or a type nested in it declares a test, found without going into any member's body.</summary>
    private static bool HoldsTests(TypeDeclarationSyntax type) =>
        type.DescendantNodesAndSelf(n => n is TypeDeclarationSyntax).OfType<TypeDeclarationSyntax>().Any(t => t.Members.OfType<MethodDeclarationSyntax>().Any(m => IsTest(m, NoneOwn)));

    /// <summary>An abstract or generic type, or one inside such a type: a runner runs its tests as a derived type's.</summary>
    private static bool InheritedOnly(TypeDeclarationSyntax type) =>
        type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Any(t => t.TypeParameterList is not null || t.Modifiers.Any(SyntaxKind.AbstractKeyword));

    /// <summary>The traits as VSTest's adapters name them: xUnit's Trait(name, value), NUnit's Category and MSTest's
    /// TestCategory, when written as string literals.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> Traits(SyntaxList<AttributeListSyntax> lists)
    {
        List<KeyValuePair<string, string>> traits = [];
        foreach (AttributeSyntax attribute in lists.SelectMany(l => l.Attributes))
        {
            string[] values = [.. (attribute.ArgumentList?.Arguments ?? []).Where(a => a.NameEquals is null && a.NameColon is null)
                .Select(a => a.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal ? literal.Token.ValueText : null)
                .OfType<string>()];
            switch (Name(attribute))
            {
                case "Trait" when values.Length == 2:
                    traits.Add(new(values[0], values[1]));
                    break;
                case "Category" or "TestCategory" when values.Length == 1:
                    traits.Add(new(Name(attribute), values[0]));
                    break;
            }
        }
        return traits;
    }

    private static string Name(AttributeSyntax attribute) => Simple(attribute.Name);

    /// <summary>A type's name as written, less its namespace, its type arguments and an "Attribute" at its end.</summary>
    private static string Simple(TypeSyntax type) => Unsuffixed(type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString(),
    });

    private static string Unsuffixed(string name) => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;

    private static string FullName(TypeDeclarationSyntax type)
    {
        List<string> names = [type.Identifier.ValueText];
        SyntaxNode? parent = type.Parent;
        string? space = null;
        while (parent is not null)
        {
            switch (parent)
            {
                case TypeDeclarationSyntax outer:
                    names.Insert(0, outer.Identifier.ValueText + "+");
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    space = space is null ? ns.Name.ToString() : $"{ns.Name}.{space}";
                    break;
            }
            parent = parent.Parent;
        }
        string typeName = string.Concat(names);
        return space is null ? typeName : $"{space}.{typeName}";
    }
}
