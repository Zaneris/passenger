#nullable enable

using System;

namespace Passenger.Debugger.Interop;

public static class Cor
{
    public const int S_OK = 0;

    public static readonly Guid IID_IMetaDataImport = new Guid(2108457479u, 54190, 19573, 155, 103, 146, 128, 26, 73, 125, 68);

    public const int CORDBG_S_FUNC_EVAL_HAS_NO_RESULT = 1250070;

    /// <summary>The evaluation ended, aborted (0x131319).</summary>
    public const int CORDBG_S_FUNC_EVAL_ABORTED = 1250073;

    public const int CORDBG_E_PROCESS_TERMINATED = -2146233599;

    public const int CORDBG_E_FUNC_EVAL_BAD_START_POINT = -2146233581;

    public const int CORDBG_E_SUPERFLOUS_CONTINUE = -2146233553;

    public const int CORDBG_E_OBJECT_NEUTERED = -2146233521;

    /// <summary>A frame has no home for the argument or local at its IP, as optimized code leaves some (0x80131304).</summary>
    public const int CORDBG_E_IL_VAR_NOT_AVAILABLE = -2146233596;

    public const int CORDBG_E_ILLEGAL_SHUTDOWN_ORDER = -2146231275;

    public const int CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT = -2146231261;

    public const int CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE = -2146231258;
}
