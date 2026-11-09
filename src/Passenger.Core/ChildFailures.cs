namespace Passenger.Core;

/// <summary>What starting and running a child through <see cref="ChildProcesses"/> throws when the child could not be had,
/// told from a fault of the IDE's own, which a catch of these leaves to go on to the boundary that says it whole.</summary>
public static class ChildFailures
{
    /// <summary>A child that could not start or run to its end: its program missing or not one to run (Win32Exception), no
    /// .NET command line, helper or CLI to start (FileNotFoundException), its pipes broken or its output past what is read of
    /// it (IOException, ChildOutputException among them), its limit passed (TimeoutException), or a process the system has
    /// let go of or will not start (InvalidOperationException).</summary>
    public static bool Of(Exception error) => error is System.ComponentModel.Win32Exception or IOException or TimeoutException or InvalidOperationException;
}
