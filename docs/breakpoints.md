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
    { "file": "src/Shop/Cart.cs", "line": 80, "waitFor": { "file": "src/Shop/Cart.cs", "line": 42 } }
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
  one's arity as `Name<T>`, with its parameters' types for one overload. `file` and `line` are where it was set, for the
  gutter's mark; one without them stands by its name alone.
- Each breakpoint may give `enabled` and `suspend` (both `true` when left out), `condition`, `hitCount` (`==3`, `>=2`,
  `%2`) and `logMessage`; a line or method breakpoint also `temporary` (removed once hit) and `waitFor`, the breakpoint
  whose hit it waits for: a line breakpoint by its `file` and `line` (with its `column` or `attribute`), or a method
  breakpoint by its `method`.
- An exception breakpoint's `type` is its full name, or `Any exception`. `scenarios` lists where it stops, as
  `breakpoint_set` takes them: `thrownInUserCode`, `thrownInExternalCode`, `handledInUserCode`, `handledInExternalCode`,
  `unhandled`; left out, a type's stops in all of them.
- What a breakpoint does by default is left out of an exported file. Comments and trailing commas are read.
