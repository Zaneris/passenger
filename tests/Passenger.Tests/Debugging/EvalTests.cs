using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class EvalTests(ITestOutputHelper testOutputHelper)
{
    /// <summary>At a stop in a method, its locals and parameters are listed as they are, and an evaluation reads and assigns
    /// them, its fields and properties (static ones too, by any qualified name), and calls its methods, a chain of calls
    /// keeping each one's result alive.</summary>
    [Fact]
    public async Task At_a_stop_the_locals_are_listed_as_they_are_and_an_evaluation_reads_assigns_and_calls_what_the_method_sees()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest(Debuggees.MyMethodEnd, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"))
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
            new() {Name = "this", Value = "{DebuggableConsoleApp.MyClass}", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "this", VariablesReference = 3 },
            new() {Name = "myParam", Value = "13", Type = "long", EvaluateName = "myParam" },
            new() {Name = "myIntParam", Value = "6", Type = "int", EvaluateName = "myIntParam" },
            new() {Name = "myInt", Value = "4", Type = "int", EvaluateName = "myInt" },
            new() {Name = "enumVar", Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum", EvaluateName = "enumVar", VariablesReference = 4 },
            new() {Name = "enumWithFlagsVar", Value = "FlagValue1 | FlagValue3", Type = "DebuggableConsoleApp.MyEnumWithFlags", EvaluateName = "enumWithFlagsVar", VariablesReference = 5 },
            new() {Name = "nullableInt", Value = "null", Type = "int?", EvaluateName = "nullableInt" },
            new() {Name = "structVar", Value = "{DebuggableConsoleApp.MyStruct}", Type = "DebuggableConsoleApp.MyStruct", EvaluateName = "structVar", VariablesReference = 6 },
            new() {Name = "nullableIntWithVal", Value = "4", Type = "int?", EvaluateName = "nullableIntWithVal" },
            new() {Name = "nullableRefType", Value = "null", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "nullableRefType" },
            new() {Name = "anotherVar", Value = "\"asdf\"", Type = "string", EvaluateName = "anotherVar" },
        ];

        debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out List<DapVariable>? variables);

        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt + 10", out EvaluateResponse? evaluateResponse);
        Assert.Equal("14", evaluateResponse.Result);
        Assert.Equal(0, evaluateResponse.VariablesReference);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt + myInt", out EvaluateResponse? evaluateResponse2);
        Assert.Equal("8", evaluateResponse2.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myIntParam + 4", out EvaluateResponse? evaluateResponse3);
        Assert.Equal("10", evaluateResponse3.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceField + 4", out EvaluateResponse? evaluateResponse4);
        Assert.Equal("9", evaluateResponse4.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField + 4", out EvaluateResponse? evaluateResponse5);
        Assert.Equal("10", evaluateResponse5.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField = _instanceStaticField + 4", out EvaluateResponse? evaluateResponse6);
        Assert.Equal("10", evaluateResponse6.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField", out EvaluateResponse? evaluateResponse7);
        Assert.Equal("10", evaluateResponse7.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty + 4", out EvaluateResponse? evaluateResponse8);
        Assert.Equal("14", evaluateResponse8.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntStaticProperty + 4", out EvaluateResponse? evaluateResponse9);
        Assert.Equal("14", evaluateResponse9.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "ClassProperty.IntField + 4", out EvaluateResponse? evaluateResponse10);
        Assert.Equal("10", evaluateResponse10.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.Get14() + 4", out EvaluateResponse? evaluateResponse11);
        Assert.Equal("18", evaluateResponse11.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "MyClass.IntStaticProperty + 4", out EvaluateResponse? evaluateResponse12);
        Assert.Equal("14", evaluateResponse12.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "DebuggableConsoleApp.MyClass.IntStaticProperty + 4", out EvaluateResponse? evaluateResponse13);
        Assert.Equal("14", evaluateResponse13.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty + 4", out EvaluateResponse? evaluateResponse14);
        Assert.Equal("14", evaluateResponse14.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(4)", out EvaluateResponse? evaluateResponse15);
        Assert.Equal("8", evaluateResponse15.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(4f)", out EvaluateResponse? evaluateResponse16);
        Assert.Equal("8", evaluateResponse16.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "Get14()", out EvaluateResponse? evaluateResponse17);
        Assert.Equal("14", evaluateResponse17.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "GetObject().IntProperty", out EvaluateResponse? chainedFuncEvalResponse);
        Assert.Equal("6", chainedFuncEvalResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "ReadObjectIn(GetObject())", out EvaluateResponse? referenceInTemporaryResponse);
        Assert.Equal("6", referenceInTemporaryResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "GetObjectByReference().IntProperty", out EvaluateResponse? referenceReturnResponse);
        Assert.Equal("6", referenceReturnResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classField.AddToIntField(ClearClassFieldAndCollect())", out EvaluateResponse? referenceAcrossFuncEvalResponse);
        Assert.Equal("6", referenceAcrossFuncEvalResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "WasClearedClassFieldReleased()", out EvaluateResponse? releasedEvaluationHandleResponse);
        Assert.Equal("true", releasedEvaluationHandleResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new MyClass2().IntProperty", out EvaluateResponse? chainedFuncEvalResponse2);
        Assert.Equal("6", chainedFuncEvalResponse2.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty.ToString()", out EvaluateResponse? evaluateResponse18);
        Assert.Equal("\"10\"", evaluateResponse18.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.TestMethod(4, \"asdf\")", out EvaluateResponse? evaluateResponse19);
        Assert.Equal("8", evaluateResponse19.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"Count = {IntProperty}\"", out EvaluateResponse? evaluateResponse20);
        Assert.Equal("\"Count = 10\"", evaluateResponse20.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this._classWithDebugDisplay", out EvaluateResponse? evaluateResponse21);
        Assert.Equal("IntProperty = 14", evaluateResponse21.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classWithDebugDisplay", out EvaluateResponse? evaluateResponse22);
        Assert.Equal("IntProperty = 14", evaluateResponse22.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{_instanceField}\"", out EvaluateResponse? evaluateResponse23);
        Assert.Equal("\"5\"", evaluateResponse23.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList", out EvaluateResponse? evaluateResponse24);
        Assert.Equal("Count = 4", evaluateResponse24.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"Count = {_intList.Count}\"", out EvaluateResponse? evaluateResponse25);
        Assert.Equal("\"Count = 4\"", evaluateResponse25.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classWithDebugDisplay2", out EvaluateResponse? evaluateResponse26);
        Assert.Equal("Test = stringValue1", evaluateResponse26.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classWithDebugDisplay3", out EvaluateResponse? evaluateResponse27);
        Assert.Equal("Test = stringValue2", evaluateResponse27.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt = 5", out EvaluateResponse? evaluateResponse28);
        Assert.Equal("5", evaluateResponse28.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt * 2", out EvaluateResponse? evaluateResponse29);
        Assert.Equal("10", evaluateResponse29.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar", out EvaluateResponse? evaluateResponse30);
        Assert.Equal("SecondValue", evaluateResponse30.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar = MyEnum.ThirdValue", out EvaluateResponse? evaluateResponse31);
        Assert.Equal("ThirdValue", evaluateResponse31.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "GenericTypeWithStaticField<string>.IntValue", out EvaluateResponse? evaluateResponse32);
        Assert.Equal("4", evaluateResponse32.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "GenericTypeWithStaticField<string>.IntProperty", out EvaluateResponse? evaluateResponse33);
        Assert.Equal("5", evaluateResponse33.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty = 16", out EvaluateResponse? evaluateResponse34);
        Assert.Equal("16", evaluateResponse34.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty", out EvaluateResponse? evaluateResponse35);
        Assert.Equal("16", evaluateResponse35.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.MyStaticMethod()", out EvaluateResponse? evaluateResponse36);
        Assert.Equal("Count = 1", evaluateResponse36.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty = 17", out EvaluateResponse? evaluateResponse37);
        Assert.Equal("17", evaluateResponse37.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty", out EvaluateResponse? evaluateResponse38);
        Assert.Equal("17", evaluateResponse38.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "ClassProperty", out EvaluateResponse? evaluateResponse39);
        Assert.Equal("{DebuggableConsoleApp.MyClass2}", evaluateResponse39.Result);
        Assert.True(evaluateResponse39.VariablesReference > 0);
        debugProtocolHost.WithVariablesRequest(evaluateResponse39.VariablesReference, out List<DapVariable>? variablesResponse39);
        Assert.Equal(3, variablesResponse39.Count);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.ExtensionMethod()", out EvaluateResponse? evaluateResponse40);
        Assert.Equal("5", evaluateResponse40.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt == 5 && myInt < 10", out EvaluateResponse? evaluateResponse41);
        Assert.Equal("true", evaluateResponse41.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt == 4 && myInt < 10", out EvaluateResponse? evaluateResponse42);
        Assert.Equal("false", evaluateResponse42.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt == 4 || myInt < 10", out EvaluateResponse? evaluateResponse43);
        Assert.Equal("true", evaluateResponse43.Result);
        // Short-circuit: the RHS would throw a NullReferenceException if it were evaluated
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt == 4 && nullableRefType.GetHashCode() > 0", out EvaluateResponse? evaluateResponse44);
        Assert.Equal("false", evaluateResponse44.Result);

        // Conversions must produce correctly-typed runtime values
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar = (MyEnum)2", out EvaluateResponse? evaluateResponse45);
        Assert.Equal("ThirdValue", evaluateResponse45.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myParam = 5", out EvaluateResponse? evaluateResponse46);
        Assert.Equal("5", evaluateResponse46.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)nullableIntWithVal + 1", out EvaluateResponse? evaluateResponse47);
        Assert.Equal("5", evaluateResponse47.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)5.5", out EvaluateResponse? evaluateResponse48);
        Assert.Equal("5", evaluateResponse48.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)enumVar", out EvaluateResponse? evaluateResponse49);
        Assert.Equal("2", evaluateResponse49.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableInt ?? 5", out EvaluateResponse? evaluateResponse50);
        Assert.Equal("5", evaluateResponse50.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableIntWithVal ?? 10", out EvaluateResponse? evaluateResponse51);
        Assert.Equal("4", evaluateResponse51.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(myInt > 3) == true", out EvaluateResponse? evaluateResponse52);
        Assert.Equal("true", evaluateResponse52.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(myInt > 5) == true", out EvaluateResponse? evaluateResponse53);
        Assert.Equal("false", evaluateResponse53.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt += 2", out EvaluateResponse? evaluateResponse54);
        Assert.Equal("7", evaluateResponse54.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "GenericTypeWithStaticField<int[]>.IntValue", out EvaluateResponse? evaluateResponse55);
        Assert.Equal("4", evaluateResponse55.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "GenericTypeWithStaticField<DateTime>.IntValue", out EvaluateResponse? corelibValueTypeGenericResponse);
        Assert.Equal("4", corelibValueTypeGenericResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "Array.Empty<DateTime>().Length", out EvaluateResponse? corelibValueTypeGenericMethodResponse);
        Assert.Equal("0", corelibValueTypeGenericMethodResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new int[2].Length", out EvaluateResponse? intArrayResponse);
        Assert.Equal("2", intArrayResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new MyClass[2].Length", out EvaluateResponse? classArrayResponse);
        Assert.Equal("2", classArrayResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new MyClass[0].Length", out EvaluateResponse? classZeroLengthArrayResponse);
        Assert.Equal("0", classZeroLengthArrayResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new DateTime[0].Length", out EvaluateResponse? corelibValueTypeZeroLengthArrayResponse);
        Assert.Equal("0", corelibValueTypeZeroLengthArrayResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new DateTime[2].Length", out EvaluateResponse? corelibValueTypeArrayResponse);
        Assert.Equal("2", corelibValueTypeArrayResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(int)", out EvaluateResponse? evaluateResponse56);
        Assert.Equal("0", evaluateResponse56.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(string)", out EvaluateResponse? defaultStringResponse);
        Assert.Equal("null", defaultStringResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(MyClass)", out EvaluateResponse? defaultRefTypeResponse);
        Assert.Equal("null", defaultRefTypeResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(int[])", out EvaluateResponse? defaultArrayResponse);
        Assert.Equal("null", defaultArrayResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(MyClass).ToString()", out EvaluateResponse? evaluateResponse57);
        Assert.Equal("\"DebuggableConsoleApp.MyClass\"", evaluateResponse57.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "IncrementByRef(ref myInt)", out EvaluateResponse? evaluateResponse58);
        Assert.Equal("10", evaluateResponse58.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt", out EvaluateResponse? evaluateResponse59);
        Assert.Equal("10", evaluateResponse59.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "AssignOut(out myInt)", out EvaluateResponse? evaluateResponse60);
        Assert.Equal("42", evaluateResponse60.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt", out EvaluateResponse? evaluateResponse61);
        Assert.Equal("42", evaluateResponse61.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "AddOneIn(in myInt)", out EvaluateResponse? evaluateResponse62);
        Assert.Equal("43", evaluateResponse62.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_doubler(4)", out EvaluateResponse? evaluateResponse63);
        Assert.Equal("8", evaluateResponse63.Result);

        // Operators resolved from Roslyn's bound tree: value-type operands, one declared on the right operand's type.
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(structVar + structVar).Id", out EvaluateResponse? evaluateResponse64);
        Assert.Equal("10", evaluateResponse64.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(5 + structVar).Id", out EvaluateResponse? evaluateResponse65);
        Assert.Equal("10", evaluateResponse65.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(-structVar).Id", out EvaluateResponse? evaluateResponse66);
        Assert.Equal("-5", evaluateResponse66.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar == structVar", out EvaluateResponse? evaluateResponse67);
        Assert.Equal("true", evaluateResponse67.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(myInt)", out EvaluateResponse? evaluateResponse68);
        Assert.Equal("84", evaluateResponse68.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList.Where(s => s == 4).ToList()", out EvaluateResponse? evaluateResponse69);
        Assert.Equal("Count = 1", evaluateResponse69.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList.Where(s => s == myInt).ToList()", out EvaluateResponse? evaluateResponse70);
        Assert.Equal("Count = 0", evaluateResponse70.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList.Where(s => s == myInt).Select(s => s).ToList()", out EvaluateResponse? evaluateResponse71);
        Assert.Equal("Count = 0", evaluateResponse71.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "((Func<int, int>)(value => value + 1))(4)", out EvaluateResponse? inlineLambdaInvocationResponse);
        Assert.Equal("5", inlineLambdaInvocationResponse.Result);

        // Lambdas retain the stopped frame's lexical context, including this and non-public members and types.
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList.Where(value => value == _instanceField).ToList()", out EvaluateResponse? instanceCaptureLambdaResponse);
        Assert.Equal("Count = 0", instanceCaptureLambdaResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new[] { this }.Where(value => value == this).Count()", out EvaluateResponse? thisCaptureLambdaResponse);
        Assert.Equal("1", thisCaptureLambdaResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList.Select(value => DoubleNumber(value)).Last()", out EvaluateResponse? privateMethodLambdaResponse);
        Assert.Equal("50", privateMethodLambdaResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new[] { 1 }.Select(value => typeof(EvaluationLifetimeTracker).Name).Single()", out EvaluateResponse? internalTypeLambdaResponse);
        Assert.Equal("\"EvaluationLifetimeTracker\"", internalTypeLambdaResponse.Result);

        // Nested lambdas and method groups retain their generated targets and methods.
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new[] { 1, 2 }.Where(value => new[] { value }.Any(inner => inner == value)).Count()", out EvaluateResponse? nestedLambdaResponse);
        Assert.Equal("2", nestedLambdaResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new[] { -1 }.Where(value => value < 0).Select(Math.Abs).Single()", out EvaluateResponse? lambdaAndMethodGroupResponse);
        Assert.Equal("1", lambdaAndMethodGroupResponse.Result);

        // All delegates in one expression share the same closure, including captures that are only written.
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList.Select(value => myInt = value).Last()", out EvaluateResponse? writeOnlyCaptureLambdaResponse);
        Assert.Equal("25", writeOnlyCaptureLambdaResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt", out EvaluateResponse? writeOnlyCaptureValueResponse);
        Assert.Equal("25", writeOnlyCaptureValueResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt = 42", out EvaluateResponse? resetMyIntBeforeSharedClosureResponse);
        Assert.Equal("42", resetMyIntBeforeSharedClosureResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList.Select(value => myInt++).Select(value => myInt).ToList().Last()", out EvaluateResponse? sharedClosureLambdaResponse);
        Assert.Equal("46", sharedClosureLambdaResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt", out EvaluateResponse? sharedClosureValueResponse);
        Assert.Equal("46", sharedClosureValueResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt = 42", out EvaluateResponse? resetMyIntBeforeSameExpressionResponse);
        Assert.Equal("42", resetMyIntBeforeSameExpressionResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList.Select(value => myInt++).Last() + myInt", out EvaluateResponse? sameExpressionCaptureResponse);
        Assert.Equal("85", sameExpressionCaptureResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt", out EvaluateResponse? sameExpressionCaptureValueResponse);
        Assert.Equal("43", sameExpressionCaptureValueResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt = 42", out EvaluateResponse? restoreMyIntAfterLambdaResponse);
        Assert.Equal("42", restoreMyIntAfterLambdaResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType == null", out EvaluateResponse? nullEqualityResponse);
        Assert.Equal("true", nullEqualityResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this == this", out EvaluateResponse? referenceEqualityResponse);
        Assert.Equal("true", referenceEqualityResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar == \"asdf\"", out EvaluateResponse? stringEqualityResponse);
        Assert.Equal("true", stringEqualityResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableIntWithVal.HasValue == true", out EvaluateResponse? booleanEqualityResponse);
        Assert.Equal("true", booleanEqualityResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar == MyEnum.ThirdValue", out EvaluateResponse? equalEnumResponse);
        Assert.Equal("true", equalEnumResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar == MyEnum.SecondValue", out EvaluateResponse? unequalEnumResponse);
        Assert.Equal("false", unequalEnumResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "Convert.ToByte(myInt) == myInt", out EvaluateResponse? smallIntegralEqualityResponse);
        Assert.Equal("true", smallIntegralEqualityResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int?)myInt", out EvaluateResponse? nullableConversionResponse);
        Assert.Equal("42", nullableConversionResponse.Result);
        Assert.Equal("int?", nullableConversionResponse.Type);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(MyEnum)(myInt - 40)", out EvaluateResponse? enumConversionResponse);
        Assert.Equal("ThirdValue", enumConversionResponse.Result);
        Assert.Equal("DebuggableConsoleApp.MyEnum", enumConversionResponse.Type);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumWithFlagsVar | MyEnumWithFlags.FlagValue2", out EvaluateResponse? enumOperatorResponse);
        Assert.Equal("FlagValue1 | FlagValue2 | FlagValue3", enumOperatorResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)this.DoubleNumber(4f)", out EvaluateResponse? runtimeFloatToIntResponse);
        Assert.Equal("8", runtimeFloatToIntResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(double)this.DoubleNumber(4f)", out EvaluateResponse? runtimeFloatToDoubleResponse);
        Assert.Equal("8", runtimeFloatToDoubleResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(this.DoubleNumber(4f) + 1f)", out EvaluateResponse? computedFloatArgumentResponse);
        Assert.Equal("18", computedFloatArgumentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "unchecked((ulong)(uint)(myInt - 43))", out EvaluateResponse? unsignedWideningResponse);
        Assert.Equal("4294967295", unsignedWideningResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "this as object as string", out EvaluateResponse? incompatibleAsResponse);
        Assert.Equal("null", incompatibleAsResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "((string)(object)this).Length", out EvaluateResponse? incompatibleCastResponse);
        Assert.Contains("InvalidCastException", incompatibleCastResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(object)myInt != null", out EvaluateResponse? boxingResponse);
        Assert.Equal("true", boxingResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)(object)myInt", out EvaluateResponse? unboxingResponse);
        Assert.Equal("42", unboxingResponse.Result);
        // unbox.any must reject a box whose runtime type does not match the target type
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(long)(object)myInt", out EvaluateResponse? incompatibleUnboxResponse);
        Assert.Contains("InvalidCastException", incompatibleUnboxResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)(object)myParam", out EvaluateResponse? incompatibleUnboxResponse2);
        Assert.Contains("InvalidCastException", incompatibleUnboxResponse2.Result);
        // reference type -> object is an identity conversion (no box), and must return the reference unchanged
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(object)anotherVar", out EvaluateResponse? boxReferenceResponse);
        Assert.Equal("\"asdf\"", boxReferenceResponse.Result);
        // box/unbox round-trip of an exact enum type
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(MyEnum)(object)enumVar", out EvaluateResponse? enumBoxResponse);
        Assert.Equal("ThirdValue", enumBoxResponse.Result);
        // This is technically not correct behaviour, ie doesn't match behaviour of actual code, but no other debugger handles it. Assert it.
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(MyEnum)(object)2", out EvaluateResponse? enumBoxResponse2);
        Assert.Contains("InvalidCastException", enumBoxResponse2.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "Array.Empty<int>().Length", out EvaluateResponse? genericMethodResponse);
        Assert.Equal("0", genericMethodResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "int.Parse(anotherVar)", out EvaluateResponse? throwingMethodResponse);
        Assert.Contains("FormatException", throwingMethodResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "checked(myInt + int.MaxValue)", out EvaluateResponse? checkedArithmeticResponse);
        Assert.Contains("OverflowException", checkedArithmeticResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt << (myInt - 10)", out EvaluateResponse? intShiftResponse);
        Assert.Equal("42", intShiftResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "-1 >>> (myInt - 41)", out EvaluateResponse? unsignedShiftResponse);
        Assert.Equal("2147483647", unsignedShiftResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "ulong.MaxValue / (ulong)(myInt - 40)", out EvaluateResponse? ulongDivisionResponse);
        Assert.Equal("9223372036854775807", ulongDivisionResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "0.0 / (myInt - myInt) == double.NaN", out EvaluateResponse? nanEqualityResponse);
        Assert.Equal("false", nanEqualityResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "TestMethod(myString: \"asdf\", myInt: 4)", out EvaluateResponse? namedArgumentsResponse);
        Assert.Equal("8", namedArgumentsResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intArray[0]", out EvaluateResponse? arrayIndexResponse);
        Assert.Equal("2", arrayIndexResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList[0]", out EvaluateResponse? indexerResponse);
        Assert.Equal("1", indexerResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType?.GetHashCode()", out EvaluateResponse? conditionalAccessResponse);
        Assert.Equal("null", conditionalAccessResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{myInt:D5}\"", out EvaluateResponse? interpolationFormatResponse);
        Assert.Equal("\"00042\"", interpolationFormatResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{myInt,5}\"", out EvaluateResponse? interpolationAlignmentResponse);
        Assert.Equal("\"   42\"", interpolationAlignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"x{nullableRefType}y\"", out EvaluateResponse? nullInterpolationResponse);
        Assert.Equal("\"xy\"", nullInterpolationResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{ClassProperty}\"", out EvaluateResponse? objectInterpolationResponse);
        Assert.Equal("\"DebuggableConsoleApp.MyClass2\"", objectInterpolationResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{enumVar}\"", out EvaluateResponse? enumInterpolationResponse);
        Assert.Equal("\"ThirdValue\"", enumInterpolationResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{structVar}\"", out EvaluateResponse? structInterpolationResponse);
        Assert.Equal("\"DebuggableConsoleApp.MyStruct\"", structInterpolationResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar + nullableRefType", out EvaluateResponse? nullConcatenationResponse);
        Assert.Equal("\"asdf\"", nullConcatenationResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out EvaluateResponse? structFieldReadResponse);
        Assert.Equal("5", structFieldReadResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id = 6", out EvaluateResponse? structFieldWriteResponse);
        Assert.Equal("6", structFieldWriteResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out EvaluateResponse? structFieldReadAfterWriteResponse);
        Assert.Equal("6", structFieldReadAfterWriteResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar = default(MyStruct)", out EvaluateResponse? structAssignmentResponse);
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out EvaluateResponse? structFieldReadAfterAssignmentResponse);
        Assert.Equal("0", structFieldReadAfterAssignmentResponse.Result);

        // stobj: assigning a struct value to a field (stfld)
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id = 5", out EvaluateResponse? structResetIdResponse);
        Assert.Equal("5", structResetIdResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_structField = structVar", out EvaluateResponse? structToFieldAssignmentResponse);
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structToFieldAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_structField.Id", out EvaluateResponse? structFieldValueReadBackResponse);
        Assert.Equal("5", structFieldValueReadBackResponse.Result);

        // stobj: struct value copied out of a field back into a local
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar = _structField", out EvaluateResponse? structFromFieldAssignmentResponse);
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structFromFieldAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out EvaluateResponse? structLocalValueAfterFieldCopyResponse);
        Assert.Equal("5", structLocalValueAfterFieldCopyResponse.Result);

        // ldobj: struct value loaded through a ref-return (call + ldobj + stloc)
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar = GetStructByRef()", out EvaluateResponse? structViaRefReturnResponse);
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structViaRefReturnResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out EvaluateResponse? structIdViaRefReturnResponse);
        Assert.Equal("5", structIdViaRefReturnResponse.Result);

        // stobj: struct value stored through a ref-return (call + ldloc + stobj)
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "GetStructByRef() = structVar", out EvaluateResponse? structStoreViaRefReturnResponse);
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structStoreViaRefReturnResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "GetStructByRef().Id", out EvaluateResponse? structFieldIdAfterRefStoreResponse);
        Assert.Equal("5", structFieldIdAfterRefStoreResponse.Result);

        // unboxing conversion: cast-to-struct used as the receiver of an instance method call
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "((MyStruct)(object)structVar).GetId()", out EvaluateResponse? unboxReceiverResponse);
        Assert.Equal("5", unboxReceiverResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(MyStruct).Id", out EvaluateResponse? structDefaultResponse);
        Assert.Equal("0", structDefaultResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "sizeof(int)", out EvaluateResponse? sizeOfResponse);
        Assert.Equal("4", sizeOfResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(int[]).ToString()", out EvaluateResponse? arrayTypeOfResponse);
        Assert.Equal("\"System.Int32[]\"", arrayTypeOfResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(System.Collections.Generic.List<int>).ToString()", out EvaluateResponse? genericTypeOfResponse);
        Assert.Equal("\"System.Collections.Generic.List`1[System.Int32]\"", genericTypeOfResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(MyClassContainingAnotherClass.MyNestedClass).ToString()", out EvaluateResponse? nestedTypeOfResponse);
        Assert.Equal("\"DebuggableConsoleApp.MyClassContainingAnotherClass+MyNestedClass\"", nestedTypeOfResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "TryGetObject(out var objectResult) ? objectResult.IntProperty : 0", out EvaluateResponse? outVarResponse);
        Assert.Equal("6", outVarResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "new OverloadResolutionGeneric<int>().Pick(\"hello\")", out EvaluateResponse? genericTypeOverloadResponse);
        Assert.Equal("5", genericTypeOverloadResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt = 42", out EvaluateResponse? myIntResetResponse);
        Assert.Equal("42", myIntResetResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "OverloadResolutionByRef.Pick(ref myInt)", out EvaluateResponse? byRefOverloadResponse);
        Assert.Equal("42", byRefOverloadResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar = \"updated\"", out EvaluateResponse? referenceStringAssignmentResponse);
        Assert.Equal("\"updated\"", referenceStringAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar", out EvaluateResponse? referenceStringReadBackResponse);
        Assert.Equal("\"updated\"", referenceStringReadBackResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar == \"updated\"", out EvaluateResponse? referenceStringEqualityResponse);
        Assert.Equal("true", referenceStringEqualityResponse.Result);

        // A string read from the debuggee keeps its reference, so identity comparisons and assignments alias.
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "IsSameString(anotherVar, anotherVar)", out EvaluateResponse? stringIdentityResponse);
        Assert.Equal("true", stringIdentityResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "object.ReferenceEquals(anotherVar, anotherVar)", out EvaluateResponse? stringReferenceEqualsResponse);
        Assert.Equal("true", stringReferenceEqualsResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar = _name", out EvaluateResponse? stringAliasAssignmentResponse);
        Assert.Equal("\"TestName\"", stringAliasAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "IsSameString(anotherVar, _name)", out EvaluateResponse? stringAliasIdentityResponse);
        Assert.Equal("true", stringAliasIdentityResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType = new MyClass()", out EvaluateResponse? referenceObjectAssignmentResponse);
        Assert.Equal("{DebuggableConsoleApp.MyClass}", referenceObjectAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType == null", out EvaluateResponse? referenceObjectNotNullResponse);
        Assert.Equal("false", referenceObjectNotNullResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType = null", out EvaluateResponse? referenceNullAssignmentResponse);
        Assert.Equal("null", referenceNullAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType == null", out EvaluateResponse? referenceNullReadBackResponse);
        Assert.Equal("true", referenceNullReadBackResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classField = null", out EvaluateResponse? referenceFieldNullAssignmentResponse);
        Assert.Equal("null", referenceFieldNullAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classField == null", out EvaluateResponse? referenceFieldNullReadBackResponse);
        Assert.Equal("true", referenceFieldNullReadBackResponse.Result);

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(new string[1])[0] = \"array-string\"", out EvaluateResponse? arrayStringAssignmentResponse);
        Assert.Equal("\"array-string\"", arrayStringAssignmentResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "(new MyClass2[1])[0] = null", out EvaluateResponse? arrayNullAssignmentResponse);
        Assert.Equal("null", arrayNullAssignmentResponse.Result);
    }

    /// <summary>A name a lambda's parameter shadows evaluates to the parameter where the stop is, not the method's local.</summary>
    [Fact]
    public async Task An_evaluation_of_a_name_a_lambda_shadows_reads_the_lambdas_own_parameter()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest(Debuggees.Line("DebuggableConsoleApp", "Lambdas/MyLambdaClass.cs", "return value + outerLocal;"),
                Debuggees.Source("DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse)
            .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out ScopesResponse? scopesResponse);

        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;

        // 'value' must resolve to the innermost lambda's parameter (200), not the hoisted method-local 'value' (100)
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "value", out EvaluateResponse? evaluateResponse);
        Assert.Equal("200", evaluateResponse.Result);
    }

    /// <summary>Inside a generic method its type parameter is the type it was called with: default(T), typeof(T) and its
    /// argument evaluate as that type's.</summary>
    [Fact]
    public async Task An_evaluation_inside_a_generic_method_knows_its_type_argument()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest(Debuggees.Line("DebuggableConsoleApp", "MyClassWithGenericMethod.cs", "return arg;"),
                Debuggees.Source("DebuggableConsoleApp", "MyClassWithGenericMethod.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse)
            .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out ScopesResponse? scopesResponse);

        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(T)", out EvaluateResponse? evaluateResponse);
        Assert.Equal("0", evaluateResponse.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "arg", out EvaluateResponse? evaluateResponse3);
        Assert.Equal("4", evaluateResponse3.Result);
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(T)", out EvaluateResponse? evaluateResponse4);
        Assert.Equal("System.Int32", evaluateResponse4.Result);
    }

    [Fact]
    public async Task A_LINQ_watch_evaluates_in_a_program_that_has_called_no_LINQ_method_yet()
    {
        // At the entry of a program that has loaded no System.Linq, which the expression loads.
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("TopLevelStatementsConsoleApp"), stopAtEntry: true, justMyCode: true)
            .WaitForInitializedEvent();
        host.WithConfigurationDoneRequest();
        StoppedEvent stoppedEvent = await host.WaitForStoppedEvent();
        DapStackFrame topFrame = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value);

        host.WithEvaluateRequest(topFrame.Id, "new[] { test, 7 }.Where(x => x > 3).Count()", out EvaluateResponse? linq);
        Assert.Equal("1", linq.Result);
        host.WithEvaluateRequest(topFrame.Id, "System.Linq.Enumerable.Range(1, 3).Sum()", out EvaluateResponse? named);
        Assert.Equal("6", named.Result);
    }

    /// <summary>A stop in a static class's method reads the class's static field.</summary>
    [Fact]
    public async Task An_evaluation_in_a_static_classs_method_reads_its_field()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest([Debuggees.Line("DebuggableConsoleApp", "MyStaticClass.cs", "int test = 4;")], Debuggees.Source("DebuggableConsoleApp", "MyStaticClass.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse)
            .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out ScopesResponse? scopesResponse);

        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;

        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_testInt", out EvaluateResponse? evaluateResponse);
        Assert.Equal("4", evaluateResponse.Result);
    }

    /// <summary>An evaluation calls the program's own methods that take and return a type nested in another of its types,
    /// each found as the program declares it.</summary>
    [Fact]
    public async Task An_evaluation_calls_a_method_that_takes_a_nested_type()
    {
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, true, out Process program);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(program.Id)
            .WaitForInitializedEvent();
        host
            .WithBreakpointsRequest(Debuggees.MyMethodEnd, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(program.Id, true);

        StoppedEvent stopped = await host.WaitForStoppedEvent();
        host.WithStackTraceRequest(stopped.ThreadId!.Value, out StackTraceResponse stack)
            .WithEvaluateRequest(stack.StackFrames[0].Id, "NestedTypeCalls.ValueOf(NestedTypeCalls.Nested(5))", out EvaluateResponse called);
        Assert.True(called.Result == "5", $"A call taking MyClassContainingAnotherClass.MyNestedClass evaluated to {called.Result}, not the 5 it was given");
    }
}
