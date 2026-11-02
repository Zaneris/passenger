namespace Passenger.Debugger.PresentationHintModels;

public enum AttributesValue : uint
{
    None = 0,

    /// <summary>The protocol's readOnly: the row's expression reaches only a copy the debugger made to show it, which a value
    /// set through it would change, leaving the program's own as it was.</summary>
    ReadOnly = 1,
    FailedEvaluation = 128,

    /// <summary>A failed evaluation that timed out with every other thread held, which probably needs another thread to run:
    /// the protocol's failedEvaluation and Passenger's own needsThreads (<see cref="Dap.DapAttributes.NeedsThreads"/>).</summary>
    FailedNeedingThreads = 129,
}
