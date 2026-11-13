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

    /// <summary>Ends a batch of changed type lines: the debugger applied the update.</summary>
    public const string Updated = "updated";

    /// <summary>A changed type's module identity and metadata token; one per line keeps each line bounded.</summary>
    public static string TypeLine(Guid module, int token) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"type {module:D} {token:X8}");

    /// <summary>Reads one changed type line.</summary>
    public static bool TryType(string line, out Guid module, out int token)
    {
        module = default;
        token = default;
        return line.Split(' ') is ["type", var id, var number]
            && Guid.TryParse(id, out module)
            && int.TryParse(number, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out token);
    }

    /// <summary>How a line of the hook's answer begins that says a handler failed, its words after it.</summary>
    public const string Failed = "failed ";

    /// <summary>The hook's last line of an answer: the handlers ran.</summary>
    public const string Done = "done";

    /// <summary>The name of the hook's thread in the program, as its threads list it.</summary>
    public const string ThreadName = "Passenger update handlers";
}
