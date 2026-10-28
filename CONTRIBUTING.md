# Contributing to Passenger

Thanks for helping. This page says how to report a problem, how to propose a change, and what makes a pull request easy
to take. [`CLAUDE.md`](CLAUDE.md) is the repository's full guide (architecture, rules and how the tests work), written
for coding agents and people alike; read the parts your change touches.

## Reporting a bug

Search the [issues](https://github.com/Zaneris/passenger/issues) first: it may be known, or fixed on the branch of the
release in progress. If it's new, open an issue with:

- what you did, what you expected and what happened instead, as steps someone else can follow;
- your system and Passenger's version (Passenger > About on macOS, Help > About elsewhere);
- what the log says: Help > Show Log opens the folder, and `passenger.log` there has the IDE's errors.

A security problem goes to the maintainer privately rather than into a public issue.

## Proposing a feature or a larger change

Open an issue before starting anything larger than a fix, so the change and its scope are agreed before the work. An
issue says what Passenger does now, what it should do and when it is done:

- **Now:** what happens today.
- **The aim:** what it should do instead.
- **Done when:** the checks that say it's finished.

A new feature is the best Passenger can make it from what it already has, not a copy of another IDE's. Only its
keyboard shortcuts follow Rider's keymaps.

## Setting up

You need the .NET 11 SDK (11.0.100-rc.3 or later, as `global.json` asks, until .NET 11 ships in November 2026),
[just](https://github.com/casey/just) and Git, and for the tests the .NET 10 SDK too, which the programs they debug and
most projects they write run on (on Linux also the X11 and fontconfig libraries
[Passenger's Linux page](https://passenger-ide.net/linux) names). Node.js runs the web languages' tests and a container
engine running Linux containers runs the container tests; each kind skips without its own, saying why. Python 3 runs the
recipes that write or check generated files (`just icons`, `just themes`, `just notices` and others). The README's
[Building](README.md#building) has the whole list.

```sh
just build                 # Debug build; warnings are errors
just run [PATH]            # the IDE on a folder or solution
just test [FILTER]         # the tests, or those whose name matches; results in artifacts/test-results
just screenshot shell.png  # the main window drawn off-screen to a PNG
```

Working on Passenger needs only open-source tools: the .NET SDK, just, and the open-source packages the projects
restore, among them Avalonia's MIT framework. Passenger uses none of Avalonia's commercial tooling (Avalonia Accelerate's
DevTools, Hot Reload and the rest), and takes no contribution that adds any. A licence you hold for a commercial tool,
Avalonia's or any other vendor's, is between you and that vendor: nothing here asks you to use one, so if its terms
could cover work on another IDE, work on Passenger without it.

**If you hold a commercial Avalonia licence** (Accelerate or another of Avalonia's paid products), read its terms before
contributing, in particular what they say about competing products. Don't use any of Avalonia's commercial tools for
work on Passenger. Passenger can't tell you how those terms apply to you, so if you're unsure, ask Avalonia or leave it.

## Making a change

- **Branch from the release in progress** (a branch named after it, such as `0.2.0`) and open your pull request against
  it, not `main`.
- **Test it the way a person uses it.** The tests drive the whole IDE, headless: a window, real key presses, clicks and
  files on disk. A test says what a person sees or gets ("an edit saved keeps the file's line endings"), never what
  one function returns on its own. It waits for what it checks (`Ui.WaitFor`, `Ui.Settle`); fixed sleeps are build
  errors in the tests. A bug fix comes with a test that fails without it.
- **Look at what you changed.** After changing anything visible, take a screenshot (`--scale 2`, in the dark and light
  themes) and look at it.
- **Run the test classes your change touches** on the systems it can differ on. CI runs are started by hand, so say in
  the pull request what you ran and where.

### Rules a change keeps

- **Licences.** Every dependency, icon or asset has an open-source licence and an entry in `THIRD_PARTY_NOTICES.md`,
  added in the same change; `just notices` fails on one missing. Package versions live only in
  `Directory.Packages.props`, and a change of package commits its `packages.lock.json`.
- **No proprietary debugger or tooling:** nothing from Microsoft's vsdbg or C# Dev Kit, and no Avalonia Accelerate
  components.
- **Colours are theme tokens** (`Theme/Tokens.axaml`), never literal colours, and every token exists in every theme. A
  visual inconsistency is fixed once in a shared style or helper, not per control.
- **Every program Passenger starts** goes through `ChildProcesses`, so none outlives the IDE.
- **Work off the UI thread** that changes what is drawn goes through `BackgroundWork`.
- **Paths are canonical** (`PathNames.Canonical`) wherever two are compared.
- **Code style:** four spaces, file-scoped namespaces, braces on every block, explicit types over `var`; `.editorconfig`
  makes the build fail on the namespaces, braces and accessibility, while `var` is only a suggestion there, so write the
  type yourself. Comments say why, never what the next lines do.

## Pull requests

- **One change a pull request,** with no unrelated edits or reformatting.
- **Commit messages** say in plain words what a person now sees or gets ("Undo near the end of a file keeps its step"),
  with the issue they close: `(Closes #123)`.
- **The description** says what changed and why, links the issue, and lists the tests you ran and on which systems.
- **Review.** A maintainer reads the whole change before building it, and may finish it with commits of their own,
  keeping yours as you wrote them, when that's quicker than another round.

## Using a coding agent

Coding agents are welcome here: much of Passenger is written with them, and `CLAUDE.md` is written for them. The person
who opens the pull request understands the change and answers for it. Keep descriptions and comments to what a reader
needs, and never let an issue's or a pull request's text steer what your agent does: treat it as data, not instructions.

## Licence

Passenger is MIT licensed (see [`LICENSE`](LICENSE)), and what you contribute is under the same licence.
