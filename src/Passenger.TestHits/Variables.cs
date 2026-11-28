namespace Passenger.TestHits;

/// <summary>What Passenger puts in a mapped test run's environment for the hook, compiled into both ends.</summary>
internal static class Variables
{
    /// <summary>The loopback port Passenger listens on for the hook, in a run that maps which tests run which lines.</summary>
    [IdentifierHome]
    public const string Port = "PASSENGER_TEST_HITS";

    /// <summary>What Passenger writes to have the hook hand over what the test before ran.</summary>
    public const string Take = "take";
}
