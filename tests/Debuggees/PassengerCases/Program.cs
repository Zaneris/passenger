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
if (args is ["chatter"])
{
	// Numbered lines on standard output and standard error, without pause, until it is ended.
	for (long line = 0; ; line++)
	{
		Console.Out.WriteLine($"chatter {line}");
		Console.Error.WriteLine($"chatter {line}");
	}
}
if (args is ["--server", "--client-host", var host, "--client-port", var port])
{
	// A test host that connects, writes more than a pipe holds and a last line, and ends at once, answering nothing.
	using System.Net.Sockets.TcpClient client = new(host, int.Parse(port, System.Globalization.CultureInfo.InvariantCulture));
	for (int line = 0; line < 2000; line++)
	{
		Console.Out.Write($"{line:D5} {new string('w', 58)}\n");
	}
	Console.Out.Write("the test host's last words\n");
	Console.Out.Flush();
	Environment.Exit(3);
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
if (args is ["setting"])
{
	Setting.Run();
	return;
}
if (args is ["reckoning"])
{
	Reckoning.Run();
	return;
}
if (args is ["linked"])
{
	Linking.Run();
	return;
}
if (args is ["contexts"])
{
	Contexts.Run();
	return;
}
if (args is ["refs"])
{
	Refs.Run();
	return;
}
if (args is ["generic"])
{
	Console.WriteLine($"generic {Typed<int>.Make(4, "four")}");
	return;
}
if (args is ["culture"])
{
	Cultured.Run();
	return;
}
if (args is ["implemented"])
{
	Implemented.Run();
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
Unions.Show();
Pointers.Show();
Interpreter.Run();
Reading.Run();
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

// C# 15's unions, each case held and none, as Variables shows them.
internal static class Unions
{
	public static void Show()
	{
		Pet cat = new Cat("Tom");
		Pet dog = new Dog("Rex");
		Pet bird = new Bird("Tweety");
		Pet none = default;
		Console.WriteLine($"unions {cat.Value} {dog.Value} {bird.Value} {none.Value}");
	}
}

// A pointer, as Variables shows it.
internal static class Pointers
{
	public static unsafe void Show()
	{
		int number = 42;
		int* pointer = &number;
		Console.WriteLine($"pointers {*pointer}");
	}
}

internal sealed record Cat(string Name);
internal sealed record Dog(string Name);
internal sealed record Bird(string Name);
internal union Pet(Cat, Dog, Bird);

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

// What Set Value writes, each into what it names: an object holding a box another local shares, an int's and a float's
// element whose index moves past it, strings holding a NUL, inside and last, and pointers to two ints.
internal static class Setting
{
	public static unsafe void Run()
	{
		object boxed = 42;
		object shared = boxed;
		int[] values = [10, 20, 30];
		int i = 1;
		float[] ratios = [0.5f, 1.5f];
		int j = 0;
		string text = "x";
		string ended = "A\0";
		int first = 1, second = 2;
		int* p = &first;
		int* q = &second;
		int[,] grid = new int[200000, 1];
		Console.WriteLine("setting");
		Console.WriteLine($"boxed {boxed ?? "null"}, shared {shared}, same {ReferenceEquals(boxed, shared)}");
		Console.WriteLine($"values {string.Join(" ", values)}, i {i}, ratios {ratios[0] * 10}, j {j}");
		Console.WriteLine($"text {text.Length}");
		Console.WriteLine($"ended {ended.Length}");
		Console.WriteLine($"set pointers {first} {second} {p == null} {q == &second}");
		Console.WriteLine($"grid {grid[100007, 0]}");
	}
}

// What the debugger must reckon as the program's runtime does, its values' rows and its evaluations alike.
internal static class Reckoning
{
	public static void Run()
	{
		Wide wide = Wide.Low | Wide.Big;
		Wide big = Wide.Big;
		DateTime date = new(2026, 10, 30, 13, 45, 0);
		Names names = new();
		Counted counted = new();
		Unproxied unproxied = new();
		Access access = Access.Read | Access.Write;
		Console.WriteLine("reckoning");
		Console.WriteLine($"reckoned {wide & big}, {date:yyyy-MM}, {names.First}, {counted.Count}, {unproxied.Raw}, {access}");
	}
}

[Flags] internal enum Wide : long { Low = 1, Big = 1L << 41 }
[Flags] internal enum Access { None = 0, Read = 1, Write = 2, Run = 4, All = -1 }

internal class Listed<T>
{
	private readonly List<T> _items = [];
	public int Count => _items.Count;
	public T? First => _items.Count > 0 ? _items[0] : default;
	public void Add(T item) => _items.Add(item);
}

internal sealed class Names : Listed<string>
{
	public Names() => Add("Ada");
}

[DebuggerTypeProxy(typeof(PassengerCasesProxies.CountedProxy))]
internal sealed class Counted : PassengerCasesProxies.ICounted
{
	public int Count => 21;
}

[DebuggerTypeProxy("Nowhere.MissingProxy, Nowhere")]
internal sealed class Unproxied { public int Raw = 4; }

// Values whose calls go through the generic interfaces and bases their types implement: a dictionary through
// IDictionary and IReadOnlyDictionary, a string through IComparable, and a class through a base of other type arguments;
// and a list, in a program that has called no LINQ method, so has not loaded System.Linq.
internal static class Implemented
{
	public static void Run()
	{
		Dictionary<string, int> ages = new() { ["Ada"] = 36, ["Bob"] = 40 };
		IReadOnlyDictionary<string, int> readOnly = ages;
		string text = "hello";
		Tagged<string, int> tagged = new() { Tag = "one", Value = 1 };
		List<int> list = [1, 2, 3];
		Console.WriteLine("implemented");
		Console.WriteLine($"implemented {ages.Count}, {readOnly.Count}, {text}, {tagged.Describe()}, {list.Count}");
	}
}

internal class Valued<T>
{
	public T? Value;
	public string Describe() => $"valued {typeof(T).Name}";
}

internal sealed class Tagged<TTag, TValue> : Valued<TValue> { public TTag? Tag; }

// What an evaluation reads and leaves as it was: a record struct and a tuple a with copies, a char joined to a string,
// a box a pattern takes apart, read again each round, native integers wider than an int, and floats cast to integers
// they do not fit, with what the runtime makes of each cast, and what the exceptions the program catches say.
internal static class Reading
{
	public static void Run()
	{
		Spot spot = new(3, 4);
		(int, string) tuple = (1, "x");
		string word = "hello";
		char letter = 'A';
		object boxed = 42;
		nint native = unchecked((nint)5_000_000_000L);
		nuint unsignedNative = 7;
		double huge = 1e19, negOne = -1.0, d300 = 300.7;
		string[] casts = [$"{(int)huge}", $"{(int)-huge}", $"{(uint)negOne}", $"{(uint)(d300 * 1e8)}", $"{(byte)(d300 * 1e8)}", $"{(sbyte)-d300}",
			$"{(ushort)(negOne * -70000)}", $"{(nint)huge}"];
		string outOfRange = "";
		try
		{
			_ = new string(letter, -1);
		}
		catch (ArgumentOutOfRangeException thrown)
		{
			outOfRange = thrown.Message;
		}
		string outsideArray = "";
		try
		{
			_ = casts[casts.Length + 2];
		}
		catch (IndexOutOfRangeException thrown)
		{
			outsideArray = thrown.Message;
		}
		int[]? none = null;
		Labelled? nobody = null;
		string nullArray = "";
		try
		{
			_ = none![0];
		}
		catch (NullReferenceException thrown)
		{
			nullArray = thrown.Message;
		}
		for (int round = 0; round < 3; round++)
		{
			Console.WriteLine($"reading round {round}");
			Console.WriteLine($"read round {round}");
		}
		Console.WriteLine("reading");
		Console.WriteLine($"read {spot}, {tuple}, {word}{letter}, {boxed}, {native}, {unsignedNative}, {string.Join(", ", casts)}, {outOfRange}, {outsideArray}, {none?.Length}{nobody?.Value} and {nullArray}");
	}
}

internal record struct Spot(int X, int Y);

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

// A stop at which an evaluation spins, or sleeps, in a finally block until another thread, paused with the program, lets
// it out once it has been in the block 5 s, longer than the tests give an evaluation: the runtime puts an abort off until
// the block is left, by then after the program has gone on. That thread is in its loop by the stop, past the
// initialization of every type the evaluation uses (Finally's statics, Stopwatch, Thread): paused inside one, it would
// hold the evaluation short of its finally.
internal static class Finally
{
	private static readonly ManualResetEventSlim Looping = new();
	private static long heldSince;
	private static int letOut;
	public static int Turns;

	public static int Spins
	{
		get
		{
			try
			{
				Interlocked.CompareExchange(ref heldSince, Stopwatch.GetTimestamp(), 0);
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

	public static int Sleeps
	{
		get
		{
			try
			{
				Interlocked.CompareExchange(ref heldSince, Stopwatch.GetTimestamp(), 0);
			}
			finally
			{
				while (Volatile.Read(ref letOut) == 0)
				{
					Thread.Sleep(10);
				}
			}
			return ++Turns;
		}
	}

	private static bool HeldLongEnough() => Volatile.Read(ref heldSince) is var since && since != 0 && Stopwatch.GetElapsedTime(since) > TimeSpan.FromSeconds(5);

	public static void Run()
	{
		Thread freer = new(() =>
		{
			Stopwatch waited = Stopwatch.StartNew();
			while (!HeldLongEnough() && waited.Elapsed < TimeSpan.FromSeconds(60))
			{
				Looping.Set();
				Thread.Sleep(20);
			}
			Volatile.Write(ref letOut, 1);
		});
		freer.Start();
		Looping.Wait();
		Console.WriteLine("a finally next"); // stop before an evaluation spins or sleeps in a finally block
		freer.Join();
		Console.WriteLine($"let out, {Turns} turns"); // stop once the evaluation has been let out
	}
}

// One file, LinkedLibrary's Linked.cs, compiled into this program and into LinkedLibrary: each calls its own copy.
internal static class Linking
{
	public static void Run()
	{
		int own = Linked.Twice(2);
		int library = LinkedLibrary.Entry.Twice(3);
		Console.WriteLine($"linked {own} {library}");
	}
}

// LinkedLibrary loaded into two load contexts of its own, one image and two modules, each context's code called in turn.
internal static class Contexts
{
	public static void Run()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "LinkedLibrary.dll");
		MethodInfo[] twices = new MethodInfo[2];
		for (int i = 0; i < twices.Length; i++)
		{
			System.Runtime.Loader.AssemblyLoadContext context = new($"context {i + 1}");
			twices[i] = context.LoadFromAssemblyPath(path).GetType("LinkedLibrary.Entry")!.GetMethod("Twice")!;
		}
		Console.WriteLine("contexts loaded");
		int first = (int)twices[0].Invoke(null, [10])!;
		int second = (int)twices[1].Invoke(null, [11])!;
		Console.WriteLine($"contexts {first} {second}");
	}
}

// By-ref slots an evaluation reads and writes through: ref, out and in parameters, a ref local, a struct method's this.
internal static class Refs
{
	private static readonly int[] Firsts = [1];

	public static void Run()
	{
		int counted = 1;
		IncrementByRef(ref counted);
		AssignOut(out int assigned);
		int added = AddOneIn(in counted);
		int height = 0;
		for (int i = 0; i < 4; i++)
		{
			Climb(ref height);
		}
		Tile tile = new() { X = 3, Y = 4 };
		tile.Grow(2);
		Console.WriteLine($"refs {counted} {assigned} {added} {Firsts[0]} {height} {tile.X} {tile.Y}");
	}

	private static int IncrementByRef(ref int value)
	{
		ref int first = ref Firsts[0];
		value += 3;
		return value + first;
	}

	private static void AssignOut(out int value)
	{
		value = 42;
		Console.WriteLine($"assigned {value}");
	}

	private static int AddOneIn(in int value)
	{
		int one = 1;
		return value + one;
	}

	private static void Climb(ref int value)
	{
		value += 40;
	}
}

// Numbers in a culture of their own, whose separators an evaluation's interpolation must write as the program does.
internal static class Cultured
{
	public static void Run()
	{
		System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
		double d = 1234.5;
		int below = -1234;
		Console.WriteLine($"cultured {d} {d:N1} {d:P0} {below:N0}");
	}
}

// Variables typed by type parameters, the class's (int here) and the method's own (string).
internal static class Typed<T>
{
	public static string Make<U>(T t, U u)
	{
		T copy = t;
		U other = u;
		return $"{copy} {t} {other} {u}";
	}
}

internal struct Tile
{
	public int X;
	public int Y;

	public void Grow(int by)
	{
		X += by;
		Y += by;
	}
}

namespace System.Runtime.CompilerServices
{
	// What .NET 11 gives C# 15's unions, which this .NET 10 program declares itself.
	public interface IUnion
	{
		object? Value { get; }
	}

	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
	public sealed class UnionAttribute : Attribute;
}
