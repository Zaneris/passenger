namespace DebuggableConsoleApp;

public class ColumnBreakpointClass
{
	private int _sink;

	public void Test()
	{
		int first = 1; int second = first + 1; int third = second + 1; // column-breakpoint-line
		_sink = third;

		int multi =
			1 + 25 +
			12 + 5; int after = multi + 1; // column-breakpoint-multiline
		_sink += after;
	}
}
