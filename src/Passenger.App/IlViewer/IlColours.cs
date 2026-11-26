using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.IlViewer;

/// <summary>Theme spans and hover targets for the ILSpy text produced from one built file.</summary>
internal sealed partial class IlColours(SyntaxSpan[] spans, IReadOnlyList<IlColours.Instruction> instructions) : IHighlighter
{
    private static readonly (string Group, TokenKind Kind)[] Colours =
    [
        ("comment", TokenKind.Comment), ("literal", TokenKind.String), ("address", TokenKind.Constant),
        ("directive", TokenKind.Preprocessor), ("number", TokenKind.Number), ("keyword", TokenKind.Keyword),
        ("method", TokenKind.Method), ("field", TokenKind.Field), ("type", TokenKind.Class),
    ];

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot text, int start, int length)
    {
        ReadOnlySpan<SyntaxSpan> all = spans;
        int first = Sorted.FirstAfter(all, static span => span.End, start);
        int last = Sorted.FirstAtOrAfter(all, static span => span.Start, start + length);
        return new ArraySegment<SyntaxSpan>(spans, first, last - first);
    }

    public IlOpcode? OpcodeAt(int offset)
    {
        int index = Sorted.FirstAfter(instructions, static instruction => instruction.Range.Start, offset) - 1;
        return index >= 0 && instructions[index].Range.Contains(offset) ? instructions[index].Opcode : null;
    }

    public static IlColours Parse(string text, CancellationToken cancel)
    {
        List<SyntaxSpan> spans = [];
        List<Instruction> instructions = [];
        foreach (Match token in Tokens().Matches(text))
        {
            cancel.ThrowIfCancellationRequested();
            if (token.Groups["opcode"] is { Success: true } opcode)
            {
                Group label = token.Groups["address"];
                spans.Add(new SyntaxSpan(label.Index, label.Length, TokenKind.Constant));
                spans.Add(new SyntaxSpan(opcode.Index, opcode.Length, TokenKind.Keyword));
                if (IlOpcodes.Find(opcode.Value) is { } details)
                {
                    instructions.Add(new Instruction(new TextRange(opcode.Index, opcode.Length), details));
                }
                continue;
            }
            foreach ((string group, TokenKind kind) in Colours)
            {
                if (token.Groups[group].Success)
                {
                    spans.Add(new SyntaxSpan(token.Index, token.Length, kind));
                    break;
                }
            }
        }
        return new IlColours([.. spans], instructions);
    }

    [GeneratedRegex("""
        ^ [\t ]* (?<address>IL_[0-9a-fA-F]+) : [\t ]+ (?<opcode>[a-z][\w.]*)
        | (?<comment>//[^\r\n]*|/\*[\s\S]*?\*/)
        | (?<literal>"(?:\\.|[^"\\\r\n])*")
        | (?<=::)(?:(?<method>'(?:\\.|[^'\\\r\n])*'|[\w.$]+)(?=\()|(?<field>'(?:\\.|[^'\\\r\n])*'|[\w.$]+))
        | (?<type>'(?:\\.|[^'\\\r\n])*'|\b[a-zA-Z_]\w*(?:\.[a-zA-Z_]\w*)+)
        | (?<address>\bIL_[0-9a-fA-F]+\b)
        | (?<directive>(?<![\w.])\.[a-z][a-z0-9_.]*)
        | (?<number>(?<![\w.])-?(?:0x[0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)(?![\w.]))
        | (?<type>\b(?:void|bool|char|string|object|u?int(?:8|16|32|64)|float(?:32|64))\b)
        | (?<keyword>\b(?:class|valuetype|public|private|family|assembly|static|instance|unsigned|native|cil|
            managed|unmanaged|init|hidebysig|extends|implements|catch|finally|fault|filter)\b)
        """, RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Tokens();

    internal readonly record struct Instruction(TextRange Range, IlOpcode Opcode);
}
