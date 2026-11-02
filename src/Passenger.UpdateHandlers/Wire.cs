namespace Passenger.UpdateHandlers;

/// <summary>What Passenger and its hook in a program debugged with Hot Reload say to each other, compiled into both ends:
/// the variable naming Passenger's socket, which the hook connects to as the program starts; then a line from Passenger
/// after each update the debugger applied, and the hook's answer once the handlers ran, a line of each handler's failure
/// then the end.</summary>
internal static class Wire
{
    /// <summary>The Unix socket Passenger listens at for the hook.</summary>
    [IdentifierHome]
    public const string SocketVariable = "PASSENGER_UPDATE_HANDLERS";

    /// <summary>Passenger's line: the debugger applied an update, every type of which may have changed.</summary>
    public const string Updated = "updated";

    /// <summary>How a line of the hook's answer begins that says a handler failed, its words after it.</summary>
    public const string Failed = "failed ";

    /// <summary>The hook's last line of an answer: the handlers ran.</summary>
    public const string Done = "done";
}
