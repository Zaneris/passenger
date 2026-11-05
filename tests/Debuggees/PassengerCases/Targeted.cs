using System.Diagnostics;
using Library;

[assembly: DebuggerDisplay("v{Major}.{Minor}", Target = typeof(Version))]
[assembly: DebuggerDisplay("{Level} {Unit,nq}", TargetTypeName = "Library.Gauge, PassengerCasesProxies")]
[assembly: DebuggerTypeProxy(typeof(BookcaseView), Target = typeof(Bookcase))]

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
		Console.WriteLine($"targeted {version} {gauge.Level} {bookcase.Books.Length} {ages.Count} {mixed.Count}");
	}
}

internal sealed class BookcaseView(Bookcase bookcase)
{
	public int Count => bookcase.Books.Length;

	public string First => bookcase.Books[0];
}
