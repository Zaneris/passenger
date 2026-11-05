using System.Globalization;
using System.Runtime.CompilerServices;

namespace Passenger.Workspace.GodotShader;

/// <summary>A preprocessor condition read as Godot reads it: ShaderExpression's tokens (servers/rendering/shader_expression.cpp,
/// MIT: integers and booleans, no floats, '^' and '~' passed over as it passes them) put in a tree by its base class
/// Expression's parser (core/math/expression.cpp: its operators' precedence and its words for what is wrong), then
/// worked out with Variant's operators on integers and booleans. A name left after the macros, or a call, has no value,
/// as with no instance Godot finds none. Pure.</summary>
internal sealed class GodotShaderExpression
{
    private readonly string text;
    private readonly Node? root;
    private int at;

    public GodotShaderExpression(string text)
    {
        this.text = text;
        root = Parse();
        if (Error is not null)
        {
            root = null;
        }
    }

    /// <summary>The first error reading the condition met, in Godot's words; null where it read.</summary>
    public string? Error { get; private set; }

    /// <summary>The condition's value, true or false as Godot booleanizes it; null where it has none.</summary>
    public bool? Value() => root is not null && Evaluate(root) is { } value ? value is bool b ? b : (long)value != 0 : null;

    private enum Kind
    {
        End,
        Open,
        Close,
        Constant,
        Identifier,
        Operator,
        Error,
    }

    private enum Op
    {
        Equal,
        NotEqual,
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
        And,
        Or,
        Not,
        Add,
        Subtract,
        Multiply,
        Divide,
        Module,
        ShiftLeft,
        ShiftRight,
        BitAnd,
        BitOr,
        Negate,
    }

    private readonly record struct Token(Kind Kind, object? Value = null, Op Op = Op.Equal);

    private abstract record Node;

    private sealed record ConstantNode(object Value) : Node;

    /// <summary>A name or a call, which has no value with no instance to find it on.</summary>
    private sealed record UnknownNode : Node;

    private sealed record OperatorNode(Op Op, Node Left, Node? Right) : Node;

    private void SetError(string error) => Error ??= error;

    private char CharAt(int index) => index < text.Length ? text[index] : '\0';

    private Token GetToken()
    {
        while (true)
        {
            char c = CharAt(at);
            if (at < text.Length)
            {
                at++;
            }
            switch (c)
            {
                case '\0':
                    return new Token(Kind.End);
                case '(':
                    return new Token(Kind.Open);
                case ')':
                    return new Token(Kind.Close);
                case '=':
                    c = CharAt(at);
                    if (at < text.Length)
                    {
                        at++;
                    }
                    if (c == '=')
                    {
                        return new Token(Kind.Operator, Op: Op.Equal);
                    }
                    SetError("Expected '='.");
                    return new Token(Kind.Error);
                case '!':
                    if (CharAt(at) == '=')
                    {
                        at++;
                        return new Token(Kind.Operator, Op: Op.NotEqual);
                    }
                    return new Token(Kind.Operator, Op: Op.Not);
                case '>':
                    if (CharAt(at) == '=')
                    {
                        at++;
                        return new Token(Kind.Operator, Op: Op.GreaterEqual);
                    }
                    if (CharAt(at) == '>')
                    {
                        at++;
                        return new Token(Kind.Operator, Op: Op.ShiftRight);
                    }
                    return new Token(Kind.Operator, Op: Op.Greater);
                case '<':
                    if (CharAt(at) == '=')
                    {
                        at++;
                        return new Token(Kind.Operator, Op: Op.LessEqual);
                    }
                    if (CharAt(at) == '<')
                    {
                        at++;
                        return new Token(Kind.Operator, Op: Op.ShiftLeft);
                    }
                    return new Token(Kind.Operator, Op: Op.Less);
                case '+':
                    return new Token(Kind.Operator, Op: Op.Add);
                case '-':
                    return new Token(Kind.Operator, Op: Op.Subtract);
                case '/':
                    return new Token(Kind.Operator, Op: Op.Divide);
                case '*':
                    return new Token(Kind.Operator, Op: Op.Multiply);
                case '%':
                    return new Token(Kind.Operator, Op: Op.Module);
                case '&':
                    if (CharAt(at) == '&')
                    {
                        at++;
                        return new Token(Kind.Operator, Op: Op.And);
                    }
                    return new Token(Kind.Operator, Op: Op.BitAnd);
                case '|':
                    if (CharAt(at) == '|')
                    {
                        at++;
                        return new Token(Kind.Operator, Op: Op.Or);
                    }
                    return new Token(Kind.Operator, Op: Op.BitOr);
                case '^':
                case '~':
                    // Godot's tokenizer breaks out of its switch here rather than returning, and reads on past them.
                    continue;
                case '{' or '}' or '[' or ']' or ',' or ':' or '$' or '\'' or '"':
                    SetError("Unexpected character.");
                    return new Token(Kind.Error);
                default:
                    if (c <= ' ')
                    {
                        continue;
                    }
                    if (char.IsAsciiDigit(c))
                    {
                        return Number(c);
                    }
                    if (char.IsLetter(c) || c == '_')
                    {
                        int start = at - 1;
                        while (char.IsLetterOrDigit(CharAt(at)) || CharAt(at) == '_')
                        {
                            at++;
                        }
                        string id = text[start..at];
                        return id switch
                        {
                            "true" => new Token(Kind.Constant, true),
                            "false" => new Token(Kind.Constant, false),
                            _ => new Token(Kind.Identifier, id),
                        };
                    }
                    SetError("Unexpected character.");
                    return new Token(Kind.Error);
            }
        }
    }

    /// <summary>A number as ShaderExpression reads it: decimal, 0x hex or 0b binary, a float refused.</summary>
    private Token Number(char first)
    {
        char next = CharAt(at);
        int start = at - 1;
        char c = first;
        const int Int = 1, Hex = 2, Bin = 3, Done = 6;
        int reading = Int;
        bool binary = false;
        bool hex = false;
        bool firstChar = true;
        while (true)
        {
            switch (reading)
            {
                case Int:
                    if (char.IsAsciiDigit(c))
                    {
                        if (firstChar && c == '0')
                        {
                            reading = next is 'b' or 'B' ? Bin : next is 'x' or 'X' ? Hex : reading;
                        }
                    }
                    else if (c is '.' or 'e' or 'E')
                    {
                        SetError("Floating-point number operations are not supported by the shader preprocessor.");
                        return new Token(Kind.Error);
                    }
                    else
                    {
                        reading = Done;
                    }
                    break;
                case Bin:
                    if (binary && c is not ('0' or '1'))
                    {
                        reading = Done;
                    }
                    else if (c is 'b' or 'B')
                    {
                        binary = true;
                    }
                    break;
                case Hex:
                    if (hex && !char.IsAsciiHexDigit(c))
                    {
                        reading = Done;
                    }
                    else if (c is 'x' or 'X')
                    {
                        hex = true;
                    }
                    break;
            }
            if (reading == Done)
            {
                break;
            }
            c = CharAt(at);
            if (at < text.Length)
            {
                at++;
            }
            firstChar = false;
        }
        if (c != '\0')
        {
            at--;
        }
        string number = text[start..at];
        long value = binary ? Parsed(number[2..], 2) : hex ? Parsed(number[2..], 16) : Parsed(number, 10);
        return new Token(Kind.Constant, value);

        static long Parsed(string digits, int radix)
        {
            long value = 0;
            foreach (char d in digits)
            {
                int digit = radix == 16 ? int.Parse(d.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : d - '0';
                value = unchecked((value * radix) + digit);
            }
            return value;
        }
    }

    private Node? Parse()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            SetError(GodotShaderLanguage.TooDeep);
            return null;
        }
        List<(bool IsOp, Op Op, Node? Node)> nodes = [];
        while (true)
        {
            Token token = GetToken();
            if (Error is not null)
            {
                return null;
            }
            Node expression;
            switch (token.Kind)
            {
                case Kind.Open:
                    Node? inner = Parse();
                    if (Error is not null)
                    {
                        return null;
                    }
                    if (GetToken().Kind != Kind.Close)
                    {
                        SetError("Expected ')'");
                        return null;
                    }
                    expression = inner!;
                    break;
                case Kind.Identifier:
                    int before = at;
                    if (GetToken().Kind == Kind.Open)
                    {
                        while (true)
                        {
                            int argument = at;
                            if (GetToken().Kind == Kind.Close)
                            {
                                break;
                            }
                            at = argument;
                            if (Parse() is null)
                            {
                                return null;
                            }
                            argument = at;
                            if (GetToken().Kind == Kind.Close)
                            {
                                at = argument;
                            }
                            else
                            {
                                // No comma is a token here: one is an unexpected character, said as it is read.
                                SetError("Expected ',' or ')'");
                            }
                        }
                    }
                    else
                    {
                        at = before;
                    }
                    expression = new UnknownNode();
                    break;
                case Kind.Constant:
                    expression = new ConstantNode(token.Value!);
                    break;
                case Kind.Operator when token.Op == Op.Subtract:
                    nodes.Add((true, Op.Negate, null));
                    continue;
                case Kind.Operator when token.Op == Op.Not:
                    nodes.Add((true, Op.Not, null));
                    continue;
                default:
                    SetError("Expected expression.");
                    return null;
            }
            nodes.Add((false, Op.Equal, expression));
            int afterOperand = at;
            Token next = GetToken();
            if (Error is not null)
            {
                return null;
            }
            if (next.Kind != Kind.Operator)
            {
                at = afterOperand;
                break;
            }
            nodes.Add((true, next.Op, null));
        }
        while (nodes.Count > 1)
        {
            int nextOp = -1;
            int least = int.MaxValue;
            bool unary = false;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!nodes[i].IsOp)
                {
                    continue;
                }
                (int priority, bool isUnary) = Priority(nodes[i].Op);
                if (priority < least)
                {
                    nextOp = i;
                    least = priority;
                    unary = isUnary;
                }
            }
            if (unary)
            {
                int operand = nextOp;
                while (nodes[operand].IsOp)
                {
                    operand++;
                    if (operand == nodes.Count)
                    {
                        SetError("Unexpected end of expression...");
                        return null;
                    }
                }
                for (int i = operand - 1; i >= nextOp; i--)
                {
                    nodes[i] = (false, Op.Equal, new OperatorNode(nodes[i].Op, nodes[i + 1].Node!, null));
                    nodes.RemoveAt(i + 1);
                }
                continue;
            }
            if (nextOp < 1 || nextOp >= nodes.Count - 1 || nodes[nextOp - 1].IsOp)
            {
                SetError("Parser bug...");
                return null;
            }
            if (nodes[nextOp + 1].IsOp)
            {
                SetError("Unexpected two consecutive operators.");
                return null;
            }
            nodes[nextOp - 1] = (false, Op.Equal, new OperatorNode(nodes[nextOp].Op, nodes[nextOp - 1].Node!, nodes[nextOp + 1].Node!));
            nodes.RemoveAt(nextOp);
            nodes.RemoveAt(nextOp);
        }
        return nodes.Count == 0 ? null : nodes[0].Node;
    }

    private static (int Priority, bool Unary) Priority(Op op) => op switch
    {
        Op.Negate => (2, true),
        Op.Multiply or Op.Divide or Op.Module => (3, false),
        Op.Add or Op.Subtract => (4, false),
        Op.ShiftLeft or Op.ShiftRight => (5, false),
        Op.BitAnd => (6, false),
        Op.BitOr => (8, false),
        Op.Less or Op.LessEqual or Op.Greater or Op.GreaterEqual or Op.Equal or Op.NotEqual => (9, false),
        Op.Not => (12, true),
        Op.And => (13, false),
        _ => (14, false),
    };

    /// <summary>A node's value as Variant's operators make it, a long or a bool; null where they have none.</summary>
    private object? Evaluate(Node node)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            SetError(GodotShaderLanguage.TooDeep);
            return null;
        }
        switch (node)
        {
            case ConstantNode constant:
                return constant.Value;
            case OperatorNode { Right: null } unary:
                object? operand = Evaluate(unary.Left);
                return operand is null ? null : unary.Op switch
                {
                    Op.Negate => operand is long n ? unchecked(-n) : null,
                    _ => !Truth(operand),
                };
            case OperatorNode binary:
                if (Evaluate(binary.Left) is not { } a || Evaluate(binary.Right!) is not { } b)
                {
                    return null;
                }
                if (binary.Op is Op.And or Op.Or)
                {
                    return binary.Op == Op.And ? Truth(a) && Truth(b) : Truth(a) || Truth(b);
                }
                if (a is bool x && b is bool y)
                {
                    return binary.Op switch
                    {
                        Op.Equal => x == y,
                        Op.NotEqual => x != y,
                        Op.Less => !x && y,
                        Op.LessEqual => !x || y,
                        Op.Greater => x && !y,
                        Op.GreaterEqual => x || !y,
                        _ => null,
                    };
                }
                if (a is not long l || b is not long r)
                {
                    return null;
                }
                return binary.Op switch
                {
                    Op.Add => unchecked(l + r),
                    Op.Subtract => unchecked(l - r),
                    Op.Multiply => unchecked(l * r),
                    Op.Divide => r == 0 ? null : l == long.MinValue && r == -1 ? l : l / r,
                    Op.Module => r == 0 ? null : l == long.MinValue && r == -1 ? 0L : l % r,
                    Op.ShiftLeft => l < 0 || r < 0 ? null : l << (int)r,
                    Op.ShiftRight => l < 0 || r < 0 ? null : l >> (int)r,
                    Op.BitAnd => l & r,
                    Op.BitOr => l | r,
                    Op.Equal => l == r,
                    Op.NotEqual => l != r,
                    Op.Less => l < r,
                    Op.LessEqual => l <= r,
                    Op.Greater => l > r,
                    Op.GreaterEqual => l >= r,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static bool Truth(object value) => value is bool b ? b : (long)value != 0;
}
