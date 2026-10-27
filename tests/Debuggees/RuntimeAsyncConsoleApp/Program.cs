using System.Diagnostics;

if (args is ["child"])
{
	// The child runs until its parent closes its input.
	Console.In.ReadToEnd();
	return;
}
int total = await OuterAsync(3);
Console.WriteLine($"total {total}");
int ran = await Task.Run(RunAsync);
Console.WriteLine($"ran {ran}");
int[] both = await Task.WhenAll(TwiceAsync(1), TwiceAsync(2));
Console.WriteLine($"both {both[0]} {both[1]}");
await WaitForChildAsync();

static async Task<int> OuterAsync(int n)
{
	int sum = 0;
	for (int i = 0; i < n; i++)
	{
		sum += await InnerAsync(i);
		Console.WriteLine($"after {i} sum {sum}");
	}
	int done = await DoneAsync(n);
	sum += done;
	return sum;
}

static async Task<int> InnerAsync(int i)
{
	int before = i;
	await Task.Delay(10);
	int doubled = before * 2;
	return doubled;
}

static async Task<int> DoneAsync(int n)
{
	await Task.CompletedTask;
	int result = n + 1;
	return result;
}

static async Task<int> RunAsync()
{
	int started = 1;
	await Task.Delay(20);
	int ended = started + 1;
	return ended;
}

// Two runs at once, each with a chain of its own once it yields: the second goes on after its delay first.
static async Task<int> TwiceAsync(int which)
{
	await Task.Yield();
	int first = which;
	await Task.Delay(which == 1 ? 200 : 10);
	int second = first * 10;
	return second;
}

// .NET 11's own Process.WaitForExitAsync is runtime-async: its await waits for the child, which ends once its input is
// closed. A step into Id first has the debugger decompile Process's module.
static async Task WaitForChildAsync()
{
	ProcessStartInfo start = new(Environment.ProcessPath!, [Environment.GetCommandLineArgs()[0], "child"]) { RedirectStandardInput = true };
	using Process child = Process.Start(start)!;
	int id = child.Id;
	Task exited = child.WaitForExitAsync();
	child.StandardInput.Close();
	await exited;
	Console.WriteLine($"child ended with {child.ExitCode}");
}
