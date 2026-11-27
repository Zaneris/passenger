using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.App.Debugging;

/// <summary>A .NET MAUI program's XAML as the code MAUI's XAML source generator wrote of each page (its .xsg.cs), read from
/// the build's PDB, which embeds it as the program runs it: each statement of a page's InitializeComponent read for the
/// element it makes, at the place MAUI records for it (RegisterSourceInfo), or the property it sets there, so a breakpoint
/// on an element's line binds where that element is made, one on an attribute where its property is set, and a stop in
/// that code is shown at the XAML. A page the generator made no code of says why its breakpoints cannot bind.</summary>
public sealed class MauiXamlCode
{
    private readonly string program;
    private readonly Dictionary<string, MauiXamlPage> pages = new(PathNames.Comparer);
    private readonly Dictionary<string, MauiXamlPage> documents = new(PathNames.Comparer);
    private readonly HashSet<string> declared = new(PathNames.Comparer);
    private readonly bool symbols;

    private MauiXamlCode(string program, bool symbols)
    {
        this.program = program;
        this.symbols = symbols;
    }

    /// <summary>The code of each XAML file of the program built as <paramref name="assembly"/> (its PDB beside it), the
    /// files named from <paramref name="folder"/>, its project's, as MAUI's build names them; read off the window's thread.</summary>
    public static MauiXamlCode Read(string assembly, string folder)
    {
        string pdb = Path.ChangeExtension(assembly, ".pdb");
        MauiXamlCode code = new(Path.GetFileName(assembly), File.Exists(pdb));
        foreach ((string document, string text) in PortablePdbs.EmbeddedSources(pdb, d => d.EndsWith(".xsg.cs", StringComparison.OrdinalIgnoreCase)
            && !d.EndsWith(".uc.xsg.cs", StringComparison.OrdinalIgnoreCase) || d.EndsWith(".sg.cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (document.EndsWith(".xsg.cs", StringComparison.OrdinalIgnoreCase))
            {
                if (MauiXamlPage.Read(document, text, folder) is { } page)
                {
                    code.pages[page.Xaml] = page;
                    code.documents[document] = page;
                }
            }
            else if (FilePath(text) is { } relative)
            {
                code.declared.Add(PathNames.Canonical(Path.GetFullPath(Path.Combine(folder, relative))));
            }
        }
        return code;
    }

    /// <summary>The page a XAML file's code is; null with why its breakpoints cannot bind where there is none.</summary>
    public (MauiXamlPage? Page, string? Why) Of(string xaml) =>
        pages.TryGetValue(xaml, out MauiXamlPage? page) ? (page, null)
        : (null, !symbols ? $"{program}'s symbols are not beside it, so where its XAML's code runs is not known: build it again"
            : declared.Contains(xaml) ? $"{Path.GetFileName(xaml)} is not made by MAUI's XAML source generator in this build (its inflator is XamlC or Runtime), "
                + "so it has no code a breakpoint can stop in"
            : $"{Path.GetFileName(xaml)} has no code in {program}'s build: build it again, or debug the program whose project holds it");

    /// <summary>The page whose generated code a document a frame is in is, by the name the PDB gives it.</summary>
    public MauiXamlPage? OfDocument(string document) => documents.GetValueOrDefault(document);

    /// <summary>The file a code-behind's generated part names (XamlFilePath), relative to its project.</summary>
    private static string? FilePath(string text)
    {
        const string Named = "XamlFilePath(\"";
        int at = text.IndexOf(Named, StringComparison.Ordinal);
        int end = at < 0 ? -1 : text.IndexOf('"', at + Named.Length);
        return end < 0 ? null : text[(at + Named.Length)..end];
    }
}

/// <summary>One page's InitializeComponent as MAUI's source generator wrote it: where each element it makes is in the XAML
/// (from one, as MAUI records it), and each statement's line (from one, as the PDB's sequence points count) with the
/// element and the property it is of.</summary>
public sealed class MauiXamlPage
{
    private readonly Dictionary<string, (int Line, int Column)> places;
    private readonly List<(int Line, string Element, string? Owner, string? Property, bool Made)> statements;

    private MauiXamlPage(string xaml, string document, Dictionary<string, (int, int)> places, List<(int, string, string?, string?, bool)> statements)
    {
        Xaml = xaml;
        Document = document;
        this.places = places;
        this.statements = statements;
    }

    /// <summary>The XAML file, as the disk names it.</summary>
    public string Xaml { get; }

    /// <summary>The generated code, by the name the PDB gives it, which a breakpoint is sent for.</summary>
    public string Document { get; }

    public static MauiXamlPage? Read(string document, string text, string folder)
    {
        CompilationUnitSyntax root = (CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot();
        if (root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text is "InitializeComponent" or "InitializeComponentSourceGen")
            is not { Body: { } body })
        {
            return null;
        }
        Dictionary<string, (int, int)> places = [];
        string? relative = null;
        foreach (InvocationExpressionSyntax call in body.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => Called(c) == "RegisterSourceInfo"))
        {
            if (call.ArgumentList.Arguments is [var made, var uri, var line, var column] && Named(made.Expression) is { } element
                && int.TryParse(line.ToString(), out int at) && int.TryParse(column.ToString(), out int from))
            {
                places.TryAdd(element, (at, from));
                relative ??= uri.Expression.DescendantTokens().FirstOrDefault(t => t.IsKind(SyntaxKind.StringLiteralToken)).ValueText.Split(';')[0];
            }
        }
        if (relative is null)
        {
            return null;
        }
        List<(int, string, string?, string?, bool)> statements = [];
        foreach (StatementSyntax statement in body.DescendantNodes().OfType<StatementSyntax>().Where(s => s is ExpressionStatementSyntax or LocalDeclarationStatementSyntax))
        {
            if (Of(statement, places) is { } of)
            {
                statements.Add((statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1, of.Element, of.Owner, of.Property, of.Made));
            }
        }
        return new MauiXamlPage(PathNames.Canonical(Path.GetFullPath(Path.Combine(folder, relative))), document, places, statements);
    }

    /// <summary>The element a statement is of, the property it sets (by its owner where it names one, Grid.Row's), and
    /// whether it is where the element is made (MAUI records its place as it is).</summary>
    private static (string Element, string? Owner, string? Property, bool Made)? Of(StatementSyntax statement, Dictionary<string, (int, int)> places)
    {
        switch (statement)
        {
            case LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer.Value: ObjectCreationExpressionSyntax } made] }
                when places.ContainsKey(made.Identifier.Text):
                return (made.Identifier.Text, null, null, false);
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Expression: var target } left } assigned }:
                // this.Field = element: the element's; element.Property = value: its property's.
                return target is ThisExpressionSyntax
                    ? Named(assigned.Right) is { } value && places.ContainsKey(value) ? (value, null, null, false) : null
                    : Named(target) is { } setOn && places.ContainsKey(setOn) ? (setOn, null, left.Name.Identifier.Text, false) : null;
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }:
                string? method = Called(call);
                if (method == "RegisterSourceInfo")
                {
                    return call.ArgumentList.Arguments is [var made, ..] && Named(made.Expression) is { } element && places.ContainsKey(element) ? (element, null, null, true) : null;
                }
                if (method is "SetValue" or "SetBinding" or "SetDynamicResource" && call.Expression is MemberAccessExpressionSyntax { Expression: var on }
                    && Named(on) is { } set && places.ContainsKey(set) && call.ArgumentList.Arguments is [{ Expression: MemberAccessExpressionSyntax field }, ..]
                    && field.Name.Identifier.Text is { Length: > 8 } name && name.EndsWith("Property", StringComparison.Ordinal))
                {
                    string? owner = field.Expression is MemberAccessExpressionSyntax { Name: var type } ? type.Identifier.Text : (field.Expression as IdentifierNameSyntax)?.Identifier.Text;
                    return (set, owner, name[..^"Property".Length], false);
                }
                // A list's Add, a name registered, an element registered: the element it hands on.
                return call.ArgumentList.Arguments.Reverse().Select(a => Named(a.Expression)).FirstOrDefault(n => n is not null && places.ContainsKey(n)) is { } handed
                    ? (handed, null, null, false)
                    : call.Expression is MemberAccessExpressionSyntax { Expression: var receiver } && Named(receiver) is { } of && places.ContainsKey(of) ? (of, null, null, false) : null;
            default:
                return null;
        }
    }

    /// <summary>The name a call calls.</summary>
    private static string? Called(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax name => name.Identifier.Text,
        _ => null,
    };

    /// <summary>The local an expression names, through casts, parentheses and the null-forgiving operator.</summary>
    private static string? Named(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.Text,
        CastExpressionSyntax cast => Named(cast.Expression),
        ParenthesizedExpressionSyntax inner => Named(inner.Expression),
        PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } forgiven => Named(forgiven.Operand),
        _ => null,
    };

    /// <summary>The line of the code (from one) a breakpoint on a line of the XAML (from zero, as built) binds to: on an
    /// attribute (<paramref name="attribute"/>, the one at <paramref name="column"/>), where its property is set; else where
    /// the element whose name the line begins is made, or where the first property the line sets is set. Null where the
    /// line makes and sets nothing.</summary>
    public int? Bind(TextSnapshot text, int line, string? attribute, int? column)
    {
        if (line < 0 || line >= text.LineCount)
        {
            return null;
        }
        LineSpan span = text.GetLine(line);
        int? named = attribute is null ? null : XamlBreakpoints.AttributeRange(text, line, attribute, column)?.Start;
        foreach (XmlElement element in XmlTree.Parse(text.ToString()).Elements.Where(e => !e.IsPropertyElement))
        {
            if (element.Start > span.End)
            {
                break;
            }
            string? made = ElementAt(text, element);
            if (named is null && made is not null && element.NameStart >= span.Start && element.NameStart < span.End)
            {
                return statements.FirstOrDefault(s => s.Element == made && s.Made).Line is > 0 and int at ? at : null;
            }
            foreach (XmlAttribute set in element.Attributes.Where(a => named is int start ? a.NameStart == start : XamlBreakpoints.Sets(a) && a.NameStart >= span.Start && a.NameStart < span.End))
            {
                if (made is not null && statements.FirstOrDefault(s => s.Element == made && Sets(s, set.Name)) is { Line: > 0 } setting)
                {
                    return setting.Line;
                }
            }
        }
        return null;
    }

    /// <summary>Where in the XAML (from zero, as built) a line of the code (from one) is: the attribute whose property it sets,
    /// else the element it makes or is of; null for a line of no element's.</summary>
    public TextPosition? Place(TextSnapshot? text, int line)
    {
        if (statements.FirstOrDefault(s => s.Line == line) is not { Element: { } element } statement || !places.TryGetValue(element, out (int Line, int Column) at))
        {
            return null;
        }
        TextPosition made = new(at.Line - 1, Math.Max(at.Column - 1, 0));
        if (statement.Property is null || text is null)
        {
            return made;
        }
        XmlElement? owner = XmlTree.Parse(text.ToString()).Elements.FirstOrDefault(e => !e.IsPropertyElement && text.GetPosition(e.NameStart) == made);
        return owner?.Attributes.FirstOrDefault(a => Sets(statement, a.Name)) is { } set ? text.GetPosition(set.NameStart) : made;
    }

    /// <summary>The local the code makes an element in, by the place MAUI records it at.</summary>
    private string? ElementAt(TextSnapshot text, XmlElement element)
    {
        TextPosition at = text.GetPosition(element.NameStart);
        return places.FirstOrDefault(p => p.Value.Line == at.Line + 1 && p.Value.Column == at.Column + 1).Key;
    }

    /// <summary>Whether a statement sets the property an attribute names: Grid.Row by its owner, Text by its name alone.</summary>
    private static bool Sets((int Line, string Element, string? Owner, string? Property, bool Made) statement, string attribute)
    {
        if (statement.Property is null)
        {
            return false;
        }
        int dot = attribute.LastIndexOf('.');
        return dot < 0 ? statement.Property == attribute : statement.Property == attribute[(dot + 1)..] && statement.Owner == attribute[..dot];
    }
}
