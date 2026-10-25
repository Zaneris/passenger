namespace DebuggableConsoleApp;

public class MyClassNoMembers
{
	public void MyMethod(long myParam)
	{
		int myInt = 4;
		string anotherVar = "asdf";
	}

	public string Test(int test)
	{
		return "stringValue2";
	}
}

public class MyClassWithGeneric<T>
{
	public required T[] GenericItemsField;
	public required T[] GenericItems { get; set; }
}
