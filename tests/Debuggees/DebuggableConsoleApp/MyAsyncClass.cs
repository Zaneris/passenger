using DebuggableConsoleApp.Namespace1;

namespace DebuggableConsoleApp;

public class MyAsyncClass
{
	public async Task<int> MyMethodAsync(int myParam)
	{
		int intVar = 10;
		intVar = 10;
		int result = await AnotherClass.AnotherMethodAsync();
		int result2 = await AnotherClass.AnotherMethodAsync();
		int result3 = await AnotherClass.AnotherMethodAsync();
		AnotherClass.AsyncVoidMethod();
		return result;
	}

	public async Task<int> MyAsyncMethodWithNoAwaits()
	{
		int intVar = 10;
		int result = AnotherClass.AnotherMethodAsync().GetAwaiter().GetResult();
		string myString = "Hello";
		int result2 = AnotherClass.AnotherMethodAsync().Result;
		int result3 = AnotherClass.AnotherMethodAsync().GetAwaiter().GetResult();
		return result;
	}

	private int _fieldInAsyncClass = 42;
}
