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

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest()
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse)
            .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out ScopesResponse? scopesResponse);

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

        debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out List<DapVariable>? variables);

        Assert.Equal(11, variables.Count);
        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
        debugProtocolHost.AssertStructMemberVariables(variables.Single(s => s.Name == "structVar").VariablesReference);
        debugProtocolHost.AssertInstanceThisInstanceVariables(variables.Single(s => s.Name == "this").VariablesReference);

        List<DapVariable> expectedEnumVariables =
        [
            new() {Name = "Static members", Value = "", Type = "", EvaluateName = "Static members", VariablesReference = 72, PresentationHint = new DapPresentationHint("class")},
            new() {Name = "value__", Value = "1", Type = "int", EvaluateName = "value__" },
        ];

        debugProtocolHost.WithVariablesRequest(variables.Single(s => s.Name == "enumVar").VariablesReference, out List<DapVariable>? enumNestedVariables);
        enumNestedVariables.ShouldBeEquivalentToDebuggerVariables(expectedEnumVariables);

        List<DapVariable> expectedEnumStaticMemberVariables =
        [
            new() { Name = "FirstValue", Value = "FirstValue", Type = "DebuggableConsoleApp.MyEnum", EvaluateName = "FirstValue" },
            new() { Name = "SecondValue", Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum", EvaluateName = "SecondValue" },
            new() { Name = "ThirdValue", Value = "ThirdValue", Type = "DebuggableConsoleApp.MyEnum", EvaluateName = "ThirdValue" },
        ];

        debugProtocolHost.WithVariablesRequest(enumNestedVariables.Single(s => s.Name == "Static members").VariablesReference, out List<DapVariable>? enumStaticVariables);
        enumStaticVariables.ShouldBeEquivalentToDebuggerVariables(expectedEnumStaticMemberVariables);
        // TODO: Assert that none of the variable references are the same (other than 0)

        StoppedEvent stoppedEvent2 = await debugProtocolHost
            .WithContinueRequest()
            .WaitForStoppedEvent();
        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2)
            .WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out ScopesResponse? scopesResponse2)
            .WithVariablesRequest(scopesResponse2.Scopes.Single().VariablesReference, out List<DapVariable>? variables2);
        // The code is in a loop, so the same variables after a continue mean the references were numbered afresh.
        variables2.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }
}

file static class TestExtensions
{
    public static void AssertStructMemberVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "Id", EvaluateName = "Id", Value = "5", Type = "int" },
            new() { Name = "Name", EvaluateName = "Name", Value = "\"StructName\"", Type = "string" },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? structMemberVariables);
        structMemberVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }
    public static void AssertInstanceThisInstanceVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "_name", EvaluateName = "_name", Value = "\"TestName\"", Type = "string" },
            new() { Name = "_classField", EvaluateName = "_classField", Value = "{DebuggableConsoleApp.MyClass3}", Type = "DebuggableConsoleApp.MyClass3", VariablesReference = 6 },
            new() { Name = "_recordField", EvaluateName = "_recordField", Value = "MyRecord1 { X = 1, Y = 2 }", Type = "DebuggableConsoleApp.MyRecord1", VariablesReference = 14 },
            new() { Name = "ClassProperty", EvaluateName = "ClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 17 },
            new() { Name = "ClassProperty2", EvaluateName = "ClassProperty2", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 18 },
            new() { Name = "_intList", EvaluateName = "_intList", Value = "Count = 4", Type = "System.Collections.Generic.List<int>", VariablesReference = 7 },
            new() { Name = "_intArray", EvaluateName = "_intArray", Value = "int[4]", Type = "int[]", VariablesReference = 8 },
            new() { Name = "_instanceField", EvaluateName = "_instanceField", Value = "5", Type = "int" },
            new() { Name = "_structField", EvaluateName = "_structField", Value = "{DebuggableConsoleApp.MyStruct}", Type = "DebuggableConsoleApp.MyStruct", VariablesReference = 15 },
            new() { Name = "IntProperty", EvaluateName = "IntProperty", Value = "10", Type = "int" },
            new() { Name = "_classWithDebugDisplay", EvaluateName = "_classWithDebugDisplay", Value = "IntProperty = 14", Type = "DebuggableConsoleApp.ClassWithDebugDisplay", VariablesReference = 9 },
            new() { Name = "_classWithDebugDisplay2", EvaluateName = "_classWithDebugDisplay2", Value = "Test = stringValue1", Type = "DebuggableConsoleApp.ClassWithDebugDisplay2", VariablesReference = 10 },
            new() { Name = "_classWithDebugDisplay3", EvaluateName = "_classWithDebugDisplay3", Value = "Test = stringValue2", Type = "DebuggableConsoleApp.ClassWithDebugDisplay3", VariablesReference = 11 },
            new() { Name = "_myClassWithGeneric", EvaluateName = "_myClassWithGeneric", Value = "{DebuggableConsoleApp.MyClassWithGeneric<int>}", Type = "DebuggableConsoleApp.MyClassWithGeneric<int>", VariablesReference = 12 },
            new() { Name = "_intDictionary", EvaluateName = "_intDictionary", Value = "Count = 3", Type = "System.Collections.Generic.Dictionary<int, int>", VariablesReference = 13 },
            new() { Name = "FieldFromBase", EvaluateName = "FieldFromBase", Value = "42", Type = "int" },
            new() { Name = "PropertyFromBase", EvaluateName = "PropertyFromBase", Value = "84", Type = "int" },
            new() { Name = "_doubler", EvaluateName = "_doubler", Value = "{System.Func<int, int>}", Type = "System.Func<int, int>", VariablesReference = 16 },
            new() { Name = "Static members", Value = "", Type = "", EvaluateName = "Static members", VariablesReference = 19, PresentationHint = new DapPresentationHint("class")},
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? thisInstanceVariables);
        thisInstanceVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
        debugProtocolHost.AssertIntArrayVariables(thisInstanceVariables.Single(s => s.Name == "_intArray").VariablesReference);
        debugProtocolHost.AssertInstanceThisStaticVariables(thisInstanceVariables.Single(s => s.Name == "Static members").VariablesReference);
        debugProtocolHost.AssertClassWithDebuggerTypeProxyVariables(thisInstanceVariables.Single(s => s.Name == "_classWithDebugDisplay").VariablesReference);
        debugProtocolHost.AssertGenericClassVariables(thisInstanceVariables.Single(s => s.Name == "_myClassWithGeneric").VariablesReference);
        debugProtocolHost.AssertIntListVariables(thisInstanceVariables.Single(s => s.Name == "_intList").VariablesReference);
        debugProtocolHost.AssertDictionaryVariables(thisInstanceVariables.Single(s => s.Name == "_intDictionary").VariablesReference);
        debugProtocolHost.AssertClassWithFieldOfNestedClassType_Variables(thisInstanceVariables.Single(s => s.Name == "_classField").VariablesReference);
        debugProtocolHost.AssertPropertyStoredClass_Variables(thisInstanceVariables.Single(s => s.Name == "ClassProperty").VariablesReference);
        debugProtocolHost.AssertRecord_Variables(thisInstanceVariables.Single(s => s.Name == "_recordField").VariablesReference);
    }

    private static void AssertInstanceThisStaticVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        string expectedDateTime = new DateTime(2026, 6, 13, 7, 18, 38).ToString();
        List<DapVariable> expectedVariables =
        [
            new() { Name = "_counter", EvaluateName = "_counter", Value = "1", Type = "int" },
            new() { Name = "IntStaticProperty", EvaluateName = "IntStaticProperty", Value = "10", Type = "int" },
            new() { Name = "StaticClassProperty", EvaluateName = "StaticClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 25 },
            new() { Name = "_staticClassField", EvaluateName = "_staticClassField", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 20 },
            new() { Name = "_staticIntList", EvaluateName = "_staticIntList", Value = "Count = 4", Type = "System.Collections.Generic.List<int>", VariablesReference = 21 },
            new() { Name = "_fieldDictionary", EvaluateName = "_fieldDictionary", Value = "Count = 0", Type = "System.Collections.Generic.Dictionary<DebuggableConsoleApp.MyClass2, DebuggableConsoleApp.MyClass>", VariablesReference = 22 },
            new() { Name = "_utcNow", EvaluateName = "_utcNow", Value = expectedDateTime, Type = "System.DateTime", VariablesReference = 23 },
            new() { Name = "_nullableUtcNow", EvaluateName = "_nullableUtcNow", Value = expectedDateTime, Type = "System.DateTime?", VariablesReference = 24 },
            new() { Name = "_instanceStaticField", EvaluateName = "_instanceStaticField", Value = "6", Type = "int" },
            new() { Name = "StaticFieldFromBase", EvaluateName = "StaticFieldFromBase", Value = "168", Type = "int" },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? instanceThisStaticVariables);
        instanceThisStaticVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    private static readonly DapPresentationHint _arrayElementPresentationHint = new("data");
    public static void AssertIntArrayVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "[0]", EvaluateName = "[0]", Value = "2", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[1]", EvaluateName = "[1]", Value = "3", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[2]", EvaluateName = "[2]", Value = "5", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[3]", EvaluateName = "[3]", Value = "7", Type = "int", PresentationHint = _arrayElementPresentationHint },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? intArrayVariables);
        intArrayVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static void AssertGenericClassVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "GenericItemsField", EvaluateName = "GenericItemsField", Value = "int[1]", Type = "int[]", VariablesReference = 27 },
            new() { Name = "GenericItems", EvaluateName = "GenericItems", Value = "int[1]", Type = "int[]", VariablesReference = 28 },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? genericClassVariables);
        genericClassVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static void AssertIntListVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "[0]", EvaluateName = "[0]", Value = "1", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[1]", EvaluateName = "[1]", Value = "4", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[2]", EvaluateName = "[2]", Value = "8", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[3]", EvaluateName = "[3]", Value = "25", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "Raw View", EvaluateName = "Raw View", Value = "", Type = "", VariablesReference = 29, PresentationHint = new DapPresentationHint("class") },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? intListVariables);
        intListVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    // An entry is named by its key and shows its value, as its [DebuggerDisplay("{Value}", Name = "[{Key}]")] asks.
    public static void AssertDictionaryVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "[5]", EvaluateName = "[5]", Value = "50", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 30, PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[10]", EvaluateName = "[10]", Value = "100", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 31, PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[15]", EvaluateName = "[15]", Value = "150", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 32, PresentationHint = _arrayElementPresentationHint },
            new() { Name = "Raw View", EvaluateName = "Raw View", Value = "", Type = "", VariablesReference = 33, PresentationHint = new DapPresentationHint("class") },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? dictionaryVariables);
        dictionaryVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static void AssertClassWithFieldOfNestedClassType_Variables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "IntField", EvaluateName = "IntField", Value = "6", Type = "int" },
            new() { Name = "IntProperty", EvaluateName = "IntProperty", Value = "6", Type = "int" },
            new() { Name = "MyProperty", EvaluateName = "MyProperty", Value = "\"Hello\"", Type = "string" },
            new() { Name = "NestedClassProperty", EvaluateName = "NestedClassProperty", Value = "{DebuggableConsoleApp.MyClassContainingAnotherClass.MyNestedClass}", Type = "DebuggableConsoleApp.MyClassContainingAnotherClass.MyNestedClass", VariablesReference = 34 },
            new() { Name = "NestedGenericClassProperty", EvaluateName = "NestedGenericClassProperty", Value = "{DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<string, int>.MyNestedGenericClass<long, float>}", Type = "DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<string, int>.MyNestedGenericClass<long, float>", VariablesReference = 35 },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? classWithNestedClassFieldVariables);
        classWithNestedClassFieldVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static void AssertPropertyStoredClass_Variables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "IntField", EvaluateName = "IntField", Value = "6", Type = "int" },
            new() { Name = "IntProperty", EvaluateName = "IntProperty", Value = "6", Type = "int" },
            new() { Name = "MyProperty", EvaluateName = "MyProperty", Value = "\"Hello\"", Type = "string" },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? classWithNestedClassFieldVariables);
        classWithNestedClassFieldVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    public static void AssertClassWithDebuggerTypeProxyVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "IntPropertyViaDebugView", EvaluateName = "IntPropertyViaDebugView", Value = "14", Type = "int" },
            new() { Name = "[0]", EvaluateName = "[0]", Value = "2", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[1]", EvaluateName = "[1]", Value = "3", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[2]", EvaluateName = "[2]", Value = "5", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "[3]", EvaluateName = "[3]", Value = "7", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { Name = "Raw View", EvaluateName = "Raw View", Value = "", Type = "", VariablesReference = 26, PresentationHint = new DapPresentationHint("class") },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? classWithDebuggerTypeProxyVariables);
        classWithDebuggerTypeProxyVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    private static void AssertRecord_Variables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { Name = "EqualityContract", EvaluateName = "EqualityContract", Value = "DebuggableConsoleApp.MyRecord1", Type = "System.RuntimeType", VariablesReference = 36 },
            new() { Name = "X", EvaluateName = "X", Value = "1", Type = "int" },
            new() { Name = "Y", EvaluateName = "Y", Value = "2", Type = "int" },
        ];
        debugProtocolHost.WithVariablesRequest(variablesReference, out List<DapVariable>? recordVariables);
        recordVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        debugProtocolHost.WithVariablesRequest(recordVariables.Single(s => s.Name == "EqualityContract").VariablesReference, out List<DapVariable>? recordEqualityContractVariables);
    }
}
