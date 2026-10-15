Console.WriteLine($"started {Environment.ProcessId}"); // Main's first line
if (args is ["output"])
{
    while (true)
    {
        Console.WriteLine(new string('x', 4096));
    }
}
if (args is ["output-child"])
{
    // Retains the terminal's output handle while its parent waits: closing the PTY must keep draining output too.
    System.Diagnostics.ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    }
    start.ArgumentList.Add("output");
    System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;
    File.WriteAllText("terminal-child.pid", child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Thread.Sleep(Timeout.Infinite);
}
// A program that needs a real terminal, as Run and Debug start one in the IDE's Terminal window: it clears the screen,
// puts the cursor where it wants, writes in colour, reads a key and then a line from the keyboard, and ends with an exit
// code of its own. With "child" it first starts a program that runs until it is ended, and says its id.
Console.Clear();
if (args is ["child"])
{
    System.Diagnostics.ProcessStartInfo start = OperatingSystem.IsWindows()
        ? new("ping", ["-n", "600", "127.0.0.1"]) { RedirectStandardOutput = true }
        : new("/bin/sleep", ["600"]);
    System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;
    Console.WriteLine($"child {child.Id}");
}
Console.SetCursorPosition(4, 2);
Console.ForegroundColor = ConsoleColor.Red;
Console.Write("red");
Console.ResetColor();
Console.WriteLine(" at the third row's fifth column");
Console.Write("key? ");
ConsoleKeyInfo key = Console.ReadKey(intercept: true);
Console.WriteLine($"key {key.KeyChar}");
Console.Write("line? ");
string? line = Console.ReadLine();
Console.WriteLine($"line {line}");
return 3;
