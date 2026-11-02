using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Exceptions in each shape a stack trace takes, each caught, whose traces the debugger reads from what they store:
// generic methods and types, a nested type, a lambda, a local function, an iterator, an await, a rethrow, hidden and
// inlined frames, a fault, an inner exception, and parameters by reference, arrays and pointers.
internal static class Traces
{
	public static void Run()
	{
		Catch(() => Throws(4, "four"));
		Catch(() => Holder<int>.Fail([1, 2]));
		Catch(Outer.Inner.Fail);
		Catch(() => throw new InvalidOperationException("from a lambda"));
		Catch(Locally);
		Catch(() =>
		{
			foreach (int number in Numbers())
			{
				Console.WriteLine(number);
			}
		});
		Catch(() => AwaitsAsync().GetAwaiter().GetResult());
		Catch(Rethrows);
		Catch(Hides);
		Catch(() => Faults(null));
		Catch(Wraps);
		Catch(Referenced);
		Console.WriteLine("traces done");
	}

	/// <summary>One exception wrapping another, each caught where it is thrown.</summary>
	public static void Wrapped()
	{
		Catch(Wraps);
		Console.WriteLine("wrapped done");
	}

	private static void Catch(Action action)
	{
		try
		{
			action();
		}
		catch (Exception e)
		{
			Console.WriteLine($"caught {e.GetType().Name}");
		}
	}

	private static T Throws<T>(T value, string name) => throw new ArgumentException($"{value} named {name}", nameof(value));

	private static void Locally()
	{
		Local(2);

		static void Local(int depth) => throw new NotSupportedException($"from a local function at {depth}");
	}

	private static IEnumerable<int> Numbers()
	{
		yield return 1;
		throw new InvalidOperationException("from an iterator");
	}

	private static async Task AwaitsAsync()
	{
		await Task.Yield();
		throw new TimeoutException("from an await");
	}

	private static void Rethrows()
	{
		ExceptionDispatchInfo thrown;
		try
		{
			Locally();
			return;
		}
		catch (Exception e)
		{
			thrown = ExceptionDispatchInfo.Capture(e);
		}
		thrown.Throw();
	}

	private static void Hides() => Inlined();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void Inlined() => Hidden();

	[StackTraceHidden]
	private static void Hidden() => throw new InvalidOperationException("from a hidden frame");

	private static int Faults(int[]? items) => items![0];

	private static void Wraps()
	{
		try
		{
			Outer.Inner.Fail();
		}
		catch (Exception e)
		{
			throw new InvalidOperationException("wrapped", e);
		}
	}

	private static unsafe void Referenced()
	{
		int number = 3;
		int* pointer = &number;
		ByReference(ref number, out _, pointer, [[1]], new int[1, 1]);
	}

	private static unsafe void ByReference(ref int number, out string text, int* pointer, int[][] jagged, int[,] grid) =>
		throw new FormatException($"{number} {*pointer} {jagged.Length} {grid.Length}");

	internal static class Outer
	{
		internal static class Inner
		{
			public static void Fail() => throw new KeyNotFoundException("from a nested type");
		}
	}

	internal static class Holder<T>
	{
		public static void Fail(T[] items) => throw new IndexOutOfRangeException($"{items.Length} {typeof(T).Name}");
	}
}
