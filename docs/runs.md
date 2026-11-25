# Runs of several parts

A run of several parts starts what an app needs together, set up once in Edit Configurations (Add New Configuration ›
Several Parts) and started by one Run or Debug: .NET projects, a package.json script run by its own package manager, a
package's dependencies installed by it, commands, and a Compose application's services. Each part starts once the one before it is ready, and Stop ends
them all. Each part not debugged has a tab of its own in the Run window (or the Terminal window, where its .NET project's
configuration runs in the terminal), saying where it stands; a tab's Rerun restarts its part alone, the others running
on, and its Stop ends that part alone. Debug debugs each .NET project set to Start, and a Compose part's .NET services as
its files' own configuration debugs them, each in a Debug tab of its own.

Where a run holds only .NET projects of the solution, each with nothing but its action and launch profile, it is kept in
the solution's `.slnLaunch` (the person's own in `.slnLaunch.user`), as Visual Studio writes and reads it. Any other run
is kept whole in the folder's `passenger-runs.json`, at its top, which the team shares (the person's own with their other
configurations): one run is kept in one file, never half in each. Passenger moves a run between the two as its parts
change, and follows the files and folders its parts name as they move.

## Format 1

```json
{
  "passengerRuns": 1,
  "runs": [
    {
      "name": "Web",
      "parts": [
        { "compose": ["compose.yaml"], "services": ["db"] },
        { "install": "WebApp/frontend/package.json" },
        { "script": "build:watch", "package": "WebApp/frontend/package.json", "readyWhen": { "line": "built in" } },
        { "project": "WebApp/WebApp.csproj", "launchProfile": "https", "readyWhen": "listening" },
        { "command": "redis-server --port 6380", "folder": "tools", "environment": { "REDIS_LOG": "1" } }
      ]
    }
  ]
}
```

- `passengerRuns` is the format's version, `1`. A Passenger refuses a file of a later format, saying so.
- Each run has a `name` and its `parts`, in the order they start.
- A part's kind is the member naming what it runs, each path from the folder `passenger-runs.json` is in, in forward slashes:
  - `project`, a .NET project's file, started as its own run configuration starts it (the person's changes to it
    included), in its `launchProfile` (its first that runs here where none is named);
  - `script`, a script of the package.json `package` names, run by the package's own package manager, with any
    `arguments` after it;
  - `install`, a package.json whose dependencies its package manager installs (`npm install`, `pnpm install`…), ready
    once it ends: a step;
  - `command`, a program and its arguments, found as a terminal finds a command, run in `folder` (the folder's top where
    none is named);
  - `compose`, a Compose application's files, the first the base and the rest its overrides, brought up as the folder's
    container run (as its files' own run configuration brings them up, the person's choices for it kept), the `services`
    named and what they depend on, every service where none is named. A run holds one, as the folder runs one Compose
    application at a time; its console is the Run window's own, where its log is followed, and Stop takes it down.
- `action` is `start` (the default), `startWithoutDebugging` (a .NET project run, never debugged), or `none` (left out).
  Debug debugs each .NET project set to `start` and a `start` Compose part's .NET services (those its configuration
  debugs), and runs every other part.
- `readyWhen` says when the parts after it start: `started` (the default, at once), `exited` (once it ends with exit code
  0; a part ending otherwise stops the run's start), `{ "line": "…" }` (once a line of its output matches the regular
  expression), or `{ "listening": "…" }` (once something takes a connection at the address); `listening` alone waits, for
  a .NET project, until its host says it has started. A Compose part is `healthy`, its one readiness: ready once its
  services are up, healthy where they check.
- `environment` gives the part's variables, over its own run configuration's for a .NET project.
- `browser` is a page opened once something answers there; a .NET project also opens its own as its launch profile says.
- `name` names the part's console; without one, a part is named for what it runs.
- Members Passenger does not know are kept as written, and comments and trailing commas are read as JSON with comments
  reads them.

An AppHost is never a part: it starts its own resources, so its own configuration runs it.
