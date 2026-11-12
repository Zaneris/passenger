using System.Diagnostics;

// Code that runs under hidden code: the runtime's helpers the compiled code calls, which throw an index's exception and
// run a static constructor, one before a call it counts, and a method of the program's own that hides itself from
// debuggers and stack traces alike.
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
		int sum = Later.Value + Bump();
		Console.WriteLine(sum);
	}

	/// <summary>How many times <see cref="Bump"/> ran, which tells whether a step ran past it.</summary>
	public static int Bumped;

	private static int Bump() => ++Bumped;

	private static async Task IndexesPastTheEndAsync()
	{
		await Task.Delay(1);
		int[] numbers = new int[2];
		numbers[5] = 1;
	}

	[DebuggerHidden]
	[StackTraceHidden]
	private static void Fail(string why) => throw new InvalidOperationException(why);

	private static class Later
	{
		public static readonly int Value;

		static Later()
		{
			Value = 5;
		}
	}
}
