namespace Passenger.Debugger.PresentationHintModels;

public enum AttributesValue : uint
{
    None = 0,

    /// <summary>The protocol's readOnly: the row's expression reaches only a copy the debugger made to show it, which a value
    /// set through it would change, leaving the program's own as it was.</summary>
    ReadOnly = 1,
    FailedEvaluation = 128,
}
