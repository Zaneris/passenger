using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Core;

namespace Passenger.Debugger;

/// <summary>A method a function breakpoint names, as C# writes it: its type (where named, with each generic's arity), its
/// name, a constructor's .ctor, a static constructor's .cctor, a finalizer's ~Type or Finalize, a generic method's arity,
/// and, where it names them, its parameters' types (<see cref="ParameterType"/>), each matched to a method's as its
/// metadata gives them.</summary>
internal sealed record FunctionBreakpointPattern(string? TypeName, string MethodName, int? MethodArity, ImmutableArray<ParameterType>? Parameters)
{
    /// <summary>A constructor's and a static constructor's names in metadata.</summary>
    public static readonly string Constructor = ConstructorInfo.ConstructorName;
    public static readonly string StaticConstructor = ConstructorInfo.TypeConstructorName;

    /// <summary>A finalizer's name in metadata, which C# writes ~Type.</summary>
    public const string Finalizer = "Finalize";

    public static FunctionBreakpointPattern Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Function name cannot be empty.");
        }

        value = value.Trim();

        ImmutableArray<ParameterType>? parameters = null;
        int openingParenthesis = FindTopLevel(value, '(');
        if (openingParenthesis >= 0)
        {
            if (value[^1] != ')')
            {
                throw new ArgumentException("The function parameter list is not closed.");
            }

            string parameterText = value[(openingParenthesis + 1)..^1];
            parameters = string.IsNullOrWhiteSpace(parameterText)
                ? []
                : [.. SplitTopLevel(parameterText, ',').Select(ParameterType.Parse)];
            value = value[..openingParenthesis].Trim();
        }

        foreach (string constructor in (string[])[StaticConstructor, Constructor])
        {
            // Ns.Type..ctor: the dot before .ctor ends the type's name.
            if (value == constructor || value.EndsWith("." + constructor, StringComparison.Ordinal))
            {
                string type = value[..^constructor.Length].TrimEnd('.');
                return new FunctionBreakpointPattern(type.Length == 0 ? null : NormalizeQualifiedName(type), constructor, null, parameters);
            }
        }

        int lastDot = FindLastTopLevel(value, '.');
        string? typeName = lastDot < 0 ? null : NormalizeQualifiedName(value[..lastDot]);
        string method = lastDot < 0 ? value : value[(lastDot + 1)..];
        if (method.TrimStart().StartsWith('~'))
        {
            return new FunctionBreakpointPattern(typeName, Finalizer, null, parameters);
        }
        (string? methodName, int? methodArity) = NormalizeNameSegment(method);
        if (methodName.Length == 0)
        {
            throw new ArgumentException("Function name cannot be empty.");
        }

        return new FunctionBreakpointPattern(typeName, methodName, methodArity, parameters);
    }

    public bool MatchesType(string candidate) => TypeName is null ||
        candidate == TypeName || candidate.EndsWith('.' + TypeName, StringComparison.Ordinal);

    /// <summary>How far a method's parameters are from those named (<see cref="ParameterType.Distance"/>): 0 where each is
    /// the type named; null where they differ, or are fewer or more. Any method's are near where none are named.</summary>
    public int? Distance(ImmutableArray<ParameterType> candidate)
    {
        if (Parameters is not { } named)
        {
            return 0;
        }
        if (named.Length != candidate.Length)
        {
            return null;
        }
        int distance = 0;
        for (int i = 0; i < named.Length; i++)
        {
            if (ParameterType.Distance(named[i], candidate[i]) is not { } apart)
            {
                return null;
            }
            distance += apart;
        }
        return distance;
    }

    private static string NormalizeQualifiedName(string value) => string.Join('.',
        SplitTopLevel(value, '.').Select(segment =>
        {
            (string? name, int? arity) = NormalizeNameSegment(segment);
            return arity is null ? name : $"{name}`{arity}";
        }));

    private static (string Name, int? Arity) NormalizeNameSegment(string value)
    {
        value = value.Trim();
        int genericStart = FindTopLevel(value, '<');
        if (genericStart < 0)
        {
            return (value, null);
        }

        if (value[^1] != '>')
        {
            throw new ArgumentException($"Generic name '{value}' is not closed.");
        }

        List<string> arguments = SplitTopLevel(value[(genericStart + 1)..^1], ',');
        return (value[..genericStart].Trim(), arguments.Count);
    }

    private static int FindTopLevel(string value, char target)
    {
        int depth = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == target && depth == 0)
            {
                return i;
            }

            if (value[i] == '<')
            {
                depth++;
            }
            else if (value[i] == '>')
            {
                depth--;
            }

            if (depth < 0)
            {
                throw new ArgumentException("Unexpected '>'.");
            }
        }
        return -1;
    }

    private static int FindLastTopLevel(string value, char target)
    {
        int depth = 0;
        for (int i = value.Length - 1; i >= 0; i--)
        {
            if (value[i] == '>')
            {
                depth++;
            }
            else if (value[i] == '<')
            {
                depth--;
            }
            else if (value[i] == target && depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>A list split at each separator outside a generic's arguments, an array's rank and a tuple's elements, whose
    /// commas are their own.</summary>
    internal static List<string> SplitTopLevel(string value, char separator)
    {
        List<string> result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is '<' or '[' or '(')
            {
                depth++;
            }
            else if (value[i] is '>' or ']' or ')')
            {
                depth--;
            }
            else if (value[i] == separator && depth == 0)
            {
                result.Add(value[start..i]);
                start = i + 1;
            }
        }
        result.Add(value[start..]);
        if (result.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A name or parameter is missing.");
        }

        return result;
    }
}

/// <summary>A parameter's type as a function breakpoint names it and as a method's signature gives it: a type by its name
/// (with each generic's arity, as metadata names it) and its type arguments, an array of a rank, a reference (ref, out,
/// in), a pointer, a function pointer, or, named alone, a type written T? that may be a value type or not
/// (<see cref="MaybeNullable"/>).</summary>
internal abstract record ParameterType
{
    public sealed record Named(string Name, ImmutableArray<ParameterType> Arguments) : ParameterType;

    public sealed record Array(ParameterType Element, int Rank) : ParameterType;

    public sealed record Reference(ParameterType Element) : ParameterType;

    public sealed record Pointer(ParameterType Element) : ParameterType;

    public sealed record FunctionPointer : ParameterType;

    /// <summary>T? where T is neither a keyword's type nor an array, which metadata gives as Nullable`1 of T for a value type
    /// and as T alone for a reference type or a type parameter, its mark an attribute.</summary>
    public sealed record MaybeNullable(ParameterType Element) : ParameterType;

    private const string Nullable = "System.Nullable`1";

    /// <summary>How far a method's parameter type is from one named: 0 where it is that type, 1 more for each T? it takes as T
    /// alone; null where it is not. A name matches the same name or one it ends, after a dot.</summary>
    public static int? Distance(ParameterType named, ParameterType candidate) => (named, candidate) switch
    {
        (MaybeNullable maybe, Named { Name: Nullable, Arguments: [{ } value] }) => Distance(maybe.Element, value),
        (MaybeNullable maybe, _) => Distance(maybe.Element, candidate) + 1,
        (Named one, Named other) when (one.Name == other.Name || other.Name.EndsWith('.' + one.Name, StringComparison.Ordinal))
            && one.Arguments.Length == other.Arguments.Length => Sum(one.Arguments, other.Arguments),
        (Array one, Array other) when one.Rank == other.Rank => Distance(one.Element, other.Element),
        (Reference one, Reference other) => Distance(one.Element, other.Element),
        (Pointer one, Pointer other) => Distance(one.Element, other.Element),
        (FunctionPointer, FunctionPointer) => 0,
        _ => null,
    };

    private static int? Sum(ImmutableArray<ParameterType> named, ImmutableArray<ParameterType> candidate)
    {
        int sum = 0;
        for (int i = 0; i < named.Length; i++)
        {
            if (Distance(named[i], candidate[i]) is not { } apart)
            {
                return null;
            }
            sum += apart;
        }
        return sum;
    }

    /// <summary>A parameter as C# writes it: its attributes, its modifiers (ref, out and in making it a reference; scoped,
    /// readonly, params and this none), its type, and its name and default value, which are passed over.</summary>
    public static ParameterType Parse(string parameter)
    {
        TypeReader reader = new(parameter);
        bool reference = false;
        while (true)
        {
            reader.SkipSpace();
            if (reader.Peek('['))
            {
                reader.SkipBracketed('[', ']');
            }
            else if (reader.TakeWord("ref") || reader.TakeWord("out") || reader.TakeWord("in"))
            {
                reference = true;
            }
            else if (!(reader.TakeWord("readonly") || reader.TakeWord("scoped") || reader.TakeWord("params") || reader.TakeWord("this")))
            {
                break;
            }
        }
        ParameterType type = reader.Type();
        reader.SkipName();
        reader.End();
        return reference ? new Reference(type) : type;
    }

    /// <summary>A C# type read from text: a qualified name with its type arguments (global:: passed over, a keyword as its
    /// System type, dynamic as object), a tuple as the ValueTuple it is, a function pointer, then its suffixes: ? (a nullable
    /// value type, a reference type's mark passed over), * and array ranks, a run of them written outermost first.</summary>
    private sealed class TypeReader(string text)
    {
        private int at;

        public bool Peek(char c)
        {
            SkipSpace();
            return at < text.Length && text[at] == c;
        }

        public void SkipSpace()
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }
        }

        /// <summary>A whole word taken where it stands next, never the start of a longer name (int from internal).</summary>
        public bool TakeWord(string word)
        {
            SkipSpace();
            if (string.CompareOrdinal(text, at, word, 0, word.Length) != 0 || (at + word.Length < text.Length && IsNamePart(text[at + word.Length])))
            {
                return false;
            }
            at += word.Length;
            return true;
        }

        public void SkipBracketed(char open, char close)
        {
            int depth = 0;
            do
            {
                depth += text[at] == open ? 1 : text[at] == close ? -1 : 0;
                at++;
            }
            while (depth > 0 && at < text.Length);
        }

        /// <summary>The parameter's name and its default value, after its type.</summary>
        public void SkipName()
        {
            SkipSpace();
            int start = at;
            while (at < text.Length && IsNamePart(text[at]))
            {
                at++;
            }
            SkipSpace();
            if (at < text.Length && text[at] == '=' && at > start)
            {
                at = text.Length;
            }
        }

        public void End()
        {
            SkipSpace();
            if (at < text.Length)
            {
                throw new ArgumentException($"'{text.Trim()}' is not a parameter's type.");
            }
        }

        public ParameterType Type()
        {
            SkipSpace();
            ParameterType type = Peek('(') ? Tuple() : TakeWord("delegate") ? FunctionPointerType() : Name();
            List<int> ranks = [];
            while (true)
            {
                SkipSpace();
                if (at < text.Length && text[at] == '[')
                {
                    int open = at;
                    SkipBracketed('[', ']');
                    string rank = text[(open + 1)..(at - 1)];
                    if (rank.Any(c => c != ',' && !char.IsWhiteSpace(c)))
                    {
                        throw new ArgumentException($"Array type '{text.Trim()}' is not a type and its rank.");
                    }
                    ranks.Add(rank.Count(c => c == ',') + 1);
                    continue;
                }
                if (at < text.Length && text[at] == '?')
                {
                    at++;
                    // An array's nullable mark is no type of its own.
                    if (ranks.Count == 0)
                    {
                        type = NullableOf(type);
                    }
                    continue;
                }
                type = Ranked(type, ranks);
                ranks.Clear();
                if (at < text.Length && text[at] == '*')
                {
                    at++;
                    type = new Pointer(type);
                    continue;
                }
                return type;
            }
        }

        /// <summary>An array's ranks applied as C# writes them, the first the outermost: int[,][] is a two-dimensional array of int[].</summary>
        private static ParameterType Ranked(ParameterType element, List<int> ranks)
        {
            for (int i = ranks.Count - 1; i >= 0; i--)
            {
                element = new Array(element, ranks[i]);
            }
            return element;
        }

        private static ParameterType NullableOf(ParameterType type) => type switch
        {
            Named { Arguments.Length: 0 } named when PrimitiveTypes.Named(named.Name) is { } primitive =>
                primitive.Type.IsValueType ? new Named(Nullable, [type]) : type,
            Named { Name: Nullable } => type,
            Named named when named.Name.StartsWith("System.ValueTuple`", StringComparison.Ordinal) => new Named(Nullable, [type]),
            _ => new MaybeNullable(type),
        };

        private ParameterType Tuple()
        {
            at++;
            List<ParameterType> elements = [];
            while (true)
            {
                elements.Add(Type());
                SkipName();
                SkipSpace();
                if (at < text.Length && text[at] == ',')
                {
                    at++;
                    continue;
                }
                if (at < text.Length && text[at] == ')')
                {
                    at++;
                    break;
                }
                throw new ArgumentException($"Tuple type '{text.Trim()}' is not closed.");
            }
            if (elements.Count < 2)
            {
                throw new ArgumentException($"Tuple type '{text.Trim()}' has fewer than two elements.");
            }
            return ValueTuple(elements);
        }

        /// <summary>A tuple's elements as its ValueTuple, those past the seventh in a ValueTuple of their own as its eighth.</summary>
        private static Named ValueTuple(List<ParameterType> elements) => elements.Count <= 7
            ? new Named($"System.ValueTuple`{elements.Count}", [.. elements])
            : new Named("System.ValueTuple`8", [.. elements.Take(7), ValueTuple([.. elements.Skip(7)])]);

        /// <summary>delegate*, its calling convention and its types, which metadata gives as a function pointer alone.</summary>
        private FunctionPointer FunctionPointerType()
        {
            SkipSpace();
            if (at >= text.Length || text[at] != '*')
            {
                throw new ArgumentException($"'{text.Trim()}' is not a parameter's type.");
            }
            at++;
            while (at < text.Length && text[at] != '<')
            {
                at++;
            }
            if (at < text.Length)
            {
                SkipBracketed('<', '>');
            }
            return new FunctionPointer();
        }

        /// <summary>A qualified name, each part's arity in the name and every part's type arguments in turn, as metadata gives a
        /// nested type of a generic one (Outer&lt;A&gt;.Inner&lt;B&gt; is Outer`1.Inner`1 of A and B).</summary>
        private ParameterType Name()
        {
            SkipSpace();
            List<string> parts = [];
            List<ParameterType> arguments = [];
            while (true)
            {
                int start = at;
                while (at < text.Length && IsNamePart(text[at]))
                {
                    at++;
                }
                if (at == start)
                {
                    throw new ArgumentException($"'{text.Trim()}' is not a parameter's type.");
                }
                string part = text[start..at];
                SkipSpace();
                if (at + 1 < text.Length && text[at] == ':' && text[at + 1] == ':')
                {
                    // global:: and an alias's qualifier name no part of the type.
                    at += 2;
                    SkipSpace();
                    continue;
                }
                int arity = 0;
                if (at < text.Length && text[at] == '<')
                {
                    at++;
                    while (true)
                    {
                        arguments.Add(Type());
                        arity++;
                        SkipSpace();
                        if (at < text.Length && text[at] == ',')
                        {
                            at++;
                            continue;
                        }
                        if (at < text.Length && text[at] == '>')
                        {
                            at++;
                            break;
                        }
                        throw new ArgumentException($"Generic type '{text.Trim()}' is not closed.");
                    }
                }
                parts.Add(arity > 0 ? $"{part}`{arity}" : part);
                SkipSpace();
                if (at < text.Length && text[at] == '.')
                {
                    at++;
                    SkipSpace();
                    continue;
                }
                break;
            }
            string name = parts is [{ } only] ? only == "dynamic" ? "System.Object" : PrimitiveTypes.OfKeyword(only)?.SystemName ?? only : string.Join('.', parts);
            return new Named(name, [.. arguments]);
        }

        private static bool IsNamePart(char c) => CSharpNames.Continues(c) || c == '@';
    }
}

/// <summary>A method signature's parameter types as <see cref="ParameterType"/>s, a generic parameter named as its method or
/// type declares it (<see cref="GenericNames"/>), nested types joined by '.', as a function breakpoint names them.</summary>
internal sealed class ParameterTypes : ISignatureTypeProvider<ParameterType, GenericNames?>
{
    public static ParameterTypes Instance { get; } = new();

    public ParameterType GetArrayType(ParameterType elementType, ArrayShape shape) => new ParameterType.Array(elementType, shape.Rank);
    public ParameterType GetByReferenceType(ParameterType elementType) => new ParameterType.Reference(elementType);
    public ParameterType GetFunctionPointerType(MethodSignature<ParameterType> signature) => new ParameterType.FunctionPointer();
    public ParameterType GetGenericInstantiation(ParameterType genericType, ImmutableArray<ParameterType> typeArguments) =>
        genericType is ParameterType.Named named ? named with { Arguments = typeArguments } : genericType;
    public ParameterType GetGenericMethodParameter(GenericNames? genericContext, int index) =>
        new ParameterType.Named(genericContext?.Method.ElementAtOrDefault(index) ?? $"!!{index}", []);
    public ParameterType GetGenericTypeParameter(GenericNames? genericContext, int index) =>
        new ParameterType.Named(genericContext?.Type.ElementAtOrDefault(index) ?? $"!{index}", []);
    public ParameterType GetModifiedType(ParameterType modifier, ParameterType unmodifiedType, bool isRequired) => unmodifiedType;
    public ParameterType GetPinnedType(ParameterType elementType) => elementType;
    public ParameterType GetPointerType(ParameterType elementType) => new ParameterType.Pointer(elementType);
    public ParameterType GetPrimitiveType(PrimitiveTypeCode typeCode) => new ParameterType.Named(MetadataTypeNames.Of(typeCode), []);
    public ParameterType GetSZArrayType(ParameterType elementType) => new ParameterType.Array(elementType, 1);
    public ParameterType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        new ParameterType.Named(MetadataTypeNames.Of(reader, handle, '.'), []);
    public ParameterType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        new ParameterType.Named(MetadataTypeNames.Of(reader, handle, '.'), []);
    public ParameterType GetTypeFromSpecification(MetadataReader reader, GenericNames? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}

/// <summary>The names a method's type parameters and its type's are declared with, by their places.</summary>
internal sealed record GenericNames(ImmutableArray<string> Type, ImmutableArray<string> Method)
{
    public static GenericNames Of(MetadataReader reader, TypeDefinition type, MethodDefinition method) =>
        new(Names(reader, type.GetGenericParameters()), Names(reader, method.GetGenericParameters()));

    private static ImmutableArray<string> Names(MetadataReader reader, GenericParameterHandleCollection parameters) =>
        [.. parameters.Select(p => reader.GetString(reader.GetGenericParameter(p).Name))];
}

internal sealed record ResolvedFunctionBreakpoint(int MethodToken, ModuleMetadataReader.ResolvedBreakpoint Source);

/// <summary>The methods of a module a function breakpoint names, each bound at its entry: of each type named, those of
/// the name and arity whose parameters are nearest those named (<see cref="FunctionBreakpointPattern.Distance"/>), so a
/// T? names a value type's overload taking Nullable`1 of it, where there is one, before the one taking it alone.</summary>
internal static class FunctionBreakpointMetadataResolver
{
    public static IEnumerable<ResolvedFunctionBreakpoint> Resolve(ModuleMetadataReader metadataReader, FunctionBreakpointPattern pattern)
    {
        MetadataReader reader = metadataReader.PeMetadataReader;
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            // Nested types joined by '.', as the person writes them.
            if (!pattern.MatchesType(MetadataTypeNames.Of(reader, typeHandle, '.')))
            {
                continue;
            }

            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            List<(MethodDefinitionHandle Method, int Distance)> named = [];
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) != pattern.MethodName)
                {
                    continue;
                }

                if (pattern.MethodArity is not null && method.GetGenericParameters().Count != pattern.MethodArity)
                {
                    continue;
                }

                if (pattern.Distance(method.DecodeSignature(ParameterTypes.Instance, GenericNames.Of(reader, type, method)).ParameterTypes) is { } distance)
                {
                    named.Add((methodHandle, distance));
                }
            }

            foreach ((MethodDefinitionHandle methodHandle, int distance) in named)
            {
                if (distance > named.Min(n => n.Distance))
                {
                    continue;
                }
                int token = MetadataTokens.GetToken(methodHandle);
                ModuleMetadataReader.ResolvedBreakpoint? source = metadataReader.ResolveBreakpointAtMethodEntry(token);
                if (source is not null)
                {
                    yield return new ResolvedFunctionBreakpoint(token, source);
                }
            }
        }
    }
}
