using Passenger.Dap;
using Passenger.Debugger;
using Passenger.Debugger.Models;
using Passenger.Debugger.Models.Response;
using Passenger.Debugger.PresentationHintModels;

namespace Passenger.DebugAdapter;

/// <summary>The engine behind the Debug Adapter Protocol. Each request runs under the engine's lock,
/// after the runtime events queued before it, so a request never sees the debuggee half way through
/// an event; each engine event goes out as the protocol's. Lines and columns are the client's choice
/// of base, the engine's own counting from one.</summary>
public sealed class DebugAdapter
{
    private readonly ManagedDebugger debugger;
    private readonly DapConnection connection;
    private readonly Action<string>? log;
    private bool linesStartAt1 = true;
    private int? stoppedThread;
    private bool columnsStartAt1 = true;
    private int shutdownRequested;

    /// <summary>Whether the client said at initialize that it starts programs in its terminal.</summary>
    private bool runsInTerminal;

    /// <summary>The longest the client may take to start a program in its terminal.</summary>
    private static readonly TimeSpan RunInTerminalLimit = TimeSpan.FromSeconds(30);

    /// <summary>The engine's lock as configurationDone leaves it, held until its answer has been written.</summary>
    private IDisposable? configuring;

    /// <summary>The client went without disconnecting (<see cref="AbortAsync"/>): no answer of configurationDone's will be written.</summary>
    private bool aborted;

    /// <summary>The adapter over a connection; <paramref name="symbols"/> keeps the symbols decompiled for assemblies that
    /// ship none, without which their frames have no source.</summary>
    public DebugAdapter(DapConnection connection, Action<string>? log = null, Passenger.Debugger.Decompilation.SymbolCache? symbols = null)
    {
        this.connection = connection;
        this.log = log;
        debugger = new ManagedDebugger(log, symbols);
        Subscribe();
        Register();
    }

    /// <summary>Raised once a disconnect has been answered: the session is over and the process may end.
    /// A terminate is not the end, since a client may still disconnect after it. Raised on any thread.</summary>
    public event Action? ShutdownRequested;

    /// <summary>Lets the debuggee go when the client went away without disconnecting, under the engine's lock as a disconnect
    /// is, so the event loop is not amid one of the program's events. Does nothing after a disconnect, which let it go
    /// already. A request still holding the lock after 10 s (one that never ends) is not waited for longer.</summary>
    public async Task AbortAsync()
    {
        // The client that went may have gone before configurationDone's answer could be written.
        Volatile.Write(ref aborted, true);
        debugger.CancelStartup();
        LetEventsGo();
        try
        {
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(10));
            IDisposable? held = null;
            try
            {
                held = await debugger.DapRequestAndRuntimeEventLock.LockAsync(limit.Token);
                await debugger.DrainRuntimeEventQueue();
            }
            catch (OperationCanceledException)
            {
                log?.Invoke("Abort: a request still holds the engine after 10 s; the program is let go without waiting for it");
            }
            using (held)
            {
                debugger.Disconnect(true);
            }
        }
        catch (Exception error)
        {
            log?.Invoke($"Abort: {error}");
        }
    }

    private void Register()
    {
        connection.Handle<InitializeArguments, DapCapabilities>("initialize", Initialize, then: () => connection.SendEvent("initialized"));
        connection.Handle<LaunchArguments>("launch", Launch);
        connection.Handle<AttachArguments>("attach", Attach);
        connection.Handle<DapEmpty>("configurationDone", ConfigurationDone, then: LetEventsGo);
        connection.Handle<SetBreakpointsArguments, SetBreakpointsResponse>("setBreakpoints", SetBreakpoints);
        connection.Handle<SetFunctionBreakpointsArguments, SetBreakpointsResponse>("setFunctionBreakpoints", SetFunctionBreakpoints);
        connection.Handle<SetExceptionBreakpointsArguments, SetExceptionBreakpointsResponse>("setExceptionBreakpoints", SetExceptionBreakpoints);
        connection.Handle<DataBreakpointInfoArguments, DataBreakpointInfoResponse>("dataBreakpointInfo", async arguments =>
        {
            DataBreakpointAnswer answer = await Locked(() => debugger.DataBreakpointInfo(arguments.VariablesReference, arguments.Name));
            return new DataBreakpointInfoResponse
            {
                DataId = answer.DataId,
                Description = answer.Description,
                AccessTypes = answer.DataId is null ? null : ["write"],
                CanPersist = false,
            };
        });
        connection.Handle<SetDataBreakpointsArguments, SetDataBreakpointsResponse>("setDataBreakpoints", SetDataBreakpoints);
        connection.Handle<DapEmpty, ThreadsResponse>("threads", async _ =>
        {
            List<(int id, string name)> threads = await Locked(() => debugger.GetThreads());
            return new ThreadsResponse([.. threads.Select(t => new DapThread(t.id, t.name))]);
        });
        connection.Handle<StackTraceArguments, StackTraceResponse>("stackTrace", async arguments =>
        {
            List<StackFrameInfo> frames = await Locked(() => debugger.GetStackTrace(arguments.ThreadId, arguments.StartFrame ?? 0, arguments.Levels));
            return new StackTraceResponse([.. frames.Select(ToProtocol)]);
        });
        // The frame's assembly is decompiled between two holds of the engine's lock, never under one (CoreLib's takes many
        // seconds), so a request meanwhile, a disconnect among them, is answered; the second fails if the program ran on.
        connection.Handle<ResolveStackFrameArguments, ResolveStackFrameResponse>("resolveStackFrame", async arguments =>
        {
            if (await Locked(() => debugger.SymbolsWantedAt(arguments.StackFrameId)) is { } wanted)
            {
                await debugger.DecompileAsync(wanted).ConfigureAwait(false);
            }
            return new ResolveStackFrameResponse(ToProtocol(await Locked(() => debugger.ResolveStackFrame(arguments.StackFrameId))));
        });
        connection.Handle<ScopesArguments, ScopesResponse>("scopes", async arguments =>
        {
            List<ScopeInfo> scopes = await Locked(() => debugger.GetScopes(arguments.FrameId));
            return new ScopesResponse([.. scopes.Select(s => new DapScope(s.Name, s.VariablesReference, s.Expensive))]);
        });
        connection.Handle<VariablesArguments, VariablesResponse>("variables", async arguments =>
        {
            List<VariableInfo> variables = await Locked(() => debugger.GetVariables(arguments.VariablesReference));
            return new VariablesResponse([.. variables.Select(v => new DapVariable
            {
                Name = v.Name,
                EvaluateName = v.EvaluateName ?? v.Name,
                Value = v.Value,
                Type = v.Type,
                PresentationHint = ToProtocol(v.PresentationHint),
                VariablesReference = v.VariablesReference,
            })]);
        });
        connection.Handle<EvaluateArguments, EvaluateResponse>("evaluate", async arguments =>
        {
            // A hover, or a value the client shows of itself, is evaluated as the Variables view's getters are.
            VariableInfo value = await Locked(() => debugger.Evaluate(arguments.Expression, arguments.FrameId, implicitly: arguments.Context is "hover" or "variables"));
            return new EvaluateResponse
            {
                Result = value.Value,
                Type = value.Type,
                VariablesReference = value.VariablesReference,
                PresentationHint = ToProtocol(value.PresentationHint),
            };
        });
        // Set Next Statement: a target per line asked of, in the method the last stopped thread is in; its id is the
        // IL offset, which goto hands back.
        connection.Handle<GotoTargetsArguments, GotoTargetsResponse>("gotoTargets", async arguments =>
        {
            if (stoppedThread is not int thread || arguments.Source.Path is not { } path)
            {
                return new GotoTargetsResponse();
            }
            (int Line, int Column, int ILOffset)? target = await Locked(() => debugger.GotoTarget(thread, path, LineIn(arguments.Line)));
            return target is { } found
                ? new GotoTargetsResponse { Targets = [new GotoTarget(found.ILOffset, $"Line {found.Line}", LineOut(found.Line), ColumnOut(found.Column))] }
                : new GotoTargetsResponse();
        });
        connection.Handle<GotoArguments, object>("goto", async arguments =>
        {
            await Locked(() =>
            {
                debugger.SetNextStatement(arguments.ThreadId, arguments.TargetId);
                return 0;
            });
            return new object();
        });
        connection.Handle<SetExpressionArguments, SetExpressionResponse>("setExpression", async arguments =>
        {
            // The expression compiler compiles an assignment as it does any expression, and the interpreter writes it
            // through to the frame, the object or the array; the value is then read back as it now is.
            VariableInfo assigned = await Locked(() => debugger.Evaluate($"{arguments.Expression} = {arguments.Value}", arguments.FrameId));
            if (assigned.PresentationHint?.Attributes == AttributesValue.FailedEvaluation)
            {
                throw new InvalidOperationException(assigned.Value.StartsWith("error: ", StringComparison.Ordinal) ? assigned.Value["error: ".Length..] : assigned.Value);
            }
            VariableInfo now = await Locked(() => debugger.Evaluate(arguments.Expression, arguments.FrameId));
            return new SetExpressionResponse { Value = now.Value, Type = now.Type, VariablesReference = now.VariablesReference };
        });
        connection.Handle<ThreadArguments, ContinueResponse>("continue", async _ =>
        {
            await Locked(() => debugger.Continue());
            return new ContinueResponse(AllThreadsContinued: true);
        });
        connection.Handle<ThreadArguments>("next", arguments => Locked(() => debugger.Step(arguments.ThreadId, StepType.StepOver)));
        connection.Handle<StepInArguments>("stepIn", arguments => Locked(() => arguments.TargetId is int target
            ? debugger.StepIntoTarget(arguments.ThreadId, target)
            : debugger.Step(arguments.ThreadId, StepType.StepIn)));
        // Smart Step Into: the calls of the frame's statement, each placed where it is written.
        connection.Handle<StepInTargetsArguments, StepInTargetsResponse>("stepInTargets", async arguments =>
        {
            List<StepInTargetInfo> targets = await Locked(() => debugger.StepInTargets(arguments.FrameId));
            return new StepInTargetsResponse
            {
                Targets = [.. targets.Select(t => new StepInTarget
                {
                    Id = t.Id,
                    Label = t.Label,
                    Line = t.Line is int line ? LineOut(line) : null,
                    Column = t.Column is int column ? ColumnOut(column) : null,
                    EndLine = t.EndLine is int endLine ? LineOut(endLine) : null,
                    EndColumn = t.EndColumn is int endColumn ? ColumnOut(endColumn) : null,
                })],
            };
        });
        // Hot Reload: what the language's Edit and Continue needs of the program, and its updates applied.
        connection.Handle<DapEmpty, HotReloadStateResponse>("hotReloadState", async _ => await Locked(() =>
        {
            (IReadOnlyList<string>? capabilities, string? unknown) = debugger.HotReloadCapabilities();
            return new HotReloadStateResponse
            {
                Capabilities = capabilities,
                CapabilitiesUnknown = unknown,
                Modules = [.. debugger.HotReloadModules().Select(m => new DapHotReloadModule(m.Mvid, m.Name, m.Path, m.Editable, m.WhyNot))],
                ActiveStatements = [.. debugger.ActiveStatements().Select(s => new DapActiveStatement(s.Module, s.Token, s.Version, s.ILOffset, s.Document,
                    s.StartLine, s.StartColumn, s.EndLine, s.EndColumn, s.Flags))],
            };
        }));
        connection.Handle<ApplyChangesArguments, ApplyChangesResponse>("applyChanges", async arguments =>
        {
            List<ModuleUpdate> updates = [.. arguments.Updates.Select(u => new ModuleUpdate(u.Module, u.Name, u.Metadata, u.IL, u.Pdb, u.UpdatedMethods, u.UpdatedTypes,
                [.. u.Lines.Select(l => (l.Document, (IReadOnlyList<(int, int)>)[.. l.Updates.Select(x => (x.OldLine, x.NewLine))]))],
                [.. u.ActiveStatements.Select(a => new StatementUpdate(a.Token, a.Version, a.ILOffset, a.StartLine, a.StartColumn, a.EndLine, a.EndColumn))]))];
            IReadOnlyList<ModuleApplied> applied = await Locked(() => debugger.ApplyChanges(updates));
            return new ApplyChangesResponse([.. applied.Select(a => new DapModuleApplied(a.Module, a.Name, a.Applied, a.Message))]);
        });
        connection.Handle<ThreadArguments>("stepOut", arguments => Locked(() => debugger.Step(arguments.ThreadId, StepType.StepOut)));
        connection.Handle<ThreadArguments>("pause", _ => Locked(() => debugger.Pause()));
        connection.Handle<DisconnectArguments>("disconnect", arguments =>
        {
            debugger.CancelStartup();
            return Locked(() => debugger.Disconnect(arguments.TerminateDebuggee ?? false));
        }, then: RequestShutdown);
        connection.Handle<TerminateArguments>("terminate", _ =>
        {
            debugger.CancelStartup();
            return Locked(() => debugger.Terminate());
        });
        connection.Handle<ThreadArguments, ExceptionInfoResponse>("exceptionInfo", ExceptionInfo);
        // Not under the engine's lock: a program waiting for input may be running, and the write is all.
        connection.Handle<InputArguments>("input", arguments =>
        {
            debugger.WriteInput(arguments.Text, arguments.Close ?? false);
            return Task.CompletedTask;
        });
    }

    private void Subscribe()
    {
        debugger.OnStopped += stop =>
        {
            stoppedThread = stop.ThreadId;
            connection.SendEvent("stopped", new StoppedEvent
            {
                Reason = StopReason(stop.Reason),
                Text = stop.Text,
                ThreadId = stop.ThreadId,
                AllThreadsStopped = true,
                HitBreakpointIds = stop.HitBreakpointIds,
                Source = stop.FilePath is { } path ? new DapSource { Path = path } : null,
                Line = stop.FilePath is null ? null : LineOut(stop.Line),
                Column = stop.FilePath is null ? null : ColumnOut(stop.Column),
            });
        };
        debugger.OnBreakpointChanged += breakpoint => connection.SendEvent("breakpoint", new BreakpointEvent { Reason = "changed", Breakpoint = ToProtocol(breakpoint, isEvent: true) });
        // Told only when known: a process attached to on macOS or Linux tells its exit code to its parent alone.
        debugger.OnExited += exitCode =>
        {
            if (exitCode is int code)
            {
                connection.SendEvent("exited", new ExitedEvent(code));
            }
        };
        debugger.OnTerminated += () => connection.SendEvent("terminated", new TerminatedEvent());
        debugger.OnThreadStarted += threadId => connection.SendEvent("thread", new ThreadEvent("started", threadId));
        debugger.OnThreadExited += threadId => connection.SendEvent("thread", new ThreadEvent("exited", threadId));
        debugger.OnModuleLoaded += (id, name, path) => connection.SendEvent("module", new ModuleEvent("new", new DapModule(id, name, path)));
        debugger.OnModuleUnloaded += (id, name, path) => connection.SendEvent("module", new ModuleEvent("removed", new DapModule(id, name, path)));
        debugger.OnProcessStarted += (processId, name) => connection.SendEvent("process", new ProcessEvent(name, processId, IsLocalProcess: true, StartMethod: "launch"));
        debugger.OnAttached += processId => connection.SendEvent("process", new ProcessEvent($"process {processId}", processId, IsLocalProcess: true, StartMethod: "attach"));
        debugger.OnOutput += (output, isStdError) => connection.SendEvent("output", new OutputEvent(output, isStdError ? "stderr" : "stdout"));
        debugger.OnConsole += message => connection.SendEvent("output", new OutputEvent(message, "console"));
        debugger.SendRunInTerminalRequest += RunInTerminal;
    }

    private Task<DapCapabilities> Initialize(InitializeArguments arguments)
    {
        linesStartAt1 = arguments.LinesStartAt1 ?? true;
        columnsStartAt1 = arguments.ColumnsStartAt1 ?? true;
        runsInTerminal = arguments.SupportsRunInTerminalRequest ?? false;
        return Task.FromResult(new DapCapabilities
        {
            SupportsConfigurationDoneRequest = true,
            SupportsFunctionBreakpoints = true,
            SupportsConditionalBreakpoints = true,
            SupportsHitConditionalBreakpoints = true,
            SupportsLogPoints = true,
            SupportsEvaluateForHovers = true,
            SupportsStepBack = false,
            SupportsSetVariable = false,
            SupportsSetExpression = true,
            SupportsRestartFrame = false,
            SupportsTerminateRequest = true,
            SupportsExceptionInfoRequest = true,
            SupportsExceptionFilterOptions = true,
            SupportsExceptionOptions = true,
            SupportsStepInTargetsRequest = true,
            SupportsGotoTargetsRequest = true,
            SupportsDataBreakpoints = ManagedDebugger.DataBreakpointsUnavailable is null,
            DataBreakpointsUnavailable = ManagedDebugger.DataBreakpointsUnavailable,
            ExceptionBreakpointFilters =
            [
                new DapExceptionFilter("all", "All Exceptions", Default: false),
                new DapExceptionFilter("user-unhandled", "User-Unhandled Exceptions", Default: true),
            ],
        });
    }

    private async Task Launch(LaunchArguments arguments)
    {
        try
        {
            string program = string.IsNullOrEmpty(arguments.Program) ? throw new InvalidOperationException("Missing program path") : arguments.Program;
            List<string> args = [.. arguments.Args ?? []];
            LaunchRequestConsoleType console = arguments.Console switch
            {
                "integratedTerminal" => LaunchRequestConsoleType.IntegratedTerminal,
                "externalTerminal" => LaunchRequestConsoleType.ExternalTerminal,
                "internalConsole" or null => LaunchRequestConsoleType.InternalConsole,
                _ => throw new InvalidOperationException($"Invalid console type: '{arguments.Console}'"),
            };
            if (console != LaunchRequestConsoleType.InternalConsole && !runsInTerminal)
            {
                throw new InvalidOperationException($"the client cannot start a program in a terminal ({arguments.Console}); use internalConsole");
            }
            // A .dll runs under the host; an apphost or another executable runs itself.
            if (Path.GetExtension(program).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                args.Insert(0, program);
                program = "dotnet";
            }
            Dictionary<string, string> environment = arguments.Env?.ToDictionary() ?? [];
            bool hotReload = arguments.HotReload ?? false;
            if (hotReload)
            {
                // The runtime's own switch for code that may change, as dotnet watch sets it; a launch's own is kept.
                environment.TryAdd("DOTNET_MODIFIABLE_ASSEMBLIES", "debug");
            }
            LaunchInfo launch = new()
            {
                Program = program,
                Arguments = args,
                Cwd = arguments.Cwd,
                Env = environment,
                StopAtEntry = arguments.StopAtEntry ?? false,
                LaunchRequestConsoleType = console,
                HotReload = hotReload,
            };
            await Locked(() => debugger.Launch(launch, arguments.JustMyCode ?? true));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Failed to launch: {error.Message}", error);
        }
    }

    private async Task Attach(AttachArguments arguments)
    {
        try
        {
            int processId = arguments.ProcessId ?? throw new InvalidOperationException("Missing process ID");
            bool justMyCode = arguments.JustMyCode ?? true;
            await Locked(() => debugger.Attach(processId, justMyCode));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Failed to attach: {error.Message}", error);
        }
    }

    /// <summary>Launches or attaches as asked, under the engine's lock, which is held until the answer has been written
    /// (<see cref="LetEventsGo"/>): the program runs from here, and none of its events (a stop at its first line, its end) is
    /// handled, and so told, before the client hears that its configuration is done.</summary>
    private async Task ConfigurationDone(DapEmpty _)
    {
        IDisposable held = await debugger.DapRequestAndRuntimeEventLock.LockAsync();
        try
        {
            await debugger.DrainRuntimeEventQueue();
            await debugger.ConfigurationDone().ConfigureAwait(false);
        }
        catch
        {
            held.Dispose();
            throw;
        }
        Interlocked.Exchange(ref configuring, held);
        if (Volatile.Read(ref aborted))
        {
            LetEventsGo();
        }
    }

    /// <summary>configurationDone's answer is written, or never will be (the client went): the program's events are handled.</summary>
    private void LetEventsGo() => Interlocked.Exchange(ref configuring, null)?.Dispose();

    private async Task<SetBreakpointsResponse> SetBreakpoints(SetBreakpointsArguments arguments)
    {
        string path = arguments.Source?.Path ?? throw new InvalidOperationException("Missing source path");
        BreakpointRequest[] requests = [.. (arguments.Breakpoints ?? []).Select(b => new BreakpointRequest(
            LineIn(b.Line),
            b.Condition,
            b.HitCondition,
            b.Column is int column ? ColumnIn(column) : null,
            b.LogMessage,
            b.Suspend))];
        List<BreakpointManager.BreakpointInfo> breakpoints = await Locked(() => debugger.SetBreakpoints(path, requests));
        return new SetBreakpointsResponse([.. breakpoints.Select(b => ToProtocol(b, isEvent: false))]);
    }

    private async Task<SetBreakpointsResponse> SetFunctionBreakpoints(SetFunctionBreakpointsArguments arguments)
    {
        FunctionBreakpointRequest[] requests = [.. (arguments.Breakpoints ?? []).Select(b => new FunctionBreakpointRequest(b.Name, b.Condition, b.HitCondition, b.LogMessage, b.Suspend))];
        List<BreakpointManager.BreakpointInfo> breakpoints = await Locked(() => debugger.SetFunctionBreakpoints(requests));
        return new SetBreakpointsResponse([.. breakpoints.Select(b => new DapBreakpoint { Id = b.Id, Verified = b.Verified, Message = b.Message, Reason = b.Refused ? "failed" : null })]);
    }

    private async Task<SetExceptionBreakpointsResponse> SetExceptionBreakpoints(SetExceptionBreakpointsArguments arguments)
    {
        List<ExceptionBreakpointRequest> requests = [];
        foreach (string filter in arguments.Filters ?? [])
        {
            requests.Add(new ExceptionBreakpointRequest(ExceptionFilter(filter)));
        }
        foreach (DapExceptionFilterOptions option in arguments.FilterOptions ?? [])
        {
            requests.Add(new ExceptionBreakpointRequest(ExceptionFilter(option.FilterId), option.Condition));
        }
        List<ExceptionRuleRequest>? rules = arguments.ExceptionOptions?.Select(Rule).ToList();
        log?.Invoke($"Exception breakpoints: {string.Join(", ", requests)}{(rules is null ? string.Empty : $"; {string.Join(", ", rules)}")}");
        IReadOnlyList<ExceptionRule> made = await Locked(() => debugger.SetExceptionBreakpoints(requests, rules));
        return new SetExceptionBreakpointsResponse(
        [
            .. requests.Select(_ => new DapBreakpoint { Verified = true }),
            .. made.Select(rule => new DapBreakpoint { Id = rule.Id, Verified = rule.Refused is null, Message = rule.Refused, Reason = rule.Refused is null ? null : "failed" }),
        ]);

        static ExceptionBreakpointFilter ExceptionFilter(string filter) => filter switch
        {
            "all" => ExceptionBreakpointFilter.All,
            "user-unhandled" => ExceptionBreakpointFilter.UserUnhandled,
            _ => throw new InvalidOperationException($"Unknown exception breakpoint filter: '{filter}'"),
        };

        // The path is CLR, then the types (none: every exception); Rider's scenarios, when given, stand for the break mode.
        static ExceptionRuleRequest Rule(DapExceptionOptions option)
        {
            IReadOnlyList<DapExceptionPathSegment> path = option.Path ?? [];
            if (path.Count > 0 && !path[0].Names.Contains("CLR"))
            {
                throw new InvalidOperationException($"Exception options are for CLR exceptions, not {string.Join(", ", path[0].Names)}");
            }
            DapExceptionPathSegment? types = path.Count > 1 ? path[1] : null;
            Passenger.Dap.ExceptionScenarios scenarios = option.Scenarios is { } named
                ? ExceptionScenarioNames.Parse(named)
                : ExceptionScenarioNames.OfBreakMode(option.BreakMode);
            return new ExceptionRuleRequest([.. types?.Names ?? []], types?.Negate ?? false, scenarios,
                string.IsNullOrWhiteSpace(option.Condition) ? null : option.Condition,
                string.IsNullOrWhiteSpace(option.HitCondition) ? null : option.HitCondition,
                string.IsNullOrEmpty(option.LogMessage) ? null : option.LogMessage,
                option.Suspend ?? string.IsNullOrEmpty(option.LogMessage));
        }
    }

    /// <summary>The data breakpoints, each watching for a write: a write access alone is this debugger's, as the runtime's.</summary>
    private async Task<SetDataBreakpointsResponse> SetDataBreakpoints(SetDataBreakpointsArguments arguments)
    {
        List<DataBreakpointRequest> requests = [];
        foreach (DapDataBreakpoint breakpoint in arguments.Breakpoints ?? [])
        {
            if (breakpoint.AccessType is not (null or "write"))
            {
                throw new InvalidOperationException($"A data breakpoint here stops at a write, not a {breakpoint.AccessType}");
            }
            requests.Add(new DataBreakpointRequest(breakpoint.DataId,
                string.IsNullOrWhiteSpace(breakpoint.Condition) ? null : breakpoint.Condition,
                string.IsNullOrWhiteSpace(breakpoint.HitCondition) ? null : breakpoint.HitCondition,
                string.IsNullOrEmpty(breakpoint.LogMessage) ? null : breakpoint.LogMessage,
                breakpoint.Suspend ?? string.IsNullOrEmpty(breakpoint.LogMessage)));
        }
        List<DataBreakpointResult> results = await Locked(() => debugger.SetDataBreakpoints(requests));
        return new SetDataBreakpointsResponse([.. results.Select(r => new DapBreakpoint { Id = r.Id, Verified = r.Verified, Message = r.Message, Reason = r.Verified ? null : "failed" })]);
    }

    private async Task<ExceptionInfoResponse> ExceptionInfo(ThreadArguments arguments)
    {
        ExceptionInfo info = await Locked(() => debugger.ExceptionInfo(new ThreadId(arguments.ThreadId)));
        return new ExceptionInfoResponse
        {
            ExceptionId = info.ExceptionId,
            Description = info.Description,
            BreakMode = info.BreakMode switch
            {
                ExceptionBreakMode.Never => "never",
                ExceptionBreakMode.Always => "always",
                ExceptionBreakMode.Unhandled => "unhandled",
                ExceptionBreakMode.UserUnhandled => "userUnhandled",
                _ => "unhandled",
            },
            Code = info.Code,
            Details = ToProtocol(info.Details),
        };
    }

    /// <summary>Asks the client to start the program in its terminal, held at its start for the debugger to take hold of,
    /// and answers with its process. Asked from inside configurationDone, under the engine's lock, so a client that never
    /// answers fails the request after <see cref="RunInTerminalLimit"/> rather than holding the engine for good.</summary>
    private async Task<int> RunInTerminal(LaunchInfo launch)
    {
        string programForTitle = launch.Program is "dotnet" ? launch.Arguments[0] : launch.Program;
        Dictionary<string, string> env = new(launch.Env)
        {
            // The program waits at startup for the debugger to attach and let it go.
            ["DOTNET_DefaultDiagnosticPortSuspend"] = "1",
        };
        RunInTerminalArguments arguments = new(
            launch.LaunchRequestConsoleType switch
            {
                LaunchRequestConsoleType.IntegratedTerminal => "integrated",
                LaunchRequestConsoleType.ExternalTerminal => "external",
                _ => throw new ArgumentOutOfRangeException(nameof(launch), $"Invalid console type for runInTerminal: '{launch.LaunchRequestConsoleType}'"),
            },
            $"{Path.GetFileName(programForTitle)} [DEBUG]",
            launch.Cwd,
            [launch.Program, .. launch.Arguments],
            env);
        using CancellationTokenSource limit = new(RunInTerminalLimit);
        RunInTerminalResponse response;
        try
        {
            // The connection reads the answer on a thread of its own, so this wait never holds up the read that brings it.
            response = await connection.RequestAsync<RunInTerminalResponse>("runInTerminal", arguments, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            throw new TimeoutException($"The client did not start {Path.GetFileName(programForTitle)} in its terminal within {RunInTerminalLimit.TotalSeconds:0} s");
        }
        catch (DapException refused)
        {
            throw new InvalidOperationException($"The client could not start {Path.GetFileName(programForTitle)} in its terminal: {refused.Reason}", refused);
        }
        // VS Code answers with no process for its terminals (microsoft/vscode#61640).
        return response.ProcessId ?? throw new InvalidOperationException("runInTerminal returned no process ID; use internalConsole, or a client that returns one.");
    }

    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) == 0)
        {
            log?.Invoke("Shutdown requested");
            ShutdownRequested?.Invoke();
        }
    }

    private async Task<T> Locked<T>(Func<Task<T>> work)
    {
        using (await debugger.DapRequestAndRuntimeEventLock.LockAsync())
        {
            await debugger.DrainRuntimeEventQueue();
            return await work().ConfigureAwait(false);
        }
    }

    private async Task<T> Locked<T>(Func<T> work)
    {
        using (await debugger.DapRequestAndRuntimeEventLock.LockAsync())
        {
            await debugger.DrainRuntimeEventQueue();
            return work();
        }
    }

    private async Task Locked(Func<Task> work)
    {
        using (await debugger.DapRequestAndRuntimeEventLock.LockAsync())
        {
            await debugger.DrainRuntimeEventQueue();
            await work().ConfigureAwait(false);
        }
    }

    private async Task Locked(Action work)
    {
        using (await debugger.DapRequestAndRuntimeEventLock.LockAsync())
        {
            await debugger.DrainRuntimeEventQueue();
            work();
        }
    }

    private DapBreakpoint ToProtocol(BreakpointManager.BreakpointInfo breakpoint, bool isEvent)
    {
        bool bound = breakpoint is { IsFunctionBreakpoint: false, Verified: true };
        DapSource? source = isEvent
            ? (bound ? new DapSource { Path = breakpoint.FilePath, Name = Path.GetFileName(breakpoint.FilePath), SourceReference = 0 } : null)
            : new DapSource { Path = breakpoint.FilePath };
        return new DapBreakpoint
        {
            Id = breakpoint.Id,
            Verified = breakpoint.Verified,
            Line = breakpoint.IsFunctionBreakpoint ? null : LineOut(breakpoint.Line),
            Column = bound && breakpoint.Column is int column ? ColumnOut(column) : null,
            EndLine = bound ? LineOut(breakpoint.EndLine) : null,
            EndColumn = bound && breakpoint.EndColumn is int endColumn ? ColumnOut(endColumn) : null,
            Offset = isEvent && bound ? 0 : null,
            Message = breakpoint.Message,
            Reason = breakpoint.Refused ? "failed" : null,
            Source = source,
        };
    }

    private DapStackFrame ToProtocol(StackFrameInfo frame) => new()
    {
        Id = frame.Id,
        Name = frame.Name,
        Line = LineOut(frame.Line),
        EndLine = frame.EndLine is int endLine ? LineOut(endLine) : null,
        Column = ColumnOut(frame.Column),
        EndColumn = frame.EndColumn is int endColumn ? ColumnOut(endColumn) : null,
        Source = frame.Source is not null ? new DapSource { Path = frame.Source, Name = Path.GetFileName(frame.Source), SourceReference = 0 } : null,
        PresentationHint = frame.IsUserCode ? null : "subtle",
        IsResolved = frame.IsResolved,
        DecompiledSourceInfo = frame.DecompiledSourceInfo is { } decompiled
            ? new DapDecompiledSource(decompiled.TypeFullName, new DapAssembly(decompiled.Assembly.AssemblyPath, decompiled.Assembly.Mvid), decompiled.CallingUserCodeAssemblyPath)
            : null,
    };

    private static DapPresentationHint? ToProtocol(VariablePresentationHint? hint)
    {
        if (hint is not { } h)
        {
            return null;
        }
        string? kind = h.Kind switch
        {
            null => null,
            PresentationHintKind.Property => "property",
            PresentationHintKind.Method => "method",
            PresentationHintKind.Event => "event",
            PresentationHintKind.Class => "class",
            PresentationHintKind.Data => "data",
            _ => throw new ArgumentOutOfRangeException(nameof(hint), h.Kind, null),
        };
        IReadOnlyList<string>? attributes = h.Attributes switch
        {
            null or AttributesValue.None => null,
            AttributesValue.FailedEvaluation => ["failedEvaluation"],
            _ => throw new ArgumentOutOfRangeException(nameof(hint), h.Attributes, null),
        };
        return new DapPresentationHint(kind, attributes);
    }

    private static DapExceptionDetails ToProtocol(ExceptionInfo.ExceptionDetails details) => new()
    {
        Message = details.Message,
        TypeName = details.TypeName,
        FullTypeName = details.FullTypeName,
        EvaluateName = details.EvaluateName,
        StackTrace = details.StackTrace,
        InnerException = [.. details.InnerException.Select(ToProtocol)],
        FormattedDescription = details.FormattedDescription,
        HResult = details.HResult,
        Source = details.Source,
    };

    private static string StopReason(string reason) => reason.ToLowerInvariant() switch
    {
        "step" or "breakpoint" or "exception" or "pause" or "entry" or "goto" or "function breakpoint" or "data breakpoint" or "instruction breakpoint" => reason.ToLowerInvariant(),
        _ => "unknown",
    };

    private int LineIn(int line) => linesStartAt1 ? line : line + 1;

    private int LineOut(int line) => linesStartAt1 ? line : line - 1;

    private int ColumnIn(int column) => columnsStartAt1 ? column : column + 1;

    private int ColumnOut(int column) => columnsStartAt1 ? column : column - 1;
}
