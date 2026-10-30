namespace Passenger.App.Run;

/// <summary>What a program's output says of its serving, in the words .NET's host writes as it starts
/// (Microsoft.Hosting.Lifetime's, in whatever format its logger gives them): each address it listens at, once, and
/// whether its host has started, after which it serves until it is stopped.</summary>
public sealed class ListeningLines
{
    private const string Listening = "Now listening on: ";
    private const string Started = "Application started.";

    private static readonly System.Buffers.SearchValues<char> Ends = System.Buffers.SearchValues.Create(" \t\u001b\"");

    private readonly List<string> addresses = [];

    /// <summary>The addresses heard, in the order the program said them.</summary>
    public IReadOnlyList<string> Addresses => addresses;

    /// <summary>Whether the program said its host has started.</summary>
    public bool HostStarted { get; private set; }

    /// <summary>Takes in a whole line of the program's output.</summary>
    public void Hear(ReadOnlySpan<char> line)
    {
        int at = line.IndexOf(Listening, StringComparison.Ordinal);
        if (at >= 0)
        {
            ReadOnlySpan<char> rest = line[(at + Listening.Length)..].TrimStart();
            // The address ends where a space, a colour's escape or a JSON string does.
            int end = rest.IndexOfAny(Ends);
            string address = (end < 0 ? rest : rest[..end]).ToString();
            if (address.Length > 0 && !addresses.Contains(address))
            {
                addresses.Add(address);
            }
        }
        else if (line.Contains(Started, StringComparison.Ordinal))
        {
            HostStarted = true;
        }
    }
}
