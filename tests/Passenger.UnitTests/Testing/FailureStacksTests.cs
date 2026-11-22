using Passenger.App.Testing;

namespace Passenger.UnitTests.Testing;

/// <summary>A failure's stack shown once, from what NUnit's adapter writes (its texts here as it wrote them for a failing
/// assertion, a thrown exception, and several failures in one test): a numbered stack that repeats the test's own or one its
/// message gives is left out, and one said nowhere else, or a stack of any other runner, stays as written.</summary>
public class FailureStacksTests
{
    private const string Single = "   at Cases.Failing.Single() in /src/Cases.cs:line 8\n\n1)    at Cases.Failing.Single() in /src/Cases.cs:line 8\n\n";

    private const string Thrown = "   at Cases.Failing.Deep(Int32 n) in /src/Cases.cs:line 27\n   at Cases.Failing.Throws() in /src/Cases.cs:line 21\n"
        + "1)    at Cases.Failing.Deep(Int32 n) in /src/Cases.cs:line 27\n   at Cases.Failing.Throws() in /src/Cases.cs:line 21\n";

    private const string Several = "   at Cases.Failing.Twice() in /src/Cases.cs:line 13\n\n"
        + "1)    at Cases.Failing.<>c.<Twice>b__1_0() in /src/Cases.cs:line 15\n   at NUnit.Framework.Assert.Multiple(Action action)\n   at Cases.Failing.Twice() in /src/Cases.cs:line 13\n\n"
        + "2)    at Cases.Failing.<>c.<Twice>b__1_0() in /src/Cases.cs:line 16\n   at NUnit.Framework.Assert.Multiple(Action action)\n   at Cases.Failing.Twice() in /src/Cases.cs:line 13\n\n";

    private const string SeveralSaid = "Multiple failures or warnings in test:\n  1)   Assert.That(2 + 2, Is.EqualTo(5))\n  Expected: 5\n  But was:  4\n\n"
        + "     at Cases.Failing.<>c.<Twice>b__1_0() in /src/Cases.cs:line 15\n   at NUnit.Framework.Assert.Multiple(Action action)\n   at Cases.Failing.Twice() in /src/Cases.cs:line 13\n\n"
        + "  2)   Assert.That(3 + 3, Is.EqualTo(7))\n  Expected: 7\n  But was:  6\n\n"
        + "     at Cases.Failing.<>c.<Twice>b__1_0() in /src/Cases.cs:line 16\n   at NUnit.Framework.Assert.Multiple(Action action)\n   at Cases.Failing.Twice() in /src/Cases.cs:line 13\n\n\n";

    [Fact]
    public void A_failures_stack_is_shown_once_and_one_said_nowhere_else_as_written() => Cases.All(
        ("a numbered stack said already is left out", () => Cases.Each(
        [
            (Single, "  Assert.That(2 + 2, Is.EqualTo(5))\n  Expected: 5\n  But was:  4\n", "   at Cases.Failing.Single() in /src/Cases.cs:line 8"),
            (Thrown, "System.InvalidOperationException : thrown on purpose",
                "   at Cases.Failing.Deep(Int32 n) in /src/Cases.cs:line 27\n   at Cases.Failing.Throws() in /src/Cases.cs:line 21"),
            (Several, SeveralSaid, "   at Cases.Failing.Twice() in /src/Cases.cs:line 13"),
            ("   at A.B() in /a.cs:line 1\r\n\r\n1)    at A.B() in /a.cs:line 1\r\n\r\n", (string?)null, "   at A.B() in /a.cs:line 1"),
        ], row => LeftOut(row.Item1, row.Item2, row.Item3))),
        ("a stack said nowhere else stays as written", () => Cases.Each(
        [
            ("   at A.B() in /a.cs:line 1\n\n1)    at A.C() in /a.cs:line 2\n\n", (string?)null),
            ("   at Xunit.Assert.Equal(Int32 expected, Int32 actual)\n   at Cases.Sums.Adds() in /src/Sums.cs:line 9", "Assert.Equal() Failure"),
            (null, null),
        ], row => Stays(row.Item1, row.Item2))));

    private static void LeftOut(string stack, string? message, string shown)
    {
        Assert.True(FailureStacks.Once(stack, message) == shown, $"The stack\n{stack}\nwith the message\n{message}\nis shown as\n{FailureStacks.Once(stack, message)}");
    }

    private static void Stays(string? stack, string? message)
    {
        string? shown = FailureStacks.Once(stack, message);
        Assert.True(shown == stack, $"The stack\n{stack}\nwith the message\n{message}\nis shown as\n{shown}");
    }
}
