using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class BreakpointTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int MyIntSet = Debuggees.Line("DebuggableConsoleApp", "MyClass.cs", "int myInt = 4;");

    [Gate]
    [Fact]
    public async Task A_breakpoint_set_before_its_module_loads_is_told_pending_then_bound_at_its_statement()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "MyClass.cs");
        await debugProtocolHost
            .BreakpointsAsync([MyIntSet], breakpointedFilePath)
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        BreakpointEvent expectedBreakpointEvent1 = new BreakpointEvent
        {
            Reason = "changed",
            Breakpoint = new DapBreakpoint
            {
                Id = 1,
                Message = "Breakpoint has not been processed by the debugger.",
                Verified = false,
                Line = MyIntSet
            }
        };
        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>();
        Assert.Equivalent(expectedBreakpointEvent1, breakpointEvent, strict: true);

        BreakpointEvent expectedBreakpointEvent2 = new BreakpointEvent
        {
            Reason = "changed",
            Breakpoint = new DapBreakpoint
            {
                Id = 1,
                Verified = true,
                Line = MyIntSet,
                Column = 3,
                EndLine = MyIntSet,
                EndColumn = 17,
                Offset = 0,
                Source = new DapSource { Path = breakpointedFilePath, Name = "MyClass.cs", SourceReference = 0 }
            }
        };

        BreakpointEvent breakpointEvent2 = await debugProtocolHost.WaitForEvent<BreakpointEvent>();
        Assert.Equivalent(expectedBreakpointEvent2, breakpointEvent2, strict: true);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(MyIntSet, stopInfo.line);
    }

    [Gate]
    [Fact]
    public async Task A_breakpoint_given_a_column_stops_at_the_statement_at_that_column()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ColumnBreakpointClass.cs");
        int line = Debuggees.Line("DebuggableConsoleApp", "ColumnBreakpointClass.cs", "column-breakpoint-line");
        int column = GetColumnNumber(breakpointedFilePath, line, "int third");
        int endColumn = column + "int third = second + 1;".Length;

        await debugProtocolHost
            .BreakpointsAsync(breakpointedFilePath, [new DapSourceBreakpoint(line, Column: column)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified);
        Assert.Equal(line, breakpointEvent.Breakpoint.Line);
        Assert.Equal(column, breakpointEvent.Breakpoint.Column);
        Assert.Equal(endColumn, breakpointEvent.Breakpoint.EndColumn);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();

        Assert.EndsWith("ColumnBreakpointClass.cs", topFrame.Source?.Path);
        Assert.Equal(line, topFrame.Line);
        Assert.Equal(column, topFrame.Column);
        Assert.Equal(endColumn, topFrame.EndColumn);
    }

    [Gate]
    [Fact]
    public async Task A_breakpoint_on_a_line_of_several_statements_stops_at_the_first()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ColumnBreakpointClass.cs");
        int line = Debuggees.Line("DebuggableConsoleApp", "ColumnBreakpointClass.cs", "column-breakpoint-line");
        int expectedColumn = GetColumnNumber(breakpointedFilePath, line, "int first");

        await debugProtocolHost
            .BreakpointsAsync(breakpointedFilePath, [new DapSourceBreakpoint(line)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();

        Assert.EndsWith("ColumnBreakpointClass.cs", topFrame.Source?.Path);
        Assert.Equal(line, topFrame.Line);
        Assert.Equal(expectedColumn, topFrame.Column);
    }

    [Gate]
    [Fact]
    public async Task A_breakpoint_inside_a_statement_of_several_lines_stops_at_its_start()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ColumnBreakpointClass.cs");
        int statementStartLine = Debuggees.Line("DebuggableConsoleApp", "ColumnBreakpointClass.cs", "int multi =");
        int requestedLine = statementStartLine + 1;
        int requestedColumn = GetColumnNumber(breakpointedFilePath, requestedLine, "1 + 25");
        int expectedColumn = GetColumnNumber(breakpointedFilePath, statementStartLine, "int multi");

        await debugProtocolHost
            .BreakpointsAsync(breakpointedFilePath, [new DapSourceBreakpoint(requestedLine, Column: requestedColumn)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified);
        Assert.Equal(statementStartLine, breakpointEvent.Breakpoint.Line);
        Assert.Equal(expectedColumn, breakpointEvent.Breakpoint.Column);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();

        Assert.EndsWith("ColumnBreakpointClass.cs", topFrame.Source?.Path);
        Assert.Equal(statementStartLine, topFrame.Line);
        Assert.Equal(expectedColumn, topFrame.Column);
    }

    [Gate]
    [Fact]
    public async Task A_breakpoint_set_once_its_symbols_have_loaded_is_answered_with_where_it_bound()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ColumnBreakpointClass.cs");
        int line = Debuggees.Line("DebuggableConsoleApp", "ColumnBreakpointClass.cs", "column-breakpoint-line");

        await debugProtocolHost
            .BreakpointsAsync(breakpointedFilePath, [new DapSourceBreakpoint(line)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        await debugProtocolHost.WaitForStoppedEvent();

        int column = GetColumnNumber(breakpointedFilePath, line, "int second");
        int endColumn = column + "int second = first + 1;".Length;

        SetBreakpointsResponse? response = await debugProtocolHost.SetBreakpointsAsync(breakpointedFilePath, [new DapSourceBreakpoint(line, Column: column)]);

        DapBreakpoint breakpoint = response.Breakpoints.Single();

        Assert.True(breakpoint.Verified);
        Assert.Equal(line, breakpoint.Line);
        Assert.Equal(column, breakpoint.Column);
        Assert.Equal(line, breakpoint.EndLine);
        Assert.Equal(endColumn, breakpoint.EndColumn);
    }

    [Gate]
    [Fact]
    public async Task A_breakpoint_in_a_file_named_as_the_programs_in_another_folder_binds_to_none_of_it()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        // MyClass.cs is in the project's root; SomeFolder holds other files, none of them this one.
        string wrongFolder = Debuggees.Source("DebuggableConsoleApp", "SomeFolder", "MyClass.cs");
        SetBreakpointsResponse wrong = await debugProtocolHost.RequestAsync<SetBreakpointsResponse>(DapCommands.SetBreakpoints,
            new SetBreakpointsArguments(new DapSource { Path = wrongFolder }, [new DapSourceBreakpoint(Debuggees.MyMethodEnd)]));
        string dotted = Debuggees.Source("DebuggableConsoleApp", "Namespace1", "..", "MyClass.cs");
        SetBreakpointsResponse right = await debugProtocolHost.SetBreakpointsAsync(dotted, [new DapSourceBreakpoint(Debuggees.MyMethodEnd)]);
        await debugProtocolHost
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), Path.GetFullPath(stoppedEvent.ReadStopInfo().filePath));
        Assert.Equal(Debuggees.MyMethodEnd, stoppedEvent.ReadStopInfo().line);
        Assert.Equal([right.Breakpoints.Single().Id!.Value], stoppedEvent.HitBreakpointIds);
        Assert.False(wrong.Breakpoints.Single().Verified);

        // With the program's symbols loaded, the other folder's name still binds to nothing.
        SetBreakpointsResponse again = await debugProtocolHost.RequestAsync<SetBreakpointsResponse>(DapCommands.SetBreakpoints,
            new SetBreakpointsArguments(new DapSource { Path = wrongFolder }, [new DapSourceBreakpoint(Debuggees.MyMethodEnd)]));
        Assert.False(again.Breakpoints.Single().Verified);
    }

    private static int GetColumnNumber(string filePath, int lineNumber, string marker)
    {
        string line = File.ReadLines(filePath).ElementAt(lineNumber - 1);
        int index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"Marker '{marker}' not found on line {lineNumber} in {filePath}");
        }

        return index + 1;
    }
}
