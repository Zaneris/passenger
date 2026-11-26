using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.Decompiler.Documentation;

namespace Passenger.App.IlViewer;

internal sealed record IlOpcode(string Name, string Description, string Stack);

/// <summary>Opcode names and stack effects come from the runtime, with summaries from ILSpy's .NET XML documentation loader.</summary>
internal static class IlOpcodes
{
    private static readonly IReadOnlyDictionary<string, (OpCode Code, string Field)> Definitions = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (Code: (OpCode)field.GetValue(null)!, Field: field.Name))
        .ToDictionary(entry => entry.Code.Name!, StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IlOpcode> Described = new(StringComparer.Ordinal);

    public static IlOpcode? Find(string name) => Definitions.TryGetValue(name, out (OpCode Code, string Field) entry)
        ? Described.GetOrAdd(name, static (opcode, found) => Describe(opcode, found.Code, found.Field), entry)
        : null;

    private static IlOpcode Describe(string name, OpCode code, string field)
    {
        string description = Summary(field) ?? Fallback(name, code.FlowControl);
        return new IlOpcode(name, description, $"Stack   pop {Count(code.StackBehaviourPop)} · push {Count(code.StackBehaviourPush)}");
    }

    private static string Count(StackBehaviour behavior) => behavior switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 => "0",
        StackBehaviour.Varpop or StackBehaviour.Varpush => "varies",
        _ => (behavior.ToString().Count(ch => ch == '_') + 1).ToString(CultureInfo.InvariantCulture),
    };

    private static string? Summary(string field)
    {
        try
        {
            string? xml = XmlDocLoader.MscorlibDocumentation?.GetDocumentation($"F:System.Reflection.Emit.OpCodes.{field}");
            if (xml is null || XElement.Parse($"<member>{xml}</member>").Element("summary") is not { } summary)
            {
                return null;
            }
            foreach (XElement reference in summary.Descendants("see").Where(element => element.IsEmpty).ToArray())
            {
                string name = (string?)reference.Attribute("langword") ?? (string?)reference.Attribute("cref") ?? string.Empty;
                reference.ReplaceWith(new XText(name[(name.LastIndexOf('.') + 1)..]));
            }
            string words = string.Join(' ', summary.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return words.Length > 0 ? words : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Fallback(string name, FlowControl flow) => name switch
    {
        "nop" => "Performs no operation.",
        "add" => "Adds the top two stack values.",
        "sub" => "Subtracts the top value from the value below it.",
        "mul" => "Multiplies the top two stack values.",
        "div" => "Divides the second stack value by the top value.",
        "ldfld" => "Loads an instance field's value.",
        "stfld" => "Stores a value in an instance field.",
        "ldsfld" => "Loads a static field's value.",
        "stsfld" => "Stores a value in a static field.",
        "ldstr" => "Pushes a reference to the literal string.",
        "ldnull" => "Pushes a null reference.",
        "newobj" => "Creates an object by calling its constructor.",
        "dup" => "Duplicates the value on top of the stack.",
        "pop" => "Discards the value on top of the stack.",
        "ret" => "Returns to the caller, with the result for a non-void method.",
        _ when name.StartsWith("ldc.", StringComparison.Ordinal) => "Pushes a numeric constant onto the stack.",
        _ when name.StartsWith("ldarga", StringComparison.Ordinal) => "Pushes the address of an argument.",
        _ when name.StartsWith("ldarg", StringComparison.Ordinal) => "Pushes the value of an argument.",
        _ when name.StartsWith("ldloca", StringComparison.Ordinal) => "Pushes the address of a local variable.",
        _ when name.StartsWith("ldloc", StringComparison.Ordinal) => "Pushes the value of a local variable.",
        _ when name.StartsWith("stloc", StringComparison.Ordinal) => "Stores the top stack value in a local variable.",
        _ => flow switch
        {
            FlowControl.Call => "Invokes the specified member.",
            FlowControl.Branch => "Transfers control to another IL offset.",
            FlowControl.Cond_Branch => "Chooses a branch target based on the stack values.",
            FlowControl.Throw => "Raises an exception.",
            FlowControl.Meta => "Modifies how the next instruction is executed.",
            _ => string.Empty,
        },
    };
}
