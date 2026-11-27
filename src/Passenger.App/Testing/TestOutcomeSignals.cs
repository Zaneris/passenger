namespace Passenger.App.Testing;

/// <summary>The exceptions test frameworks throw to end a test with an outcome that is no failure (skipped, inconclusive,
/// ignored, passed early), which the framework catches to tell the outcome: Any exception passes over them in every
/// debugged program, a desktop's or a device's test host alike, so a debugged run stops at a test's own exception, a
/// failed assertion and a breakpoint, never at a skip. A breakpoint on one of their types stops at it. Each is named as
/// the debugger names an exception's type.</summary>
public static class TestOutcomeSignals
{
    public static IReadOnlyList<string> Exceptions { get; } =
    [
        // MSTest's Assert.Inconclusive.
        "Microsoft.VisualStudio.TestTools.UnitTesting.AssertInconclusiveException",
        // NUnit's Assert.Inconclusive and Assume.That, Assert.Ignore and Assert.Pass.
        "NUnit.Framework.InconclusiveException",
        "NUnit.Framework.IgnoreException",
        "NUnit.Framework.SuccessException",
        // xUnit's Assert.Skip, SkipUnless and SkipWhen.
        "Xunit.Sdk.SkipException",
        // TUnit's Skip.Test and its inconclusive result.
        "TUnit.Core.Exceptions.SkipTestException",
        "TUnit.Core.Exceptions.InconclusiveTestException",
    ];
}
