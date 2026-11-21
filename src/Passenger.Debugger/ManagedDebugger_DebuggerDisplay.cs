using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Dap;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>A type's [DebuggerDisplay]: the value's format, and the name and type columns' when it
/// gives them.</summary>
public sealed record DebuggerDisplay(string Value, string? Name, string? Type);

/// <summary>[DebuggerDisplay] rendered, its format read as a tracepoint's message is (<see cref="DisplayFormat"/>): each
/// fragment is an expression on the value, evaluated on its own, so one that fails says so in its own place and the rest
/// still shows. A fragment's value is formatted as the debugger formats any value, a string quoted, a value with its own
/// display shown by it, then as its specifiers say (",nq" a string unquoted, ",h" a whole number in hexadecimal), as
/// every debugger shows one (<see cref="DisplayFormat.Shown"/>). The attribute is inherited, as it is declared to be.</summary>
public partial class ManagedDebugger
{
    /// <summary>How deep displays inside displays go, before a value is shown by its type alone.</summary>
    private const int DisplayNesting = 3;

    /// <summary>The display of a type or the nearest type above it with one: one an assembly declares for it
    /// (<see cref="TargetedAt"/>), else its own [DebuggerDisplay], else the value a presentation the client gave for it
    /// shows (<see cref="PresentationOf"/>).</summary>
    private DebuggerDisplay? FindDebuggerDisplay(ICorDebugType type)
    {
        for (ICorDebugType? each = type; each is not null; each = each.Base)
        {
            ICorDebugClass corClass = each.Class;
            IMetaDataImport metadata = corClass.Module.GetMetaDataInterface<IMetaDataImport>();
            string name = metadata.GetTypeDefProps(corClass.Token).szTypeDef;
            if (name is "System.Object" or "System.ValueType")
            {
                return null;
            }
            if (TargetedAt(each).LastOrDefault(t => t.Display is not null)?.Display is { } targeted)
            {
                return targeted;
            }
            if (metadata.TryGetCustomAttributeByName(corClass.Token, "System.Diagnostics.DebuggerDisplayAttribute", out nint blob, out uint size) is Cor.S_OK)
            {
                return ReadDebuggerDisplay(blob, checked((int)size));
            }
            if (PresentationOf(corClass, name) is { Value: { } shown })
            {
                return new DebuggerDisplay(shown, null, null);
            }
        }
        return null;
    }

    private static unsafe DebuggerDisplay ReadDebuggerDisplay(nint blob, int size)
    {
        BlobReader reader = new((byte*)blob, size);
        (string value, Dictionary<string, string?> named) = ModuleMetadataReader.ReadStringAttribute(ref reader);
        return new DebuggerDisplay(value, named.GetValueOrDefault("Name"), named.GetValueOrDefault("Type"));
    }

    private Dictionary<string, TargetedAttribute[]> _targeted = [];

    private int _targetedFor = -1;

    /// <summary>The [DebuggerDisplay] and [DebuggerTypeProxy] attributes the program's assemblies declare for a type, its
    /// definition or just this construction of it, as though it carried them, the last loaded last and one for the
    /// construction after any for the definition; gathered again once the modules change.</summary>
    private IReadOnlyList<TargetedAttribute> TargetedAt(ICorDebugType type)
    {
        if (_targetedFor != ModuleSet_Version)
        {
            _targeted = _modules.Values.SelectMany(module => module.MetadataReader.Targeted).GroupBy(t => t.Target, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            _targetedFor = ModuleSet_Version;
        }
        if (_targeted.Count == 0)
        {
            return [];
        }
        ICorDebugClass corClass = type.Class;
        string definition = MetadataTypeNames.Of(corClass.Module.GetMetaDataInterface<IMetaDataImport>(), corClass.Token);
        return _targeted.TryGetValue(definition, out TargetedAttribute[]? declared) ? TargetedAttribute.Fitting(declared, ConstructedName(type)) : [];
    }

    /// <summary>A type's name as <see cref="TargetNames"/> compares it.</summary>
    private static string ConstructedName(ICorDebugType type)
    {
        if (MetadataTypeNames.PrimitiveOf(type.Type) is { } primitive)
        {
            return primitive.SystemName;
        }
        if (type.Type is CorElementType.SZARRAY)
        {
            return TargetNames.Array(ConstructedName(type.FirstTypeParameter), 1);
        }
        if (type.Type is CorElementType.ARRAY)
        {
            return TargetNames.Array(ConstructedName(type.FirstTypeParameter), type.Rank);
        }
        if (type.Type is CorElementType.PTR)
        {
            return $"{ConstructedName(type.FirstTypeParameter)}*";
        }
        ICorDebugClass corClass = type.Class;
        return TargetNames.Constructed(MetadataTypeNames.Of(corClass.Module.GetMetaDataInterface<IMetaDataImport>(), corClass.Token), type.TypeParameters.Select(ConstructedName));
    }

    /// <summary>A display format rendered against a value, whether any fragment failed, and, for a format that is one
    /// fragment alone ("{Value}"), the type of the value it shows.</summary>
    private async Task<(string Text, bool Failed, string? LoneType)> RenderDebuggerDisplayAsync(string format, ICorDebugValue? target, ThreadId threadId, FrameStackDepth frameStackDepth, int nesting)
    {
        StringBuilder rendered = new();
        bool failed = false;
        string? loneType = null;
        IReadOnlyList<(string Text, bool Expression)> pieces = DisplayFormat.Pieces(format);
        foreach ((string text, bool expression) in pieces)
        {
            if (!expression)
            {
                rendered.Append(text);
                continue;
            }
            (string shown, bool fragmentFailed, string? type) = await RenderFragmentAsync(text, target, threadId, frameStackDepth, nesting);
            rendered.Append(shown);
            failed |= fragmentFailed;
            loneType = pieces.Count == 1 ? type : null;
        }
        return (rendered.ToString(), failed, loneType);
    }

    /// <summary>A fragment's text, whether it failed, and the type of the value it shows, where it shows one.</summary>
    private async Task<(string Text, bool Failed, string? Type)> RenderFragmentAsync(string fragment, ICorDebugValue? target, ThreadId threadId, FrameStackDepth frameStackDepth, int nesting)
    {
        (string expression, IReadOnlyList<string> specifiers) = DisplayFormat.Specifiers(fragment);
        if (expression.Length == 0)
        {
            return (string.Empty, false, null);
        }
        ICorDebugThread thread = _process!.GetThread(threadId.Value);
        try
        {
            CompiledExpressionEvaluationContext context = new(thread, threadId, frameStackDepth, target);
            using EvaluationResult result = await _expressionEvaluator!.Evaluate(expression, context);
            if (result.Text is { } text)
            {
                return (DisplayFormat.Shown(DisplayValue.OfText(text, SymbolDisplay.FormatLiteral(text, quote: true)), specifiers), false, "string");
            }
            if (result.Void)
            {
                return (DapRows.RanWithNoValue, false, "void");
            }
            if ((result.Error is not null || result.Value is null) && target is not null && IsDottedName(expression)
                && await target.GetPropertyValue(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, GetIlFrameForThreadIdAndStackDepth(threadId, frameStackDepth), expression) is { } implemented)
            {
                // Roslyn's own types name an explicitly implemented property by its metadata name (System.Collections.ICollection.Count).
                return await SpecifiedAsync(ResolveCorDebugValue(implemented));
            }
            if (result.Error is not null || result.Value is null)
            {
                return ($"<error: {ShortError(result.Error)}>", true, null);
            }
            return await SpecifiedAsync(ResolveCorDebugValue(result.Value));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return ($"<error: {ShortError(error.Message)}>", true, null);
        }

        async Task<(string Text, bool Failed, string? Type)> SpecifiedAsync(ResolvedCorDebugValue value)
        {
            FormattedValue formatted = await FormatCorDebugValueAsync(value, threadId, frameStackDepth, true, nesting + 1);
            return (DisplayFormat.Shown(Displayed(value.Value, formatted), specifiers), formatted.ResultIsError, formatted.FriendlyTypeName);
        }
    }

    /// <summary>A fragment's value as every debugger tells one to its specifiers (<see cref="DisplayFormat.Shown"/>): a
    /// string with its own text, a whole number of the program's integer types with its value and size, anything else as the
    /// debugger formatted it.</summary>
    private static DisplayValue Displayed(ICorDebugValue? value, FormattedValue formatted) => formatted.ResultIsError ? DisplayValue.Said(formatted.Value)
        : value?.UnwrapDebugValue() switch
        {
            ICorDebugStringValue text => DisplayValue.OfText(text.String, formatted.Value),
            ICorDebugGenericValue number when WholeOf(number) is { } whole => DisplayValue.OfWhole(whole, number.Size),
            _ => DisplayValue.Said(formatted.Value),
        };

    /// <summary>The value of a whole number of one of the program's integer types (its native ones as wide as it holds them);
    /// null for any other value.</summary>
    private static Int128? WholeOf(ICorDebugGenericValue value)
    {
        if (value.Type is not (CorElementType.I1 or CorElementType.U1 or CorElementType.I2 or CorElementType.U2 or CorElementType.I4 or CorElementType.U4
            or CorElementType.I8 or CorElementType.U8 or CorElementType.I or CorElementType.U))
        {
            return null;
        }
        byte[] bytes = value.GetValueAsBytes();
        // Little-endian, as the runtime holds them on every processor it runs on.
        return (value.Type, bytes.Length) switch
        {
            (CorElementType.I1, _) => (sbyte)bytes[0],
            (CorElementType.U1, _) => bytes[0],
            (CorElementType.I2, _) => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            (CorElementType.U2, _) => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            (CorElementType.I4 or CorElementType.I, 4) => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            (CorElementType.U4 or CorElementType.U, 4) => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            (CorElementType.I8 or CorElementType.I, _) => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        };
    }

    /// <summary>Whether a fragment is names joined by dots, as an explicitly implemented member's full name is.</summary>
    private static bool IsDottedName(string expression)
    {
        string[] parts = expression.Split('.');
        return parts.Length > 1 && parts.All(p => p.Length > 0 && SyntaxFacts.IsIdentifierStartCharacter(p[0]) && p.All(SyntaxFacts.IsIdentifierPartCharacter));
    }

    /// <summary>The compiler's message without its code: "The name 'x' does not exist in the current context".</summary>
    private static string ShortError(string? error)
    {
        string text = error ?? "no value";
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && text.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? text[(colon + 2)..] : text;
    }
}
