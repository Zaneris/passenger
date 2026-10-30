using Passenger.Debugger.PresentationHintModels;

namespace Passenger.Debugger.Models.Response;

public class VariableInfo
{
    public required string Name { get; set; }

    /// <summary>The C# that reaches the value from the frame (a return value's $ReturnValue, a member's after its owner's);
    /// null for a row no expression reaches (a scope, a range of elements, a raw view).</summary>
    public string? EvaluateName { get; set; }

    /// <summary>The type declaring a member that a type derived from it hides by its name: C# reaches it through a cast to
    /// this type. Null for any other row.</summary>
    public string? DeclaringType { get; set; }
    public required string Value { get; set; }
    public required string? Type { get; set; }
    public required int VariablesReference { get; set; }
    public VariablePresentationHint? PresentationHint { get; set; }

    /// <summary>The member this row is, by the name C# reaches it by from its owner's expression; null for a row reached
    /// otherwise.</summary>
    internal string? Member { get; set; }

    /// <summary>The interface whose explicit implementation the member is, which only a cast reaches it through.</summary>
    internal string? Interface { get; set; }
}
