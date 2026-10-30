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
    [Gate]
    [Fact]
    public async Task At_a_stop_the_locals_are_listed_as_they_are_and_an_evaluation_reads_assigns_and_calls_what_the_method_sees()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        await debugProtocolHost
            .BreakpointsAsync(Debuggees.MyMethodEnd, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"))
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        ScopesResponse? scopesResponse = await debugProtocolHost.ScopesAsync(stackTraceResponse.StackFrames!.First().Id);

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

        List<DapVariable>? variables = await debugProtocolHost.VariablesAsync(scope.VariablesReference);

        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt + 10");
        Assert.Equal("14", evaluateResponse.Result);
        Assert.Equal(0, evaluateResponse.VariablesReference);
        EvaluateResponse? evaluateResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt + myInt");
        Assert.Equal("8", evaluateResponse2.Result);
        EvaluateResponse? evaluateResponse3 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myIntParam + 4");
        Assert.Equal("10", evaluateResponse3.Result);
        EvaluateResponse? evaluateResponse4 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_instanceField + 4");
        Assert.Equal("9", evaluateResponse4.Result);
        EvaluateResponse? evaluateResponse5 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_instanceStaticField + 4");
        Assert.Equal("10", evaluateResponse5.Result);
        EvaluateResponse? evaluateResponse6 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_instanceStaticField = _instanceStaticField + 4");
        Assert.Equal("10", evaluateResponse6.Result);
        EvaluateResponse? evaluateResponse7 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_instanceStaticField");
        Assert.Equal("10", evaluateResponse7.Result);
        EvaluateResponse? evaluateResponse8 = await debugProtocolHost.EvaluateAsync(stackFrameId, "IntProperty + 4");
        Assert.Equal("14", evaluateResponse8.Result);
        EvaluateResponse? evaluateResponse9 = await debugProtocolHost.EvaluateAsync(stackFrameId, "IntStaticProperty + 4");
        Assert.Equal("14", evaluateResponse9.Result);
        EvaluateResponse? evaluateResponse10 = await debugProtocolHost.EvaluateAsync(stackFrameId, "ClassProperty.IntField + 4");
        Assert.Equal("10", evaluateResponse10.Result);
        EvaluateResponse? evaluateResponse11 = await debugProtocolHost.EvaluateAsync(stackFrameId, "this.Get14() + 4");
        Assert.Equal("18", evaluateResponse11.Result);
        EvaluateResponse? evaluateResponse12 = await debugProtocolHost.EvaluateAsync(stackFrameId, "MyClass.IntStaticProperty + 4");
        Assert.Equal("14", evaluateResponse12.Result);
        EvaluateResponse? evaluateResponse13 = await debugProtocolHost.EvaluateAsync(stackFrameId, "DebuggableConsoleApp.MyClass.IntStaticProperty + 4");
        Assert.Equal("14", evaluateResponse13.Result);
        EvaluateResponse? evaluateResponse14 = await debugProtocolHost.EvaluateAsync(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty + 4");
        Assert.Equal("14", evaluateResponse14.Result);
        EvaluateResponse? evaluateResponse15 = await debugProtocolHost.EvaluateAsync(stackFrameId, "this.DoubleNumber(4)");
        Assert.Equal("8", evaluateResponse15.Result);
        EvaluateResponse? evaluateResponse16 = await debugProtocolHost.EvaluateAsync(stackFrameId, "this.DoubleNumber(4f)");
        Assert.Equal("8", evaluateResponse16.Result);
        EvaluateResponse? evaluateResponse17 = await debugProtocolHost.EvaluateAsync(stackFrameId, "Get14()");
        Assert.Equal("14", evaluateResponse17.Result);
        EvaluateResponse? chainedFuncEvalResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "GetObject().IntProperty");
        Assert.Equal("6", chainedFuncEvalResponse.Result);
        EvaluateResponse? referenceInTemporaryResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "ReadObjectIn(GetObject())");
        Assert.Equal("6", referenceInTemporaryResponse.Result);
        EvaluateResponse? referenceReturnResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "GetObjectByReference().IntProperty");
        Assert.Equal("6", referenceReturnResponse.Result);
        EvaluateResponse? referenceAcrossFuncEvalResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_classField.AddToIntField(ClearClassFieldAndCollect())");
        Assert.Equal("6", referenceAcrossFuncEvalResponse.Result);
        EvaluateResponse? releasedEvaluationHandleResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "WasClearedClassFieldReleased()");
        Assert.Equal("true", releasedEvaluationHandleResponse.Result);
        EvaluateResponse? chainedFuncEvalResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId, "new MyClass2().IntProperty");
        Assert.Equal("6", chainedFuncEvalResponse2.Result);
        EvaluateResponse? evaluateResponse18 = await debugProtocolHost.EvaluateAsync(stackFrameId, "IntProperty.ToString()");
        Assert.Equal("\"10\"", evaluateResponse18.Result);
        EvaluateResponse? evaluateResponse19 = await debugProtocolHost.EvaluateAsync(stackFrameId, "this.TestMethod(4, \"asdf\")");
        Assert.Equal("8", evaluateResponse19.Result);
        EvaluateResponse? evaluateResponse20 = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"Count = {IntProperty}\"");
        Assert.Equal("\"Count = 10\"", evaluateResponse20.Result);
        EvaluateResponse? evaluateResponse21 = await debugProtocolHost.EvaluateAsync(stackFrameId, "this._classWithDebugDisplay");
        Assert.Equal("IntProperty = 14", evaluateResponse21.Result);
        EvaluateResponse? evaluateResponse22 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_classWithDebugDisplay");
        Assert.Equal("IntProperty = 14", evaluateResponse22.Result);
        EvaluateResponse? evaluateResponse23 = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"{_instanceField}\"");
        Assert.Equal("\"5\"", evaluateResponse23.Result);
        EvaluateResponse? evaluateResponse24 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList");
        Assert.Equal("Count = 4", evaluateResponse24.Result);
        EvaluateResponse? evaluateResponse25 = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"Count = {_intList.Count}\"");
        Assert.Equal("\"Count = 4\"", evaluateResponse25.Result);
        EvaluateResponse? evaluateResponse26 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_classWithDebugDisplay2");
        Assert.Equal("Test = stringValue1", evaluateResponse26.Result);
        EvaluateResponse? evaluateResponse27 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_classWithDebugDisplay3");
        Assert.Equal("Test = stringValue2", evaluateResponse27.Result);
        EvaluateResponse? evaluateResponse28 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt = 5");
        Assert.Equal("5", evaluateResponse28.Result);
        EvaluateResponse? evaluateResponse29 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt * 2");
        Assert.Equal("10", evaluateResponse29.Result);
        EvaluateResponse? evaluateResponse30 = await debugProtocolHost.EvaluateAsync(stackFrameId, "enumVar");
        Assert.Equal("SecondValue", evaluateResponse30.Result);
        EvaluateResponse? evaluateResponse31 = await debugProtocolHost.EvaluateAsync(stackFrameId, "enumVar = MyEnum.ThirdValue");
        Assert.Equal("ThirdValue", evaluateResponse31.Result);
        EvaluateResponse? evaluateResponse32 = await debugProtocolHost.EvaluateAsync(stackFrameId, "GenericTypeWithStaticField<string>.IntValue");
        Assert.Equal("4", evaluateResponse32.Result);
        EvaluateResponse? evaluateResponse33 = await debugProtocolHost.EvaluateAsync(stackFrameId, "GenericTypeWithStaticField<string>.IntProperty");
        Assert.Equal("5", evaluateResponse33.Result);
        EvaluateResponse? evaluateResponse34 = await debugProtocolHost.EvaluateAsync(stackFrameId, "IntProperty = 16");
        Assert.Equal("16", evaluateResponse34.Result);
        EvaluateResponse? evaluateResponse35 = await debugProtocolHost.EvaluateAsync(stackFrameId, "IntProperty");
        Assert.Equal("16", evaluateResponse35.Result);
        EvaluateResponse? evaluateResponse36 = await debugProtocolHost.EvaluateAsync(stackFrameId, "Namespace1.AnotherClass.MyStaticMethod()");
        Assert.Equal("Count = 1", evaluateResponse36.Result);
        EvaluateResponse? evaluateResponse37 = await debugProtocolHost.EvaluateAsync(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty = 17");
        Assert.Equal("17", evaluateResponse37.Result);
        EvaluateResponse? evaluateResponse38 = await debugProtocolHost.EvaluateAsync(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty");
        Assert.Equal("17", evaluateResponse38.Result);
        EvaluateResponse? evaluateResponse39 = await debugProtocolHost.EvaluateAsync(stackFrameId, "ClassProperty");
        Assert.Equal("{DebuggableConsoleApp.MyClass2}", evaluateResponse39.Result);
        Assert.True(evaluateResponse39.VariablesReference > 0);
        List<DapVariable>? variablesResponse39 = await debugProtocolHost.VariablesAsync(evaluateResponse39.VariablesReference);
        Assert.Equal(3, variablesResponse39.Count);
        EvaluateResponse? evaluateResponse40 = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar.ExtensionMethod()");
        Assert.Equal("5", evaluateResponse40.Result);
        EvaluateResponse? evaluateResponse41 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt == 5 && myInt < 10");
        Assert.Equal("true", evaluateResponse41.Result);
        EvaluateResponse? evaluateResponse42 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt == 4 && myInt < 10");
        Assert.Equal("false", evaluateResponse42.Result);
        EvaluateResponse? evaluateResponse43 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt == 4 || myInt < 10");
        Assert.Equal("true", evaluateResponse43.Result);
        // Short-circuit: the RHS would throw a NullReferenceException if it were evaluated
        EvaluateResponse? evaluateResponse44 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt == 4 && nullableRefType.GetHashCode() > 0");
        Assert.Equal("false", evaluateResponse44.Result);

        // Conversions must produce correctly-typed runtime values
        EvaluateResponse? evaluateResponse45 = await debugProtocolHost.EvaluateAsync(stackFrameId, "enumVar = (MyEnum)2");
        Assert.Equal("ThirdValue", evaluateResponse45.Result);
        EvaluateResponse? evaluateResponse46 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myParam = 5");
        Assert.Equal("5", evaluateResponse46.Result);
        EvaluateResponse? evaluateResponse47 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(int)nullableIntWithVal + 1");
        Assert.Equal("5", evaluateResponse47.Result);
        EvaluateResponse? evaluateResponse48 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(int)5.5");
        Assert.Equal("5", evaluateResponse48.Result);
        EvaluateResponse? evaluateResponse49 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(int)enumVar");
        Assert.Equal("2", evaluateResponse49.Result);
        EvaluateResponse? evaluateResponse50 = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableInt ?? 5");
        Assert.Equal("5", evaluateResponse50.Result);
        EvaluateResponse? evaluateResponse51 = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableIntWithVal ?? 10");
        Assert.Equal("4", evaluateResponse51.Result);
        EvaluateResponse? evaluateResponse52 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(myInt > 3) == true");
        Assert.Equal("true", evaluateResponse52.Result);
        EvaluateResponse? evaluateResponse53 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(myInt > 5) == true");
        Assert.Equal("false", evaluateResponse53.Result);
        EvaluateResponse? evaluateResponse54 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt += 2");
        Assert.Equal("7", evaluateResponse54.Result);
        EvaluateResponse? evaluateResponse55 = await debugProtocolHost.EvaluateAsync(stackFrameId, "GenericTypeWithStaticField<int[]>.IntValue");
        Assert.Equal("4", evaluateResponse55.Result);
        EvaluateResponse? corelibValueTypeGenericResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "GenericTypeWithStaticField<DateTime>.IntValue");
        Assert.Equal("4", corelibValueTypeGenericResponse.Result);
        EvaluateResponse? corelibValueTypeGenericMethodResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "Array.Empty<DateTime>().Length");
        Assert.Equal("0", corelibValueTypeGenericMethodResponse.Result);
        EvaluateResponse? intArrayResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new int[2].Length");
        Assert.Equal("2", intArrayResponse.Result);
        EvaluateResponse? classArrayResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new MyClass[2].Length");
        Assert.Equal("2", classArrayResponse.Result);
        EvaluateResponse? classZeroLengthArrayResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new MyClass[0].Length");
        Assert.Equal("0", classZeroLengthArrayResponse.Result);
        EvaluateResponse? corelibValueTypeZeroLengthArrayResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new DateTime[0].Length");
        Assert.Equal("0", corelibValueTypeZeroLengthArrayResponse.Result);
        EvaluateResponse? corelibValueTypeArrayResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new DateTime[2].Length");
        Assert.Equal("2", corelibValueTypeArrayResponse.Result);
        EvaluateResponse? evaluateResponse56 = await debugProtocolHost.EvaluateAsync(stackFrameId, "default(int)");
        Assert.Equal("0", evaluateResponse56.Result);
        EvaluateResponse? defaultStringResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "default(string)");
        Assert.Equal("null", defaultStringResponse.Result);
        EvaluateResponse? defaultRefTypeResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "default(MyClass)");
        Assert.Equal("null", defaultRefTypeResponse.Result);
        EvaluateResponse? defaultArrayResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "default(int[])");
        Assert.Equal("null", defaultArrayResponse.Result);
        EvaluateResponse? evaluateResponse57 = await debugProtocolHost.EvaluateAsync(stackFrameId, "typeof(MyClass).ToString()");
        Assert.Equal("\"DebuggableConsoleApp.MyClass\"", evaluateResponse57.Result);
        EvaluateResponse? evaluateResponse58 = await debugProtocolHost.EvaluateAsync(stackFrameId, "IncrementByRef(ref myInt)");
        Assert.Equal("10", evaluateResponse58.Result);
        EvaluateResponse? evaluateResponse59 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt");
        Assert.Equal("10", evaluateResponse59.Result);
        EvaluateResponse? evaluateResponse60 = await debugProtocolHost.EvaluateAsync(stackFrameId, "AssignOut(out myInt)");
        Assert.Equal("42", evaluateResponse60.Result);
        EvaluateResponse? evaluateResponse61 = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt");
        Assert.Equal("42", evaluateResponse61.Result);
        EvaluateResponse? evaluateResponse62 = await debugProtocolHost.EvaluateAsync(stackFrameId, "AddOneIn(in myInt)");
        Assert.Equal("43", evaluateResponse62.Result);
        EvaluateResponse? evaluateResponse63 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_doubler(4)");
        Assert.Equal("8", evaluateResponse63.Result);

        // Operators resolved from Roslyn's bound tree: value-type operands, one declared on the right operand's type.
        EvaluateResponse? evaluateResponse64 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(structVar + structVar).Id");
        Assert.Equal("10", evaluateResponse64.Result);
        EvaluateResponse? evaluateResponse65 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(5 + structVar).Id");
        Assert.Equal("10", evaluateResponse65.Result);
        EvaluateResponse? evaluateResponse66 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(-structVar).Id");
        Assert.Equal("-5", evaluateResponse66.Result);
        EvaluateResponse? evaluateResponse67 = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar == structVar");
        Assert.Equal("true", evaluateResponse67.Result);
        EvaluateResponse? evaluateResponse68 = await debugProtocolHost.EvaluateAsync(stackFrameId, "this.DoubleNumber(myInt)");
        Assert.Equal("84", evaluateResponse68.Result);

        EvaluateResponse? evaluateResponse69 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList.Where(s => s == 4).ToList()");
        Assert.Equal("Count = 1", evaluateResponse69.Result);
        EvaluateResponse? evaluateResponse70 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList.Where(s => s == myInt).ToList()");
        Assert.Equal("Count = 0", evaluateResponse70.Result);
        EvaluateResponse? evaluateResponse71 = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList.Where(s => s == myInt).Select(s => s).ToList()");
        Assert.Equal("Count = 0", evaluateResponse71.Result);
        EvaluateResponse? inlineLambdaInvocationResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "((Func<int, int>)(value => value + 1))(4)");
        Assert.Equal("5", inlineLambdaInvocationResponse.Result);

        // Lambdas retain the stopped frame's lexical context, including this and non-public members and types.
        EvaluateResponse? instanceCaptureLambdaResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList.Where(value => value == _instanceField).ToList()");
        Assert.Equal("Count = 0", instanceCaptureLambdaResponse.Result);
        EvaluateResponse? thisCaptureLambdaResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new[] { this }.Where(value => value == this).Count()");
        Assert.Equal("1", thisCaptureLambdaResponse.Result);
        EvaluateResponse? privateMethodLambdaResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList.Select(value => DoubleNumber(value)).Last()");
        Assert.Equal("50", privateMethodLambdaResponse.Result);
        EvaluateResponse? internalTypeLambdaResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new[] { 1 }.Select(value => typeof(EvaluationLifetimeTracker).Name).Single()");
        Assert.Equal("\"EvaluationLifetimeTracker\"", internalTypeLambdaResponse.Result);

        // Nested lambdas and method groups retain their generated targets and methods.
        EvaluateResponse? nestedLambdaResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new[] { 1, 2 }.Where(value => new[] { value }.Any(inner => inner == value)).Count()");
        Assert.Equal("2", nestedLambdaResponse.Result);
        EvaluateResponse? lambdaAndMethodGroupResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new[] { -1 }.Where(value => value < 0).Select(Math.Abs).Single()");
        Assert.Equal("1", lambdaAndMethodGroupResponse.Result);

        // All delegates in one expression share the same closure, including captures that are only written.
        EvaluateResponse? writeOnlyCaptureLambdaResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList.Select(value => myInt = value).Last()");
        Assert.Equal("25", writeOnlyCaptureLambdaResponse.Result);
        EvaluateResponse? writeOnlyCaptureValueResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt");
        Assert.Equal("25", writeOnlyCaptureValueResponse.Result);
        EvaluateResponse? resetMyIntBeforeSharedClosureResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt = 42");
        Assert.Equal("42", resetMyIntBeforeSharedClosureResponse.Result);
        EvaluateResponse? sharedClosureLambdaResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList.Select(value => myInt++).Select(value => myInt).ToList().Last()");
        Assert.Equal("46", sharedClosureLambdaResponse.Result);
        EvaluateResponse? sharedClosureValueResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt");
        Assert.Equal("46", sharedClosureValueResponse.Result);
        EvaluateResponse? resetMyIntBeforeSameExpressionResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt = 42");
        Assert.Equal("42", resetMyIntBeforeSameExpressionResponse.Result);
        EvaluateResponse? sameExpressionCaptureResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList.Select(value => myInt++).Last() + myInt");
        Assert.Equal("85", sameExpressionCaptureResponse.Result);
        EvaluateResponse? sameExpressionCaptureValueResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt");
        Assert.Equal("43", sameExpressionCaptureValueResponse.Result);
        EvaluateResponse? restoreMyIntAfterLambdaResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt = 42");
        Assert.Equal("42", restoreMyIntAfterLambdaResponse.Result);

        EvaluateResponse? nullEqualityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableRefType == null");
        Assert.Equal("true", nullEqualityResponse.Result);
        EvaluateResponse? referenceEqualityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "this == this");
        Assert.Equal("true", referenceEqualityResponse.Result);
        EvaluateResponse? stringEqualityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "anotherVar == \"asdf\"");
        Assert.Equal("true", stringEqualityResponse.Result);
        EvaluateResponse? booleanEqualityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableIntWithVal.HasValue == true");
        Assert.Equal("true", booleanEqualityResponse.Result);
        EvaluateResponse? equalEnumResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "enumVar == MyEnum.ThirdValue");
        Assert.Equal("true", equalEnumResponse.Result);
        EvaluateResponse? unequalEnumResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "enumVar == MyEnum.SecondValue");
        Assert.Equal("false", unequalEnumResponse.Result);
        EvaluateResponse? smallIntegralEqualityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "Convert.ToByte(myInt) == myInt");
        Assert.Equal("true", smallIntegralEqualityResponse.Result);

        EvaluateResponse? nullableConversionResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(int?)myInt");
        Assert.Equal("42", nullableConversionResponse.Result);
        Assert.Equal("int?", nullableConversionResponse.Type);
        EvaluateResponse? enumConversionResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(MyEnum)(myInt - 40)");
        Assert.Equal("ThirdValue", enumConversionResponse.Result);
        Assert.Equal("DebuggableConsoleApp.MyEnum", enumConversionResponse.Type);
        EvaluateResponse? enumOperatorResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "enumWithFlagsVar | MyEnumWithFlags.FlagValue2");
        Assert.Equal("FlagValue1 | FlagValue2 | FlagValue3", enumOperatorResponse.Result);
        EvaluateResponse? runtimeFloatToIntResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(int)this.DoubleNumber(4f)");
        Assert.Equal("8", runtimeFloatToIntResponse.Result);
        EvaluateResponse? runtimeFloatToDoubleResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(double)this.DoubleNumber(4f)");
        Assert.Equal("8", runtimeFloatToDoubleResponse.Result);
        EvaluateResponse? computedFloatArgumentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "this.DoubleNumber(this.DoubleNumber(4f) + 1f)");
        Assert.Equal("18", computedFloatArgumentResponse.Result);
        EvaluateResponse? unsignedWideningResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "unchecked((ulong)(uint)(myInt - 43))");
        Assert.Equal("4294967295", unsignedWideningResponse.Result);
        EvaluateResponse? incompatibleAsResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "this as object as string");
        Assert.Equal("null", incompatibleAsResponse.Result);
        EvaluateResponse? incompatibleCastResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "((string)(object)this).Length");
        Assert.Contains("InvalidCastException", incompatibleCastResponse.Result);
        EvaluateResponse? boxingResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(object)myInt != null");
        Assert.Equal("true", boxingResponse.Result);
        EvaluateResponse? unboxingResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(int)(object)myInt");
        Assert.Equal("42", unboxingResponse.Result);
        // unbox.any must reject a box whose runtime type does not match the target type
        EvaluateResponse? incompatibleUnboxResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(long)(object)myInt");
        Assert.Contains("InvalidCastException", incompatibleUnboxResponse.Result);
        EvaluateResponse? incompatibleUnboxResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(int)(object)myParam");
        Assert.Contains("InvalidCastException", incompatibleUnboxResponse2.Result);
        // reference type -> object is an identity conversion (no box), and must return the reference unchanged
        EvaluateResponse? boxReferenceResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(object)anotherVar");
        Assert.Equal("\"asdf\"", boxReferenceResponse.Result);
        // box/unbox round-trip of an exact enum type
        EvaluateResponse? enumBoxResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(MyEnum)(object)enumVar");
        Assert.Equal("ThirdValue", enumBoxResponse.Result);
        // An int's box unboxes as an int-based enum, and an enum's as its int, as the runtime allows.
        EvaluateResponse? enumBoxResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId, "(MyEnum)(object)2");
        Assert.True(enumBoxResponse2.Result == "ThirdValue", $"(MyEnum)(object)2 is {enumBoxResponse2.Result}, where the program unboxes an int's box as an int-based enum");
        EvaluateResponse? enumUnderlyingUnboxResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(int)(object)MyEnum.SecondValue");
        Assert.True(enumUnderlyingUnboxResponse.Result == "1", $"(int)(object)MyEnum.SecondValue is {enumUnderlyingUnboxResponse.Result}, where the program unboxes an int-based enum's box as its int");
        EvaluateResponse? enumWiderUnboxResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(long)(object)MyEnum.SecondValue");
        Assert.Contains("InvalidCastException", enumWiderUnboxResponse.Result);
        EvaluateResponse? genericMethodResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "Array.Empty<int>().Length");
        Assert.Equal("0", genericMethodResponse.Result);
        EvaluateResponse? throwingMethodResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "int.Parse(anotherVar)");
        Assert.Contains("FormatException", throwingMethodResponse.Result);
        EvaluateResponse? checkedArithmeticResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "checked(myInt + int.MaxValue)");
        Assert.Contains("OverflowException", checkedArithmeticResponse.Result);
        EvaluateResponse? intShiftResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt << (myInt - 10)");
        Assert.Equal("42", intShiftResponse.Result);
        EvaluateResponse? unsignedShiftResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "-1 >>> (myInt - 41)");
        Assert.Equal("2147483647", unsignedShiftResponse.Result);
        EvaluateResponse? ulongDivisionResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "ulong.MaxValue / (ulong)(myInt - 40)");
        Assert.Equal("9223372036854775807", ulongDivisionResponse.Result);
        EvaluateResponse? nanEqualityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "0.0 / (myInt - myInt) == double.NaN");
        Assert.Equal("false", nanEqualityResponse.Result);
        EvaluateResponse? namedArgumentsResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "TestMethod(myString: \"asdf\", myInt: 4)");
        Assert.Equal("8", namedArgumentsResponse.Result);
        EvaluateResponse? arrayIndexResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intArray[0]");
        Assert.Equal("2", arrayIndexResponse.Result);
        EvaluateResponse? indexerResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_intList[0]");
        Assert.Equal("1", indexerResponse.Result);
        EvaluateResponse? conditionalAccessResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableRefType?.GetHashCode()");
        Assert.Equal("null", conditionalAccessResponse.Result);

        EvaluateResponse? interpolationFormatResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"{myInt:D5}\"");
        Assert.Equal("\"00042\"", interpolationFormatResponse.Result);
        EvaluateResponse? interpolationAlignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"{myInt,5}\"");
        Assert.Equal("\"   42\"", interpolationAlignmentResponse.Result);
        EvaluateResponse? nullInterpolationResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"x{nullableRefType}y\"");
        Assert.Equal("\"xy\"", nullInterpolationResponse.Result);
        EvaluateResponse? objectInterpolationResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"{ClassProperty}\"");
        Assert.Equal("\"DebuggableConsoleApp.MyClass2\"", objectInterpolationResponse.Result);
        EvaluateResponse? enumInterpolationResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"{enumVar}\"");
        Assert.Equal("\"ThirdValue\"", enumInterpolationResponse.Result);
        EvaluateResponse? structInterpolationResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "$\"{structVar}\"");
        Assert.Equal("\"DebuggableConsoleApp.MyStruct\"", structInterpolationResponse.Result);
        EvaluateResponse? nullConcatenationResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "anotherVar + nullableRefType");
        Assert.Equal("\"asdf\"", nullConcatenationResponse.Result);

        EvaluateResponse? structFieldReadResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar.Id");
        Assert.Equal("5", structFieldReadResponse.Result);
        EvaluateResponse? structFieldWriteResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar.Id = 6");
        Assert.Equal("6", structFieldWriteResponse.Result);
        EvaluateResponse? structFieldReadAfterWriteResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar.Id");
        Assert.Equal("6", structFieldReadAfterWriteResponse.Result);
        EvaluateResponse? structAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar = default(MyStruct)");
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structAssignmentResponse.Result);
        EvaluateResponse? structFieldReadAfterAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar.Id");
        Assert.Equal("0", structFieldReadAfterAssignmentResponse.Result);

        // stobj: assigning a struct value to a field (stfld)
        EvaluateResponse? structResetIdResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar.Id = 5");
        Assert.Equal("5", structResetIdResponse.Result);
        EvaluateResponse? structToFieldAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_structField = structVar");
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structToFieldAssignmentResponse.Result);
        EvaluateResponse? structFieldValueReadBackResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_structField.Id");
        Assert.Equal("5", structFieldValueReadBackResponse.Result);

        // stobj: struct value copied out of a field back into a local
        EvaluateResponse? structFromFieldAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar = _structField");
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structFromFieldAssignmentResponse.Result);
        EvaluateResponse? structLocalValueAfterFieldCopyResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar.Id");
        Assert.Equal("5", structLocalValueAfterFieldCopyResponse.Result);

        // ldobj: struct value loaded through a ref-return (call + ldobj + stloc)
        EvaluateResponse? structViaRefReturnResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar = GetStructByRef()");
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structViaRefReturnResponse.Result);
        EvaluateResponse? structIdViaRefReturnResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "structVar.Id");
        Assert.Equal("5", structIdViaRefReturnResponse.Result);

        // stobj: struct value stored through a ref-return (call + ldloc + stobj)
        EvaluateResponse? structStoreViaRefReturnResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "GetStructByRef() = structVar");
        Assert.Equal("{DebuggableConsoleApp.MyStruct}", structStoreViaRefReturnResponse.Result);
        EvaluateResponse? structFieldIdAfterRefStoreResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "GetStructByRef().Id");
        Assert.Equal("5", structFieldIdAfterRefStoreResponse.Result);

        // unboxing conversion: cast-to-struct used as the receiver of an instance method call
        EvaluateResponse? unboxReceiverResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "((MyStruct)(object)structVar).GetId()");
        Assert.Equal("5", unboxReceiverResponse.Result);

        EvaluateResponse? structDefaultResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "default(MyStruct).Id");
        Assert.Equal("0", structDefaultResponse.Result);
        EvaluateResponse? sizeOfResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "sizeof(int)");
        Assert.Equal("4", sizeOfResponse.Result);
        EvaluateResponse? arrayTypeOfResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "typeof(int[]).ToString()");
        Assert.Equal("\"System.Int32[]\"", arrayTypeOfResponse.Result);
        EvaluateResponse? genericTypeOfResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "typeof(System.Collections.Generic.List<int>).ToString()");
        Assert.Equal("\"System.Collections.Generic.List`1[System.Int32]\"", genericTypeOfResponse.Result);
        EvaluateResponse? nestedTypeOfResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "typeof(MyClassContainingAnotherClass.MyNestedClass).ToString()");
        Assert.Equal("\"DebuggableConsoleApp.MyClassContainingAnotherClass+MyNestedClass\"", nestedTypeOfResponse.Result);
        EvaluateResponse? outVarResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "TryGetObject(out var objectResult) ? objectResult.IntProperty : 0");
        Assert.Equal("6", outVarResponse.Result);

        EvaluateResponse? genericTypeOverloadResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "new OverloadResolutionGeneric<int>().Pick(\"hello\")");
        Assert.Equal("5", genericTypeOverloadResponse.Result);
        EvaluateResponse? myIntResetResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "myInt = 42");
        Assert.Equal("42", myIntResetResponse.Result);
        EvaluateResponse? byRefOverloadResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "OverloadResolutionByRef.Pick(ref myInt)");
        Assert.Equal("42", byRefOverloadResponse.Result);

        EvaluateResponse? referenceStringAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "anotherVar = \"updated\"");
        Assert.Equal("\"updated\"", referenceStringAssignmentResponse.Result);
        EvaluateResponse? referenceStringReadBackResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "anotherVar");
        Assert.Equal("\"updated\"", referenceStringReadBackResponse.Result);
        EvaluateResponse? referenceStringEqualityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "anotherVar == \"updated\"");
        Assert.Equal("true", referenceStringEqualityResponse.Result);

        // A string read from the debuggee keeps its reference, so identity comparisons and assignments alias.
        EvaluateResponse? stringIdentityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "IsSameString(anotherVar, anotherVar)");
        Assert.Equal("true", stringIdentityResponse.Result);
        EvaluateResponse? stringReferenceEqualsResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "object.ReferenceEquals(anotherVar, anotherVar)");
        Assert.Equal("true", stringReferenceEqualsResponse.Result);
        EvaluateResponse? stringAliasAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "anotherVar = _name");
        Assert.Equal("\"TestName\"", stringAliasAssignmentResponse.Result);
        EvaluateResponse? stringAliasIdentityResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "IsSameString(anotherVar, _name)");
        Assert.Equal("true", stringAliasIdentityResponse.Result);

        EvaluateResponse? referenceObjectAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableRefType = new MyClass()");
        Assert.Equal("{DebuggableConsoleApp.MyClass}", referenceObjectAssignmentResponse.Result);
        EvaluateResponse? referenceObjectNotNullResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableRefType == null");
        Assert.Equal("false", referenceObjectNotNullResponse.Result);
        EvaluateResponse? referenceNullAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableRefType = null");
        Assert.Equal("null", referenceNullAssignmentResponse.Result);
        EvaluateResponse? referenceNullReadBackResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "nullableRefType == null");
        Assert.Equal("true", referenceNullReadBackResponse.Result);

        EvaluateResponse? referenceFieldNullAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_classField = null");
        Assert.Equal("null", referenceFieldNullAssignmentResponse.Result);
        EvaluateResponse? referenceFieldNullReadBackResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_classField == null");
        Assert.Equal("true", referenceFieldNullReadBackResponse.Result);

        EvaluateResponse? arrayStringAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(new string[1])[0] = \"array-string\"");
        Assert.Equal("\"array-string\"", arrayStringAssignmentResponse.Result);
        EvaluateResponse? arrayNullAssignmentResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "(new MyClass2[1])[0] = null");
        Assert.Equal("null", arrayNullAssignmentResponse.Result);
    }

    /// <summary>A name a lambda's parameter shadows evaluates to the parameter where the stop is, not the method's local.</summary>
    [Gate]
    [Fact]
    public async Task An_evaluation_of_a_name_a_lambda_shadows_reads_the_lambdas_own_parameter()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        await debugProtocolHost
            .BreakpointsAsync(Debuggees.Line("DebuggableConsoleApp", "Lambdas/MyLambdaClass.cs", "return value + outerLocal;"),
                Debuggees.Source("DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        ScopesResponse? scopesResponse = await debugProtocolHost.ScopesAsync(stackTraceResponse.StackFrames!.First().Id);

        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;

        // 'value' must resolve to the innermost lambda's parameter (200), not the hoisted method-local 'value' (100)
        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "value");
        Assert.Equal("200", evaluateResponse.Result);
    }

    /// <summary>Inside a generic method its type parameter is the type it was called with: default(T), typeof(T) and its
    /// argument evaluate as that type's.</summary>
    [Gate]
    [Fact]
    public async Task An_evaluation_inside_a_generic_method_knows_its_type_argument()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        await debugProtocolHost
            .BreakpointsAsync(Debuggees.Line("DebuggableConsoleApp", "MyClassWithGenericMethod.cs", "return arg;"),
                Debuggees.Source("DebuggableConsoleApp", "MyClassWithGenericMethod.cs"))
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        ScopesResponse? scopesResponse = await debugProtocolHost.ScopesAsync(stackTraceResponse.StackFrames!.First().Id);

        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;

        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "default(T)");
        Assert.Equal("0", evaluateResponse.Result);
        EvaluateResponse? evaluateResponse3 = await debugProtocolHost.EvaluateAsync(stackFrameId, "arg");
        Assert.Equal("4", evaluateResponse3.Result);
        EvaluateResponse? evaluateResponse4 = await debugProtocolHost.EvaluateAsync(stackFrameId, "typeof(T)");
        Assert.Equal("System.Int32", evaluateResponse4.Result);
    }

    [Gate]
    [Fact]
    public async Task A_LINQ_watch_evaluates_in_a_program_that_has_called_no_LINQ_method_yet()
    {
        // At the entry of a program that has loaded no System.Linq, which the expression loads.
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("TopLevelStatementsConsoleApp"), stopAtEntry: true, justMyCode: true)
            .WaitForInitializedEvent();
        await host.ConfigurationDoneAsync();
        StoppedEvent stoppedEvent = await host.WaitForStoppedEvent();
        DapStackFrame topFrame = await host.TopFrameAsync(stoppedEvent.ThreadId!.Value);

        EvaluateResponse? linq = await host.EvaluateAsync(topFrame.Id, "new[] { test, 7 }.Where(x => x > 3).Count()");
        Assert.Equal("1", linq.Result);
        EvaluateResponse? named = await host.EvaluateAsync(topFrame.Id, "System.Linq.Enumerable.Range(1, 3).Sum()");
        Assert.Equal("6", named.Result);
    }

    /// <summary>A stop in a static class's method reads the class's static field.</summary>
    [Gate]
    [Fact]
    public async Task An_evaluation_in_a_static_classs_method_reads_its_field()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        await debugProtocolHost
            .BreakpointsAsync([Debuggees.Line("DebuggableConsoleApp", "MyStaticClass.cs", "int test = 4;")], Debuggees.Source("DebuggableConsoleApp", "MyStaticClass.cs"))
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        ScopesResponse? scopesResponse = await debugProtocolHost.ScopesAsync(stackTraceResponse.StackFrames!.First().Id);

        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;

        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_testInt");
        Assert.Equal("4", evaluateResponse.Result);
    }

    /// <summary>An evaluation calls the program's own methods that take and return a type nested in another of its types,
    /// each found as the program declares it.</summary>
    [Gate]
    [Fact]
    public async Task An_evaluation_calls_a_method_that_takes_a_nested_type()
    {
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, true, out Process program);
        await host
            .InitializeAsync()
            .AttachAsync(program.Id)
            .WaitForInitializedEvent();
        await host
            .BreakpointsAsync(Debuggees.MyMethodEnd, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"))
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(program.Id, true);

        StoppedEvent stopped = await host.WaitForStoppedEvent();
        StackTraceResponse stack = await host.StackTraceAsync(stopped.ThreadId!.Value);
        EvaluateResponse called = await host.EvaluateAsync(stack.StackFrames[0].Id, "NestedTypeCalls.ValueOf(NestedTypeCalls.Nested(5))");
        Assert.True(called.Result == "5", $"A call taking MyClassContainingAnotherClass.MyNestedClass evaluated to {called.Result}, not the 5 it was given");
    }
}
