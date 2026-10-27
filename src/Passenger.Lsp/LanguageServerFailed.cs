namespace Passenger.Lsp;

/// <summary>The language server could not answer: none is running (yet, or any longer), it ended or was ended while the
/// request waited, or it refused the request or cancelled it. The one failure a caller catches of what it asks the
/// server; its own cancellation stays an <see cref="OperationCanceledException"/>, and a bug of its own (a null, an
/// argument out of range, an answer that does not read) is never one of these.</summary>
public sealed class LanguageServerFailed(string reason, string? method = null, Exception? inner = null)
    : Exception(string.Concat(reason[..1].ToUpperInvariant(), reason[1..]), inner)
{
    /// <summary>Why, as a clause to follow what failed: "the language server stopped before it answered".</summary>
    public string Reason { get; } = reason;

    /// <summary>The request that failed, when one was sent.</summary>
    public string? Method { get; } = method;

    /// <summary>Whether the server had ended, or was ended, so nothing answered: not a refusal of its own. One started again
    /// may answer.</summary>
    public bool Ended { get; init; }
}
