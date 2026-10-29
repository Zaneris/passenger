using Passenger.Text;
using Passenger.Xaml.Metadata;

namespace Passenger.Xaml.Features;

/// <summary>A step of a binding's path where it is written: a member of the type it is read on, or the type a cast
/// names.</summary>
public sealed record PathStep(TextRange Range, string Name, ClrType On, ClrMember? Member = null, ClrType? Cast = null);

/// <summary>How far a binding's path was read.</summary>
public enum PathEnd
{
    /// <summary>To its end, every step resolved.</summary>
    Whole,

    /// <summary>To a step the type before it does not have.</summary>
    Missing,

    /// <summary>To a cast whose type nothing resolves.</summary>
    UnknownCast,

    /// <summary>To a step it does not follow (an element, a relative source, an attached property, a stream, an indexer
    /// of a type it cannot read), left to the compiler.</summary>
    Unread,
}

/// <summary>A binding's path read from the type it starts at, as the compiler reads it: each step a property (or a
/// method) of the one before, an indexer the item its collection holds, and where bindings compile <c>?.</c> a step too
/// and a cast (<c>((vm:Shop)Item)</c>, or <c>(vm:Shop)</c> of what comes before) the next step's type. The one reading
/// of a path for the check, Go to Declaration, a symbol's uses, completion and the types a path gives.</summary>
public sealed record BindingPath(IReadOnlyList<PathStep> Steps, PathEnd End)
{
    /// <summary>The type the whole path has where it is read whole, as its last step declares it (a collection's
    /// arguments kept); the start's for no step.</summary>
    public TypeRef? Type { get; init; }

    /// <summary>Where the path stopped being read: the step its type lacks, or the cast nothing resolves.</summary>
    public PathStep? Stop { get; init; }

    /// <summary>Whether the step it stopped at is the path's last, nothing after it.</summary>
    public bool StopsLast { get; init; }

    /// <summary>The type the whole path has, an array's as <c>System.Array</c>; null where it is not read whole or its type
    /// is not known.</summary>
    public ClrType? Reached(TypeSystem types) => End == PathEnd.Whole && Type is { } type ? Resolve(types, type) : null;

    /// <summary>The step written at an offset of the file (its end included), resolved; null for none.</summary>
    public PathStep? At(int offset) => Steps.FirstOrDefault(s => offset >= s.Range.Start && offset <= s.Range.End);

    /// <summary>Reads <paramref name="path"/>, written at <paramref name="at"/>, from <paramref name="start"/>; a cast's
    /// type is what <paramref name="cast"/> resolves its name to. Leading <c>!</c>s are passed over.</summary>
    public static BindingPath Read(TypeSystem types, ClrType start, string path, int at, bool compiled, Func<string, ClrType?> cast)
    {
        string text = path.TrimStart('!').TrimEnd();
        int offset = at + (path.Length - path.TrimStart('!').Length);
        List<PathStep> steps = [];
        ClrType current = start;
        TypeRef currentRef = TypeRef.Named(start.FullName);
        if (text.Length == 0 || text == ".")
        {
            return new BindingPath(steps, PathEnd.Whole) { Type = currentRef };
        }
        if (text.IndexOfAny(['$', '#', '^', ' ', '/']) >= 0 || (!compiled && text.Contains('(', StringComparison.Ordinal)))
        {
            return new BindingPath(steps, PathEnd.Unread);
        }
        for (int i = 0; i < text.Length;)
        {
            string? castName = null;
            int castAt = 0;
            string step;
            int stepAt;
            if (text[i] == '(')
            {
                bool member = i + 1 < text.Length && text[i + 1] == '(';
                castAt = i + (member ? 2 : 1);
                int close = text.IndexOf(')', castAt);
                int end = member && close > 0 ? text.IndexOf(')', close + 1) : close;
                if (end < 0)
                {
                    return new BindingPath(steps, PathEnd.Unread);
                }
                castName = text[castAt..close];
                step = member ? text[(close + 1)..end] : string.Empty;
                stepAt = close + 1;
                i = end + 1;
                // An attached property ((Grid.Row)), or a cast of one, is not followed.
                if (castName.Length == 0 || castName.IndexOfAny(['.', '(']) >= 0 || step.IndexOfAny(['.', '(']) >= 0)
                {
                    return new BindingPath(steps, PathEnd.Unread);
                }
            }
            else
            {
                int end = compiled ? text.IndexOfAny(['.', '?', '(', '['], i) : text.IndexOfAny(['.', '['], i);
                end = end < 0 ? text.Length : end;
                step = text[i..end];
                stepAt = i;
                i = end;
            }
            if (step.Length > 0)
            {
                ClrMember? property = types.MembersOf(current).FirstOrDefault(m => m.Kind is MemberKind.Property or MemberKind.Method && !m.IsStatic && m.Name == step);
                PathStep written = new(new TextRange(offset + stepAt, step.Length), step, current, property);
                if (property is null)
                {
                    // An interface's members may come from the interfaces it extends, which only a compiler that reads them judges.
                    return current.Kind == ClrTypeKind.Interface && (!current.IsFromSource || !compiled)
                        ? new BindingPath(steps, PathEnd.Unread)
                        : new BindingPath(steps, PathEnd.Missing) { Stop = written, StopsLast = i >= text.Length && castName is null };
                }
                steps.Add(written);
                if (castName is null)
                {
                    currentRef = property.Type;
                }
            }
            if (castName is not null)
            {
                if (cast(castName.Trim()) is not { } castType)
                {
                    return new BindingPath(steps, PathEnd.UnknownCast) { Stop = new PathStep(new TextRange(offset + castAt, castName.Length), castName.Trim(), current) };
                }
                steps.Add(new PathStep(new TextRange(offset + castAt, castName.Length), castName.Trim(), current, Cast: castType));
                currentRef = TypeRef.Named(castType.FullName);
            }
            while (i < text.Length && text[i] == '[')
            {
                int close = text.IndexOf(']', i);
                if (close < 0 || ItemOf(types, currentRef) is not { } item)
                {
                    return new BindingPath(steps, PathEnd.Unread);
                }
                currentRef = item;
                i = close + 1;
            }
            if (i >= text.Length)
            {
                return new BindingPath(steps, PathEnd.Whole) { Type = currentRef };
            }
            if (Resolve(types, currentRef) is not { } next || next.FullName == "System.Object")
            {
                return new BindingPath(steps, PathEnd.Unread);
            }
            current = next;
            if (text[i] == '.')
            {
                i++;
            }
            else if (compiled && text.AsSpan(i).StartsWith("?.", StringComparison.Ordinal))
            {
                i += 2;
            }
            else if (text[i] != '(')
            {
                return new BindingPath(steps, PathEnd.Unread);
            }
        }
        return new BindingPath(steps, PathEnd.Whole) { Type = currentRef };
    }

    private static ClrType? Resolve(TypeSystem types, TypeRef type) => type.IsArray ? types.Find("System.Array") : types.Resolve(type);

    /// <summary>The type an indexer of a collection gives, as its declared indexer gives it: an array's element, a list's
    /// item, a dictionary's value, found through the bases and interfaces of what the collection is; null where none of
    /// those says it.</summary>
    public static TypeRef? ItemOf(TypeSystem types, TypeRef collection) => collection.IsArray ? collection with { IsArray = false } : ItemOf(types, collection, 8);

    private static TypeRef? ItemOf(TypeSystem types, TypeRef collection, int depth)
    {
        if (depth == 0 || types.Resolve(collection) is not { } resolved || collection.Arguments.Count != resolved.GenericParameters.Count)
        {
            return null;
        }
        switch (resolved.FullName)
        {
            case "System.Collections.Generic.IList`1" or "System.Collections.Generic.IReadOnlyList`1":
                return collection.Arguments[0];
            case "System.Collections.Generic.IDictionary`2" or "System.Collections.Generic.IReadOnlyDictionary`2":
                return collection.Arguments[1];
        }
        foreach (TypeRef named in resolved.Base is { } baseType ? [baseType, .. resolved.Interfaces] : resolved.Interfaces)
        {
            if (ItemOf(types, Given(named, resolved.GenericParameters, collection.Arguments), depth - 1) is { } item)
            {
                return item;
            }
        }
        return null;
    }

    /// <summary>A type a generic type names among its bases, its parameters given the arguments it was given.</summary>
    private static TypeRef Given(TypeRef named, IReadOnlyList<string> parameters, IReadOnlyList<TypeRef> arguments)
    {
        if (named.Arguments.Count == 0)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i] == named.Name)
                {
                    return named.IsArray ? arguments[i] with { IsArray = true } : arguments[i];
                }
            }
            return named;
        }
        return named with { Arguments = [.. named.Arguments.Select(a => Given(a, parameters, arguments))] };
    }
}
