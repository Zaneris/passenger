using System.Xml.Linq;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollector.InProcDataCollector;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.InProcDataCollector;

namespace Passenger.TestHits;

/// <summary>The hook in a VSTest test host, as the run's settings name it among its in-process data collectors, its
/// configuration giving Passenger's port: as each test ends it tells Passenger the test's full name and what it counted
/// since the one before (<see cref="Hits.Push"/>). VSTest makes it by its type's name from the file the settings name.</summary>
public sealed class TestHitsCollector : InProcDataCollection
{
    private Hits.Pushing? pushing;

    public void Initialize(IDataCollectionSink dataCollectionSink) => Hits.Register();

    public void TestSessionStart(TestSessionStartArgs testSessionStartArgs)
    {
        if (testSessionStartArgs.Configuration is { Length: > 0 } given
            && int.TryParse((string?)XElement.Parse(given).Element("Port"), out int port) && Hits.Push(port) is { } connected)
        {
            pushing = connected;
            // What the host ran before its first test is no test's.
            _ = Hits.Take();
        }
    }

    public void TestCaseStart(TestCaseStartArgs testCaseStartArgs)
    {
    }

    public void TestCaseEnd(TestCaseEndArgs testCaseEndArgs)
    {
        if (testCaseEndArgs.DataCollectionContext.TestCase is { } test)
        {
            pushing?.Ended(test.FullyQualifiedName);
        }
    }

    public void TestSessionEnd(TestSessionEndArgs testSessionEndArgs) => pushing?.Dispose();
}
