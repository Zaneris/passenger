# A file of breakpoints

Run › Export Breakpoints… (or Export… in the Breakpoints window) writes a folder's line, method and CLR exception
breakpoints to a JSON file; Run › Import Breakpoints… (or Import…) adds a file's breakpoints to the folder's, so they
move to another machine, another clone of the repository, or a colleague. Import adds only what is not set already: a
line, a method or an exception type that has a breakpoint keeps its own, and "Any exception" stays as the folder has it.
Data and property breakpoints belong to one debugging session and are never written.

## Format 1

```json
{
  "passengerBreakpoints": 1,
  "lines": [
    { "file": "src/Shop/Cart.cs", "line": 42 },
    { "file": "src/Shop/Cart.cs", "line": 57, "condition": "total > 100", "hitCount": ">=2", "temporary": true },
    { "file": "src/Shop/Cart.cs", "line": 61, "column": 30, "suspend": false, "logMessage": "item is {item}" },
    { "file": "src/Shop/MainWindow.axaml", "line": 12, "column": 9, "attribute": "Text" },
    { "file": "src/Shop/Cart.cs", "line": 80, "waitFor": { "file": "src/Shop/Cart.cs", "line": 42 } },
    { "file": "src/Shop/Cart.cs", "line": 88, "condition": "total > 100", "snapshot": { "expressions": ["items.Count"], "limit": 20 } }
  ],
  "methods": [
    { "name": "Shop.Cart.Add(int, string)", "file": "src/Shop/Cart.cs", "line": 30 },
    { "name": "Checkout", "enabled": false, "waitFor": { "method": "Shop.Cart.Add(int, string)" } }
  ],
  "exceptions": [
    { "type": "Any exception" },
    { "type": "System.InvalidOperationException", "scenarios": ["thrownInUserCode", "unhandled"], "condition": "Message.Length > 0" }
  ]
}
```

- `passengerBreakpoints` is the format's version, `1`. A Passenger refuses a file of a later format, saying so.
- `file` is named from the open folder in forward slashes (`../Shared/Util.cs` for one beside it); a full path is taken as
  it stands. `line` and `column` count from 1.
- A line breakpoint's `column` is where a C# lambda's body begins on its line, for a breakpoint inside the lambda, or,
  with `attribute`, where a XAML attribute's name begins.
- A method breakpoint's `name` is the method as C# writes it: `Name`, `Type.Name` or `Namespace.Type.Name`, a generic
  one's arity as `Name<T>`, a constructor as `Type..ctor`, a static constructor as `Type..cctor` and a finalizer as
  `Type.Finalize` (or `Type.~Type`), with its parameters as C# writes them for one overload (`ref string?`, `out int`, a
  type parameter, a tuple), matched to the types the compiled method takes. `file` and `line` are where it was set, for
  the gutter's mark; one without them stands by its name alone.
- Each breakpoint may give `enabled` and `suspend` (both `true` when left out), `condition`, `hitCount` (`==3`, `>=2`,
  `%2`) and `logMessage`; a line or method breakpoint also `temporary` (removed once hit) and `waitFor`, the breakpoint
  whose hit it waits for: a line breakpoint by its `file` and `line` (with its `column` or `attribute`), or a method
  breakpoint by its `method`.
- A line or method breakpoint with `snapshot` is a snapshot point: it never stops the program, and each hit it takes
  (its condition held, its hit count met) captures the thread's frames and the values of its frames of the user's code,
  with its own `expressions`, then lets the program go on. It takes `limit` snapshots in a debugging session (`100` when
  left out, from 1 to 10,000), its later hits passing by.
- An exception breakpoint's `type` is its full name, or `Any exception`. `scenarios` lists where it stops, as
  `breakpoint_set` takes them: `thrownInUserCode`, `thrownInExternalCode`, `handledInUserCode`, `handledInExternalCode`,
  `unhandled`; left out, a type's stops in all of them.
- What a breakpoint does by default is left out of an exported file. Comments and trailing commas are read.

## Snapshot points

A snapshot point (Run › Toggle Snapshot Point, Add Snapshot Point in the gutter's menu, or Take a snapshot in a
breakpoint's popup) reads a running program's state at a line without stopping it, through Passenger's own .NET debugger,
in a program launched, attached to or running in a container. At each hit it takes, the debugger reads the thread's frames
and, for its ten top frames of the user's code, their locals and arguments and their members two levels down, as a stop's
Variables reads them, a thousand values at most; it runs none of the program's code for them, so a getter that is not an
auto-property, a ToString or a debugger view is not read, and says so. The runtime's lists, dictionaries, sets, queues and
stacks show their counts and items, and a record its members as it prints them, each read from what it stores. The point's own expressions are read in its top
frame as a watch's are, running the code they call within the time any evaluation is given. Then the program goes on: it
was held only while the debugger read, which each snapshot says.

The Debug window's Snapshots tab lists the folder's snapshots, the newest first, through its debugging sessions and after
them; Enter or a double click shows one in Frames and Variables as a past stop, its values as they were then. An agent
sets one with `breakpoint_set`'s `snapshot` and reads them with `debug_state` of `snapshots`.

## Recorded calls

Run › Record Calls (or its toggle before Step Back on the Debug window's toolbar; `debug_start`'s `record` for an agent)
has the programs debugged by Passenger's own .NET debugger record the calls of their own code, from their start when it
is on before Debug, else from then on. It is off until asked for, as each call recorded holds the program while the
debugger reads it (a few milliseconds a call, where a breakpoint that does not stop holds it one or two).

Each call of a method the program's symbols place (none the compiler wrote, a lambda's or an auto-property's) is kept as
it begins, its arguments and their members one level down read as a snapshot reads them, running none of the program's
code, and a call returning a value again as it returns, that value read where the runtime keeps it: in the user's code
that called it, or at its own closing brace for a call code that is not the user's made (LINQ calling a selector; an
expression-bodied method's return keeps no value there and is kept without one). An async method's call is kept where
the code its state machine runs begins and where it completes, with the value it completes with, on whichever thread it
went on on after an await. An exception is kept where it is thrown in the user's code (or reaches it) and where the
user's code catches it, and a hit of one of the person's breakpoints that went on (its condition false, its hit count
not reached, a tracepoint's message logged, a snapshot taken) with its frame's values. A recording keeps its newest
10,000 moments and 32 MB of their values, letting the oldest go past them, which its timeline says.

At a stop, Run › Step Back and Step Forward (or `debug_step`'s `back` and `forward`) go through the moments of the run
the stop is in: its thread's, and an async call's on whichever thread it went on on. Each is shown in Frames and
Variables as a past stop, a snapshot's way: the method's frame where it stood (its first line at a call, its last at a
return) above the recorded calls it ran in, each where it made the next call with its arguments as they were at its own
call, its values as they were then, which the editor's lines show as a stop's show its own. Step Forward past the newest
moment comes back to the live stop. The Debug window's Timeline tab lists the moments, each under the call it ran in;
Enter shows one. An agent lists them with `debug_state` of `timeline`.
