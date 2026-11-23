
internal class Program
{
	public static async Task Main(string[] args)
	{
		int test = 4;
		await Task.Delay(10);
		Console.WriteLine(test);
		if (args.Length > 0)
		{
			// Captured in a block of its own, so its closure is not made until the block runs.
			int given = args.Length;
			Console.WriteLine(new Func<int>(() => given)());
		}
	}
}
