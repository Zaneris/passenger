using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class Variables2Tests(ITestOutputHelper testOutputHelper)
{
    private static readonly int TestEnd = Debuggees.Line("DebuggableConsoleApp", "VariablesClass.cs", "CompositeValueFixtures localCompositeValues = new CompositeValueFixtures();") + 1;

    /// <summary>A float's and a double's values read as C# writes them whatever the machine's culture, and Set Value takes
    /// back the text shown. The culture comes from LANG and LC_ALL on macOS and Linux; Windows takes it from the user's
    /// settings, so there the test holds the values to C#'s alone.</summary>
    [Fact]
    public async Task A_float_and_a_double_read_as_CSharp_writes_them_under_a_German_culture_and_take_back_the_text_shown()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper, environment: new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" });
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("DebuggableConsoleApp"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        await host.BreakpointsAsync(TestEnd, Debuggees.Source("DebuggableConsoleApp", "VariablesClass.cs"))
            .ConfigurationDoneAsync();
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        StackTraceResponse? frames = await host.StackTraceAsync(stopped.ThreadId!.Value);
        ScopesResponse? scopes = await host.ScopesAsync(frames.StackFrames.First().Id);
        List<DapVariable>? locals = await host.VariablesAsync(scopes.Scopes.Single().VariablesReference);
        int frame = frames.StackFrames.First().Id;

        Assert.Equal(("1.5", "2.5", "3.5"), (Shown("localFloat"), Shown("localDouble"), Shown("localDecimal")));
        Assert.Equal("3.14", await Set("localDouble", "3.14"));
        Assert.Equal("3.14", await Set("localFloat", "3.14f"));
        // C# writes neither a float's nor a decimal's literal as shown, but Set Value takes the text shown.
        string pi = await Set("localDouble", "Math.PI");
        Assert.Equal("3.141592653589793", pi);
        Assert.Equal("0", await Set("localDouble", "0"));
        Assert.Equal(pi, await Set("localDouble", pi));
        string third = await Set("localFloat", "1f / 3");
        Assert.Equal("0.33333334", third);
        Assert.Equal("0", await Set("localFloat", "0"));
        Assert.Equal(third, await Set("localFloat", third));
        Assert.Equal("2.75", await Set("localDecimal", "2.75"));
        Assert.Equal("-1E+20", await Set("localDouble", "-1E+20"));

        async Task<string> Set(string name, string value) => (await host.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments(name, value, frame))).Value;

        string? Shown(string name) => locals.FirstOrDefault(v => v.Name == name)?.Value;
    }

    [Fact]
    public async Task A_stop_lists_locals_and_fields_of_every_kind_with_their_values_and_types()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "VariablesClass.cs");
        await debugProtocolHost
            .BreakpointsAsync([TestEnd], breakpointedFilePath)
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal((breakpointedFilePath, TestEnd, 3), stoppedEvent.ReadStopInfo());
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        ScopesResponse? scopesResponse = await debugProtocolHost.ScopesAsync(stackTraceResponse.StackFrames!.First().Id);

        Assert.Single(scopesResponse.Scopes);
        DapScope scope = scopesResponse.Scopes.Single();

        string expectedDateTimeString = new DateTime(2026, 6, 13, 5, 42, 39).ToString();
        List<DapVariable> expectedVariables =
        [
            new() { VariablesReference = 2,  Name = "this",                     EvaluateName = "this",                      Value = "{DebuggableConsoleApp.VariablesClass}",    Type = "DebuggableConsoleApp.VariablesClass" },
            new() { VariablesReference = 0,  Name = "localBool",                EvaluateName = "localBool",                 Value = "true",                                     Type = "bool" },
            new() { VariablesReference = 0,  Name = "localByte",                EvaluateName = "localByte",                 Value = "1",                                        Type = "byte" },
            new() { VariablesReference = 0,  Name = "localSByte",               EvaluateName = "localSByte",                Value = "-1",                                       Type = "sbyte" },
            new() { VariablesReference = 0,  Name = "localShort",               EvaluateName = "localShort",                Value = "-2",                                       Type = "short" },
            new() { VariablesReference = 0,  Name = "localUShort",              EvaluateName = "localUShort",               Value = "2",                                        Type = "ushort" },
            new() { VariablesReference = 0,  Name = "localInt",                 EvaluateName = "localInt",                  Value = "3",                                        Type = "int" },
            new() { VariablesReference = 0,  Name = "localUInt",                EvaluateName = "localUInt",                 Value = "4",                                        Type = "uint" },
            new() { VariablesReference = 0,  Name = "localLong",                EvaluateName = "localLong",                 Value = "5",                                        Type = "long" },
            new() { VariablesReference = 0,  Name = "localULong",               EvaluateName = "localULong",                Value = "6",                                        Type = "ulong" },
            new() { VariablesReference = 0,  Name = "localChar",                EvaluateName = "localChar",                 Value = "90 'Z'",                                   Type = "char" },
            new() { VariablesReference = 0,  Name = "localFloat",               EvaluateName = "localFloat",                Value = "1.5",                                      Type = "float" },
            new() { VariablesReference = 0,  Name = "localDouble",              EvaluateName = "localDouble",               Value = "2.5",                                      Type = "double" },
            new() { VariablesReference = 0,  Name = "localDecimal",             EvaluateName = "localDecimal",              Value = "3.5",                                      Type = "decimal" },
            new() { VariablesReference = 0,  Name = "localNullableInt",         EvaluateName = "localNullableInt",          Value = "123",                                      Type = "int?" },
            new() { VariablesReference = 0,  Name = "localNullableIntNull",     EvaluateName = "localNullableIntNull",      Value = "null",                                     Type = "int?" },
            new() { VariablesReference = 0,  Name = "localNullableDecimal",     EvaluateName = "localNullableDecimal",      Value = "2.5",                                      Type = "decimal?" },
            new() { VariablesReference = 0,  Name = "localNullableDecimalNull", EvaluateName = "localNullableDecimalNull",  Value = "null",                                     Type = "decimal?" },
            new() { VariablesReference = 0,  Name = "localString",              EvaluateName = "localString",               Value = "\"hello\"",                                Type = "string" },
            new() { VariablesReference = 0,  Name = "localNullableString",      EvaluateName = "localNullableString",       Value = "null",                                     Type = "string" },
            new() { VariablesReference = 3,  Name = "localObject",              EvaluateName = "localObject",               Value = "{object}",                                 Type = "object" },
            new() { VariablesReference = 0,  Name = "localNullableObject",      EvaluateName = "localNullableObject",       Value = "null",                                     Type = "object" },
            new() { VariablesReference = 0,  Name = "localBoxedInt",            EvaluateName = "localBoxedInt",             Value = "42",                                       Type = "int" },
            new() { VariablesReference = 4,  Name = "localArray",               EvaluateName = "localArray",                Value = "int[3]",                                   Type = "int[]" },
            new() { VariablesReference = 5,  Name = "localList",                EvaluateName = "localList",                 Value = "Count = 2",                                Type = "System.Collections.Generic.List<string>" },
            new() { VariablesReference = 6,  Name = "localDictionary",          EvaluateName = "localDictionary",           Value = "Count = 1",                                Type = "System.Collections.Generic.Dictionary<int, string>" },
            new() { VariablesReference = 7,  Name = "localStruct",              EvaluateName = "localStruct",               Value = "{DebuggableConsoleApp.TestStruct}",        Type = "DebuggableConsoleApp.TestStruct" },
            new() { VariablesReference = 8,  Name = "localClass",               EvaluateName = "localClass",                Value = "{DebuggableConsoleApp.TestClass}",         Type = "DebuggableConsoleApp.TestClass" },
            new() { VariablesReference = 9,  Name = "localRecord",              EvaluateName = "localRecord",               Value = "TestRecord { Name = record, Age = 1 }",    Type = "DebuggableConsoleApp.TestRecord" },
            new() { VariablesReference = 10, Name = "localInterface",           EvaluateName = "localInterface",            Value = "{DebuggableConsoleApp.TestClass}",         Type = "DebuggableConsoleApp.TestClass" },
            new() { VariablesReference = 11, Name = "localDelegate",            EvaluateName = "localDelegate",             Value = "{System.Func<int, int>}",                  Type = "System.Func<int, int>" },
            new() { VariablesReference = 12, Name = "localTuple",               EvaluateName = "localTuple",                Value = "(1, \"stringInTuple\")",                       Type = "System.Tuple<int, string>" },
            new() { VariablesReference = 13, Name = "localValueTuple",          EvaluateName = "localValueTuple",           Value = "(2, \"stringInValueTuple\")",                  Type = "System.ValueTuple<int, string>" },
            new() { VariablesReference = 14, Name = "localGeneric",             EvaluateName = "localGeneric",              Value = "{DebuggableConsoleApp.GenericBox<int>}",   Type = "DebuggableConsoleApp.GenericBox<int>" },
            new() { VariablesReference = 0,  Name = "localDynamic",             EvaluateName = "localDynamic",              Value = "241",                                      Type = "int" },
            new() { VariablesReference = 15, Name = "localAnonymous",           EvaluateName = "localAnonymous",            Value = "{ Id = 1, Name = \"Anonymous\" }",             Type = "<>f__AnonymousType0<int, string>" },
            new() { VariablesReference = 16, Name = "localDateTime",            EvaluateName = "localDateTime",             Value = expectedDateTimeString,                     Type = "System.DateTime" },
            new() { VariablesReference = 17, Name = "localGuid",                EvaluateName = "localGuid",                 Value = "27de5b68-af24-4e59-a785-dde52e2ea7af",     Type = "System.Guid" },
            new() { VariablesReference = 18, Name = "localCompositeValues", EvaluateName = "localCompositeValues", Value = "{DebuggableConsoleApp.CompositeValueFixtures}", Type = "DebuggableConsoleApp.CompositeValueFixtures" },
        ];

        List<DapVariable>? variables = await debugProtocolHost.VariablesAsync(scope.VariablesReference);

        Assert.Equal(39, variables.Count);
        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
        await debugProtocolHost.AssertInstanceThisInstanceVariables(variables.Single(s => s.Name == "this").VariablesReference, breakpointedFilePath);
        await debugProtocolHost.AssertCompositeValueVariables(variables.Single(s => s.Name == "localCompositeValues").VariablesReference);
    }
}

file static class TestExtensions
{
    private static readonly int Throwing = Debuggees.Line("DebuggableConsoleApp", "VariablesClass.cs", "private int ThrowingProperty =>");

    public static async Task AssertInstanceThisInstanceVariables(this DebugSession debugProtocolHost, int variablesReference, string breakpointedFilePath)
    {
        string expectedDateTimeField = new DateTime(2026, 6, 15, 10, 5, 8).ToString();
        string expectedDateOnlyField = new DateOnly(2026, 6, 15).ToString();
        string expectedTimeOnlyField = new TimeOnly(10, 2, 16).ToString();
        string expectedTimeSpanField = TimeSpan.FromMinutes(5).ToString();
        string expectedGuidField = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        string expectedNullableGuidField = "f0e1d2c3-b4a5-9687-7869-5a4b3c2d1e0f";
        string expectedThrowingPropertyValue = $"System.InvalidOperationException: ThrowingProperty was accessed{Environment.NewLine}   at DebuggableConsoleApp.VariablesClass.get_ThrowingProperty() in {breakpointedFilePath}:line {Throwing}";

        List<DapVariable> expectedVariables =
        [
            new() { VariablesReference =  0,  Name = "BoolField",                   EvaluateName = "this.BoolField",                   Value = "true",                                                      Type = "bool" },
            new() { VariablesReference =  0,  Name = "ByteField",                   EvaluateName = "this.ByteField",                   Value = "1",                                                         Type = "byte" },
            new() { VariablesReference =  0,  Name = "SByteField",                  EvaluateName = "this.SByteField",                  Value = "-1",                                                        Type = "sbyte" },
            new() { VariablesReference =  0,  Name = "ShortField",                  EvaluateName = "this.ShortField",                  Value = "-2",                                                        Type = "short" },
            new() { VariablesReference =  0,  Name = "UShortField",                 EvaluateName = "this.UShortField",                 Value = "2",                                                         Type = "ushort" },
            new() { VariablesReference =  0,  Name = "IntField",                    EvaluateName = "this.IntField",                    Value = "123",                                                       Type = "int" },
            new() { VariablesReference =  0,  Name = "UIntField",                   EvaluateName = "this.UIntField",                   Value = "123",                                                       Type = "uint" },
            new() { VariablesReference =  0,  Name = "LongField",                   EvaluateName = "this.LongField",                   Value = "123456789",                                                 Type = "long" },
            new() { VariablesReference =  0,  Name = "ULongField",                  EvaluateName = "this.ULongField",                  Value = "123456789",                                                 Type = "ulong" },
            new() { VariablesReference =  0,  Name = "CharField",                   EvaluateName = "this.CharField",                   Value = "65 'A'",                                                    Type = "char" },
            new() { VariablesReference =  0,  Name = "FloatField",                  EvaluateName = "this.FloatField",                  Value = "1.23",                                                      Type = "float" },
            new() { VariablesReference =  0,  Name = "DoubleField",                 EvaluateName = "this.DoubleField",                 Value = "2.34",                                                      Type = "double" },
            new() { VariablesReference =  0,  Name = "DecimalField",                EvaluateName = "this.DecimalField",                Value = "3.45",                                                      Type = "decimal" },
            new() { VariablesReference =  0,  Name = "NullableIntField",            EvaluateName = "this.NullableIntField",            Value = "42",                                                        Type = "int?" },
            new() { VariablesReference =  0,  Name = "NullableIntNullField",        EvaluateName = "this.NullableIntNullField",        Value = "null",                                                      Type = "int?" },
            new() { VariablesReference =  0,  Name = "NullableBoolField",           EvaluateName = "this.NullableBoolField",           Value = "true",                                                      Type = "bool?" },
            new() { VariablesReference =  0,  Name = "NullableBoolNullField",       EvaluateName = "this.NullableBoolNullField",       Value = "null",                                                      Type = "bool?" },
            new() { VariablesReference = 19,  Name = "NullableGuidField",           EvaluateName = "this.NullableGuidField",           Value = expectedNullableGuidField,                                   Type = "System.Guid?" },
            new() { VariablesReference = 20,  Name = "NullableEnumField",           EvaluateName = "this.NullableEnumField",           Value = "Friday",                                                    Type = "System.DayOfWeek?" },
            new() { VariablesReference =  0,  Name = "NullableEnumNullField",       EvaluateName = "this.NullableEnumNullField",       Value = "null",                                                      Type = "System.DayOfWeek?" },
            new() { VariablesReference =  0,  Name = "StringField",                 EvaluateName = "this.StringField",                 Value = "\"Hello\"",                                                 Type = "string" },
            new() { VariablesReference =  0,  Name = "NullableStringField",         EvaluateName = "this.NullableStringField",         Value = "null",                                                      Type = "string" },
            new() { VariablesReference = 21,  Name = "ObjectField",                 EvaluateName = "this.ObjectField",                 Value = "{object}",                                                  Type = "object" },
            new() { VariablesReference =  0,  Name = "NullableObjectField",         EvaluateName = "this.NullableObjectField",         Value = "null",                                                      Type = "object" },
            new() { VariablesReference = 22,  Name = "IntArrayField",               EvaluateName = "this.IntArrayField",               Value = "int[3]",                                                    Type = "int[]" },
            new() { VariablesReference = 23,  Name = "NullableStringArrayField",    EvaluateName = "this.NullableStringArrayField",    Value = "string[3]",                                                 Type = "string[]" },
            new() { VariablesReference = 24,  Name = "MultiDimArrayField",          EvaluateName = "this.MultiDimArrayField",          Value = "int[2, 5]",                                                 Type = "int[,]" },
            new() { VariablesReference = 25,  Name = "JaggedArrayField",            EvaluateName = "this.JaggedArrayField",            Value = "int[2][]",                                                  Type = "int[][]" },
            new() { VariablesReference = 26,  Name = "ListField",                   EvaluateName = "this.ListField",                   Value = "Count = 3",                                                 Type = "System.Collections.Generic.List<int>" },
            new() { VariablesReference = 27,  Name = "DictionaryField",             EvaluateName = "this.DictionaryField",             Value = "Count = 2",                                                 Type = "System.Collections.Generic.Dictionary<string, int>" },
            new() { VariablesReference = 28,  Name = "DateTimeField",               EvaluateName = "this.DateTimeField",               Value = expectedDateTimeField,                                       Type = "System.DateTime" },
            new() { VariablesReference = 29,  Name = "DateOnlyField",               EvaluateName = "this.DateOnlyField",               Value = expectedDateOnlyField,                                       Type = "System.DateOnly" },
            new() { VariablesReference = 30,  Name = "TimeOnlyField",               EvaluateName = "this.TimeOnlyField",               Value = expectedTimeOnlyField,                                       Type = "System.TimeOnly" },
            new() { VariablesReference = 31,  Name = "TimeSpanField",               EvaluateName = "this.TimeSpanField",               Value = expectedTimeSpanField,                                       Type = "System.TimeSpan" },
            new() { VariablesReference = 32,  Name = "GuidField",                   EvaluateName = "this.GuidField",                   Value = expectedGuidField,                                           Type = "System.Guid" },
            new() { VariablesReference = 33,  Name = "EnumField",                   EvaluateName = "this.EnumField",                   Value = "Monday",                                                    Type = "System.DayOfWeek" },
            new() { VariablesReference = 34,  Name = "StructField",                 EvaluateName = "this.StructField",                 Value = "{DebuggableConsoleApp.TestStruct}",                         Type = "DebuggableConsoleApp.TestStruct" },
            new() { VariablesReference = 35,  Name = "ClassField",                  EvaluateName = "this.ClassField",                  Value = "{DebuggableConsoleApp.TestClass}",                          Type = "DebuggableConsoleApp.TestClass" },
            new() { VariablesReference = 36,  Name = "RecordField",                 EvaluateName = "this.RecordField",                 Value = "TestRecord { Name = Alice, Age = 42 }",                     Type = "DebuggableConsoleApp.TestRecord" },
            new() { VariablesReference = 37,  Name = "RecordStructField",           EvaluateName = "this.RecordStructField",           Value = "TestRecordStruct { Value = 7 }",                            Type = "DebuggableConsoleApp.TestRecordStruct" },
            new() { VariablesReference = 38,  Name = "InterfaceField",              EvaluateName = "this.InterfaceField",              Value = "{DebuggableConsoleApp.TestClass}",                          Type = "DebuggableConsoleApp.TestClass" },
            new() { VariablesReference = 39,  Name = "DelegateField",               EvaluateName = "this.DelegateField",               Value = "{System.Func<int, int>}",                                   Type = "System.Func<int, int>" },
            new() { VariablesReference = 40,  Name = "TupleField",                  EvaluateName = "this.TupleField",                  Value = "(1, \"tuple\")",                                                Type = "System.Tuple<int, string>" },
            new() { VariablesReference = 41,  Name = "ValueTupleField",             EvaluateName = "this.ValueTupleField",             Value = "(123, \"value tuple\")",                                        Type = "System.ValueTuple<int, string>" },
            new() { VariablesReference = 42,  Name = "GenericField",                EvaluateName = "this.GenericField",                Value = "{DebuggableConsoleApp.GenericBox<string>}",                 Type = "DebuggableConsoleApp.GenericBox<string>" },
            new() { VariablesReference =  0,  Name = "DynamicField",                EvaluateName = "this.DynamicField",                Value = "\"dynamic value\"",                                         Type = "string" },
            new() { VariablesReference =  0,  Name = "ReadonlyField",               EvaluateName = "this.ReadonlyField",               Value = "\"readonly\"",                                              Type = "string" },
            new() { VariablesReference =  0,  Name = "IntProperty",                 EvaluateName = "this.IntProperty",                 Value = "100",                                                       Type = "int" },
            new() { VariablesReference =  0,  Name = "NullableStringProperty",      EvaluateName = "this.NullableStringProperty",      Value = "null",                                                      Type = "string" },
            new() { VariablesReference = 43,  Name = "_genericTypeWithStaticField", EvaluateName = "this._genericTypeWithStaticField", Value = "{DebuggableConsoleApp.GenericTypeWithStaticField<string>}", Type = "DebuggableConsoleApp.GenericTypeWithStaticField<string>" },
            new() { VariablesReference =  0,  Name = "_stringFieldWithNewLine",     EvaluateName = "this._stringFieldWithNewLine",     Value = """ "Test\nValue\\n\"quoted\"\r\t" """.Trim(),               Type = "string" },
            new() { VariablesReference = 44,  Name = "ClassProperty",               EvaluateName = "this.ClassProperty",               Value = "{DebuggableConsoleApp.TestClass}",                          Type = "DebuggableConsoleApp.TestClass" },
            new() { VariablesReference = 45,  Name = "RecordProperty",              EvaluateName = "this.RecordProperty",              Value = "TestRecord { Name = InitProperty, Age = 5 }",               Type = "DebuggableConsoleApp.TestRecord" },
            new() { VariablesReference =  0,  Name = "ComputedProperty",            EvaluateName = "this.ComputedProperty",            Value = "246",                                                       Type = "int" },
			// TODO: Type should be int
			new() { VariablesReference = 46,  Name = "ThrowingProperty",            EvaluateName = "this.ThrowingProperty",            Value = expectedThrowingPropertyValue,                               Type = "System.InvalidOperationException" },
            new() { VariablesReference = 47,  Name = "Static members",              Value = "",                                                          Type = "", PresentationHint = new DapPresentationHint("class") },
        ];
        List<DapVariable>? thisInstanceVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        Assert.Equal(expectedVariables.Count, thisInstanceVariables.Count);
        thisInstanceVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
        await debugProtocolHost.AssertStaticFieldsOnGenericType(thisInstanceVariables.Single(s => s.Name == "_genericTypeWithStaticField").VariablesReference);
        await debugProtocolHost.AssertInstanceThisStaticVariables(thisInstanceVariables.Single(s => s.Name == "Static members").VariablesReference);
        await debugProtocolHost.AssertMultiDimArrayVariables(thisInstanceVariables.Single(s => s.Name == "MultiDimArrayField").VariablesReference);
    }

    private static readonly DapPresentationHint _multiDimArrayRowPresentationHint = new("virtual");
    private static async Task AssertMultiDimArrayVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { VariablesReference =  53, Name = "[0, ...]",    Value = "",    Type = "", PresentationHint = _multiDimArrayRowPresentationHint },
            new() { VariablesReference =  54, Name = "[1, ...]",    Value = "",    Type = "", PresentationHint = _multiDimArrayRowPresentationHint },
        ];
        List<DapVariable>? multiDimArrayVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        Assert.Equal(expectedVariables.Count, multiDimArrayVariables.Count);
        multiDimArrayVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        List<DapVariable> expectedVariables2 =
        [
            new() { VariablesReference =  0, Name = "[0, 0]",    EvaluateName = "this.MultiDimArrayField[0, 0]",    Value = "1",    Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { VariablesReference =  0, Name = "[0, 1]",    EvaluateName = "this.MultiDimArrayField[0, 1]",    Value = "2",    Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { VariablesReference =  0, Name = "[0, 2]",    EvaluateName = "this.MultiDimArrayField[0, 2]",    Value = "3",    Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { VariablesReference =  0, Name = "[0, 3]",    EvaluateName = "this.MultiDimArrayField[0, 3]",    Value = "4",    Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { VariablesReference =  0, Name = "[0, 4]",    EvaluateName = "this.MultiDimArrayField[0, 4]",    Value = "5",    Type = "int", PresentationHint = _arrayElementPresentationHint },
        ];

        List<DapVariable>? multiDimArrayRankVariables = await debugProtocolHost.VariablesAsync(multiDimArrayVariables[0].VariablesReference);
        Assert.Equal(expectedVariables2.Count, multiDimArrayRankVariables.Count);
        multiDimArrayRankVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables2);
    }

    private static async Task AssertStaticFieldsOnGenericType(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable>? classVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        int staticMembersVariablesReference = classVariables.Single().VariablesReference;
        List<DapVariable> expectedVariables =
        [
            new() { VariablesReference =  0, Name = "IntValue",    Value = "4",    Type = "int" },
            new() { VariablesReference =  0, Name = "Value",       Value = "null", Type = "string" },
            new() { VariablesReference =  0, Name = "IntProperty", Value = "5",    Type = "int" },
        ];
        List<DapVariable>? staticFieldsOnGenericType = await debugProtocolHost.VariablesAsync(staticMembersVariablesReference);
        Assert.Equal(expectedVariables.Count, staticFieldsOnGenericType.Count);
        staticFieldsOnGenericType.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }

    private static async Task AssertInstanceThisStaticVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable>? staticMemberVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        List<DapVariable> expectedVariables =
        [
            new() { VariablesReference =  0, Name = "StaticField", Value = "999", Type = "int" },
            new() { VariablesReference = 49, Name = "StaticEnumerableField", Value = "Count = 4", Type = "System.Linq.Enumerable.RangeIterator<int>" },
            new() { VariablesReference =  0, Name = "ConstField",  Value = "\"const\"", Type = "string" },
            new() { VariablesReference =  0, Name = "ConstFieldNullString",  Value = "null", Type = "string" },
            new() { VariablesReference =  0, Name = "ConstFieldBool",  Value = "true", Type = "bool" },
            new() { VariablesReference =  0, Name = "ConstFieldDecimal",  Value = "4.5", Type = "decimal" },
            new() { VariablesReference =  0, Name = "ConstFieldChar",  Value = "68 'D'", Type = "char" },
            new() { VariablesReference =  0, Name = "ConstFieldEnum",  Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum" },
            new() { VariablesReference =  0, Name = "ConstFieldFlagsEnum",  Value = "FlagValue1 | FlagValue3", Type = "DebuggableConsoleApp.MyEnumWithFlags" },
            new() { VariablesReference = 50, Name = "ConstFieldByteArraySpan",  Value = "System.ReadOnlySpan<Byte>[4]", Type = "System.ReadOnlySpan<byte>" },
        ];
        Assert.Equal(expectedVariables.Count, staticMemberVariables.Count);
        staticMemberVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
        await debugProtocolHost.AssertIEnumerableMembers(staticMemberVariables.Single(s => s.Name == "StaticEnumerableField").VariablesReference);
    }

    private static readonly DapPresentationHint _arrayElementPresentationHint = new("data");
    private static async Task AssertIEnumerableMembers(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable>? enumerableMembers = await debugProtocolHost.VariablesAsync(variablesReference);
        List<DapVariable> expectedVariables =
        [
            new() { VariablesReference = 51, Name = "Raw View", Value = "", Type = "", PresentationHint = new DapPresentationHint("class") },
            new() { VariablesReference = 52, Name = "Results", Value = "Expanding will force enumeration of the object", Type = "", PresentationHint = new DapPresentationHint("class") },
        ];
        Assert.Equal(expectedVariables.Count, enumerableMembers.Count);
        enumerableMembers.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        List<DapVariable> expectedVariables2 =
        [
            new() { VariablesReference =  0, Name = "[0]", Value = "1", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { VariablesReference =  0, Name = "[1]", Value = "2", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { VariablesReference =  0, Name = "[2]", Value = "3", Type = "int", PresentationHint = _arrayElementPresentationHint },
            new() { VariablesReference =  0, Name = "[3]", Value = "4", Type = "int", PresentationHint = _arrayElementPresentationHint },
        ];

        List<DapVariable>? enumerableResultsMembers = await debugProtocolHost.VariablesAsync(enumerableMembers.Single(s => s.Name == "Results").VariablesReference);
        Assert.Equal(expectedVariables2.Count, enumerableResultsMembers.Count);
        enumerableResultsMembers.ShouldBeEquivalentToDebuggerVariables(expectedVariables2);
    }

    public static async Task AssertCompositeValueVariables(this DebugSession debugProtocolHost, int variablesReference)
    {
        List<DapVariable> expectedVariables =
        [
            new() { VariablesReference = 55, Name = "NullAndEmptyStrings", EvaluateName = "localCompositeValues.NullAndEmptyStrings", Value = "(1, null, \"\", \"null\")", Type = "System.Tuple<int, string, string, string>" },
            new() { VariablesReference = 56, Name = "EscapedString",       EvaluateName = "localCompositeValues.EscapedString",       Value = "(1, \"a\\n\\\"b\\\\c\")", Type = "System.ValueTuple<int, string>" },
            new() { VariablesReference = 57, Name = "NestedTuple",         EvaluateName = "localCompositeValues.NestedTuple",         Value = "(1, (2, \"nested\"))", Type = "System.ValueTuple<int, System.ValueTuple<int, string>>" },
            new() { VariablesReference = 58, Name = "LongValueTuple",      EvaluateName = "localCompositeValues.LongValueTuple",      Value = "(1, 2, 3, 4, 5, 6, 7, \"eight\", \"nine\")", Type = "System.ValueTuple<int, int, int, int, int, int, int, System.ValueTuple<string, string>>" },
            new() { VariablesReference = 59, Name = "LongTuple",           EvaluateName = "localCompositeValues.LongTuple",           Value = "(1, 2, 3, 4, 5, 6, 7, \"eight\")", Type = "System.Tuple<int, int, int, int, int, int, int, System.Tuple<string>>" },
            new() { VariablesReference = 60, Name = "EmptyTuple",          EvaluateName = "localCompositeValues.EmptyTuple",          Value = "()", Type = "System.ValueTuple" },
            new() { VariablesReference = 61, Name = "Anonymous",           EvaluateName = "localCompositeValues.Anonymous",           Value = "{ Text = \"a\\n\\\"b\\\\c\", Empty = \"\", Missing = null, Nested = (1, \"nested\") }", Type = "<>f__AnonymousType1<string, string, string, System.ValueTuple<int, string>>" },
            new() { VariablesReference = 62, Name = "FailedMember",        EvaluateName = "localCompositeValues.FailedMember",        Value = "(<error: The name 'DoesNotExist' does not exist in the current context>, 2)", Type = "System.Tuple<DebuggableConsoleApp.InvalidDebuggerDisplay, int>", PresentationHint = new DapPresentationHint(Attributes: ["failedEvaluation"]) },
            new() { VariablesReference = 63, Name = "NullableTuple",       EvaluateName = "localCompositeValues.NullableTuple",       Value = "(4, \"nullable\")", Type = "System.ValueTuple<int, string>?" },
            new() { VariablesReference = 0,  Name = "NullTuple",           EvaluateName = "localCompositeValues.NullTuple",           Value = "null", Type = "System.ValueTuple<int, string>?" },
            new() { VariablesReference = 64, Name = "NullableDebuggerDisplay", EvaluateName = "localCompositeValues.NullableDebuggerDisplay", Value = "display: 5", Type = "DebuggableConsoleApp.NullableDebuggerDisplay?" },
            new() { VariablesReference = 65, Name = "NullableToString",    EvaluateName = "localCompositeValues.NullableToString",    Value = "to-string: 6", Type = "DebuggableConsoleApp.NullableToString?" },
        ];
        List<DapVariable>? compositeVariables = await debugProtocolHost.VariablesAsync(variablesReference);
        Assert.Equal(expectedVariables.Count, compositeVariables.Count);
        compositeVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }
}
