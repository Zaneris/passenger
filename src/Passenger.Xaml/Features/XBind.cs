using Microsoft.CodeAnalysis.CSharp;
using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>Where an <c>{x:Bind}</c> starts: the type its path is read from, and whether that is the file's own class,
/// whose every member its compiled code reaches (a data template's <c>x:DataType</c> shows only what is not private).</summary>
public sealed record XBindRoot(ClrType Type, bool Own);

/// <summary>WinUI's <c>{x:Bind}</c>, compiled into the page's own class: its path is read from the page (its fields its
/// <c>x:Name</c>d elements) or, in a data template, from the template's <c>x:DataType</c>; each step a property, field or
/// method of the one before, a call's arguments paths of their own from the same start or constants, a cast
/// (<c>((local:Shop)Item).Name</c>) the type of what it holds, an indexer the item its collection holds, and a static
/// member of a type named with its prefix (<c>local:Format.Money(Price)</c>). The one reading of such a path for the check,
/// Go to Declaration, a symbol's uses and completion, given as a <see cref="BindingPath"/>.</summary>
public static class XBind
{
    /// <summary>Whether an extension of a file is its <c>{x:Bind}</c>.</summary>
    public static bool Is(XamlFile file, XmlElement element, MarkupExtension extension) =>
        file.Dialect.LanguageExtensions.Contains(WinUIDialect.Bind) && extension.LocalName == WinUIDialect.Bind && extension.Prefix.Length > 0
        && file.KindOf(file.UriOf(element, extension.Prefix)) == XamlNamespaceKind.Language;

    /// <summary>The path an <c>{x:Bind}</c> is given, positional or as <c>Path=</c>; null for none written.</summary>
    public static MarkupArgument? PathOf(MarkupExtension bind) => bind.Argument(null) ?? bind.Argument("Path");

    /// <summary>The modes x:Bind takes (<c>Mode=</c>, <c>x:DefaultBindMode</c>): WinUI's <c>BindingMode</c>'s members.</summary>
    public static IReadOnlyList<string> Modes(XamlFile file) =>
        file.Types.Find("Microsoft.UI.Xaml.Data.BindingMode") is { Kind: ClrTypeKind.Enum } modes
            ? [.. modes.Members.Where(m => m.Kind == MemberKind.Field && m.IsStatic).Select(m => m.Name)]
            : [];

    /// <summary>Where an <c>{x:Bind}</c> at an element starts: the nearest data template's <c>x:DataType</c>, else the file's
    /// class; null with why the compiler refuses one there (a data template with no <c>x:DataType</c>, a file with no class).</summary>
    public static (XBindRoot? Root, string? Why) RootAt(XamlFile file, XmlElement element)
    {
        for (XmlElement? at = element; at is not null; at = at.Parent)
        {
            if (!at.IsPropertyElement && at.LocalName == "DataTemplate")
            {
                if (XamlFile.Directive(at, "DataType") is not { Value.Length: > 0 } named)
                {
                    return (null, "x:Bind in a data template needs the template's x:DataType");
                }
                return file.TypeFromValue(at, named.Value) is { } type ? (new XBindRoot(type, Own: false), null) : (null, null);
            }
        }
        if (file.ClassName is null)
        {
            return (null, "x:Bind needs the file's x:Class, whose code it is compiled into");
        }
        return file.ClassType is { } page ? (new XBindRoot(page, Own: true), null) : (null, null);
    }

    /// <summary>Reads <paramref name="path"/>, written at <paramref name="at"/> of the file, from <paramref name="root"/>,
    /// names in it resolved where <paramref name="scope"/> is.</summary>
    public static BindingPath Read(XamlFile file, XmlElement scope, XBindRoot root, string path, int at) => new Reader(file, scope, root, path, at).Whole();

    /// <summary>The members of a type a path's step may name from it: from the file's own class every instance member,
    /// else those not private; a type named for its statics, its static ones.</summary>
    public static IEnumerable<ClrMember> MembersOf(XamlFile file, ClrType type, bool own, bool statics = false) =>
        file.Types.MembersOf(type).Where(m => m.Kind is MemberKind.Property or MemberKind.Field or MemberKind.Method && m.IsStatic == statics && (own || m.IsPublic)
            && !m.Name.Contains('.', StringComparison.Ordinal));

    /// <summary>What completion offers where a path is being typed (<paramref name="typed"/>, from the path's start to the
    /// caret): the members of what the step before reaches, or at a step that starts a path (the path's own start, a call's
    /// argument) the start's members and the file's named elements. Null where nothing before the caret reads whole; with
    /// where the step being typed starts in <paramref name="typed"/>.</summary>
    public static (IReadOnlyList<ClrMember> Members, bool Named, int Start)? Offered(XamlFile file, XmlElement scope, XBindRoot root, string typed, int at)
    {
        int start = typed.Length;
        while (start > 0 && SyntaxFacts.IsIdentifierPartCharacter(typed[start - 1]))
        {
            start--;
        }
        if (start == 0 || typed[start - 1] is '(' or ',' || char.IsWhiteSpace(typed[start - 1]))
        {
            return (MembersOf(file, root.Type, root.Own).ToList(), true, start);
        }
        if (typed[start - 1] != '.')
        {
            return null;
        }
        int chain = ChainStart(typed, start - 1);
        string before = typed[chain..(start - 1)];
        // A static member of a type named with its prefix (local:Format.).
        if (before.Contains(':', StringComparison.Ordinal) && !before.Contains('(', StringComparison.Ordinal) && file.ResolveType(scope, before) is { } owner)
        {
            return (MembersOf(file, owner, own: true, statics: true).ToList(), false, start);
        }
        BindingPath read = Read(file, scope, root, before, at + chain);
        return read.Reached(file.Types) is { } reached ? (MembersOf(file, reached, own: root.Own && read.Steps.Count == 0).ToList(), false, start) : null;
    }

    /// <summary>Where the chain of steps ending before <paramref name="end"/> starts: after the call's '(' or ',' it is an
    /// argument of, a bracketed part (a cast, a call's arguments, an index) passed over whole.</summary>
    private static int ChainStart(string text, int end)
    {
        int at = end;
        while (at > 0)
        {
            char c = text[at - 1];
            if (c is ')' or ']')
            {
                char open = c == ')' ? '(' : '[';
                int depth = 0;
                while (at > 0)
                {
                    at--;
                    if (text[at] == c)
                    {
                        depth++;
                    }
                    else if (text[at] == open && --depth == 0)
                    {
                        break;
                    }
                }
                continue;
            }
            if (c is '(' or ',' || char.IsWhiteSpace(c))
            {
                break;
            }
            at--;
        }
        return at;
    }

    /// <summary>Reads one path, the steps of its calls' arguments among its own, as the compiler reads x:Bind's.</summary>
    private sealed class Reader(XamlFile file, XmlElement scope, XBindRoot root, string text, int offset)
    {
        private readonly List<PathStep> steps = [];
        private int at;

        /// <summary>The path read to its end, or to the first step it does not follow.</summary>
        public BindingPath Whole()
        {
            Skip();
            if (at >= text.Length)
            {
                return new BindingPath(steps, PathEnd.Whole) { Type = TypeRef.Named(root.Type.FullName) };
            }
            (PathEnd end, TypeRef? type, PathStep? stop, bool last) = Chain();
            Skip();
            if (end == PathEnd.Whole && at < text.Length)
            {
                end = PathEnd.Unread;
            }
            return new BindingPath(steps, end) { Type = end == PathEnd.Whole ? type : null, Stop = stop, StopsLast = last };
        }

        /// <summary>A chain of steps from the start, a static type or a part in brackets, then <c>.</c>, <c>[…]</c> and
        /// calls: how far it read, the type it reached, and the step it stopped at (and whether nothing follows it).</summary>
        private (PathEnd End, TypeRef? Type, PathStep? Stop, bool Last) Chain()
        {
            if (!StackLeft.Enough())
            {
                return (PathEnd.Unread, null, null, false);
            }
            TypeRef? current;
            bool own = root.Own;
            Skip();
            if (Peek('('))
            {
                at++;
                Skip();
                if (Peek('('))
                {
                    // A part in brackets (((local:Shop)Item).Name).
                    (PathEnd End, TypeRef? Type, PathStep? Stop, bool Last) inner = Chain();
                    Skip();
                    if (inner.End != PathEnd.Whole)
                    {
                        return inner;
                    }
                    if (!Peek(')'))
                    {
                        return (PathEnd.Unread, null, null, false);
                    }
                    at++;
                    current = inner.Type;
                }
                else
                {
                    // A cast of what follows ((local:Shop)Item).
                    int close = text.IndexOf(')', at);
                    if (close < 0)
                    {
                        return (PathEnd.Unread, null, null, false);
                    }
                    string written = text[at..close];
                    string named = written.Trim();
                    PathStep step = new(new TextRange(offset + at + (written.Length - written.TrimStart().Length), named.Length), named, root.Type);
                    at = close + 1;
                    if (file.ResolveType(scope, named) is not { } cast)
                    {
                        return (PathEnd.UnknownCast, null, step, false);
                    }
                    (PathEnd End, TypeRef? Type, PathStep? Stop, bool Last) of = Chain();
                    if (of.End != PathEnd.Whole)
                    {
                        return of;
                    }
                    steps.Add(step with { Cast = cast });
                    return (PathEnd.Whole, TypeRef.Named(cast.FullName), null, false);
                }
            }
            else
            {
                (string name, int nameAt) = Name();
                int dot = name.LastIndexOf('.');
                if (name.Length == 0 || (name.Contains(':', StringComparison.Ordinal) && dot < 0))
                {
                    return (PathEnd.Unread, null, null, false);
                }
                // A static member of a type named with its prefix (local:Format.Money), else a member of the start.
                ClrType? owner = dot < 0 ? root.Type : file.ResolveType(scope, name[..dot]);
                if (owner is null)
                {
                    return (PathEnd.Unread, null, null, false);
                }
                (PathEnd End, TypeRef? Type, PathStep? Stop, bool Last) first = Step(owner, name[(dot + 1)..], nameAt + dot + 1, own && dot < 0, statics: dot >= 0);
                if (first.End != PathEnd.Whole)
                {
                    return first;
                }
                current = first.Type;
            }
            while (true)
            {
                Skip();
                if (current is null)
                {
                    return (PathEnd.Unread, null, null, false);
                }
                if (Peek('['))
                {
                    int close = text.IndexOf(']', at);
                    if (close < 0 || BindingPath.ItemOf(file.Types, current) is not { } item)
                    {
                        return (PathEnd.Unread, null, null, false);
                    }
                    current = item;
                    at = close + 1;
                    continue;
                }
                if (!Peek('.'))
                {
                    return (PathEnd.Whole, current, null, false);
                }
                at++;
                Skip();
                // An attached property of what comes before (Item.(Grid.Row)) is left to the compiler.
                if (Resolve(current) is not { } next || next.FullName == "System.Object" || Peek('('))
                {
                    return (PathEnd.Unread, null, null, false);
                }
                (string name, int nameAt) = Name();
                if (name.Length == 0 || name.Contains(':', StringComparison.Ordinal))
                {
                    return (PathEnd.Unread, null, null, false);
                }
                (PathEnd End, TypeRef? Type, PathStep? Stop, bool Last) step = Step(next, name, nameAt, own: false, statics: false);
                if (step.End != PathEnd.Whole)
                {
                    return step;
                }
                current = step.Type;
            }
        }

        /// <summary>A step named on a type, then a call's arguments: the member, its type the next step's.</summary>
        private (PathEnd End, TypeRef? Type, PathStep? Stop, bool Last) Step(ClrType on, string name, int nameAt, bool own, bool statics)
        {
            PathStep written = new(new TextRange(offset + nameAt, name.Length), name, on);
            ClrMember? member = MembersOf(file, on, own, statics).FirstOrDefault(m => m.Name == name);
            Skip();
            bool call = Peek('(');
            if (member is null && own && !statics && !call && file.Names.FirstOrDefault(n => n.Name == name).Attribute is { } element)
            {
                // An element the file names, of which its compiler makes a field of the class.
                steps.Add(written with { Named = element });
                return (PathEnd.Whole, file.ObjectOf(element.Owner) is { } made ? TypeRef.Named(made.FullName) : null, null, false);
            }
            if (member is null)
            {
                // A library's methods are not read, nor an interface's inherited members.
                return (call && !on.IsFromSource) || on.Kind == ClrTypeKind.Interface
                    ? (PathEnd.Unread, null, null, false)
                    : (PathEnd.Missing, null, written, !call && text[at..].Trim().Length == 0);
            }
            steps.Add(written with { Member = member });
            if (call)
            {
                at++;
                if (Arguments() is { } stopped)
                {
                    return stopped;
                }
            }
            return (PathEnd.Whole, member.Type, null, false);
        }

        /// <summary>A call's arguments to its ')', each a path from the start or a constant: null where they read whole, else
        /// where one stopped (an argument not closed is not read).</summary>
        private (PathEnd End, TypeRef? Type, PathStep? Stop, bool Last)? Arguments()
        {
            while (true)
            {
                Skip();
                if (at >= text.Length)
                {
                    return (PathEnd.Unread, null, null, false);
                }
                switch (text[at])
                {
                    case ')':
                        at++;
                        return null;
                    case ',':
                        at++;
                        continue;
                    case '\'' or '"':
                        int close = text.IndexOf(text[at], at + 1);
                        if (close < 0)
                        {
                            return (PathEnd.Unread, null, null, false);
                        }
                        at = close + 1;
                        continue;
                }
                if (char.IsDigit(text[at]) || text[at] == '-' || IsConstant())
                {
                    while (at < text.Length && text[at] is not (',' or ')'))
                    {
                        at++;
                    }
                    continue;
                }
                (PathEnd End, TypeRef? Type, PathStep? Stop, bool Last) argument = Chain();
                if (argument.End != PathEnd.Whole)
                {
                    return argument with { Last = false };
                }
            }
        }

        /// <summary>Whether a constant of XAML's language stands next (<c>x:True</c>, <c>x:False</c>, <c>x:Null</c>).</summary>
        private bool IsConstant()
        {
            int colon = text.IndexOf(':', at);
            return colon > at && text[at..colon].All(SyntaxFacts.IsIdentifierPartCharacter)
                && XamlDialect.IsLanguageUri(file.UriOf(scope, text[at..colon])) && text[(colon + 1)..] is var rest
                && (rest.StartsWith("True", StringComparison.Ordinal) || rest.StartsWith("False", StringComparison.Ordinal) || rest.StartsWith("Null", StringComparison.Ordinal));
        }

        /// <summary>A name: a C# identifier, or a type named with its prefix and a member of it (local:Format.Money).</summary>
        private (string Name, int At) Name()
        {
            int start = at;
            bool prefixed = false;
            while (at < text.Length)
            {
                char c = text[at];
                if (SyntaxFacts.IsIdentifierPartCharacter(c) || (c == ':' && at > start && !prefixed) || (c == '.' && prefixed))
                {
                    prefixed |= c == ':';
                    at++;
                    continue;
                }
                break;
            }
            return (text[start..at], start);
        }

        private bool Peek(char c) => at < text.Length && text[at] == c;

        private void Skip()
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }
        }

        private ClrType? Resolve(TypeRef type) => type.IsArray ? file.Types.Find("System.Array") : file.Types.Resolve(type);
    }
}
