using System.Globalization;
using System.Text;

namespace Passenger.App.Testing;

/// <summary>What <c>am instrument -r</c> writes of an Android instrumentation's run, read a line at a time: each status the
/// instrumentation sends, its keys and values (a value going on over the lines after it), handed back as its status code
/// ends it (<see cref="Read"/>); then the result it finishes with and its code. A device test project's instrumentation
/// sends a status as each test ends (<see cref="Ended"/>), and names the results file its run wrote (<see cref="ResultsPath"/>).</summary>
public sealed class InstrumentationLines
{
    private const string Status = "INSTRUMENTATION_STATUS: ";
    private const string StatusCode = "INSTRUMENTATION_STATUS_CODE: ";
    private const string Result = "INSTRUMENTATION_RESULT: ";
    private const string Code = "INSTRUMENTATION_CODE: ";
    private const string Failed = "INSTRUMENTATION_FAILED: ";

    /// <summary>The code an instrumentation finishes with where all went as it meant (Android's <c>Activity.RESULT_OK</c>).</summary>
    private const int Finished = -1;

    private Dictionary<string, string>? status;
    private string? statusKey;
    private readonly Dictionary<string, string> result = new(StringComparer.Ordinal);
    private string? resultKey;
    private string? error;
    private string? failed;
    private int? code;

    /// <summary>The results file the run wrote on the device, as the instrumentation's result names it; null for none.</summary>
    public string? ResultsPath => result.GetValueOrDefault("resultsPath") is { Length: > 0 } path ? path : null;

    /// <summary>A line of the run's output: the status it ends, else null.</summary>
    public IReadOnlyDictionary<string, string>? Read(string line)
    {
        line = line.TrimEnd('\r');
        if (line.StartsWith(StatusCode, StringComparison.Ordinal))
        {
            Dictionary<string, string>? ended = status;
            (status, statusKey, resultKey) = (null, null, null);
            // The system's own status, where the instrumentation could not start, says why.
            if (ended?.GetValueOrDefault("Error") is { Length: > 0 } refused)
            {
                error ??= refused;
            }
            return ended;
        }
        if (line.StartsWith(Status, StringComparison.Ordinal) && Split(line[Status.Length..]) is ({ } key, { } value))
        {
            status ??= new Dictionary<string, string>(StringComparer.Ordinal);
            status[key] = value;
            (statusKey, resultKey) = (key, null);
        }
        else if (line.StartsWith(Result, StringComparison.Ordinal) && Split(line[Result.Length..]) is ({ } named, { } said))
        {
            result[named] = said;
            (resultKey, statusKey) = (named, null);
        }
        else if (line.StartsWith(Code, StringComparison.Ordinal))
        {
            code = int.TryParse(line.AsSpan(Code.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
            (statusKey, resultKey) = (null, null);
        }
        else if (line.Contains(Failed, StringComparison.Ordinal))
        {
            failed ??= line.Trim();
        }
        else if (statusKey is not null && status is not null)
        {
            status[statusKey] = $"{status[statusKey]}\n{line}";
        }
        else if (resultKey is not null)
        {
            result[resultKey] = $"{result[resultKey]}\n{line}";
        }
        return null;
    }

    /// <summary>Why the run did not finish as the instrumentation meant, from what it wrote, the whole of it read: the error
    /// its result names, the crash or refusal the system said, its code; null where it finished.</summary>
    public string? Problem()
    {
        if (result.GetValueOrDefault("error") is { Length: > 0 } thrown)
        {
            return $"the instrumentation failed: {FirstLine(thrown)}";
        }
        if ((result.GetValueOrDefault("longMsg") ?? result.GetValueOrDefault("shortMsg")) is { Length: > 0 } crashed)
        {
            return $"its app ended as it ran: {FirstLine(crashed)}";
        }
        if (error is not null || failed is not null)
        {
            return $"the instrumentation could not start: {FirstLine(error ?? failed!)}";
        }
        return code switch
        {
            null => "the instrumentation ended before it finished: its app may have crashed",
            Finished => null,
            { } other => $"the instrumentation finished with code {other.ToString(CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>A test's end as a status of a device test project's instrumentation tells it: the test's full name, how it
    /// ended, and where it failed or was skipped what was said and where, each sent as written or in base64
    /// (<c>message-b64</c>, <c>stack-b64</c>); null for a status of no test's end (a test's start, the system's own).</summary>
    public static (string Test, TestState State, string? Message, string? StackTrace)? Ended(IReadOnlyDictionary<string, string> status)
    {
        if (status.GetValueOrDefault("event") is { } stage && stage != "finish"
            || status.GetValueOrDefault("test") is not { Length: > 0 } test || status.GetValueOrDefault("outcome") is not { Length: > 0 } outcome)
        {
            return null;
        }
        string? message = Decoded(status, "message-b64") ?? status.GetValueOrDefault("message");
        string? stack = Decoded(status, "stack-b64") ?? status.GetValueOrDefault("stack");
        return outcome switch
        {
            "passed" => (test, TestState.Passed, null, null),
            "skipped" => (test, TestState.Skipped, message, null),
            "failed" => (test, TestState.Failed, message, stack),
            _ => (test, TestState.Failed, $"The instrumentation told an outcome Passenger does not know: {outcome}", null),
        };
    }

    /// <summary>A value sent in base64 as UTF-8, as written where it is no base64; null where none was sent.</summary>
    private static string? Decoded(IReadOnlyDictionary<string, string> status, string key)
    {
        if (status.GetValueOrDefault(key) is not { Length: > 0 } sent)
        {
            return null;
        }
        byte[] bytes = new byte[sent.Length];
        return Convert.TryFromBase64String(sent, bytes, out int written) ? Encoding.UTF8.GetString(bytes, 0, written) : sent;
    }

    private static (string? Key, string? Value) Split(string pair) =>
        pair.IndexOf('=', StringComparison.Ordinal) is > 0 and int at ? (pair[..at].Trim(), pair[(at + 1)..]) : (null, null);

    private static string FirstLine(string words) => words.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? words.Trim();
}
