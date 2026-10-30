using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class VariablesTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task A_stop_in_a_method_lists_its_locals_as_they_are()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        await debugProtocolHost
            .BreakpointsAsync()
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        ScopesResponse? scopesResponse = await debugProtocolHost.ScopesAsync(stackTraceResponse.StackFrames!.First().Id);

        Assert.Single(scopesResponse.Scopes);
        DapScope scope = scopesResponse.Scopes.Single();

        List<DapVariable> expectedVariables =
        [
            new() { VariablesReference = 2, Name = "this",                  EvaluateName = "this",                  Value = "{DebuggableConsoleApp.MyClass}",   Type = "DebuggableConsoleApp.MyClass" },
            new() { VariablesReference = 0, Name = "myParam",               EvaluateName = "myParam",               Value = "13",                               Type = "long" },
            new() { VariablesReference = 0, Name = "myIntParam",            EvaluateName = "myIntParam",            Value = "6",                                Type = "int" },
            new() { VariablesReference = 0, Name = "myInt",                 EvaluateName = "myInt",                 Value = "4",                                Type = "int" },
            new() { VariablesReference = 3, Name = "enumVar",               EvaluateName = "enumVar",               Value = "SecondValue",                      Type = "DebuggableConsoleApp.MyEnum" },
            new() { VariablesReference = 4, Name = "enumWithFlagsVar",      EvaluateName = "enumWithFlagsVar",      Value = "FlagValue1 | FlagValue3",          Type = "DebuggableConsoleApp.MyEnumWithFlags" },
            new() { VariablesReference = 0, Name = "nullableInt",           EvaluateName = "nullableInt",           Value = "null",                             Type = "int?" },
            new() { VariablesReference = 5, Name = "structVar",             EvaluateName = "structVar",             Value = "{DebuggableConsoleApp.MyStruct}",  Type = "DebuggableConsoleApp.MyStruct" },
            new() { VariablesReference = 0, Name = "nullableIntWithVal",    EvaluateName = "nullableIntWithVal",    Value = "4",                                Type = "int?" },
            new() { VariablesReference = 0, Name = "nullableRefType",       EvaluateName = "nullableRefType",       Value = "null",                             Type = "DebuggableConsoleApp.MyClass" },
            new() { VariablesReference = 0, Name = "anotherVar",            EvaluateName = "anotherVar",            Value = "\"asdf\"",                         Type = "string" },
        ];

        List<DapVariable>? variables = await debugProtocolHost.VariablesAsync(scope.VariablesReference);

        Assert.Equal(11, variables.Count);
        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
        await debugProtocolHost.AssertStructMemberVariables(variables.Single(s => s.Name == "structVar").VariablesReference);
        await debugProtocolHost.AssertInstanceThisInstanceVariables(variables.Single(s => s.Name == "this").VariablesReference);

        List<DapVariable> expectedEnumVariables =
        [
            new() {Name = "Static members", Value = "", Type = "", VariablesReference = 72, PresentationHint = new DapPresentationHint("class")},
            new() {Name = "value__", Value = "1", Type = "int", EvaluateName = "((int)enumVar)" },
        ];

        List<DapVariable>? enumNestedVariables = await debugProtocolHost.VariablesAsync(variables.Single(s => s.Name == "enumVar").VariablesReference);
        enumNestedVariables.ShouldBeEquivalentToDebuggerVariables(expectedEnumVariables);

        List<DapVariable> expectedEnumStaticMemberVariables =
        [
            new() { Name = "FirstValue", Value = "FirstValue", Type = "DebuggableConsoleApp.MyEnum" },
            new() { Name = "SecondValue", Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum" },
            new() { Name = "ThirdValue", Value = "ThirdValue", Type = "DebuggableConsoleApp.MyEnum" },
        ];

        List<DapVariable>? enumStaticVariables = await debugProtocolHost.VariablesAsync(enumNestedVariables.Single(s => s.Name == "Static members").VariablesReference);
        enumStaticVariables.ShouldBeEquivalentToDebuggerVariables(expectedEnumStaticMemberVariables);
        // TODO: Assert that none of the variable references are the same (other than 0)

        StoppedEvent stoppedEvent2 = await debugProtocolHost
            .ContinueAsync()
            .WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);
        ScopesResponse? scopesResponse2 = await debugProtocolHost.ScopesAsync(stackTraceResponse2.StackFrames!.First().Id);
        List<DapVariable>? variables2 = await debugProtocolHost.VariablesAsync(scopesResponse2.Scopes.Single().VariablesReference);
        // The code is in a loop, so the same variables after a continue mean the references were numbered afresh.
        variables2.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }
}

file static class TestExtensions
{
    public static async Task AssertStructMemberVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "Id", EvaluateName = "structVar.Id", Value = "5", Type = "int" },
            new() { Name = "Name", EvaluateName = "structVar.Name", Value = "\"StructName\"", Type = "string" },
        ];
        List<DapVariable>? structMemberVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        structMemberVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }
    public static async Task AssertInstanceThisInstanceVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "_name", EvaluateName = "this._name", Value = "\"TestName\"", Type = "string" },
            new() { Name = "_classField", EvaluateName = "this._classField", Value = "{DebuggableConsoleApp.MyClass3}", Type = "DebuggableConsoleApp.MyClass3", VariablesReference = 6 },
            new() { Name = "_recordField", EvaluateName = "this._recordField", Value = "MyRecord1 { X = 1, Y = 2 }", Type = "DebuggableConsoleApp.MyRecord1", VariablesReference = 14 },
            new() { Name = "ClassProperty", EvaluateName = "this.ClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 17 },
            new() { Name = "ClassProperty2", EvaluateName = "this.ClassProperty2", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 18 },
            new() { Name = "_intList", EvaluateName = "this._intList", Value = "Count = 4", Type = "System.Collections.Generic.List<int>", VariablesReference = 7 },
            new() { Name = "_intArray", EvaluateName = "this._intArray", Value = "int[4]", Type = "int[]", VariablesReference = 8 },
            new() { Name = "_instanceField", EvaluateName = "this._instanceField", Value = "5", Type = "int" },
            new() { Name = "_structField", EvaluateName = "this._structField", Value = "{DebuggableConsoleApp.MyStruct}", Type = "DebuggableConsoleApp.MyStruct", VariablesReference = 15 },
            new() { Name = "IntProperty", EvaluateName = "this.IntProperty", Value = "10", Type = "int" },
            new() { Name = "_classWithDebugDisplay", EvaluateName = "this._classWithDebugDisplay", Value = "IntProperty = 14", Type = "DebuggableConsoleApp.ClassWithDebugDisplay", VariablesReference = 9 },
            new() { Name = "_classWithDebugDisplay2", EvaluateName = "this._classWithDebugDisplay2", Value = "Test = stringValue1", Type = "DebuggableConsoleApp.ClassWithDebugDisplay2", VariablesReference = 10 },
            new() { Name = "_classWithDebugDisplay3", EvaluateName = "this._classWithDebugDisplay3", Value = "Test = stringValue2", Type = "DebuggableConsoleApp.ClassWithDebugDisplay3", VariablesReference = 11 },
            new() { Name = "_myClassWithGeneric", EvaluateName = "this._myClassWithGeneric", Value = "{DebuggableConsoleApp.MyClassWithGeneric<int>}", Type = "DebuggableConsoleApp.MyClassWithGeneric<int>", VariablesReference = 12 },
            new() { Name = "_intDictionary", EvaluateName = "this._intDictionary", Value = "Count = 3", Type = "System.Collections.Generic.Dictionary<int, int>", VariablesReference = 13 },
            new() { Name = "FieldFromBase", EvaluateName = "this.FieldFromBase", Value = "42", Type = "int" },
            new() { Name = "PropertyFromBase", EvaluateName = "this.PropertyFromBase", Value = "84", Type = "int" },
            new() { Name = "_doubler", EvaluateName = "this._doubler", Value = "{System.Func<int, int>}", Type = "System.Func<int, int>", VariablesReference = 16 },
            new() { Name = "Static members", Value = "", Type = "", VariablesReference = 19, PresentationHint = new DapPresentationHint("class")},
        ];
        List<DapVariable>? thisInstanceVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        thisInstanceVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
        await debugProtocolHost.AssertIntArrayVariables(thisInstanceVariables.Single(s => s.Name == "_intArray").VariablesReference);
        await debugProtocolHost.AssertInstanceThisStaticVariables(thisInstanceVariables.Single(s => s.Name == "Static members").VariablesReference);
        await debugProtocolHost.AssertClassWithDebuggerTypeProxyVariables(thisInstanceVariables.Single(s => s.Name == "_classWithDebugDisplay").VariablesReference);
        await debugProtocolHost.AssertGenericClassVariables(thisInstanceVariables.Single(s => s.Name == "_myClassWithGeneric").VariablesReference);
        await debugProtocolHost.AssertIntListVariables(thisInstanceVariables.Single(s => s.Name == "_intList").VariablesReference);
        await debugProtocolHost.AssertDictionaryVariables(thisInstanceVariables.Single(s => s.Name == "_intDictionary").VariablesReference);
        await debugProtocolHost.AssertClassWithFieldOfNestedClassType_Variables(thisInstanceVariables.Single(s => s.Name == "_classField").VariablesReference);
        await debugProtocolHost.AssertPropertyStoredClass_Variables(thisInstanceVariables.Single(s => s.Name == "ClassProperty").VariablesReference);
        await debugProtocolHost.AssertRecord_Variables(thisInstanceVariables.Single(s => s.Name == "_recordField").VariablesReference);
    }

    private static async Task AssertInstanceThisStaticVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        string expectedDateTime = new DateTime(2026, 6, 13, 7, 18, 38).ToString();
        List<DapVariable> expectedVariables =
        [
            new() { Name = "_counter", Value = "1", Type = "int" },
            new() { Name = "IntStaticProperty", Value = "10", Type = "int" },
            new() { Name = "StaticClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 25 },
            new() { Name = "_staticClassField", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 20 },
            new() { Name = "_staticIntList", Value = "Count = 4", Type = "System.Collections.Generic.List<int>", VariablesReference = 21 },
            new() { Name = "_fieldDictionary", Value = "Count = 0", Type = "System.Collections.Generic.Dictionary<DebuggableConsoleApp.MyClass2, DebuggableConsoleApp.MyClass>", VariablesReference = 22 },
            new() { Name = "_utcNow", Value = expectedDateTime, Type = "System.DateTime", VariablesReference = 23 },
            new() { Name = "_nullableUtcNow", Value = expectedDateTime, Type = "System.DateTime?", VariablesReference = 24 },
            new() { Name = "_instanceStaticField", Value = "6", Type = "int" },
            new() { Name = "StaticFieldFromBase", Value = "168", Type = "int" },
        ];
        List<DapVariable>? instanceThisStaticVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        instanceThisStaticVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    private static readonly DapPresentationHint _arrayElementPresentationHint = new("data");

    /// <summary>A debugger view's element and member, each the view's copy, which a value set would change alone.</summary>
    private static readonly DapPresentationHint _viewedElementPresentationHint = new("data", ["readOnly"]);
    private static readonly DapPresentationHint _viewedPresentationHint = new(Attributes: ["readOnly"]);
    public static async Task AssertIntArrayVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "[0]", EvaluateName = "this._intArray[0]", Value = "2", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[1]", EvaluateName = "this._intArray[1]", Value = "3", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[2]", EvaluateName = "this._intArray[2]", Value = "5", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[3]", EvaluateName = "this._intArray[3]", Value = "7", Type = "int", PresentationHint = _arrayElementPresentationHint },
        ];
        List<DapVariable>? intArrayVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        intArrayVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static async Task AssertGenericClassVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "GenericItemsField", EvaluateName = "this._myClassWithGeneric.GenericItemsField", Value = "int[1]", Type = "int[]", VariablesReference = 27 },
            new() { Name = "GenericItems", EvaluateName = "this._myClassWithGeneric.GenericItems", Value = "int[1]", Type = "int[]", VariablesReference = 28 },
        ];
        List<DapVariable>? genericClassVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        genericClassVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static async Task AssertIntListVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "[0]", EvaluateName = "this._intList[0]", Value = "1", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[1]", EvaluateName = "this._intList[1]", Value = "4", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[2]", EvaluateName = "this._intList[2]", Value = "8", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[3]", EvaluateName = "this._intList[3]", Value = "25", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "Raw View", Value = "", Type = "", VariablesReference = 29, PresentationHint = new DapPresentationHint("class") },
        ];
        List<DapVariable>? intListVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        intListVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    // An entry is named by its key and shows its value, as its [DebuggerDisplay("{Value}", Name = "[{Key}]")] asks.
    public static async Task AssertDictionaryVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "[5]", EvaluateName = "(new System.Collections.Generic.IDictionaryDebugView<int, int>(this._intDictionary)).Items[0]", Value = "50", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 30, PresentationHint = _viewedElementPresentationHint },
            new() { Name = "[10]", EvaluateName = "(new System.Collections.Generic.IDictionaryDebugView<int, int>(this._intDictionary)).Items[1]", Value = "100", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 31, PresentationHint = _viewedElementPresentationHint },
            new() { Name = "[15]", EvaluateName = "(new System.Collections.Generic.IDictionaryDebugView<int, int>(this._intDictionary)).Items[2]", Value = "150", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 32, PresentationHint = _viewedElementPresentationHint },
            new() { Name = "Raw View", Value = "", Type = "", VariablesReference = 33, PresentationHint = new DapPresentationHint("class") },
        ];
        List<DapVariable>? dictionaryVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        dictionaryVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static async Task AssertClassWithFieldOfNestedClassType_Variables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "IntField", EvaluateName = "this._classField.IntField", Value = "6", Type = "int" },
            new() { Name = "IntProperty", EvaluateName = "this._classField.IntProperty", Value = "6", Type = "int" },
            new() { Name = "MyProperty", EvaluateName = "this._classField.MyProperty", Value = "\"Hello\"", Type = "string" },
            new() { Name = "NestedClassProperty", EvaluateName = "this._classField.NestedClassProperty", Value = "{DebuggableConsoleApp.MyClassContainingAnotherClass.MyNestedClass}", Type = "DebuggableConsoleApp.MyClassContainingAnotherClass.MyNestedClass", VariablesReference = 34 },
            new() { Name = "NestedGenericClassProperty", EvaluateName = "this._classField.NestedGenericClassProperty", Value = "{DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<string, int>.MyNestedGenericClass<long, float>}", Type = "DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<string, int>.MyNestedGenericClass<long, float>", VariablesReference = 35 },
        ];
        List<DapVariable>? classWithNestedClassFieldVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        classWithNestedClassFieldVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static async Task AssertPropertyStoredClass_Variables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "IntField", EvaluateName = "this.ClassProperty.IntField", Value = "6", Type = "int" },
            new() { Name = "IntProperty", EvaluateName = "this.ClassProperty.IntProperty", Value = "6", Type = "int" },
            new() { Name = "MyProperty", EvaluateName = "this.ClassProperty.MyProperty", Value = "\"Hello\"", Type = "string" },
        ];
        List<DapVariable>? classWithNestedClassFieldVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        classWithNestedClassFieldVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static async Task AssertClassWithDebuggerTypeProxyVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "IntPropertyViaDebugView", EvaluateName = "(new DebuggableConsoleApp.ClassWithDebugDisplayDebugView(this._classWithDebugDisplay)).IntPropertyViaDebugView", Value = "14", Type = "int", PresentationHint = _viewedPresentationHint },
            new() { Name = "[0]", EvaluateName = "(new DebuggableConsoleApp.ClassWithDebugDisplayDebugView(this._classWithDebugDisplay)).Items[0]", Value = "2", Type = "int", PresentationHint = _viewedElementPresentationHint },
            new() { Name = "[1]", EvaluateName = "(new DebuggableConsoleApp.ClassWithDebugDisplayDebugView(this._classWithDebugDisplay)).Items[1]", Value = "3", Type = "int", PresentationHint = _viewedElementPresentationHint },
            new() { Name = "[2]", EvaluateName = "(new DebuggableConsoleApp.ClassWithDebugDisplayDebugView(this._classWithDebugDisplay)).Items[2]", Value = "5", Type = "int", PresentationHint = _viewedElementPresentationHint },
            new() { Name = "[3]", EvaluateName = "(new DebuggableConsoleApp.ClassWithDebugDisplayDebugView(this._classWithDebugDisplay)).Items[3]", Value = "7", Type = "int", PresentationHint = _viewedElementPresentationHint },
            new() { Name = "Raw View", Value = "", Type = "", VariablesReference = 26, PresentationHint = new DapPresentationHint("class") },
        ];
        List<DapVariable>? classWithDebuggerTypeProxyVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        classWithDebuggerTypeProxyVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    private static async Task AssertRecord_Variables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "EqualityContract", EvaluateName = "this._recordField.EqualityContract", Value = "DebuggableConsoleApp.MyRecord1", Type = "System.RuntimeType", VariablesReference = 36 },
            new() { Name = "X", EvaluateName = "this._recordField.X", Value = "1", Type = "int" },
            new() { Name = "Y", EvaluateName = "this._recordField.Y", Value = "2", Type = "int" },
        ];
        List<DapVariable>? recordVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        recordVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        List<DapVariable>? recordEqualityContractVariables = await debugProtocolHost.VariablesAsync(recordVariables.Single(s => s.Name == "EqualityContract").VariablesReference);
    }
}
