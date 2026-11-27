using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.App.Debugging;

/// <summary>A .NET MAUI program's XAML as the code MAUI's XAML source generator wrote of each page (its .xsg.cs), read from
/// the build's PDB, which embeds it as the program runs it, and from each Hot Reload update's code as the program takes it:
/// each statement of a page's InitializeComponent, and of the patch an update runs on its open pages (UpdateComponent), read
/// for the element it makes, at the place MAUI records for it (RegisterSourceInfo), or the property it sets there, so a
/// breakpoint on an element's line binds where that element is made, one on an attribute where its property is set, and a
/// stop in that code is shown at the XAML. A page the generator made no code of says why its breakpoints cannot bind. Never
/// changed once made, so any thread may read it.</summary>
public sealed class MauiXamlCode
{
    private readonly string program;
    private readonly Dictionary<string, MauiXamlPage> pages;
    private readonly Dictionary<string, MauiXamlPage> documents;
    private readonly HashSet<string> declared;
    private readonly bool symbols;

    private MauiXamlCode(string program, bool symbols, Dictionary<string, MauiXamlPage> pages, Dictionary<string, MauiXamlPage> documents, HashSet<string> declared)
    {
        this.program = program;
        this.symbols = symbols;
        this.pages = pages;
        this.documents = documents;
        this.declared = declared;
    }

    /// <summary>The code of each XAML file of the program built as <paramref name="assembly"/> (its PDB beside it), the
    /// files named from <paramref name="folder"/>, its project's, as MAUI's build names them; read off the window's thread.</summary>
    public static MauiXamlCode Read(string assembly, string folder)
    {
        string pdb = Path.ChangeExtension(assembly, ".pdb");
        MauiXamlCode code = new(Path.GetFileName(assembly), File.Exists(pdb), new(PathNames.Comparer), new(PathNames.Comparer), new(PathNames.Comparer));
        foreach ((string document, string text) in PortablePdbs.EmbeddedSources(pdb, d => d.EndsWith(".xsg.cs", StringComparison.OrdinalIgnoreCase)
            && !d.EndsWith(".uc.xsg.cs", StringComparison.OrdinalIgnoreCase) || d.EndsWith(".sg.cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (document.EndsWith(".xsg.cs", StringComparison.OrdinalIgnoreCase))
            {
                if (MauiXamlPage.Read([(document, text)], null, folder) is { } page)
                {
                    code.Add(page);
                }
            }
            else if (FilePath(text) is { } relative)
            {
                code.declared.Add(PathNames.Canonical(Path.GetFullPath(Path.Combine(folder, relative))));
            }
        }
        return code;
    }

    /// <summary>The code as the program runs it once it took an update of <paramref name="generated"/>, the code MAUI's
    /// generator wrote of its XAML files for it: each file's page read from it in place of the one before; read off the
    /// window's thread.</summary>
    public MauiXamlCode Updated(IReadOnlyList<(string Markup, string Document, string Text)> generated)
    {
        MauiXamlCode now = new(program, symbols, new(pages, PathNames.Comparer), new(documents, PathNames.Comparer), declared);
        foreach (IGrouping<string, (string Markup, string Document, string Text)> file in generated.GroupBy(g => g.Markup, PathNames.Comparer))
        {
            if (MauiXamlPage.Read([.. file.Select(g => (g.Document, g.Text))], file.Key, string.Empty) is { } page)
            {
                now.Add(page);
            }
        }
        return now;
    }

    private void Add(MauiXamlPage page)
    {
        pages[page.Xaml] = page;
        foreach (string document in page.Documents)
        {
            documents[document] = page;
        }
    }

    /// <summary>The page a XAML file's code is; null with why its breakpoints cannot bind where there is none.</summary>
    public (MauiXamlPage? Page, string? Why) Of(string xaml) =>
        pages.TryGetValue(xaml, out MauiXamlPage? page) ? (page, null)
        : (null, !symbols ? $"{program}'s symbols are not beside it, so where its XAML's code runs is not known: build it again"
            : declared.Contains(xaml) ? $"{Path.GetFileName(xaml)} is not made by MAUI's XAML source generator in this build (its inflator is XamlC or Runtime), "
                + "so it has no code a breakpoint can stop in"
            : $"{Path.GetFileName(xaml)} has no code in {program}'s build: build it again, or debug the program whose project holds it");

    /// <summary>The page whose code a document a frame is in is, by the name the PDB gives it: the generated code's, or the
    /// XAML file's its #line directives name.</summary>
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

/// <summary>One page's code as MAUI's source generator wrote it: its InitializeComponent, and the patch the last update ran
/// on its open pages where there is one; where each element either makes is in the XAML (from one, as MAUI records it, a
/// patch's by the id it registers the element under), and each statement's place in the code (from one, as the PDB's
/// sequence points count, in the XAML itself where a #line directive maps it there) with the element and the property it
/// is of.</summary>
public sealed class MauiXamlPage
{
    /// <summary>Where each local of the code (by its document) holds an element made at, in the XAML as built.</summary>
    private readonly Dictionary<(string Code, string Local), (int Line, int Column)> places;

    /// <summary>The type each local of the code holds an element of, by the name it is made by; none for one made of no type
    /// the code names (the root, this).</summary>
    private readonly Dictionary<(string Code, string Local), string> types;

    private readonly List<Statement> statements;

    /// <summary>Each document a statement is placed in, by each name a frame may give it, to the name the PDB records.</summary>
    private readonly Dictionary<string, string> named;

    /// <summary>A statement of the code (<see cref="Code"/>, whose locals it names), placed in <see cref="Document"/> (the
    /// code's, or the XAML a #line maps it to) at a line and column from one, with the element it is of, the property it
    /// sets (by its owner where it names one, Grid.Row's), and whether it is where the element is made.</summary>
    private readonly record struct Statement(string Code, string Document, int Line, int Column, string Element, string? Owner, string? Property, bool Made);

    private MauiXamlPage(string xaml, Dictionary<(string, string), (int, int)> places, Dictionary<(string, string), string> types, List<Statement> statements,
        Dictionary<string, string> named)
    {
        Xaml = xaml;
        this.places = places;
        this.types = types;
        this.statements = statements;
        this.named = named;
    }

    /// <summary>The XAML file, as the disk names it.</summary>
    public string Xaml { get; }

    /// <summary>Every name of a document its code is placed in.</summary>
    public IEnumerable<string> Documents => named.Keys;

    /// <summary>A page read from its generated documents, its XAML file <paramref name="xaml"/> where known, else named from
    /// <paramref name="folder"/> by the place MAUI records; null where none holds an InitializeComponent placing its elements.</summary>
    public static MauiXamlPage? Read(IReadOnlyList<(string Document, string Text)> code, string? xaml, string folder)
    {
        Dictionary<(string, string), (int, int)> places = [];
        Dictionary<(string, string), string> types = [];
        List<Statement> statements = [];
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        Dictionary<string, string> named = new(PathNames.Comparer);
        string? relative = null;
        List<(string Document, BlockSyntax Body)> patches = [];
        foreach ((string document, string text) in code)
        {
            SyntaxTree tree = CSharpParsing.Parse(text);
            List<MethodDeclarationSyntax> methods = [.. tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()];
            if (methods.FirstOrDefault(m => m.Identifier.Text is "UpdateComponent") is { Body: { } patch })
            {
                patches.Add((document, patch));
                continue;
            }
            if (methods.FirstOrDefault(m => m.Identifier.Text is "InitializeComponent" or "InitializeComponentSourceGen") is not { Body: { } body })
            {
                continue;
            }
            foreach (InvocationExpressionSyntax call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (Called(call) == "RegisterSourceInfo" && call.ArgumentList.Arguments is [var made, var uri, var line, var column] && Named(made.Expression) is { } element
                    && int.TryParse(line.ToString(), out int at) && int.TryParse(column.ToString(), out int from))
                {
                    places.TryAdd((document, element), (at, from));
                    relative ??= uri.Expression.DescendantTokens().FirstOrDefault(t => t.IsKind(SyntaxKind.StringLiteralToken)).ValueText.Split(';')[0];
                }
                else if (Registered(call) is { } registered)
                {
                    ids.TryAdd(registered.Id, registered.Local);
                }
            }
            foreach ((string local, string type) in Made(body))
            {
                types.TryAdd((document, local), type);
            }
            Take(document, body, places, statements, named, _ => false);
        }
        if (relative is null)
        {
            return null;
        }
        string? initialized = places.Keys.Select(k => k.Item1).FirstOrDefault();
        foreach ((string document, BlockSyntax patch) in patches)
        {
            // A patch's locals are the elements it registers or finds under the ids InitializeComponent registers them under.
            Dictionary<string, string> made = Made(patch).ToDictionary(m => m.Local, m => m.Type);
            foreach ((string local, string id) in PatchIds(patch))
            {
                if (initialized is not null && ids.TryGetValue(id, out string? of) && places.TryGetValue((initialized, of), out (int, int) at))
                {
                    places.TryAdd((document, local), at);
                    if ((made.GetValueOrDefault(local) ?? types.GetValueOrDefault((initialized, of))) is { } type)
                    {
                        types.TryAdd((document, local), type);
                    }
                }
            }
            Take(document, patch, places, statements, named, made.ContainsKey);
        }
        return new MauiXamlPage(PathNames.Canonical(Path.GetFullPath(xaml ?? Path.Combine(folder, relative))), places, types, statements, named);
    }

    /// <summary>Each local a body of code makes an element in, with the name of the type it makes it of.</summary>
    private static IEnumerable<(string Local, string Type)> Made(BlockSyntax body) =>
        body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value is ObjectCreationExpressionSyntax { Type: var type } && Unqualified(type) is not null)
            .Select(v => (v.Identifier.Text, Unqualified(((ObjectCreationExpressionSyntax)v.Initializer!.Value).Type)!));

    /// <summary>A type's name without its namespace and type arguments, as XAML names the element of it.</summary>
    private static string? Unqualified(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => Unqualified(qualified.Right),
        AliasQualifiedNameSyntax aliased => Unqualified(aliased.Name),
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => null,
    };

    /// <summary>The statements of a body of code that are of its elements, each where the PDB places it; a patch's element
    /// is made where it is registered once made (<paramref name="madeHere"/>), as no place is recorded for it there.</summary>
    private static void Take(string document, BlockSyntax body, Dictionary<(string, string), (int, int)> places, List<Statement> statements, Dictionary<string, string> named,
        Func<string, bool> madeHere)
    {
        named.TryAdd(document, document);
        named.TryAdd(PathNames.Canonical(document), document);
        SyntaxTree tree = body.SyntaxTree;
        foreach (StatementSyntax statement in body.DescendantNodes().OfType<StatementSyntax>().Where(s => s is ExpressionStatementSyntax or LocalDeclarationStatementSyntax))
        {
            if (tree.GetLineVisibility(statement.SpanStart) == LineVisibility.Hidden || Of(statement, local => places.ContainsKey((document, local)), madeHere) is not { } of)
            {
                continue;
            }
            FileLinePositionSpan at = tree.GetMappedLineSpan(statement.Span);
            string placed = at.HasMappedPath ? at.Path : document;
            if (at.HasMappedPath && !named.ContainsKey(placed))
            {
                named[placed] = placed;
                named.TryAdd(PathNames.Canonical(placed), placed);
            }
            statements.Add(new Statement(document, placed, at.StartLinePosition.Line + 1, at.StartLinePosition.Character + 1, of.Element, of.Owner, of.Property, of.Made));
        }
    }

    /// <summary>The element a statement is of, the property it sets (by its owner where it names one, Grid.Row's), and
    /// whether it is where the element is made (MAUI records its place as it is, or a patch registers it once made).</summary>
    private static (string Element, string? Owner, string? Property, bool Made)? Of(StatementSyntax statement, Func<string, bool> known, Func<string, bool> madeHere)
    {
        switch (statement)
        {
            case LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer.Value: ObjectCreationExpressionSyntax } made] }
                when known(made.Identifier.Text):
                return (made.Identifier.Text, null, null, false);
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Expression: var target } left } assigned }:
                // this.Field = element: the element's; element.Property = value: its property's.
                return target is ThisExpressionSyntax
                    ? Named(assigned.Right) is { } value && known(value) ? (value, null, null, false) : null
                    : Named(target) is { } setOn && known(setOn) ? (setOn, null, left.Name.Identifier.Text, false) : null;
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }:
                string? method = Called(call);
                if (method == "RegisterSourceInfo")
                {
                    return call.ArgumentList.Arguments is [var made, ..] && Named(made.Expression) is { } element && known(element) ? (element, null, null, true) : null;
                }
                if (Registered(call) is { } registered && known(registered.Local) && madeHere(registered.Local))
                {
                    return (registered.Local, null, null, true);
                }
                if (method is "SetValue" or "SetBinding" or "SetDynamicResource" && call.Expression is MemberAccessExpressionSyntax { Expression: var on }
                    && Named(on) is { } set && known(set) && call.ArgumentList.Arguments is [{ Expression: MemberAccessExpressionSyntax field }, ..]
                    && field.Name.Identifier.Text is { Length: > 8 } name && name.EndsWith("Property", StringComparison.Ordinal))
                {
                    string? owner = field.Expression is MemberAccessExpressionSyntax { Name: var type } ? type.Identifier.Text : (field.Expression as IdentifierNameSyntax)?.Identifier.Text;
                    return (set, owner, name[..^"Property".Length], false);
                }
                // A list's Add, a name registered, an element registered: the element it hands on.
                return call.ArgumentList.Arguments.Reverse().Select(a => Named(a.Expression)).FirstOrDefault(n => n is not null && known(n)) is { } handed
                    ? (handed, null, null, false)
                    : call.Expression is MemberAccessExpressionSyntax { Expression: var receiver } && Named(receiver) is { } of && known(of) ? (of, null, null, false) : null;
            default:
                return null;
        }
    }

    /// <summary>An element registered for Hot Reload's patches (XamlComponentRegistry.Register(this, "id", element)): its id
    /// and the local it is in.</summary>
    private static (string Id, string Local)? Registered(InvocationExpressionSyntax call) =>
        Called(call) == "Register" && call.ArgumentList.Arguments is [{ Expression: ThisExpressionSyntax }, { Expression: LiteralExpressionSyntax id }, var element]
            && id.IsKind(SyntaxKind.StringLiteralExpression) && Named(element.Expression) is { } local
            ? (id.Token.ValueText, local)
            : null;

    /// <summary>The locals a patch holds elements in, each with the id it registers or finds its element under: one it made
    /// and registered, one it found (TryGet's out variable), and one of each of a template's (GetTemplateComponents).</summary>
    private static IEnumerable<(string Local, string Id)> PatchIds(BlockSyntax patch)
    {
        foreach (SyntaxNode node in patch.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax call when Registered(call) is { } registered:
                    yield return (registered.Local, registered.Id);
                    break;
                case InvocationExpressionSyntax call when Called(call) == "TryGet"
                    && call.ArgumentList.Arguments is [{ Expression: ThisExpressionSyntax }, { Expression: LiteralExpressionSyntax id }, { Expression: DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax found } }]:
                    yield return (found.Identifier.Text, id.Token.ValueText);
                    break;
                case ForEachStatementSyntax { Expression: InvocationExpressionSyntax call } each when Called(call) == "GetTemplateComponents"
                    && call.ArgumentList.Arguments is [{ Expression: ThisExpressionSyntax }, { Expression: LiteralExpressionSyntax id }]:
                    yield return (each.Identifier.Text, id.Token.ValueText);
                    break;
            }
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

    /// <summary>Where in the code a breakpoint on a line of the editor's XAML (from zero) binds, by each document it is placed
    /// in (the line from one): on an attribute (the one at <paramref name="column"/>), where its property is set; else where
    /// the element the line begins is made, or the line's first property set. <paramref name="built"/> gives a place of the
    /// text as built, null for one written since. None where the line makes and sets nothing; null where its element was
    /// written since, at the place of another the code makes.</summary>
    public IReadOnlyList<(string Document, int Line)>? Bind(TextSnapshot text, int line, string? attribute, int? column, Func<TextPosition, TextPosition?> built)
    {
        if (line < 0 || line >= text.LineCount)
        {
            return [];
        }
        LineSpan span = text.GetLine(line);
        int? named = attribute is null ? null : XamlBreakpoints.AttributeRange(text, line, attribute, column)?.Start;
        foreach (XmlElement element in XmlTree.Parse(text.ToString()).Elements.Where(e => !e.IsPropertyElement))
        {
            if (element.Start > span.End)
            {
                break;
            }
            (HashSet<(string, string)> made, bool other) = Holding(text, element, built);
            bool on = named is int asked ? element.Attributes.Any(a => a.NameStart == asked) : element.NameStart >= span.Start && element.NameStart < span.End;
            if (on && made.Count == 0 && other)
            {
                return null;
            }
            if (named is null && made.Count > 0 && element.NameStart >= span.Start && element.NameStart < span.End)
            {
                return Bound(statements.Where(s => s.Made && made.Contains((s.Code, s.Element))));
            }
            foreach (XmlAttribute set in element.Attributes.Where(a => named is int start ? a.NameStart == start : XamlBreakpoints.Sets(a) && a.NameStart >= span.Start && a.NameStart < span.End))
            {
                if (Bound(statements.Where(s => made.Contains((s.Code, s.Element)) && Sets(s, set.Name))) is { Count: > 0 } setting)
                {
                    return setting;
                }
            }
        }
        return [];
    }

    /// <summary>The first of the statements placed in each document, where a breakpoint at it is sent.</summary>
    private static List<(string Document, int Line)> Bound(IEnumerable<Statement> found) => [.. found.GroupBy(s => s.Document, StringComparer.Ordinal).Select(g => (g.Key, g.First().Line))];

    /// <summary>Where in the XAML (from zero, as built) a place in the code is (a frame's, by its document, line and column
    /// from one): the attribute whose property its statement sets, else the element it makes or is of; null for a place of
    /// no element's. <paramref name="built"/> gives a place of the editor's <paramref name="text"/> as built.</summary>
    public TextPosition? Place(TextSnapshot? text, string document, int line, int column, Func<TextPosition, TextPosition?> built)
    {
        if (!named.TryGetValue(document, out string? recorded))
        {
            return null;
        }
        List<Statement> there = [.. statements.Where(s => s.Document == recorded && s.Line == line)];
        if (there.Count == 0)
        {
            return null;
        }
        // Statements a #line directive maps to one line of the XAML are told apart by the column the code writes each at.
        Statement statement = there.Find(s => s.Column == column) is { Element: not null } exact ? exact : there[0];
        if (!places.TryGetValue((statement.Code, statement.Element), out (int Line, int Column) at))
        {
            return null;
        }
        TextPosition made = new(at.Line - 1, Math.Max(at.Column - 1, 0));
        if (statement.Property is null || text is null)
        {
            return made;
        }
        XmlElement? owner = XmlTree.Parse(text.ToString()).Elements.FirstOrDefault(e => !e.IsPropertyElement && built(text.GetPosition(e.NameStart)) == made
            && Holding(text, e, built).Locals.Contains((statement.Code, statement.Element)));
        return owner?.Attributes.FirstOrDefault(a => Sets(statement, a.Name)) is { } set && built(text.GetPosition(set.NameStart)) is { } placed ? placed : made;
    }

    /// <summary>The locals of the code that hold the element an element of the editor's text is, by the place MAUI records it
    /// at and the type it names; and whether the code makes another type's element there, so this one was written since.</summary>
    private (HashSet<(string, string)> Locals, bool Other) Holding(TextSnapshot text, XmlElement element, Func<TextPosition, TextPosition?> built)
    {
        if (built(text.GetPosition(element.NameStart)) is not { } at)
        {
            return ([], false);
        }
        List<(string, string)> there = [.. places.Where(p => p.Value.Line == at.Line + 1 && p.Value.Column == at.Column + 1).Select(p => p.Key)];
        HashSet<(string, string)> held = [.. there.Where(k => !types.TryGetValue(k, out string? type) || type == element.LocalName || type == element.LocalName + "Extension")];
        return (held, held.Count < there.Count && held.Count == 0);
    }

    /// <summary>Whether a statement sets the property an attribute names: Grid.Row by its owner, Text by its name alone.</summary>
    private static bool Sets(Statement statement, string attribute)
    {
        if (statement.Property is null)
        {
            return false;
        }
        int dot = attribute.LastIndexOf('.');
        return dot < 0 ? statement.Property == attribute : statement.Property == attribute[(dot + 1)..] && statement.Owner == attribute[..dot];
    }
}
