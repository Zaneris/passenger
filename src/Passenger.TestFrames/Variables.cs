namespace Passenger.TestFrames;

/// <summary>What Passenger puts in a test run's environment for the hook, compiled into both ends.</summary>
internal static class Variables
{
    /// <summary>"1" in a test run Passenger starts whose failing tests' windows are to be drawn into their results.</summary>
    [IdentifierHome]
    public const string Frames = "PASSENGER_TEST_FRAMES";
}
