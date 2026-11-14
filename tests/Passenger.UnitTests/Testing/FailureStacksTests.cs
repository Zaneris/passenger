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

    [Theory]
    [InlineData(Single, "  Assert.That(2 + 2, Is.EqualTo(5))\n  Expected: 5\n  But was:  4\n", "   at Cases.Failing.Single() in /src/Cases.cs:line 8")]
    [InlineData(Thrown, "System.InvalidOperationException : thrown on purpose",
        "   at Cases.Failing.Deep(Int32 n) in /src/Cases.cs:line 27\n   at Cases.Failing.Throws() in /src/Cases.cs:line 21")]
    [InlineData(Several, SeveralSaid, "   at Cases.Failing.Twice() in /src/Cases.cs:line 13")]
    [InlineData("   at A.B() in /a.cs:line 1\r\n\r\n1)    at A.B() in /a.cs:line 1\r\n\r\n", null, "   at A.B() in /a.cs:line 1")]
    [InlineData("   at A.B() in /a.cs:line 1\n\n1)    at A.B() in /a.cs:line 1\n\n2)    at A.C() in /a.cs:line 12\n\n", "  2) failed\n     at A.C() in /a.cs:line 1\n",
        "   at A.B() in /a.cs:line 1\n\n2)    at A.C() in /a.cs:line 12")]
    public void A_numbered_stack_said_already_is_left_out(string stack, string? message, string shown)
    {
        Assert.True(FailureStacks.Once(stack, message) == shown, $"The stack\n{stack}\nwith the message\n{message}\nis shown as\n{FailureStacks.Once(stack, message)}");
    }

    [Theory]
    [InlineData("   at A.B() in /a.cs:line 1\n\n1)    at A.C() in /a.cs:line 2\n\n", null)]
    [InlineData("   at Xunit.Assert.Equal(Int32 expected, Int32 actual)\n   at Cases.Sums.Adds() in /src/Sums.cs:line 9", "Assert.Equal() Failure")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_stack_said_nowhere_else_stays_as_written(string? stack, string? message)
    {
        string? shown = FailureStacks.Once(stack, message);
        Assert.True(shown == stack, $"The stack\n{stack}\nwith the message\n{message}\nis shown as\n{shown}");
    }
}
