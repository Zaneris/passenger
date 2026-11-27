using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Passenger.App.Testing;

/// <summary>A test's result as a TRX file (the Testing Platform's <c>--report-trx</c>, VSTest's logger) gives it: its
/// method's full name, its own name (a data row's), how it ended and how long it took, what was said where it failed or
/// was skipped, and what it wrote.</summary>
public sealed record TrxResult(string FullyQualifiedName, string DisplayName, TestState State, TimeSpan Duration, string? Message, string? StackTrace, string? Output);

/// <summary>The results a TRX file holds, read as data: no DTD, nothing it names fetched.</summary>
public static class TrxResults
{
    /// <summary>Each result in the file's order, its method's full name from the test definition it is of (the class and
    /// the method's name), else its own name; a result of an outcome that is no test's end (pending, in progress) passed over.</summary>
    public static IReadOnlyList<TrxResult> Read(Stream trx)
    {
        using XmlReader reader = XmlReader.Create(trx, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
        XElement? run = XDocument.Load(reader).Root;
        if (run is null)
        {
            return [];
        }
        XNamespace ns = run.Name.Namespace;
        Dictionary<string, string> methods = new(StringComparer.Ordinal);
        foreach (XElement test in run.Element(ns + "TestDefinitions")?.Elements(ns + "UnitTest") ?? [])
        {
            if ((string?)test.Attribute("id") is { } id && test.Element(ns + "TestMethod") is { } method
                && (string?)method.Attribute("className") is { Length: > 0 } type && (string?)method.Attribute("name") is { Length: > 0 } name)
            {
                methods[id] = $"{type}.{name}";
            }
        }
        List<TrxResult> results = [];
        foreach (XElement result in run.Element(ns + "Results")?.Elements(ns + "UnitTestResult") ?? [])
        {
            string shown = (string?)result.Attribute("testName") ?? string.Empty;
            TestState? state = (string?)result.Attribute("outcome") switch
            {
                "Passed" or "PassedButRunAborted" => TestState.Passed,
                "Failed" or "Error" or "Timeout" or "Aborted" => TestState.Failed,
                "NotExecuted" or "Inconclusive" or "NotRunnable" or "Disconnected" or "Warning" => TestState.Skipped,
                _ => null,
            };
            string? method = (string?)result.Attribute("testId") is { } of ? methods.GetValueOrDefault(of) : null;
            if (state is not { } ended || (method ?? shown).Length == 0)
            {
                continue;
            }
            XElement? output = result.Element(ns + "Output");
            XElement? error = output?.Element(ns + "ErrorInfo");
            TimeSpan took = TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out TimeSpan duration) ? duration : TimeSpan.Zero;
            results.Add(new TrxResult(method ?? shown, shown.Length > 0 ? shown : method!, ended, took, Said(error?.Element(ns + "Message")),
                Said(error?.Element(ns + "StackTrace")), Said(output?.Element(ns + "StdOut"))));
        }
        return results;
    }

    private static string? Said(XElement? words) => words is null || words.Value.Length == 0 ? null : words.Value;
}
