namespace DebuggableConsoleApp;

public class HitConditionClass
{
	private static int _count = 0;
	private static string _name = "Ada";

	public void Test()
	{
		_count++;
		; // breakpoint here
	}
}
