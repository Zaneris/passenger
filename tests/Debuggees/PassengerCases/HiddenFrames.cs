using System.Diagnostics;

// Code that runs under hidden code: the runtime's helpers the compiled code calls, which throw an index's exception and
// run a static constructor, and a method of the program's own that hides itself from debuggers and stack traces alike.
internal static class HiddenFrames
{
	public static async Task RunAsync()
	{
		try
		{
			await IndexesPastTheEndAsync();
		}
		catch (IndexOutOfRangeException)
		{
			Console.WriteLine("caught past the end");
		}
		try
		{
			Fail("hidden");
		}
		catch (InvalidOperationException)
		{
			Console.WriteLine("caught from hidden code");
		}
		Console.WriteLine(Initialized.Value);
	}

	private static async Task IndexesPastTheEndAsync()
	{
		await Task.Delay(1);
		int[] numbers = new int[2];
		numbers[5] = 1;
	}

	[DebuggerHidden]
	[StackTraceHidden]
	private static void Fail(string why) => throw new InvalidOperationException(why);

	private static class Initialized
	{
		public static readonly int Value;

		static Initialized()
		{
			Value = 7;
		}
	}
}
