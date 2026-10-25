using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

if (args is ["child"])
{
	Console.WriteLine("child ran");
	return;
}
if (args is ["flood"])
{
	// 64 KB on standard output, more than a pipe holds unread, then an exit code of its own.
	for (int i = 0; i < 1024; i++)
	{
		Console.Out.Write($"{i:D5} {new string((char)('a' + i % 26), 57)}\n");
	}
	Environment.ExitCode = 42;
	return;
}
if (args is ["busy"])
{
	Busy.Spin(TimeSpan.FromMilliseconds(800));
	Console.WriteLine($"allocated {Busy.Allocate(60_000)}");
	Children.Run();
	return;
}
if (args is ["emitted"])
{
	Emitted.Run();
	return;
}
if (args is ["reads"])
{
	Reads.Run();
	return;
}
if (args is ["corelib"])
{
	CoreLibCalls.Run();
	return;
}
if (args is ["leaves"])
{
	Leaves.Run();
	return;
}
if (args is ["lets go"])
{
	LetsGo.Run();
	return;
}
if (args is ["locks"])
{
	Locks.Run();
	return;
}
if (args is ["finally"])
{
	Finally.Run();
	return;
}
if (args is ["named main"])
{
	// Main's thread named as an app may name its UI thread: the debugger still knows it for Main's.
	Thread.CurrentThread.Name = "UI";
	Threads.Run();
	return;
}
Wrapper.CallWithCatch(() => throw new InvalidOperationException("thrown in user code"));
Attributed.StepThrough();
Attributed.NonUser();
Attributed.Hidden();
Console.WriteLine("end");
Captures.Outer(41);
Nested.Throw();
Displays.Show();
Interpreter.Run();
Jumps.Run();
Threads.Run();
Children.Run();

// The cases Passenger's own debugger tests add to upstream's. Tests find their lines by the code on them.
// Code of the user's own that says it is not theirs: Just My Code passes over it, and a catch in it
// leaves an exception unhandled by the user.
[DebuggerNonUserCode]
internal static class Wrapper
{
	public static void CallWithCatch(Action callback)
	{
		try { callback(); } catch { }
	}
}

internal static class Attributed
{
	[DebuggerStepThrough] public static void StepThrough() => Console.WriteLine("1");
	[DebuggerNonUserCode] public static void NonUser() => Console.WriteLine("2");
	[DebuggerHidden] public static void Hidden() => Console.WriteLine("3");
}

internal static class Slow
{
	// A getter that never returns: the debugger must stop evaluating it, and go on.
	public static int Forever { get { while (true) { } } }
}

internal static class Captures
{
	public static void Outer(int captured)
	{
		Func<int> f = () => captured + 1;
		Console.WriteLine(f());
	}
}

internal static class Nested
{
	public static void Throw()
	{
		try
		{
			try { throw new Exception("Message1"); }
			catch (Exception e1) { throw new InvalidOperationException("Message2", e1); }
		}
		catch { }
	}
}

// [DebuggerDisplay] as upstream's #51 and #53 spell out what it should show.
internal static class Displays
{
	public static void Show()
	{
		Money money = new() { Amount = 12.5m, Currency = "USD" };
		QuotedMoney quoted = new() { Amount = 12.5m, Currency = "USD" };
		Braced braced = new();
		Missing missing = new();
		Link tail = new("tail", null);
		Link head = new("head", tail);
		Labelled labelled = new();
		Holder holder = new();
		Derived derived = new();
		Box<Money> box = new() { Value = new Money { Amount = 1m, Currency = "EUR" } };
		Dictionary<string, Point> byName = new() { ["origin"] = new Point(0, 0) };
		LiftedObject lifted = new();
		Angry angry = new();
		Unprintable unprintable = new();
		Console.WriteLine("displays");
	}
}

[DebuggerDisplay("{Amount} {Currency,nq}")]
internal sealed class Money { public decimal Amount; public string Currency = ""; }

[DebuggerDisplay("{Amount} {Currency}")]
internal sealed class QuotedMoney { public decimal Amount; public string Currency = ""; }

[DebuggerDisplay("\\{{Value}\\}")]
internal sealed class Braced { public int Value = 3; }

[DebuggerDisplay("Value={Value} Bad={NoSuchMember}")]
internal sealed class Missing { public int Value = 2; }

[DebuggerDisplay("{_name,nq} -> {Next._name,nq}")]
internal sealed class Link(string name, Link? next) { private string _name = name; public Link? Next = next; }

[DebuggerDisplay("{Value}", Name = "renamed", Type = "RetypedThing")]
internal sealed class Labelled { public int Value = 11; }

internal sealed class Holder { public Labelled Item = new(); }

[DebuggerDisplay("base {Tag,nq}")]
internal class Base { public string Tag = "b"; }
internal sealed class Derived : Base { public int Extra = 9; }

[DebuggerDisplay("({X}, {Y})")]
internal readonly struct Point(int x, int y) { public int X { get; } = x; public int Y { get; } = y; }

[DebuggerDisplay("box of {Value}")]
internal sealed class Box<T> { public T? Value; }

internal sealed class LiftedObject
{
	[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
	public Inner Part = new();
}

internal sealed class Inner { public int A = 1; public string B = "b"; }

[DebuggerTypeProxy(typeof(AngryProxy))]
internal sealed class Angry { public int Real = 1; }

internal sealed class AngryProxy
{
	public AngryProxy(Angry target) => throw new InvalidOperationException("no");
	public int Doubled => 2;
}

// A ToString that throws, as one under debugging may: the value is still there to open.
internal sealed class Unprintable
{
	public int Kept = 5;
	public override string ToString() => throw new InvalidOperationException("no text");
}

// What the debugger's IL interpreter must get right (upstream #30).
internal static class Interpreter
{
	public static void Run()
	{
		int myInt = 5;
		uint myUInt = 7;
		SignedEnum minusOne = SignedEnum.MinusOne;
		WideEnum wide = WideEnum.Max;
		Pair<int> pairOfInt = new() { Value = 1 };
		object boxedPair = pairOfInt;
		Counter counter = new();
		decimal price = 12.5m;
		int[,] grid = { { 1, 2 }, { 3, 4 }, { 5, 6 } };
		Pair<int>[,] pairGrid = new Pair<int>[2, 2];
		Bumper.Work(new Bumped());
		Console.WriteLine("interpreter");
		Console.WriteLine($"interpreter read {myInt}, {myUInt}, {minusOne}, {wide}, {counter.Count} and {price}");
	}
}

internal enum SignedEnum : sbyte { MinusOne = -1 }
internal enum WideEnum : ushort { Max = 65535 }
internal struct Pair<T> { public T Value; }
internal struct Counter { public int Count; public void Increment() => Count++; }
internal interface IBump { void Bump(); }
internal struct Bumped : IBump { public int N; public void Bump() => N++; }

internal static class Bumper
{
	public static void Work<T>(T item) where T : IBump
	{
		Console.WriteLine("bumper");
	}
}

// Upstream's #59: two workers waiting while Main stops.
internal static class Threads
{
	private static readonly ManualResetEventSlim Gate = new(false);
	private static readonly CountdownEvent Started = new(2);

	public static void Run()
	{
		Thread named = new(Wait) { Name = "worker-named" };
		Thread unnamed = new(Wait);
		named.Start();
		unnamed.Start();
		Started.Wait();
		Console.WriteLine("both workers are waiting");
		Gate.Set();
		named.Join();
		unnamed.Join();
	}

	private static void Wait()
	{
		Started.Signal();
		Gate.Wait();
	}
}

// Set Next Statement's case: three additions to skip over or go back to.
internal static class Jumps
{
	public static void Run()
	{
		int jumps = 0;
		jumps += 1;
		jumps += 10;
		jumps += 100;
		Console.WriteLine($"jumps {jumps}");
	}
}

// A program being debugged that starts a .NET program of its own, which must run as it would undebugged.
internal static class Children
{
	public static void Run()
	{
		using Process child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [typeof(Children).Assembly.Location, "child"]) { RedirectStandardOutput = true })!;
		bool ended = child.WaitForExit(15000);
		string said = ended ? child.StandardOutput.ReadToEnd().Trim() : "nothing, held at its start";
		if (!ended)
		{
			child.Kill();
		}
		Console.WriteLine($"child said: {said}");
	}
}

// Code the program makes as it runs (System.Reflection.Emit), in a module with no file, calling back into the user's.
public static class Emitted
{
	public static void Run()
	{
		AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("EmittedAtRunTime"), AssemblyBuilderAccess.Run);
		TypeBuilder type = assembly.DefineDynamicModule("EmittedAtRunTime").DefineType("EmittedCaller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder call = type.DefineMethod("CallBack", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(int)]);
		ILGenerator il = call.GetILGenerator();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Call, typeof(Emitted).GetMethod(nameof(CalledBack))!);
		il.Emit(OpCodes.Ret);
		Action<int> callBack = type.CreateType().GetMethod("CallBack")!.CreateDelegate<Action<int>>();
		callBack(7);
	}

	public static void CalledBack(int value)
	{
		Console.WriteLine($"called back with {value}");
	}
}

// A getter blocked in a read of input, outside the runtime: an evaluation of it cannot be stopped until the program has a
// line. A worker passes a line of its own meanwhile.
internal static class Reads
{
	public static string Input => Console.ReadLine() ?? "none";

	public static void Run()
	{
		Thread worker = new(() =>
		{
			Thread.Sleep(1500);
			Console.WriteLine("worker ran");
		});
		int before = 1;
		worker.Start();
		Console.WriteLine($"before {before}");
		Console.WriteLine($"read {Input}");
		worker.Join();
	}
}

// The profiler's case: a method that keeps the thread busy, so its samples stand out.
internal static class Busy
{
	public static double Spin(TimeSpan time)
	{
		Stopwatch clock = Stopwatch.StartNew();
		double total = 0;
		while (clock.Elapsed < time)
		{
			for (int i = 1; i < 10000; i++)
			{
				total += Math.Sqrt(i);
			}
		}
		Console.WriteLine($"spun {total > 0}");
		return total;
	}

	/// <summary>About 60 MB of short-lived kilobyte arrays: allocation ticks and young collections.</summary>
	public static long Allocate(int count)
	{
		long kept = 0;
		for (int i = 0; i < count; i++)
		{
			byte[] block = new byte[1024];
			block[i % 1024] = 1;
			kept += block.Length;
		}
		return kept;
	}
}

// Code of the user's that CoreLib calls back: the frame below it is CoreLib's, which ships without symbols, so the
// debugger decompiles it (the largest assembly a program loads) to show it with source.
internal static class CoreLibCalls
{
	public static void Run()
	{
		List<int> each = [7];
		each.ForEach(value =>
		{
			Console.WriteLine($"called by CoreLib with {value}");
		});
	}
}

// A stop whose values are slow to read: a getter that says it runs and never returns, and one that counts its evaluations,
// which a stop the program has left must never run.
internal static class Leaves
{
	public static int Counted;

	public static int Count => ++Counted;

	public static int Endless
	{
		get
		{
			Console.WriteLine("evaluating");
			while (true)
			{
			}
		}
	}

	public static void Run()
	{
		int turn = 1;
		Console.WriteLine($"turn {turn}");
		Console.WriteLine($"counted {Counted}");
	}
}

// A program let go a moment after a read of its stop began an evaluation: the let-go waits for that evaluation's end, and
// the program runs on to its own.
internal static class LetsGo
{
	public static string Slow
	{
		get
		{
			Console.WriteLine("evaluating");
			Thread.Sleep(500);
			return "slow";
		}
	}

	public static void Run()
	{
		for (int turn = 1; turn <= 100; turn++)
		{
			Thread.Sleep(10); // each turn of the program let go
		}
		Console.WriteLine("ran on");
	}
}

// A stop while another thread holds a lock: an evaluation that takes it waits there, the holder paused with the program,
// and cannot end until the program goes on and the holder lets it go, once the evaluation has waited 5 s, longer than the
// tests give an evaluation. Then the lock is every thread's to take again: another thread's evaluation takes it, and so
// does that thread.
internal static class Locks
{
	private static readonly object Gate = new();
	private static readonly ManualResetEventSlim Holding = new();
	private static long waitingSince;
	private static int turns;

	// Works a while inside the lock, where an abort left on its thread would find it holding the lock.
	public static int Taken
	{
		get
		{
			Interlocked.CompareExchange(ref waitingSince, Stopwatch.GetTimestamp(), 0);
			Console.WriteLine("taking the lock");
			lock (Gate)
			{
				Thread.SpinWait(100_000);
				return ++turns;
			}
		}
	}

	private static bool WaitedLongEnough() => Volatile.Read(ref waitingSince) is var since && since != 0 && Stopwatch.GetElapsedTime(since) > TimeSpan.FromSeconds(5);

	public static void Run()
	{
		Thread holder = new(() =>
		{
			lock (Gate)
			{
				Holding.Set();
				Stopwatch held = Stopwatch.StartNew();
				while (!WaitedLongEnough() && held.Elapsed < TimeSpan.FromSeconds(60))
				{
					Thread.Sleep(20);
				}
			}
		});
		holder.Start();
		Holding.Wait();
		Console.WriteLine("the lock is held"); // stop while another thread holds the lock
		holder.Join();
		Thread later = new(() =>
		{
			Console.WriteLine("a later thread"); // stop on another thread once the program has gone on
			lock (Gate)
			{
				turns++;
			}
		});
		later.Start();
		Console.WriteLine(later.Join(TimeSpan.FromSeconds(20)) ? $"taken again, {turns} turns" : "the lock was never let go");
	}
}

// A stop at which an evaluation spins in a finally block until another thread, paused with the program, lets it out once
// it has spun 5 s, longer than the tests give an evaluation: the runtime puts an abort off until the block is left, by
// then after the program has gone on. That thread is in its loop by the stop, past the initialization of every type the
// evaluation uses (Finally's statics, Stopwatch): paused inside one, it would hold the evaluation short of its finally.
internal static class Finally
{
	private static readonly ManualResetEventSlim Looping = new();
	private static long spinningSince;
	private static int letOut;
	public static int Turns;

	public static int Spins
	{
		get
		{
			try
			{
				Interlocked.CompareExchange(ref spinningSince, Stopwatch.GetTimestamp(), 0);
			}
			finally
			{
				while (Volatile.Read(ref letOut) == 0)
				{
				}
			}
			return ++Turns;
		}
	}

	private static bool SpunLongEnough() => Volatile.Read(ref spinningSince) is var since && since != 0 && Stopwatch.GetElapsedTime(since) > TimeSpan.FromSeconds(5);

	public static void Run()
	{
		Thread freer = new(() =>
		{
			Stopwatch waited = Stopwatch.StartNew();
			while (!SpunLongEnough() && waited.Elapsed < TimeSpan.FromSeconds(60))
			{
				Looping.Set();
				Thread.Sleep(20);
			}
			Volatile.Write(ref letOut, 1);
		});
		freer.Start();
		Looping.Wait();
		Console.WriteLine("a finally next"); // stop before an evaluation spins in a finally block
		freer.Join();
		Console.WriteLine($"let out, {Turns} turns"); // stop once the evaluation has been let out
	}
}
