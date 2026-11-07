using System.Diagnostics;
using Library;

[assembly: DebuggerDisplay("v{Major}.{Minor}", Target = typeof(Version))]
[assembly: DebuggerDisplay("{Level} {Unit,nq}", TargetTypeName = "Library.Gauge, PassengerCasesProxies")]
[assembly: DebuggerTypeProxy(typeof(BookcaseView), Target = typeof(Bookcase))]
[assembly: DebuggerDisplay("any box", Target = typeof(TargetedBox<>))]
[assembly: DebuggerDisplay("number box {Held}", Target = typeof(TargetedBox<int>))]
[assembly: DebuggerDisplay("text box {Held,nq}", Target = typeof(TargetedBox<string>))]
[assembly: DebuggerDisplay("a box of boxes", Target = typeof(TargetedBox<TargetedBox<int>[]>))]
[assembly: DebuggerTypeProxy(typeof(NumberBoxView), Target = typeof(TargetedBox<int>))]

// Values of types whose displays and views the program's assembly declares, the runtime's and a library's, and
// dictionaries whose rows name their values' types.
internal static class Targeted
{
	public static void Run()
	{
		Version version = new(1, 2, 3);
		Gauge gauge = new(2.5);
		Bookcase bookcase = new("Dune", "Emma");
		Dictionary<string, int> ages = new() { ["ann"] = 31 };
		Dictionary<string, object?> mixed = new() { ["text"] = "words", ["number"] = 4.5, ["none"] = null };
		TargetedBox<int> numberBox = new(5);
		TargetedBox<string> textBox = new("words");
		TargetedBox<double> otherBox = new(4.5);
		TargetedBox<TargetedBox<int>[]> boxes = new([numberBox, numberBox]);
		Console.WriteLine($"targeted {version} {gauge.Level} {bookcase.Books.Length} {ages.Count} {mixed.Count} {numberBox.Held} {textBox.Held} {otherBox.Held} {boxes.Held.Length}");
	}
}

internal sealed class TargetedBox<T>(T held)
{
	public T Held { get; } = held;
}

internal sealed class NumberBoxView(TargetedBox<int> box)
{
	public int Doubled => box.Held * 2;
}

internal sealed class BookcaseView(Bookcase bookcase)
{
	public int Count => bookcase.Books.Length;

	public string First => bookcase.Books[0];
}
