using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

// Its stops read the exception's text by an evaluation the engine holds to 5 s, which a loaded machine can outrun.
[Collection(Alone.Name)]
public class ExceptionTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int LoopHead = Debuggees.Line("DebuggableConsoleApp", "myLambdaClass.Test();");
    private static readonly int ExceptionsCalled = Debuggees.Line("DebuggableConsoleApp", "Exceptions.Test(exceptionToThrow);");
    private static readonly int AfterThrow = ExceptionsCalled + 1;
    private static readonly int Thrown = Debuggees.Line("DebuggableConsoleApp", "Exceptions.cs", "throw new InvalidOperationException(\"Test exception\");");
    private static readonly int Parsed = Debuggees.Line("DebuggableConsoleApp", "Exceptions.cs", "int myInt = int.Parse(\"x\");");
    private static readonly int Caught = Debuggees.Line("DebuggableConsoleApp", "Exceptions.cs", "catch (Exception e)");

    [Fact]
    public async Task At_an_exceptions_stop_the_exception_is_listed_with_the_locals_and_evaluates()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost.Request(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], [new(DapExceptionFilters.All), new(DapExceptionFilters.UserUnhandled)]));
        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "Exceptions.cs");
        debugProtocolHost
            .WithBreakpointsRequest([LoopHead], Debuggees.Source("DebuggableConsoleApp", "Program.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("Program.cs", stopInfo.filePath);
        Assert.Equal(LoopHead, stopInfo.line);

        // set 'ExceptionToThrow' to .Normal - we do not want other tests to stop at the 'exception' stop event, only this one
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "exceptionToThrow = ExceptionToThrow.Normal", out EvaluateResponse? evaluateResponse);
        Assert.Equal("Normal", evaluateResponse.Result);

        debugProtocolHost.WithContinueRequest();

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("Exceptions.cs", stopInfo2.filePath);
        Assert.Equal(Thrown, stopInfo2.line); // Where the exception is thrown

        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2)
            .WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out ScopesResponse? scopesResponse2);

        Assert.Single(scopesResponse2.Scopes);
        DapScope scope = scopesResponse2.Scopes.Single();

        List<DapVariable> expectedVariables =
        [
            new() { Name = "$exception",  EvaluateName = "$exception",  Value = $"System.InvalidOperationException: Test exception{Environment.NewLine}   at DebuggableConsoleApp.Exceptions.Test(ExceptionToThrow exceptionToThrow) in {breakpointedFilePath}:line {Thrown}", Type = "System.InvalidOperationException", VariablesReference = 2 },
            new() { Name = "exceptionToThrow", EvaluateName = "exceptionToThrow", Value = "Normal",  Type = "DebuggableConsoleApp.ExceptionToThrow", VariablesReference = 3 },
        ];
        debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out List<DapVariable>? variables);

        Assert.Equal(expectedVariables.Count, variables.Count);
        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        debugProtocolHost.WithEvaluateRequest(stackTraceResponse2.StackFrames.First().Id, "$exception", out EvaluateResponse? evaluateResponse2);
        Assert.Equal(expectedVariables[0].Value, evaluateResponse2.Result);

        ExceptionInfoResponse expectedExceptionInfoResponse = new ExceptionInfoResponse
        {
            ExceptionId = "CLR/System.InvalidOperationException",
            Description = "Exception thrown: 'System.InvalidOperationException' in DebuggableConsoleApp.dll: 'Test exception'",
            BreakMode = "always",
            Code = 0,
            Details = new DapExceptionDetails
            {
                Message = "Test exception",
                TypeName = "InvalidOperationException",
                FullTypeName = "System.InvalidOperationException",
                EvaluateName = "$exception",
                StackTrace = $"   at DebuggableConsoleApp.Exceptions.Test(ExceptionToThrow exceptionToThrow) in {breakpointedFilePath}:line {Thrown}",
                InnerException = [],
                FormattedDescription = "**System.InvalidOperationException:** 'Test exception'",
                HResult = -2146233079,
                Source = "DebuggableConsoleApp"
            }
        };

        ExceptionInfoResponse exceptionInfoResponse = debugProtocolHost.Request<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(stoppedEvent2.ThreadId.Value));
        Assert.Equivalent(expectedExceptionInfoResponse, exceptionInfoResponse, strict: true);
    }

    [Fact]
    public async Task An_exception_thrown_in_code_not_the_programs_own_stops_with_no_source_under_Just_My_Code()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id, true)
            .WaitForInitializedEvent();
        debugProtocolHost.Request(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], [new(DapExceptionFilters.All), new(DapExceptionFilters.UserUnhandled)]));
        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "Exceptions.cs");
        debugProtocolHost
            .WithBreakpointsRequest([LoopHead], Debuggees.Source("DebuggableConsoleApp", "Program.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("Program.cs", stopInfo.filePath);
        Assert.Equal(LoopHead, stopInfo.line);

        // set 'ExceptionToThrow' to .ExternalCode - we do not want other tests to stop at the 'exception' stop event, only this one
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "exceptionToThrow = ExceptionToThrow.ExternalCode", out EvaluateResponse? evaluateResponse);
        Assert.Equal("ExternalCode", evaluateResponse.Result);

        debugProtocolHost.WithContinueRequest();

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Null(stoppedEvent2.Source);

        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2, null)
            .WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out ScopesResponse? scopesResponse2);

        // Numbered on from the frame the first stop gave.
        List<DapStackFrame> expectedStackFrames =
        [
            new() { Id = 2, Column = 0, EndColumn =  null, Line =  0, EndLine =  null, Name = "System.Private.CoreLib.dll!System.Number.ThrowFormatException<char>(System.ReadOnlySpan<char> value)", Source = null, PresentationHint = "subtle" },
            new() { Id = 3, Column = 0, EndColumn =  null, Line =  0, EndLine =  null, Name = "System.Private.CoreLib.dll!System.Int32.Parse(string s)",                 Source = null, PresentationHint = "subtle" },
            new() { Id = 4, Column = 5, EndColumn = 32,    Line = Parsed, EndLine = Parsed, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Exceptions.Test(DebuggableConsoleApp.ExceptionToThrow exceptionToThrow)", Source = new DapSource { Name = "Exceptions.cs", SourceReference = 0, Path = breakpointedFilePath } },
            new() { Id = 5, Column = 4, EndColumn = 38,    Line = ExceptionsCalled, EndLine = ExceptionsCalled, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args)",    Source = new DapSource { Name = "Program.cs",    SourceReference = 0, Path = Debuggees.Source("DebuggableConsoleApp", "Program.cs") } },
        ];

        Assert.Equal(expectedStackFrames, stackTraceResponse2.StackFrames.WithoutResolution());
        Assert.Single(scopesResponse2.Scopes);
        DapScope scope = scopesResponse2.Scopes.Single();

        string expectedStackTrace = $"   at System.Number.ThrowFormatException[TChar](ReadOnlySpan`1 value){Environment.NewLine}   at System.Int32.Parse(String s){Environment.NewLine}   at DebuggableConsoleApp.Exceptions.Test(ExceptionToThrow exceptionToThrow) in {breakpointedFilePath}:line {Parsed}";
        List<DapVariable> expectedVariables =
        [
            new() { Name = "$exception",  EvaluateName = "$exception",  Value = $"System.FormatException: The input string 'x' was not in a correct format.{Environment.NewLine}{expectedStackTrace}", Type = "System.FormatException", VariablesReference = 2 }
        ];
        debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out List<DapVariable>? variables);

        Assert.Equal(expectedVariables.Count, variables.Count);
        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        debugProtocolHost.WithEvaluateRequest(stackTraceResponse2.StackFrames.First().Id, "$exception", out EvaluateResponse? evaluateResponse2);
        Assert.Equal(expectedVariables[0].Value, evaluateResponse2.Result);

        ExceptionInfoResponse expectedExceptionInfoResponse = new ExceptionInfoResponse
        {
            ExceptionId = "CLR/System.FormatException",
            Description = "Exception thrown: 'System.FormatException' in System.Private.CoreLib.dll: 'The input string 'x' was not in a correct format.'",
            BreakMode = "always",
            Code = 0,
            Details = new DapExceptionDetails
            {
                Message = "The input string 'x' was not in a correct format.",
                TypeName = "FormatException",
                FullTypeName = "System.FormatException",
                EvaluateName = "$exception",
                StackTrace = expectedStackTrace,
                InnerException = [],
                FormattedDescription = "**System.FormatException:** 'The input string 'x' was not in a correct format.'",
                HResult = -2146233033,
                Source = "System.Private.CoreLib"
            }
        };

        ExceptionInfoResponse exceptionInfoResponse = debugProtocolHost.Request<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(stoppedEvent2.ThreadId.Value));
        Assert.Equivalent(expectedExceptionInfoResponse, exceptionInfoResponse, strict: true);

        // Now we should land on the catch block
        StoppedEvent stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent2.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo3 = stoppedEvent3.ReadStopInfo();
        Assert.Equal((breakpointedFilePath, Caught, 3), stopInfo3);
    }

    /// <summary>Each exception filter and condition in turn, in one program with Just My Code on: from the loop's head
    /// (no filter set while the loop came round) the filter and the exception to throw are set, and the
    /// program stops at the exception with its type and break mode, or goes on to the line after the throw without.</summary>
    [Fact]
    public async Task Each_exception_filter_and_condition_stops_at_the_exceptions_it_names_and_at_no_other()
    {
        using DebugSession host = await AttachedAfterTheThrow(justMyCode: true);
        SetExceptionBreakpointsArguments all = new([DapExceptionFilters.All], []);
        SetExceptionBreakpointsArguments userUnhandled = new([DapExceptionFilters.UserUnhandled], []);
        (string Type, string BreakMode) thrown = ("System.InvalidOperationException", "always");
        (string Type, string BreakMode) unhandled = ("System.InvalidOperationException", "userUnhandled");
        await Round(host, "all exceptions", all, "Normal", thrown);
        await Round(host, "all exceptions, one handled within external code", all, "HandledWithinExternalCode", null);
        await Round(host, "all exceptions, one thrown in external code into the user's", all, "ExternalCode", ("System.FormatException", "always"));
        await Round(host, "user-unhandled exceptions", userUnhandled, "UserUnhandled", unhandled);
        await Round(host, "user-unhandled exceptions, one the user's code handles", userUnhandled, "Normal", null);
        await Round(host, "user-unhandled of its type", Unhandled("System.InvalidOperationException"), "UserUnhandled", unhandled);
        await Round(host, "user-unhandled of another type", Unhandled("System.FormatException"), "UserUnhandled", null);
        await Round(host, "no filter", new SetExceptionBreakpointsArguments([], []), "Normal", null);
        await Round(host, "all of its type", All("System.InvalidOperationException"), "Normal", thrown);
        await Round(host, "all of another type", All("System.FormatException"), "Normal", null);
        await Round(host, "all but another type", All("!System.FormatException"), "Normal", thrown);
        await Round(host, "all but its type", All("!System.InvalidOperationException"), "Normal", null);
        await Round(host, "all of two types, its among them", All("System.FormatException,System.InvalidOperationException"), "Normal", thrown);
        await Round(host, "all but one type and of another", All("!System.FormatException,System.ArgumentException"), "Normal", thrown);
        await Round(host, "all of two types, spaced", All("System.FormatException, System.InvalidOperationException"), "Normal", thrown);

        static SetExceptionBreakpointsArguments Unhandled(string condition) => new([], [new DapExceptionFilterOptions(DapExceptionFilters.UserUnhandled, condition)]);
        static SetExceptionBreakpointsArguments All(string condition) => new([], [new DapExceptionFilterOptions(DapExceptionFilters.All, condition)]);
    }

    /// <summary>With Just My Code off, the filter for all exceptions stops at one thrown and handled within external code.</summary>
    [Fact]
    public async Task With_just_my_code_off_all_exceptions_stop_at_one_handled_within_external_code()
    {
        using DebugSession host = await AttachedAfterTheThrow(justMyCode: false);
        await Round(host, "all exceptions without Just My Code", new SetExceptionBreakpointsArguments([DapExceptionFilters.All], []), "HandledWithinExternalCode",
            ("System.ArgumentException", "always"));
    }

    private static string ProgramPath => Debuggees.Source("DebuggableConsoleApp", "Program.cs");

    /// <summary>DebuggableConsoleApp attached to as it starts, stopped at the line after its loop's throw with no
    /// exception filter set.</summary>
    private async Task<DebugSession> AttachedAfterTheThrow(bool justMyCode)
    {
        const bool startSuspended = true;
        DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process process);
        try
        {
            await host
                .WithInitializeRequest()
                .WithAttachRequest(process.Id, justMyCode)
                .WaitForInitializedEvent();
            host.WithBreakpointsRequest([AfterThrow], ProgramPath)
                .WithConfigurationDoneRequest()
                .WithOptionalResumeRuntime(process.Id, startSuspended);
            StoppedEvent first = await host.WaitForStoppedEvent();
            Assert.Equal((StopReasons.Breakpoint, AfterThrow), (first.Reason, first.ReadStopInfo().line));
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>One turn of DebuggableConsoleApp's loop from a stop after the throw: no filter while the loop
    /// comes round to its head, there the filters given and the exception chosen, then the stop at the exception with its type
    /// and break mode (or none) and the stop after the throw again. A failure names the round.</summary>
    private static async Task Round(DebugSession host, string round, SetExceptionBreakpointsArguments filters, string exceptionToThrow,
        (string Type, string BreakMode)? expected)
    {
        host.Request(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], []));
        host.WithBreakpointsRequest([LoopHead], ProgramPath).WithContinueRequest();
        StoppedEvent head = await host.WaitForStoppedEvent();
        Assert.True(head.Reason == StopReasons.Breakpoint && head.ReadStopInfo().line == LoopHead,
            $"{round}: the loop did not come round to its head with no filter set, but stopped at the {head.Reason} at line {head.Line}");

        host.Request(DapCommands.SetExceptionBreakpoints, filters);
        host.WithEvaluateRequest(host.GetTopStackFrame(head.ThreadId!.Value).Id, $"exceptionToThrow = ExceptionToThrow.{exceptionToThrow}", out EvaluateResponse chosen);
        Assert.Equal(exceptionToThrow, chosen.Result);
        host.WithBreakpointsRequest([AfterThrow], ProgramPath).WithContinueRequest();

        StoppedEvent stop = await host.WaitForStoppedEvent();
        if (expected is { } exception)
        {
            Assert.True(stop.Reason == StopReasons.Exception,
                $"{round}: the {exception.Type} thrown did not stop the program, which went on to the {stop.Reason} at line {stop.Line}");
            ExceptionInfoResponse info = host.Request<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(stop.ThreadId!.Value));
            Assert.True((info.Details?.FullTypeName, info.BreakMode) == exception,
                $"{round}: the program stopped at a {info.Details?.FullTypeName} ({info.BreakMode}), where a {exception.Type} ({exception.BreakMode}) was thrown");
            stop = await host.WithContinueRequest().WaitForStoppedEvent();
        }
        Assert.True(stop.Reason == StopReasons.Breakpoint && stop.ReadStopInfo().line == AfterThrow,
            $"{round}: the program stopped at the {stop.Reason} at line {stop.Line}, where it was to go on to the line after the throw ({AfterThrow})");
    }
}
