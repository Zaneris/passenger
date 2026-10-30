using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Core;

namespace Passenger.Debugger;

internal sealed record FunctionBreakpointPattern(string? TypeName, string MethodName, int? MethodArity, ImmutableArray<string>? Parameters)
{
    public static FunctionBreakpointPattern Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Function name cannot be empty.");
        }

        value = value.Trim();

        ImmutableArray<string>? parameters = null;
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
                : SplitTopLevel(parameterText, ',').Select(NormalizeType).ToImmutableArray();
            value = value[..openingParenthesis].Trim();
        }

        int lastDot = FindLastTopLevel(value, '.');
        string? typeName = lastDot < 0 ? null : NormalizeQualifiedName(value[..lastDot]);
        string method = lastDot < 0 ? value : value[(lastDot + 1)..];
        (string? methodName, int? methodArity) = NormalizeNameSegment(method);
        if (methodName.Length == 0)
        {
            throw new ArgumentException("Function name cannot be empty.");
        }

        return new FunctionBreakpointPattern(typeName, methodName, methodArity, parameters);
    }

    public bool MatchesType(string candidate) => TypeName is null ||
        candidate == TypeName || candidate.EndsWith('.' + TypeName, StringComparison.Ordinal);

    public bool MatchesParameters(ImmutableArray<string> candidate)
    {
        if (Parameters is null)
        {
            return true;
        }

        if (Parameters.Value.Length != candidate.Length)
        {
            return false;
        }

        return Parameters.Value.Zip(candidate).All(pair =>
            pair.First == pair.Second || pair.Second.EndsWith('.' + pair.First, StringComparison.Ordinal));
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

    /// <summary>A parameter's type as C# writes it, as the method's signature names it: aliases as the runtime's types, a
    /// nullable value type as Nullable`1, a generic's arity, and an array's rank specifiers innermost first, where C#
    /// writes them outermost first (int[,][] is a two-dimensional array of int[], System.Int32[][,]).</summary>
    private static string NormalizeType(string value)
    {
        value = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        string ranks = "";
        while (true)
        {
            // An array's nullable annotation (string[]?) is no type of its own.
            if (value.EndsWith("]?", StringComparison.Ordinal))
            {
                value = value[..^1];
            }
            if (!value.EndsWith(']'))
            {
                break;
            }
            int open = value.LastIndexOf('[');
            if (open <= 0 || value[(open + 1)..^1].Any(c => c != ','))
            {
                throw new ArgumentException($"Array type '{value}' is not a type and its rank.");
            }
            ranks += value[open..];
            value = value[..open];
        }
        if (ranks.Length > 0)
        {
            return NormalizeType(value) + ranks;
        }
        if (value.EndsWith('?'))
        {
            return $"System.Nullable`1<{NormalizeType(value[..^1])}>";
        }

        int genericStart = FindTopLevel(value, '<');
        if (genericStart >= 0)
        {
            if (value[^1] != '>')
            {
                throw new ArgumentException($"Generic type '{value}' is not closed.");
            }

            List<string> arguments = SplitTopLevel(value[(genericStart + 1)..^1], ',').Select(NormalizeType).ToList();
            return $"{NormalizeSimpleType(value[..genericStart])}`{arguments.Count}<{string.Join(',', arguments)}>";
        }
        return NormalizeSimpleType(value);
    }

    private static string NormalizeSimpleType(string value) => PrimitiveTypes.OfKeyword(value)?.SystemName ?? value;

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

    /// <summary>A list split at each separator outside a generic's arguments and an array's rank, whose commas are their own.</summary>
    private static List<string> SplitTopLevel(string value, char separator)
    {
        List<string> result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is '<' or '[')
            {
                depth++;
            }
            else if (value[i] is '>' or ']')
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

internal sealed record ResolvedFunctionBreakpoint(int MethodToken, ModuleMetadataReader.ResolvedBreakpoint Source);

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

                if (!pattern.MatchesParameters(method.DecodeSignature(SignatureKeyProvider.Dotted, null).ParameterTypes))
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
