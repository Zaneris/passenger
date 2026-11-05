using System.Collections;
using System.Diagnostics;

namespace Library;

/// <summary>A program's values of a library's types a client tells the debugger to present (PassengerCasesProxies'
/// Presentable.cs): a point, a script's node with a member it marks, and one with a [DebuggerDisplay] of its own; and a
/// bag whose [DebuggerTypeProxy] names its generic view closed.</summary>
public static class Presented
{
	public static void Run()
	{
		Point2 point = new(1.5f, -2f);
		Script root = new("root");
		root.Add(new Node("kid"));
		Labelled labelled = new("labelled");
		Bag bag = new([5, 6, 7]);
		Console.WriteLine($"presented {point.X} {root.Name} {labelled.Label} {bag.Count()}");
	}
}

public sealed class Script(string name) : Node2(name)
{
	public int Own = 4;

	[Tuned]
	public float Speed { get; set; } = 2.5f;
}

[DebuggerDisplay("Labelled {Label}")]
public sealed class Labelled(string name) : Node(name)
{
	public string Label = "own";
}

[DebuggerTypeProxy(typeof(BagView<int>))]
public sealed class Bag(int[] items) : IEnumerable<int>
{
	public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)items).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class BagView<T>(IEnumerable<T> bag)
{
	[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
	public T[] Items => [.. bag];
}
