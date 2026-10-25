
internal class Program
{
	public static async Task Main(string[] args)
	{
		int test = 4;
		await Task.Delay(10);
		Console.WriteLine(test);
	}
}
