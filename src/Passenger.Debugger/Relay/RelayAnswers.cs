using Passenger.Debugger.Interop;

namespace Passenger.Debugger.Relay;

/// <summary>How long an answer from the app holds: never kept, while the object lives, or until the program runs again.</summary>
internal enum AnswerScope
{
    None,
    Object,
    Stop,
}

/// <summary>What the stand-ins may answer without asking the app again: the facts of an object that never change while
/// it lives (a class's token, a type's element type), and what an object says of a stopped program until the program
/// runs or is written to. Also which objects the app's debugger keeps for as long as their module is loaded, whose
/// references Passenger keeps too, so their answers outlive any one stop.</summary>
internal static class RelayAnswers
{
    private static readonly Dictionary<Type, string[]> s_forTheObject = new()
    {
        [typeof(ICorDebugProcess)] = [nameof(ICorDebugProcess.TryGetID)],
        [typeof(ICorDebugAppDomain)] = [nameof(ICorDebugAppDomain.TryGetProcess), nameof(ICorDebugAppDomain.TryGetID), nameof(ICorDebugAppDomain.TryGetName)],
        [typeof(ICorDebugAssembly)] = [nameof(ICorDebugAssembly.TryGetProcess), nameof(ICorDebugAssembly.TryGetAppDomain), nameof(ICorDebugAssembly.TryGetName), nameof(ICorDebugAssembly.TryGetCodeBase)],
        [typeof(ICorDebugModule)] =
        [
            nameof(ICorDebugModule.TryGetProcess), nameof(ICorDebugModule.TryGetBaseAddress), nameof(ICorDebugModule.TryGetAssembly),
            nameof(ICorDebugModule.TryGetName), nameof(ICorDebugModule.TryGetToken), nameof(ICorDebugModule.TryIsDynamic),
            nameof(ICorDebugModule.TryGetSize), nameof(ICorDebugModule.TryIsInMemory), nameof(ICorDebugModule.TryGetFunctionFromToken),
            nameof(ICorDebugModule.TryGetClassFromToken),
        ],
        [typeof(ICorDebugClass)] = [nameof(ICorDebugClass.TryGetModule), nameof(ICorDebugClass.TryGetToken)],
        [typeof(ICorDebugType)] =
        [
            nameof(ICorDebugType.TryGetType), nameof(ICorDebugType.TryGetClass), nameof(ICorDebugType.TryGetFirstTypeParameter),
            nameof(ICorDebugType.TryGetBase), nameof(ICorDebugType.TryGetRank),
        ],
        [typeof(ICorDebugType2)] = [nameof(ICorDebugType2.TryGetTypeID)],
        [typeof(ICorDebugFunction)] = [nameof(ICorDebugFunction.TryGetModule), nameof(ICorDebugFunction.TryGetClass), nameof(ICorDebugFunction.TryGetToken)],
        [typeof(ICorDebugCode)] =
        [
            nameof(ICorDebugCode.TryIsIL), nameof(ICorDebugCode.TryGetFunction), nameof(ICorDebugCode.TryGetAddress),
            nameof(ICorDebugCode.TryGetSize), nameof(ICorDebugCode.TryGetVersionNumber), nameof(ICorDebugCode.TryGetILToNativeMapping),
            nameof(ICorDebugCode.TryGetCode),
        ],
        [typeof(ICorDebugThread)] = [nameof(ICorDebugThread.TryGetProcess), nameof(ICorDebugThread.TryGetID)],
    };

    private static readonly Dictionary<Type, string[]> s_forTheStop = new()
    {
        [typeof(ICorDebugProcess)] = [nameof(ICorDebugProcess.TryGetThread)],
        [typeof(ICorDebugValue)] = [nameof(ICorDebugValue.TryGetType), nameof(ICorDebugValue.TryGetSize), nameof(ICorDebugValue.TryGetAddress)],
        [typeof(ICorDebugValue2)] = [nameof(ICorDebugValue2.TryGetExactType)],
        [typeof(ICorDebugReferenceValue)] = [nameof(ICorDebugReferenceValue.TryIsNull), nameof(ICorDebugReferenceValue.TryGetValue), nameof(ICorDebugReferenceValue.TryDereference)],
        [typeof(ICorDebugObjectValue)] = [nameof(ICorDebugObjectValue.TryGetClass), nameof(ICorDebugObjectValue.TryIsValueClass)],
        [typeof(ICorDebugBoxValue)] = [nameof(ICorDebugBoxValue.TryGetObject)],
        [typeof(ICorDebugStringValue)] = [nameof(ICorDebugStringValue.TryGetLength), nameof(ICorDebugStringValue.TryGetString)],
        [typeof(ICorDebugArrayValue)] =
        [
            nameof(ICorDebugArrayValue.TryGetElementType), nameof(ICorDebugArrayValue.TryGetRank), nameof(ICorDebugArrayValue.TryGetCount),
            nameof(ICorDebugArrayValue.TryGetDimensions), nameof(ICorDebugArrayValue.TryHasBaseIndicies), nameof(ICorDebugArrayValue.TryGetBaseIndicies),
        ],
        [typeof(ICorDebugFrame)] =
        [
            nameof(ICorDebugFrame.TryGetChain), nameof(ICorDebugFrame.TryGetCode), nameof(ICorDebugFrame.TryGetFunction),
            nameof(ICorDebugFrame.TryGetFunctionToken), nameof(ICorDebugFrame.TryGetStackRange), nameof(ICorDebugFrame.TryGetCaller),
            nameof(ICorDebugFrame.TryGetCallee),
        ],
        [typeof(ICorDebugILFrame)] = [nameof(ICorDebugILFrame.TryGetIP)],
        [typeof(ICorDebugChain)] =
        [
            nameof(ICorDebugChain.TryGetThread), nameof(ICorDebugChain.TryGetStackRange), nameof(ICorDebugChain.TryGetCaller),
            nameof(ICorDebugChain.TryGetCallee), nameof(ICorDebugChain.TryIsManaged), nameof(ICorDebugChain.TryGetReason),
            nameof(ICorDebugChain.TryGetActiveFrame),
        ],
        [typeof(ICorDebugThread)] =
        [
            nameof(ICorDebugThread.TryGetAppDomain), nameof(ICorDebugThread.TryGetActiveChain), nameof(ICorDebugThread.TryGetActiveFrame),
            nameof(ICorDebugThread.TryGetUserState), nameof(ICorDebugThread.TryGetDebugState),
        ],
    };

    private static readonly HashSet<Type> s_kept =
    [
        typeof(ICorDebug), typeof(ICorDebugProcess), typeof(ICorDebugAppDomain), typeof(ICorDebugAssembly), typeof(ICorDebugModule),
        typeof(ICorDebugClass), typeof(ICorDebugType), typeof(ICorDebugFunction), typeof(ICorDebugCode),
    ];

    private static readonly HashSet<string> s_changing =
    [
        nameof(ICorDebugController.TryContinue), nameof(ICorDebugController.TryStop), nameof(ICorDebugController.TryDetach),
        nameof(ICorDebugController.TryTerminate), nameof(ICorDebugProcess.TryWriteMemory), nameof(ICorDebugModule2.TryApplyChanges),
        nameof(ICorDebugILFrame2.TryRemapFunction),
    ];

    public static IReadOnlyDictionary<Type, string[]> ForTheObject => s_forTheObject;

    public static IReadOnlyDictionary<Type, string[]> ForTheStop => s_forTheStop;

    public static AnswerScope ScopeOf(Type type, string method) =>
        s_forTheObject.TryGetValue(type, out string[]? lasting) && lasting.Contains(method) ? AnswerScope.Object
        : s_forTheStop.TryGetValue(type, out string[]? stopped) && stopped.Contains(method) ? AnswerScope.Stop
        : AnswerScope.None;

    /// <summary>Whether the app's debugger keeps an object of this kind for as long as its module or process lives, so
    /// that holding it costs the app nothing.</summary>
    public static bool Kept(Type type) => s_kept.Contains(type);

    /// <summary>Whether a call lets the program run or writes to it, after which nothing it said while stopped holds.</summary>
    public static bool Changes(string method) =>
        s_changing.Contains(method) || method.StartsWith("TrySet", StringComparison.Ordinal) || method.StartsWith("TryClear", StringComparison.Ordinal);
}
