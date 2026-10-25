using DebuggableConsoleApp.Lambdas;
using DebuggableConsoleApp.Namespace2;

namespace DebuggableConsoleApp;

public static class Program
{
	public static void Main(string[] args)
	{
		Console.WriteLine("DebuggableConsoleApp is running");
		Console.WriteLine("Log2");
		MyLambdaClass myLambdaClass = new MyLambdaClass();MyClassWithGenericMethod myClassWithGenericMethod = new MyClassWithGenericMethod();
		MyClass myClass = new MyClass();
		MyAsyncClass myAsyncClass = new MyAsyncClass();
		AsyncMethodEvalClass myAsyncMethodEvalClass = new AsyncMethodEvalClass();
		MyClassNoMembers myClassNoMembers = new MyClassNoMembers();
		HitConditionClass hitConditionClass = new HitConditionClass();
		VariablesClass variablesClass = new VariablesClass();
		ColumnBreakpointClass columnBreakpointClass = new ColumnBreakpointClass();
		ExceptionToThrow exceptionToThrow = ExceptionToThrow.None;
		while (true)
		{
			// Keep the application running to allow debugging
			myLambdaClass.Test();
			myClass.MyMethod(13, 6);
			myClassNoMembers.MyMethod(42);
			hitConditionClass.Test();
			variablesClass.Test();
			columnBreakpointClass.Test();
			MultilineSwitchInMethodCall.Test();
			ClassWithBclCall.Test(4);
			int asyncResult = myAsyncClass.MyMethodAsync(4).GetAwaiter().GetResult();
			myAsyncMethodEvalClass.Test().GetAwaiter().GetResult();
			Exceptions.Test(exceptionToThrow);
			MyGenericClassContainingAnotherGenericClass<int, string>.MyNestedGenericClass<double, bool>.Test();
			SameNamedClass.Test();
			Namespace3.SameNamedClass.Test();
			OverloadedMethodsClass.CallAllMethods();
			MyStaticClass.Test();
			myLambdaClass.VariableShadowingMethod();
			myClassWithGenericMethod.Test(4);
			AsyncStackTraceClass.TestAsync().GetAwaiter().GetResult();
			Thread.Sleep(100);
			//await Task.Delay(500);
		}
	}
}
