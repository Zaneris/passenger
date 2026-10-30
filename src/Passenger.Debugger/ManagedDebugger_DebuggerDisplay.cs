using System.Reflection.Metadata;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>A type's [DebuggerDisplay]: the value's format, and the name and type columns' when it
/// gives them.</summary>
public sealed record DebuggerDisplay(string Value, string? Name, string? Type);

/// <summary>[DebuggerDisplay] rendered. The format is literal text and {fragments}, \{ and \} being braces; each
/// fragment is an expression on the value, with format specifiers after its last top-level comma (",nq" leaves a string
/// unquoted), and is evaluated on its own, so one that fails says so in its own place and the rest still shows. A
/// fragment's value is formatted as the debugger formats any value: a string quoted, a value with its own display shown
/// by it. The attribute is inherited, as it is declared to be.</summary>
public partial class ManagedDebugger
{
    /// <summary>How deep displays inside displays go, before a value is shown by its type alone.</summary>
    private const int DisplayNesting = 3;

    private static DebuggerDisplay? FindDebuggerDisplay(ICorDebugType type)
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
            if (metadata.TryGetCustomAttributeByName(corClass.Token, "System.Diagnostics.DebuggerDisplayAttribute", out nint blob, out uint size) is Cor.S_OK)
            {
                return ReadDebuggerDisplay(blob, checked((int)size));
            }
        }
        return null;
    }

    private static unsafe DebuggerDisplay ReadDebuggerDisplay(nint blob, int size)
    {
        BlobReader reader = new((byte*)blob, size);
        if (reader.ReadUInt16() != 0x0001)
        {
            throw new InvalidOperationException("Invalid custom attribute prolog");
        }
        string value = reader.ReadSerializedString() ?? string.Empty;
        string? name = null;
        string? type = null;
        ushort named = reader.ReadUInt16();
        for (int i = 0; i < named; i++)
        {
            reader.ReadByte(); // field or property
            reader.ReadSignatureTypeCode(); // every named argument it has is a string or a Type, written as a string
            string argument = reader.ReadSerializedString() ?? string.Empty;
            string? text = reader.ReadSerializedString();
            if (argument == "Name")
            {
                name = text;
            }
            else if (argument == "Type")
            {
                type = text;
            }
        }
        return new DebuggerDisplay(value, name, type);
    }

    /// <summary>A display format rendered against a value, and whether any fragment failed.</summary>
    private async Task<(string Text, bool Failed)> RenderDebuggerDisplayAsync(string format, ICorDebugValue? target, ThreadId threadId, FrameStackDepth frameStackDepth, int nesting)
    {
        StringBuilder rendered = new();
        bool failed = false;
        int i = 0;
        while (i < format.Length)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length && format[i + 1] is '{' or '}')
            {
                rendered.Append(format[i + 1]);
                i += 2;
            }
            else if (c == '{' && FragmentEnd(format, i) is int end)
            {
                (string text, bool fragmentFailed) = await RenderFragmentAsync(format[(i + 1)..end], target, threadId, frameStackDepth, nesting);
                rendered.Append(text);
                failed |= fragmentFailed;
                i = end + 1;
            }
            else
            {
                rendered.Append(c);
                i++;
            }
        }
        return (rendered.ToString(), failed);
    }

    private async Task<(string Text, bool Failed)> RenderFragmentAsync(string fragment, ICorDebugValue? target, ThreadId threadId, FrameStackDepth frameStackDepth, int nesting)
    {
        (string expression, IReadOnlyList<string> specifiers) = SplitSpecifiers(fragment);
        if (expression.Length == 0)
        {
            return (string.Empty, false);
        }
        ICorDebugThread thread = _process!.GetThread(threadId.Value);
        try
        {
            CompiledExpressionEvaluationContext context = new(thread, threadId, frameStackDepth, target);
            using EvaluationResult result = await _expressionEvaluator!.Evaluate(expression, context);
            // "nq" (no quotes) and "raw" (as it is, which CoreLib's spans use for their ToString()) show a string bare.
            bool quoted = !specifiers.Contains("nq") && !specifiers.Contains("raw");
            if (result.Text is { } text)
            {
                return (quoted ? SymbolDisplay.FormatLiteral(text, quote: true) : text, false);
            }
            if ((result.Error is not null || result.Value is null) && target is not null && IsDottedName(expression)
                && await target.GetPropertyValue(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, GetIlFrameForThreadIdAndStackDepth(threadId, frameStackDepth), expression) is { } implemented)
            {
                // Roslyn's own types name an explicitly implemented property by its metadata name (System.Collections.ICollection.Count).
                FormattedValue shown = await FormatCorDebugValueAsync(ResolveCorDebugValue(implemented), threadId, frameStackDepth, quoted, nesting + 1);
                return (shown.Value, shown.ResultIsError);
            }
            if (result.Error is not null || result.Value is null)
            {
                return ($"<error: {ShortError(result.Error)}>", true);
            }
            FormattedValue formatted = await FormatCorDebugValueAsync(ResolveCorDebugValue(result.Value), threadId, frameStackDepth, quoted, nesting + 1);
            return (formatted.Value, formatted.ResultIsError);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return ($"<error: {ShortError(error.Message)}>", true);
        }
    }

    /// <summary>Whether a fragment is names joined by dots, as an explicitly implemented member's full name is.</summary>
    private static bool IsDottedName(string expression)
    {
        string[] parts = expression.Split('.');
        return parts.Length > 1 && parts.All(p => p.Length > 0 && (char.IsLetter(p[0]) || p[0] == '_') && p.All(c => char.IsLetterOrDigit(c) || c == '_'));
    }

    /// <summary>The compiler's message without its code: "The name 'x' does not exist in the current context".</summary>
    private static string ShortError(string? error)
    {
        string text = error ?? "no value";
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && text.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? text[(colon + 2)..] : text;
    }

    /// <summary>Where a fragment opened at <paramref name="open"/> closes, skipping braces nested in it
    /// and anything inside quotes; null for one that never closes, which is then literal text.</summary>
    private static int? FragmentEnd(string format, int open)
    {
        int depth = 0;
        char? quote = null;
        for (int i = open; i < format.Length; i++)
        {
            char c = format[i];
            if (quote is { } q)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
            }
        }
        return null;
    }

    /// <summary>A fragment's expression and the format specifiers after its last comma that is outside
    /// brackets and quotes: "Items.Count, d" is the count, in decimal.</summary>
    private static (string Expression, IReadOnlyList<string> Specifiers) SplitSpecifiers(string fragment)
    {
        int depth = 0;
        char? quote = null;
        int comma = -1;
        for (int i = 0; i < fragment.Length; i++)
        {
            char c = fragment[i];
            if (quote is { } q)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(' or '[' or '{' or '<':
                    depth++;
                    break;
                case ')' or ']' or '}' or '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    comma = i;
                    break;
            }
        }
        if (comma < 0)
        {
            return (fragment.Trim(), []);
        }
        string[] specifiers = fragment[(comma + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Only words are specifiers: "a, b" in a call's arguments never reaches here, but "x, 2" is not one either.
        return specifiers.All(s => s.All(char.IsAsciiLetterOrDigit) && char.IsAsciiLetter(s[0]))
            ? (fragment[..comma].Trim(), specifiers)
            : (fragment.Trim(), []);
    }
}
