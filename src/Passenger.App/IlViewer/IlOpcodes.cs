using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.Decompiler.Documentation;
using Passenger.Core;

namespace Passenger.App.IlViewer;

internal sealed record OpcodeHelp(string Name, Words Description, Words Detail);

/// <summary>Opcode names and stack effects come from the runtime, with summaries from ILSpy's .NET XML documentation loader.</summary>
internal static class IlOpcodes
{
    private static readonly IReadOnlyDictionary<string, (OpCode Code, string Field)> Definitions = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (Code: (OpCode)field.GetValue(null)!, Field: field.Name))
        .ToDictionary(entry => entry.Code.Name!, StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, OpcodeHelp> Described = new(StringComparer.Ordinal);

    public static OpcodeHelp? Find(string name) => Definitions.TryGetValue(name, out (OpCode Code, string Field) entry)
        ? Described.GetOrAdd(name, static (opcode, found) => Describe(opcode, found.Code, found.Field), entry)
        : null;

    private static OpcodeHelp Describe(string name, OpCode code, string field)
    {
        Words description = Summary(field) is { } summary ? Words.Verbatim(summary) : Fallback(name, code.FlowControl);
        return new OpcodeHelp(name, description, IlWords.Stack(Count(code.StackBehaviourPop), Count(code.StackBehaviourPush)));
    }

    private static Words Count(StackBehaviour behavior) => behavior switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 => Words.Verbatim("0"),
        StackBehaviour.Varpop or StackBehaviour.Varpush => IlWords.Varies,
        _ => Words.Verbatim((behavior.ToString().Count(ch => ch == '_') + 1).ToString(CultureInfo.InvariantCulture)),
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

    private static Words Fallback(string name, FlowControl flow) => name switch
    {
        "nop" => IlWords.Nop,
        "add" => IlWords.Add,
        "sub" => IlWords.Sub,
        "mul" => IlWords.Mul,
        "div" => IlWords.Div,
        "ldfld" => IlWords.Ldfld,
        "stfld" => IlWords.Stfld,
        "ldsfld" => IlWords.Ldsfld,
        "stsfld" => IlWords.Stsfld,
        "ldstr" => IlWords.Ldstr,
        "ldnull" => IlWords.Ldnull,
        "newobj" => IlWords.Newobj,
        "dup" => IlWords.Dup,
        "pop" => IlWords.Pop,
        "ret" => IlWords.Ret,
        _ when name.StartsWith("ldc.", StringComparison.Ordinal) => IlWords.Ldc,
        _ when name.StartsWith("ldarga", StringComparison.Ordinal) => IlWords.Ldarga,
        _ when name.StartsWith("ldarg", StringComparison.Ordinal) => IlWords.Ldarg,
        _ when name.StartsWith("ldloca", StringComparison.Ordinal) => IlWords.Ldloca,
        _ when name.StartsWith("ldloc", StringComparison.Ordinal) => IlWords.Ldloc,
        _ when name.StartsWith("stloc", StringComparison.Ordinal) => IlWords.Stloc,
        _ => flow switch
        {
            FlowControl.Call => IlWords.Call,
            FlowControl.Branch => IlWords.Branch,
            FlowControl.Cond_Branch => IlWords.CondBranch,
            FlowControl.Throw => IlWords.Throw,
            FlowControl.Meta => IlWords.Meta,
            _ => Words.Verbatim(string.Empty),
        },
    };
}
