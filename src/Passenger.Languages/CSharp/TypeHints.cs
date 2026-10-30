using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Languages.CSharp;

/// <summary>Type name hints from Roslyn's, read from the file's syntax as it stands: a name's type after the name
/// (<c>total:int</c>), where Roslyn puts it before, and none where the type is evident: a <c>var</c> whose initializer
/// shows it (a <c>new</c>, a cast, a literal, a factory or conversion named for it), a name that says it
/// (<c>isReady</c>, <c>index</c>, <c>customer</c>), a lambda whose delegate is declared beside it, a <c>new()</c> whose
/// type the declaration it initializes writes.</summary>
public static class TypeHints
{
    /// <summary>Words that make a static method returning its own type a factory (ReSharper's list).</summary>
    private static readonly string[] FactoryWords = ["Create", "Build", "Construct", "Make", "Generate", "Produce", "New", "Instance"];

    /// <summary>A name's first word that says it holds a bool.</summary>
    private static readonly HashSet<string> BooleanPrefixes = new(StringComparer.Ordinal)
    {
        "has", "have", "had", "is", "are", "was", "were", "can", "could", "may", "might", "must", "shall", "should", "will", "would",
    };

    private static readonly string[] IntegerEndings = ["index", "count", "position"];
    private static readonly string[] StringEndings = ["text", "message", "name", "path", "description"];

    private static readonly HashSet<string> Integers = new(StringComparer.Ordinal)
    {
        "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint", "char",
    };

    /// <summary>The hint for the type Roslyn would show at an offset (its label as Roslyn writes the type), or null
    /// where none is shown.</summary>
    public static InlayHint? Place(ParsedText syntax, int offset, string type)
    {
        SyntaxNode root = syntax.Root;
        if (offset <= 0 || offset > root.FullSpan.End || type.Length == 0)
        {
            return null;
        }
        SyntaxToken token = root.FindToken(offset);
        if (token.IsKind(SyntaxKind.IdentifierToken) && token.SpanStart == offset)
        {
            return Hidden(token, type) ? null : new InlayHint(token.Span.End, ":" + type, Kind: InlayHintKind.Type);
        }
        SyntaxToken before = root.FindToken(offset - 1);
        if (before.IsKind(SyntaxKind.NewKeyword) && before.Span.End == offset && before.Parent is ImplicitObjectCreationExpressionSyntax creation)
        {
            return TargetEvident(creation) ? null : new InlayHint(offset, type, PadLeft: true, Kind: InlayHintKind.Type);
        }
        return null;
    }

    /// <summary>Whether the type of the name a token declares is left off.</summary>
    private static bool Hidden(SyntaxToken name, string type)
    {
        string text = name.ValueText;
        if (text.Trim('_').Length == 0)
        {
            return true;
        }
        return name.Parent switch
        {
            VariableDeclaratorSyntax { Initializer.Value: { } value } => Evident(value, type) || NameSays(text, type),
            ParameterSyntax parameter => LambdaEvident(parameter) || NameSays(text, type),
            // A recursive pattern that writes its type (o is Order { } order) says it already.
            SingleVariableDesignationSyntax { Parent: RecursivePatternSyntax { Type: not null } } => true,
            _ => NameSays(text, type),
        };
    }

    /// <summary>Whether a lambda's parameter types are written beside it: it initializes a declared delegate
    /// (<c>Func&lt;int, int&gt; f = x =&gt; x</c>) or is the one argument of a delegate's creation.</summary>
    private static bool LambdaEvident(ParameterSyntax parameter)
    {
        SyntaxNode? lambda = parameter.Parent is ParameterListSyntax list ? list.Parent : parameter.Parent;
        if (lambda is not LambdaExpressionSyntax)
        {
            return false;
        }
        SyntaxNode? up = lambda.Parent;
        while (up is ParenthesizedExpressionSyntax)
        {
            up = up.Parent;
        }
        return up is EqualsValueClauseSyntax
            || (up is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: ObjectCreationExpressionSyntax } });
    }

    /// <summary>Whether the type a target-typed <c>new</c> makes is written where it stands: a field's, property's,
    /// local's or parameter's declared type it initializes, an expression-bodied member's, or an array's or
    /// collection's initializer it is an element of.</summary>
    private static bool TargetEvident(ImplicitObjectCreationExpressionSyntax creation)
    {
        SyntaxNode? up = creation.Parent;
        while (up is ParenthesizedExpressionSyntax)
        {
            up = up.Parent;
        }
        return up switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type.IsVar: false } } } => true,
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax or ParameterSyntax } => true,
            ArrowExpressionClauseSyntax { Parent: not null } => true,
            InitializerExpressionSyntax initializer => initializer.IsKind(SyntaxKind.ArrayInitializerExpression) || initializer.IsKind(SyntaxKind.CollectionInitializerExpression),
            _ => false,
        };
    }

    /// <summary>Whether an initializer shows the type it gives a <c>var</c>.</summary>
    private static bool Evident(ExpressionSyntax value, string type)
    {
        ExpressionSyntax expression = Unwrapped(value);
        switch (expression)
        {
            case ObjectCreationExpressionSyntax or CastExpressionSyntax or DefaultExpressionSyntax or ArrayCreationExpressionSyntax:
                return true;
            case BinaryExpressionSyntax binary:
                return binary.IsKind(SyntaxKind.AsExpression);
            case LiteralExpressionSyntax literal:
                return !literal.IsKind(SyntaxKind.NullLiteralExpression) && !literal.IsKind(SyntaxKind.DefaultLiteralExpression);
            case TupleExpressionSyntax tuple:
                return tuple.Arguments.All(a => Evident(a.Expression, string.Empty));
            case ImplicitArrayCreationExpressionSyntax array:
                return array.Initializer.Expressions.Count <= 42 && array.Initializer.Expressions.All(e => Evident(e, string.Empty));
            case InvocationExpressionSyntax invocation:
                return InvocationEvident(invocation, type);
            case MemberAccessExpressionSyntax member:
                // An enum's member, or a static field of the type it is on (string.Empty, int.MaxValue).
                return NamesType(member.Expression, type);
            default:
                return false;
        }
    }

    /// <summary>Whether a call shows the type it returns: a generic method given it (<c>Get&lt;Foo&gt;()</c>), a
    /// conversion named for it (<c>ToString()</c>), or a factory on the type itself (<c>Foo.Create()</c>).</summary>
    private static bool InvocationEvident(InvocationExpressionSyntax invocation, string type)
    {
        SimpleNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            SimpleNameSyntax simple => simple,
            _ => null,
        };
        if (name is null || type.Length == 0)
        {
            return false;
        }
        string bare = Bare(type);
        if (name is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Any(a => Same(a.ToString(), bare)))
        {
            return true;
        }
        string method = name.Identifier.ValueText;
        string declared = DeclaredName(bare);
        if (declared.Length > 0 && !bare.Contains('<', StringComparison.Ordinal) && method == "To" + declared)
        {
            return true;
        }
        if (invocation.Expression is MemberAccessExpressionSyntax { Expression: { } owner } && NamesType(owner, type))
        {
            string ownerName = LastName(owner);
            bool factory = method.Contains(ownerName, StringComparison.Ordinal) || FactoryWords.Any(w => method.Contains(w, StringComparison.Ordinal));
            return factory && (!bare.Contains('<', StringComparison.Ordinal) || invocation.ArgumentList.Arguments.All(a => Evident(a.Expression, string.Empty)));
        }
        return false;
    }

    /// <summary>Whether an expression is the type itself, by the name it is written with.</summary>
    private static bool NamesType(ExpressionSyntax owner, string type)
    {
        string written = LastName(owner);
        string bare = Bare(type);
        return written.Length > 0 && (written == ShortName(bare) || written == DeclaredName(bare));
    }

    private static string LastName(ExpressionSyntax expression) => expression switch
    {
        PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static ExpressionSyntax Unwrapped(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    break;
                case AssignmentExpressionSyntax assignment:
                    expression = assignment.Right;
                    break;
                default:
                    return expression;
            }
        }
    }

    /// <summary>Whether a name says the type it holds.</summary>
    private static bool NameSays(string name, string type)
    {
        string bare = Bare(type);
        List<string> parts = Parts(name);
        if (bare == "bool")
        {
            return parts.Count > 0 && BooleanPrefixes.Contains(parts[0]);
        }
        if (Integers.Contains(bare) && IntegerEndings.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (bare == "string" && StringEndings.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        string declared = DeclaredName(bare);
        if (declared.Length == 0)
        {
            return false;
        }
        if (string.Equals(declared, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        List<string> typeParts = Parts(declared);
        return Initials(name.ToLowerInvariant(), typeParts) || Shares(parts, typeParts);
    }

    /// <summary>Whether a name is the first letters of a type's words (<c>sb</c> for StringBuilder).</summary>
    private static bool Initials(string name, List<string> typeParts)
    {
        if (name.Length == 0 || typeParts.Count == 0)
        {
            return false;
        }
        if (name.Length == 1 && (typeParts.Count > 1 || typeParts[0].Length > 1))
        {
            return false;
        }
        if (name.Length > typeParts.Count)
        {
            return false;
        }
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != typeParts[i][0])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a name and a type share a word, past a first word both begin with when both have more.</summary>
    private static bool Shares(List<string> name, List<string> type)
    {
        if (name.Count == 0 || type.Count == 0)
        {
            return false;
        }
        int from = name.Count > 1 && type.Count > 1 && name[0] == type[0] ? 1 : 0;
        return name.Skip(from).Any(n => type.Skip(from).Contains(n, StringComparer.Ordinal));
    }

    /// <summary>A name's words, lower case and without the digits that end them: <c>_xmlReader2</c> is xml and reader.</summary>
    private static List<string> Parts(string name)
    {
        List<string> parts = [];
        int start = -1;
        for (int i = 0; i <= name.Length; i++)
        {
            bool end = i == name.Length || !char.IsLetterOrDigit(name[i]);
            bool hump = !end && start >= 0 && char.IsUpper(name[i])
                && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if ((end || hump) && start >= 0)
            {
                string part = name[start..i].ToLowerInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (part.Length > 0)
                {
                    parts.Add(part);
                }
                start = -1;
            }
            if (!end && start < 0)
            {
                start = i;
            }
        }
        return parts;
    }

    /// <summary>A type as written without its nullable mark: <c>string?</c> is string.</summary>
    private static string Bare(string type) => type.EndsWith('?') ? type[..^1] : type;

    /// <summary>The name a type's declaration gives it, generic arguments and namespaces left off: <c>List</c> for
    /// <c>List&lt;int&gt;</c>, <c>Int32</c> for int; empty for an array, a tuple or a pointer, which no type declares.</summary>
    private static string DeclaredName(string bare) => PrimitiveTypes.OfKeyword(bare)?.Name ?? ShortName(bare);

    private static string ShortName(string bare)
    {
        if (bare.Length == 0 || bare[0] == '(' || bare.EndsWith(']') || bare.EndsWith('*'))
        {
            return string.Empty;
        }
        int generic = bare.IndexOf('<', StringComparison.Ordinal);
        string name = generic >= 0 ? bare[..generic] : bare;
        int dot = name.LastIndexOfAny(['.', ':']);
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private static bool Same(string written, string type) =>
        string.Equals(written.Replace(" ", string.Empty, StringComparison.Ordinal), type.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
}
