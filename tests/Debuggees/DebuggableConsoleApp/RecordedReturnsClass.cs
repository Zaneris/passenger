namespace DebuggableConsoleApp;

/// <summary>Returns a recording keeps where no local holds them: expression-bodied methods LINQ calls, and an async
/// iterator's yields.</summary>
public static class RecordedReturnsClass
{
	public static void Test() => TestAsync().GetAwaiter().GetResult();

	private static async Task TestAsync()
	{
		string[] names = Enumerable.Range(1, 2).Select(Named).ToArray();
		double[] halves = Enumerable.Range(1, 2).Select(Half).ToArray();
		int total = 0;
		await foreach (int item in CountAsync(2))
		{
			total += item;
		}
	}

	private static string Named(int x) => $"n{x}";

	private static double Half(int x) => x / 2.0;

	private static async IAsyncEnumerable<int> CountAsync(int n)
	{
		for (int i = 1; i <= n; i++)
		{
			await Task.Yield();
			yield return i * 10;
		}
	}
}
