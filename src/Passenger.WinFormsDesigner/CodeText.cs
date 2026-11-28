using System.CodeDom;
using System.CodeDom.Compiler;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.CSharp;

namespace Passenger.WinFormsDesigner;

/// <summary>Windows Forms' serializers' code (CodeDOM) as a designer file writes it. A file of .NET Framework's designer
/// (<c>qualified</c>) takes CodeDOM's own C# as that designer wrote it: <c>this.</c>, full type names, every cast and
/// operation in parentheses. Any other takes it as .NET's designer writes it: the class's members by name alone, each type
/// by the shortest name the file's scope reads as it (<see cref="Types.Spelled"/>), a cast of a number or of flags ored
/// together left out, flags ored without parentheses, a handler by its method's name.</summary>
internal sealed class CodeText(bool qualified, Types types)
{
    private static readonly CodeGeneratorOptions Options = new() { BracingStyle = "C", IndentString = "    ", BlankLinesBetweenMembers = false };

    /// <summary>The types a number's cast to is left out for: C# converts the number itself where it fits.</summary>
    private static readonly HashSet<string> Numbers = ["System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64",
        "System.UInt64", "System.Single", "System.Double", "System.Decimal", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal"];

    private readonly CSharpCodeProvider provider = new();

    /// <summary>A statement's code, its lines broken with '\n'.</summary>
    public string Statement(CodeStatement statement) => qualified ? Generated(writer => provider.GenerateCodeFromStatement(statement, writer, Options)) : Modern(statement);

    /// <summary>A field's declaration.</summary>
    public string Field(CodeMemberField field) => qualified
        ? Generated(writer => provider.GenerateCodeFromMember(field, writer, Options))
        : $"{Access(field.Attributes)} {Type(field.Type)} {field.Name};";

    /// <summary>A statement as CodeDOM writes it, whatever the file's style, which tells two statements' code apart exactly.</summary>
    public string Exact(CodeStatement statement) => Generated(writer => provider.GenerateCodeFromStatement(statement, writer, Options));

    private static string Generated(Action<TextWriter> generate)
    {
        using StringWriter writer = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
        generate(writer);
        return writer.ToString().TrimEnd('\n');
    }

    private static string Access(MemberAttributes attributes) => (attributes & MemberAttributes.AccessMask) switch
    {
        MemberAttributes.Public => "public",
        MemberAttributes.Assembly => "internal",
        MemberAttributes.Family => "protected",
        MemberAttributes.FamilyOrAssembly => "protected internal",
        _ => "private",
    };

    private string Modern(CodeStatement statement) => statement switch
    {
        CodeAssignStatement assigned => $"{Expression(assigned.Left)} = {Expression(assigned.Right)};",
        CodeAttachEventStatement hooked => $"{Member(hooked.Event.TargetObject, hooked.Event.EventName)} += {Expression(hooked.Listener)};",
        CodeRemoveEventStatement unhooked => $"{Member(unhooked.Event.TargetObject, unhooked.Event.EventName)} -= {Expression(unhooked.Listener)};",
        CodeExpressionStatement { Expression: var expression } => $"{Expression(expression)};",
        CodeVariableDeclarationStatement local => local.InitExpression is { } made
            ? $"{Type(local.Type)} {local.Name} = {Expression(made)};"
            : $"{Type(local.Type)} {local.Name};",
        _ => Exact(statement),
    };

    private string Expression(CodeExpression expression) => expression switch
    {
        CodeThisReferenceExpression => "this",
        CodeBaseReferenceExpression => "base",
        CodeVariableReferenceExpression variable => variable.VariableName,
        CodeArgumentReferenceExpression argument => argument.ParameterName,
        CodeFieldReferenceExpression field => Member(field.TargetObject, field.FieldName),
        CodePropertyReferenceExpression property => Member(property.TargetObject, property.PropertyName),
        CodeMethodInvokeExpression call => $"{Member(call.Method.TargetObject, call.Method.MethodName)}({Arguments(call.Parameters)})",
        CodeObjectCreateExpression made => $"new {Type(made.CreateType)}({Arguments(made.Parameters)})",
        CodePrimitiveExpression literal => Literal(literal.Value),
        CodeCastExpression { Expression: CodePrimitiveExpression literal } cast when Numbers.Contains(cast.TargetType.BaseType) => Literal(literal.Value),
        CodeCastExpression { Expression: CodeCastExpression { Expression: CodePrimitiveExpression } inner } cast when Numbers.Contains(cast.TargetType.BaseType) => Expression(inner),
        CodeCastExpression { Expression: CodeBinaryOperatorExpression { Operator: CodeBinaryOperatorType.BitwiseOr } ored } => Expression(ored),
        CodeCastExpression cast => $"({Type(cast.TargetType)}){Operand(cast.Expression)}",
        CodeBinaryOperatorExpression { Operator: CodeBinaryOperatorType.BitwiseOr } ored => $"{Ored(ored.Left)} | {Ored(ored.Right)}",
        CodeBinaryOperatorExpression operation => $"{Operand(operation.Left)} {Operator(operation.Operator)} {Operand(operation.Right)}",
        CodeArrayCreateExpression array when array.Initializers.Count > 0 => $"new {Type(array.CreateType)}[] {{ {Arguments(array.Initializers)} }}",
        CodeArrayCreateExpression array => $"new {Type(array.CreateType)}[{(array.SizeExpression is { } size ? Expression(size) : array.Size.ToString(CultureInfo.InvariantCulture))}]",
        CodeTypeOfExpression typed => $"typeof({Type(typed.Type)})",
        CodeTypeReferenceExpression type => Type(type.Type),
        CodeDelegateCreateExpression handler => handler.TargetObject is CodeThisReferenceExpression ? handler.MethodName : Member(handler.TargetObject, handler.MethodName),
        CodeIndexerExpression indexed => $"{Operand(indexed.TargetObject)}[{Arguments(indexed.Indices)}]",
        CodeArrayIndexerExpression indexed => $"{Operand(indexed.TargetObject)}[{Arguments(indexed.Indices)}]",
        CodeSnippetExpression snippet => snippet.Value,
        _ => Generated(writer => provider.GenerateCodeFromExpression(expression, writer, Options)),
    };

    /// <summary>A member of what an expression gives: of the class itself by its name alone, of a type through the type's
    /// name, of a cast's result through the cast in parentheses.</summary>
    private string Member(CodeExpression? on, string name) => on switch
    {
        null or CodeThisReferenceExpression => name,
        CodeCastExpression cast => $"(({Type(cast.TargetType)}){Operand(cast.Expression)}).{name}",
        _ => $"{Operand(on)}.{name}",
    };

    /// <summary>An operand: in parentheses where it is an operation, a cast or a negative number.</summary>
    private string Operand(CodeExpression expression)
    {
        string written = Expression(expression);
        return expression is CodeBinaryOperatorExpression or CodeCastExpression || written.StartsWith('-') ? $"({written})" : written;
    }

    private string Ored(CodeExpression expression) => expression is CodeBinaryOperatorExpression { Operator: CodeBinaryOperatorType.BitwiseOr } inner
        ? $"{Ored(inner.Left)} | {Ored(inner.Right)}"
        : expression is CodeCastExpression { Expression: CodeBinaryOperatorExpression { Operator: CodeBinaryOperatorType.BitwiseOr } cast } ? Ored(cast) : Operand(expression);

    private string Arguments(CodeExpressionCollection arguments)
    {
        StringBuilder written = new();
        foreach (CodeExpression argument in arguments)
        {
            written.Append(written.Length > 0 ? ", " : string.Empty).Append(Expression(argument));
        }
        return written.ToString();
    }

    private static string Operator(CodeBinaryOperatorType type) => type switch
    {
        CodeBinaryOperatorType.Add => "+",
        CodeBinaryOperatorType.Subtract => "-",
        CodeBinaryOperatorType.Multiply => "*",
        CodeBinaryOperatorType.Divide => "/",
        CodeBinaryOperatorType.Modulus => "%",
        CodeBinaryOperatorType.BitwiseAnd => "&",
        CodeBinaryOperatorType.BooleanAnd => "&&",
        CodeBinaryOperatorType.BooleanOr => "||",
        CodeBinaryOperatorType.IdentityEquality or CodeBinaryOperatorType.ValueEquality => "==",
        CodeBinaryOperatorType.IdentityInequality => "!=",
        CodeBinaryOperatorType.LessThan => "<",
        CodeBinaryOperatorType.LessThanOrEqual => "<=",
        CodeBinaryOperatorType.GreaterThan => ">",
        CodeBinaryOperatorType.GreaterThanOrEqual => ">=",
        _ => "|",
    };

    /// <summary>A literal as C# writes it: a float with F, a double with D, a decimal with M, a string and a char escaped.</summary>
    private static string Literal(object? value) => value switch
    {
        null => "null",
        string text => Quoted(text, '"'),
        char single => Quoted(single.ToString(), '\''),
        bool truth => truth ? "true" : "false",
        float number => number.ToString("R", CultureInfo.InvariantCulture) + "F",
        double number => number.ToString("R", CultureInfo.InvariantCulture) + "D",
        decimal number => number.ToString(CultureInfo.InvariantCulture) + "M",
        long number => number.ToString(CultureInfo.InvariantCulture) + "L",
        ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
        uint number => number.ToString(CultureInfo.InvariantCulture) + "U",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    private static string Quoted(string text, char quote)
    {
        StringBuilder written = new();
        written.Append(quote);
        foreach (char c in text)
        {
            written.Append(c switch
            {
                '\\' => "\\\\",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when c == quote => $"\\{quote}",
                _ when char.IsControl(c) || c is '\u2028' or '\u2029' or '\u0085' => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }
        return written.Append(quote).ToString();
    }

    private string Type(CodeTypeReference type)
    {
        if (type.ArrayElementType is { } element && type.ArrayRank > 0)
        {
            return $"{Type(element)}[]";
        }
        string name = type.BaseType;
        if (types.Find(name) is { } found)
        {
            return types.Spelled(found);
        }
        return name.Replace('+', '.');
    }
}
