namespace Library;

/// <summary>A library's types that carry no debugger attributes, which the program's assembly gives a display and a view
/// of its own ([assembly: DebuggerDisplay] and [assembly: DebuggerTypeProxy] with a Target).</summary>
public sealed class Gauge(double level)
{
	public double Level = level;

	public string Unit = "bar";
}

public sealed class Bookcase(params string[] books)
{
	public string[] Books = books;
}
