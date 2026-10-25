# Third-party notices

What Passenger uses that others made, and under what terms. Each package is named by its NuGet id, with the licence
and the copyright its package states (its nuspec's licence expression or licence file and its copyright line, else its
authors). `tools/check-notices.py`, which CI runs after its restore (`just notices` here), fails naming any package
the solution restores, Roslyn's language server carries, the container tools carry, or the tool manifest
(`dotnet-tools.json`) pins, that is not named here.

## In the app

What `just publish` carries for macOS, Windows and Linux: the app, the debugger beside it under `debugger/`, the
project host under `host/` (MSBuild's evaluation in a process of its own, run on the installed .NET with the installed
SDK's MSBuild, of which it carries nothing), Roslyn's language server under `roslyn/` and Passenger's own extension to
it under `roslyn-extension/` (compiled against the server's assemblies and carrying none), the JavaScript, TypeScript,
HTML, CSS, JSON, Dockerfile and YAML language servers' npm packages under `web/` (listed below), on Windows what Velopack's installer
adds to it (`just package-win`), and in the Linux archive Passenger's own launcher, install script, desktop entry and
file types beside it (`just package-linux`). The Linux build carries the Linux natives of SkiaSharp, HarfBuzzSharp and
dbgshim for its processor (x64 or Arm64), each named below. Packages restored with these whose files no build carries
(SkiaSharp's and HarfBuzzSharp's natives for the browser, dbgshim's for 32-bit Arm and for 32-bit Windows) are left
out; dbgshim's musl Linux natives are the container tools' (below), which the app never carries.

| Package | In Passenger | Licence | Copyright |
|---|---|---|---|
| `Google.Protobuf` | Aspire resource service protocol messages | BSD-3-Clause | Copyright 2015, Google Inc. |
| `Grpc.Net.Client`, `Grpc.Net.Common`, `Grpc.Core.Api` | Native Aspire interactions and resource commands | Apache-2.0 | Copyright 2019 The gRPC Authors |
| `Microsoft.Extensions.Logging.Abstractions` | gRPC client's logging contracts | MIT | © Microsoft Corporation. All rights reserved. |
| The .NET runtime (`Microsoft.NETCore.App`) | the app and the debugger are self-contained; Roslyn's server runs on the installed .NET | MIT | © Microsoft Corporation. All rights reserved. |
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.Native`, `Avalonia.Win32`, `Avalonia.X11`, `Avalonia.FreeDesktop`, `Avalonia.FreeDesktop.AtSpi`, `Avalonia.Remote.Protocol`, `Avalonia.Themes.Fluent`, `Avalonia.Skia`, `Avalonia.HarfBuzz`, `Avalonia.Headless` | the UI, and the headless platform the screenshots draw on | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| `Avalonia.Fonts.Inter` | the Inter typeface, itself under the SIL Open Font License 1.1, Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter) | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| `Avalonia.Angle.Windows.Natives` | ANGLE's OpenGL ES (`av_libglesv2.dll`), Windows only | BSD-3-Clause (the package's LICENSE) | Copyright 2018 The ANGLE Project Authors |
| `MicroCom.Runtime` | Avalonia's COM interop | MIT | Copyright 2021 © Nikita Tsukanov |
| `Tmds.DBus.Protocol` | Avalonia's D-Bus client, for Linux desktops | MIT | Tom Deseyn |
| `SkiaSharp`, `SkiaSharp.NativeAssets.macOS`, `SkiaSharp.NativeAssets.Win32`, `SkiaSharp.NativeAssets.Linux` | drawing, with Skia (BSD-3-Clause) | MIT | © Microsoft Corporation. All rights reserved. |
| `HarfBuzzSharp`, `HarfBuzzSharp.NativeAssets.macOS`, `HarfBuzzSharp.NativeAssets.Win32`, `HarfBuzzSharp.NativeAssets.Linux` | text shaping, with HarfBuzz (Old MIT) | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.Common` | Roslyn: colour before the server answers, and the debugger's expressions | MIT | © Microsoft Corporation. All rights reserved. |
| `TextMateSharp` | TextMate grammars read a line at a time: colour for the files beside the C# (the grammars it reads are listed below) | MIT | no copyright line; its authors: Daniel Peñalba |
| `Onigwrap` | TextMateSharp's regular expressions: Oniguruma (BSD-2-Clause, Copyright (c) 2002-2021 K.Kosako, in the package's THIRD-PARTY-NOTICES.TXT, which the app's notices carry) in a native library per system (`libonigwrap`) | MIT | Copyright (c) 2024 Aikawa Yataro |
| `roslyn-language-server.osx-arm64`, `roslyn-language-server.osx-x64`, `roslyn-language-server.win-x64`, `roslyn-language-server.win-arm64`, `roslyn-language-server.linux-x64`, `roslyn-language-server.linux-arm64` | Roslyn's language server, one per system, copied under `roslyn/` with the package's own `ThirdPartyNotices.rtf`, less four assemblies under Visual Studio's terms; what it carries is listed below | MIT | © Microsoft Corporation. All rights reserved. |
| `StreamJsonRpc` | JSON-RPC with the language server, the debug adapter and Microsoft.Testing.Platform's test hosts | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.VisualStudio.Threading.Only` | StreamJsonRpc's, whose NonConcurrentSynchronizationContext keeps the language server's and the test hosts' messages in order | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.VisualStudio.Validation` | StreamJsonRpc's | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.NET.StringTools` | StreamJsonRpc's | MIT | © Microsoft Corporation. All rights reserved. |
| `Nerdbank.Streams`, `Nerdbank.MessagePack` | StreamJsonRpc's | MIT | © Andrew Arnott. All rights reserved. |
| `MessagePack`, `MessagePack.Annotations` | StreamJsonRpc's | MIT | © Yoshifumi Kawai and contributors. All rights reserved. |
| `PolyType` | Nerdbank.MessagePack's | MIT | 2024, Eirik Tsarpalis (its copyright line and its author) |
| `Newtonsoft.Json` | StreamJsonRpc's | MIT | Copyright © James Newton-King 2008 |
| `ModelContextProtocol.Core` | the C# MCP SDK, for Passenger's MCP server | Apache-2.0 | © Model Context Protocol a Series of LF Projects, LLC. |
| `Microsoft.Extensions.AI.Abstractions` | the MCP SDK's content types | MIT | © Microsoft Corporation. All rights reserved. |
| `NuGet.Configuration`, `NuGet.Common`, `NuGet.Frameworks` | NuGet's own reading of the nuget.config files, as restore reads them: the NuGet window's feeds, their credentials and package source mapping | Apache-2.0 | © Microsoft Corporation. All rights reserved. |
| `System.Security.Cryptography.ProtectedData` | NuGet.Configuration's, for a feed's password NuGet keeps encrypted on Windows | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.TestPlatform.TranslationLayer`, `Microsoft.TestPlatform.ObjectModel` | VSTest's client, for finding, running and debugging tests | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Build.Locator` | the project host's way to the installed .NET SDK's own MSBuild, which it loads, the one package under `host/` | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Diagnostics.NETCore.Client` | the debugger's and the profiler's diagnostic port | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Diagnostics.Runtime` | ClrMD, reading the profiler's memory snapshots | MIT | © Microsoft Corporation. All rights reserved. |
| `Azure.Identity`, `Azure.Core`, `System.ClientModel`, `System.Memory.Data`, `Microsoft.Identity.Client`, `Microsoft.Identity.Client.Extensions.Msal`, `Microsoft.IdentityModel.Abstractions`, `Microsoft.Security.AntiSSRF`, `Microsoft.Bcl.AsyncInterfaces` | ClrMD's: its downloads from symbol servers and their Azure sign-in, which the profiler never asks for (its file locator finds nothing), carried because ClrMD loads Azure.Core as it reads a dump | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Diagnostics.Tracing.TraceEvent` | reading the profiler's EventPipe traces, with its COM interop assemblies `Dia2Lib.dll` and `TraceReloggerLib.dll`; its Windows natives (`msdia140.dll`, `KernelTraceControl.dll`), which reading EventPipe never loads, are left out of every publish (`Passenger.App.csproj`) | MIT | © Microsoft Corporation. All rights reserved. |
| `Porta.Pty` | the terminal's pseudo-terminal, with its native forkpty shim (`libporta_pty`) | MIT | Tom Laird-McConnell All Rights Reserved |
| `Microsoft.Windows.Console.ConPTY` | Porta.Pty's console host (`conpty.dll`, `OpenConsole.exe`), Windows only | MIT | © Microsoft Corporation |
| `Microsoft.Diagnostics.DbgShim`, `Microsoft.Diagnostics.DbgShim.osx-arm64`, `Microsoft.Diagnostics.DbgShim.osx-x64`, `Microsoft.Diagnostics.DbgShim.win-x64`, `Microsoft.Diagnostics.DbgShim.win-arm64`, `Microsoft.Diagnostics.DbgShim.linux-x64`, `Microsoft.Diagnostics.DbgShim.linux-arm64` | the debugger's way into a runtime (dbgshim), one native per system | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.DiaSymReader` | the expression compiler's | MIT | © Microsoft Corporation. All rights reserved. |
| `ICSharpCode.Decompiler` | ILSpy's decompiler: the debugger's frames without source, and a library's type opened from XAML | MIT | Copyright 2011-2026 AlphaSierraPapa |
| `MP.Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator.ExpressionCompiler`, `MP.Microsoft.CodeAnalysis.ExpressionEvaluator.ExpressionCompiler` | Roslyn's expression compiler, packed from Roslyn's source by MattParkerDev (https://github.com/MattParkerDev/roslyn, branch custom-ee-5.9.0) | MIT | no copyright line; its authors: .NET Foundation and contributors |
| `ZLinq` | the debugger's queries | MIT | © Cysharp, Inc. |
| `Velopack` | the Windows install's hooks, which register and remove the file types Passenger opens as it is installed, updated and uninstalled, and its updates: the UpdateManager that downloads a newer release's package from its feed and checks its size and SHA-256 | MIT | Copyright © Velopack Ltd. All rights reserved. |
| `vpk` | in the Windows installer, Velopack's own programs, written in Rust with open-source crates: `Setup.exe` (the installer itself), and in the install `Update.exe` (which installs an update once Passenger has closed, and uninstalls it from Settings > Apps) and a `Passenger.exe` that starts `current\Passenger.exe` | MIT | Copyright © Velopack Ltd. All rights reserved. |

### Inside `roslyn/`

Roslyn's language server carries these packages' assemblies in its own package, which Microsoft gives as a whole under
MIT with the `ThirdPartyNotices.rtf` copied beside them. Its Razor support (`Microsoft.CodeAnalysis.Razor.Compiler`,
`.Razor.Workspaces`, `.Remote.Razor`, `Microsoft.VisualStudioCode.RazorExtension`,
`Microsoft.AspNetCore.Razor.Utilities.Shared`) is Roslyn's own, built in the Roslyn repository with the server, so the
package's MIT is its licence; it is what colours, completes, checks, navigates, renames and formats .razor and .cshtml
files in Passenger, cohosted in the server, and nothing of it comes from VS Code's C# extension. What each package says
of itself on nuget.org, at the version the server's `.deps.json` names, or, for a version not published there, what its
releases there say:

| Package | Licence | Copyright |
|---|---|---|
| `Microsoft.Bcl.AsyncInterfaces`, `Microsoft.Build.Framework`, `Microsoft.Build.Locator`, `Microsoft.CodeAnalysis.Elfie`, `Microsoft.DiaSymReader`, `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.FileSystemGlobbing`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Logging.Configuration`, `Microsoft.Extensions.Logging.Console`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Extensions.Primitives`, `Microsoft.NET.StringTools`, `Microsoft.TestPlatform.ObjectModel`, `Microsoft.TestPlatform.TranslationLayer`, `Microsoft.VisualStudio.SolutionPersistence`, `Microsoft.VisualStudio.Threading.Only`, `System.CodeDom`, `System.ComponentModel.Composition`, `System.Composition.AttributedModel`, `System.Composition.Convention`, `System.Composition.Hosting`, `System.Composition.Runtime`, `System.Composition.TypedParts`, `System.Configuration.ConfigurationManager`, `System.Diagnostics.EventLog`, `System.Diagnostics.PerformanceCounter`, `System.Management`, `System.Security.Cryptography.Pkcs`, `System.Security.Cryptography.ProtectedData`, `System.Security.Permissions`, `System.Windows.Extensions` | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.ServiceHub.Framework` (4.10.147), `Microsoft.VisualStudio.Composition` (18.9.20), `Microsoft.VisualStudio.Validation` (18.7.1), `StreamJsonRpc` (2.26.10), `System.CommandLine` (3.0.0-preview.6.26324.102): versions not on nuget.org | MIT (their releases there) | © Microsoft Corporation. All rights reserved. |
| `NuGet.Common`, `NuGet.Configuration`, `NuGet.DependencyResolver.Core`, `NuGet.Frameworks`, `NuGet.LibraryModel`, `NuGet.Packaging`, `NuGet.ProjectModel`, `NuGet.Protocol`, `NuGet.Versioning` | Apache-2.0 | © Microsoft Corporation. All rights reserved. |
| `Humanizer.Core` | MIT | Copyright (c) .NET Foundation and Contributors |
| `ICSharpCode.Decompiler` (9.1.0.7988) | MIT | Copyright 2011-2025 AlphaSierraPapa |
| `MessagePack`, `MessagePack.Annotations` | MIT | © Yoshifumi Kawai and contributors. All rights reserved. |
| `Nerdbank.MessagePack`, `Nerdbank.Streams` | MIT | © Andrew Arnott. All rights reserved. |
| `Newtonsoft.Json` (13.0.4) | MIT | Copyright © James Newton-King 2008 |
| `PolyType` | MIT | 2024, Eirik Tsarpalis |
| `SQLite3MC.PCLRaw.bundle`, `SQLite3MC.PCLRaw.lib`, `SQLite3MC.PCLRaw.provider` (SQLite3 Multiple Ciphers, the native `libsqlite3mc`) | MIT | Copyright (c) 2023-2026 Ulrich Telle, et al. |
| `SQLitePCLRaw.core` | Apache-2.0 | Copyright 2014-2025 SourceGear, LLC |
| `Microsoft.VisualStudio.Telemetry` (18.3.124, not on nuget.org) | not open source: its releases there link the Microsoft Visual Studio Telemetry license terms (https://visualstudio.microsoft.com/license-terms/mt736442/) | © Microsoft Corporation. All rights reserved. |
| `Microsoft.VisualStudio.Utilities.Internal` (16.3.90) | not open source: the Microsoft Visual Studio Internal Utilities license terms (https://visualstudio.microsoft.com/license-terms/mt736439/) | © Microsoft Corporation. All rights reserved. |

The package carries six assemblies under Visual Studio's terms rather than an open-source licence. Which of them the
server loads was found by logging every assembly each server and build host loaded through Passenger's C# tests (startup,
projects and solutions opened, completion, hover, signature help, pulled diagnostics, colours, definitions, usages,
renames, code actions and fixes, formatting, inlay hints), by reading Roslyn's source at the package's commit
(dotnet/roslyn dc1db3e7), and by running those tests without each.

Left out of `roslyn/` (`RoslynServerLeftOut` in `src/Passenger.Lsp/RoslynServer.targets`, with their satellite
resources; the build fails if the package stops carrying one, and a test checks that none is beside the server and that
the server never looks for one), since no path Passenger takes loads them:

- `Microsoft.ServiceHub.Client` (4.6.3200; the Visual Studio Add-ons and Extensions terms): Visual Studio's ServiceHub
  client, used only by its out-of-process host (`ServiceHubRemoteHostClient`). The server reads it only because it
  searches every `Microsoft.ServiceHub*` file beside it for MEF parts, and it has none the server uses.
- `Microsoft.VisualStudio.Debugger.Contracts` (18.3.0-beta.26277.4) and `Microsoft.VisualStudio.RpcContracts` (18.9.918),
  under the same terms: Visual Studio's Hot Reload logging and RPC contracts, used only by Roslyn's ServiceHub host
  (`Microsoft.CodeAnalysis.Remote.ServiceHub`), which the server never loaded on any of those paths.
- `Microsoft.VisualStudio.RemoteControl` (16.3.52; the Telemetry terms, mt736442): Visual Studio Telemetry's client for
  remote settings, experiments and its manifest, which no server loaded on any of those paths (the telemetry session
  Razor makes, below, is never started).

Kept: `Microsoft.VisualStudio.Telemetry` and `Microsoft.VisualStudio.Utilities.Internal`, which the server's own Razor
support needs. It is in the server's MEF composition whenever `Microsoft.VisualStudioCode.RazorExtension.dll` is beside
it, and no option turns it off. Its telemetry reporter derives from a class that takes a Visual Studio telemetry session,
so without Telemetry the composition has errors and is never cached, Razor's start fails with an error in the server's
log at every start, and a server whose composition was cached with Telemetry fails at `initialized` and loads no project;
leaving out the Razor extension too only moves the error to the server's own part that implements one of its interfaces.
At `initialized` Razor's in-process services create Visual Studio Telemetry's default session, which loads
Utilities.Internal; nothing in the server starts that session, which it must be to send anything (`--telemetryLevel off`
governs only Roslyn's own telemetry, which goes nowhere without C# Dev Kit).

### Inside `web/`

The language servers of JavaScript, TypeScript, HTML, CSS, JSON, Dockerfiles and YAML, npm packages of plain JavaScript
that run on the machine's Node.js, one copy for every system. `src/Passenger.Lsp/WebServers/package.json` pins the servers and
`package-lock.json` every package they bring, each with its tarball's sha512, which the build checks as it unpacks them
(`tools/Passenger.NodePackages`, run by `src/Passenger.Lsp/WebServers.targets`); nothing is downloaded as the app runs.
They are carried as their tarballs hold them, less the HTML server's own TypeScript's tsserver, tsc and translated
messages, which it never loads. Each package is named here by its npm name, with the licence its package.json states and
the copyright line its licence file carries (else its author); the app's notices carry each one's licence file whole,
and the notices TypeScript (`ThirdPartyNoticeText.txt`) and VS Code's protocol libraries (`thirdpartynotices.txt`)
carry of their own. `tools/check-notices.py` fails naming any package the lock file pins that is not named here.

| Package | In Passenger | Licence | Copyright |
|---|---|---|---|
| `typescript-language-server` | the TypeScript and JavaScript language server, over TypeScript's own tsserver (a project's own `node_modules/typescript` when it has one) | Apache-2.0 | Copyright (c) 2015 - present Microsoft Corporation |
| `typescript` (6.0.3, and 4.9.5 under vscode-langservers-extracted) | TypeScript's compiler and tsserver for a project without its own; the HTML server's for a page's scripts | Apache-2.0 | no copyright line; its author: Microsoft Corp. |
| `vscode-langservers-extracted` | VS Code's HTML, CSS, JSON and ESLint language servers, as extracted from VS Code and vscode-eslint (the ESLint server runs a project's own eslint package; its Markdown server, carried with them, is not run) | MIT | Copyright (c) 2020 hrsh7th |
| `vscode-css-languageservice`, `vscode-html-languageservice`, `vscode-json-languageservice`, `jsonc-parser`, `request-light`, `vscode-uri` | the servers' language services | MIT | Copyright (c) Microsoft |
| `vscode-languageserver`, `vscode-languageserver-protocol`, `vscode-languageserver-types`, `vscode-languageserver-textdocument`, `vscode-jsonrpc`, `vscode-nls`, `vscode-markdown-languageservice` | the servers' side of the Language Server Protocol | MIT | Copyright (c) Microsoft Corporation |
| `@vscode/l10n` | the servers' messages | MIT | no copyright line; its author: Microsoft Corporation |
| `core-js` | the extracted servers' JavaScript library | MIT | Copyright (c) 2013–2025 Denis Pushkarev (zloirock.ru) |
| `regenerator-runtime` | the extracted servers' generators | MIT | Copyright (c) 2014-present, Facebook, Inc. |
| `semver` | version ranges | ISC | Copyright (c) Isaac Z. Schlueter and Contributors |
| `node-html-parser` | the Markdown service's HTML | MIT | Copyright 2019 Tao Qiufeng |
| `css-select`, `css-what`, `domelementtype`, `domhandler`, `domutils`, `entities`, `nth-check` | node-html-parser's | BSD-2-Clause | Copyright (c) Felix Böhm |
| `boolbase` | css-select's | ISC | no copyright line; its author: Felix Boehm |
| `dom-serializer` | domutils' | MIT | Copyright (c) 2014 The cheeriojs contributors |
| `he` | node-html-parser's | MIT | Copyright Mathias Bynens |
| `picomatch` | the Markdown service's and the YAML server's globs | MIT | Copyright (c) 2017-present, Jon Schlinkert. |
| `dockerfile-language-server-nodejs` | the Dockerfile language server | MIT | Copyright (c) 2017 Remy Suen |
| `dockerfile-language-service`, `dockerfile-utils` | the Dockerfile server's language service and checks | MIT | Copyright (c) 2018 Remy Suen (dockerfile-utils: 2017) |
| `dockerfile-ast` | the Dockerfile server's parser | MIT | Copyright (c) 2017-2021 Remy Suen |
| `yaml-language-server` | the YAML language server, a compose file's checks and completion by the Compose specification's schema | MIT | Copyright (c) 2017 Red Hat Inc. and others. |
| `yaml` | the YAML server's parser | ISC | Copyright Eemeli Aro <eemeli@gmail.com> |
| `prettier` | the YAML server's formatter | MIT | Copyright © James Long and contributors |
| `ajv`, `ajv-draft-04`, `ajv-i18n`, `fast-deep-equal`, `json-schema-traverse` | the YAML server's JSON schema checks | MIT | Copyright (c) 2015-2021 Evgeny Poberezkin |
| `fast-uri` | ajv's URIs | BSD-3-Clause | Copyright (c) 2011-2021, Gary Court until https://github.com/garycourt/uri-js/commit/a1acf730b4bba3f1097c9f52e7d9d3aba8cdcaae |
| `require-from-string` | ajv's | MIT | Copyright (c) Vsevolod Strukchinsky <floatdrop@gmail.com> (github.com/floatdrop) |

## In the container tools

What `just container-tools` makes and each release carries as `Passenger-container-tools-<rid>.tar.gz`
(`src/Passenger.ContainerTools`, for linux-x64, linux-arm64, linux-musl-x64 and linux-musl-arm64): `passenger-tools`,
which Passenger copies into or mounts in a Linux container to debug and profile the .NET programs there, self-contained,
with `LICENSE.txt` and notices of its own beside it, `THIRD-PARTY-NOTICES.txt`, written at its build by
`tools/Passenger.Notices` from the packages its `.deps.json` names (each one's copyright, licence text and own notices),
then the debugger's copied source below (SharpDbg and ICorDebugSharp, ILSpy's and ClrDebug's code). The app never
carries them. `tools/check-notices.py` fails naming any package they carry on one of their systems that is not named
here.

| Package | In the container tools | Licence | Copyright |
|---|---|---|---|
| The .NET runtime (`Microsoft.NETCore.App.Runtime.linux-x64`, `Microsoft.NETCore.App.Runtime.linux-arm64`, `Microsoft.NETCore.App.Runtime.linux-musl-x64`, `Microsoft.NETCore.App.Runtime.linux-musl-arm64`) | the runtime pack of the tools' system, which they run on whatever .NET the container has, or none | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Diagnostics.DbgShim`, `Microsoft.Diagnostics.DbgShim.linux-x64`, `Microsoft.Diagnostics.DbgShim.linux-arm64`, `Microsoft.Diagnostics.DbgShim.linux-musl-x64`, `Microsoft.Diagnostics.DbgShim.linux-musl-arm64` | the debugger's way into a runtime (`libdbgshim.so`), the tools' system's alone: on a musl system musl's own (`KeepMuslDbgShim` in the tools' project, since the glibc one needs ld-linux, which Alpine lacks) | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Diagnostics.NETCore.Client` | the diagnostic port: the debugger's attach and the profiler's recording, in the container | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Diagnostics.Runtime` | ClrMD, reading a heap snapshot in the container with its runtime's own DAC | MIT | © Microsoft Corporation. All rights reserved. |
| `Azure.Identity`, `Azure.Core`, `System.ClientModel`, `System.Memory.Data`, `Microsoft.Identity.Client`, `Microsoft.Identity.Client.Extensions.Msal`, `Microsoft.IdentityModel.Abstractions`, `Microsoft.Security.AntiSSRF`, `Microsoft.Bcl.AsyncInterfaces` | ClrMD's, carried as the app carries them (above) | MIT | © Microsoft Corporation. All rights reserved. |
| `System.Security.Cryptography.ProtectedData` (4.5.0) | the Azure sign-in's (MSAL's), which ClrMD never asks for | MIT (its package's `LICENSE.TXT`; its nuspec names only the licence's URL) | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Diagnostics.Tracing.TraceEvent` | the profiler's EventPipe providers and their events, with its COM interop assemblies `Dia2Lib.dll` and `TraceReloggerLib.dll`; no Windows native | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.Common`, `Microsoft.DiaSymReader` | the debugger's expressions | MIT | © Microsoft Corporation. All rights reserved. |
| `MP.Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator.ExpressionCompiler`, `MP.Microsoft.CodeAnalysis.ExpressionEvaluator.ExpressionCompiler` | Roslyn's expression compiler, as in the app | MIT | no copyright line; its authors: .NET Foundation and contributors |
| `ICSharpCode.Decompiler` | ILSpy's decompiler: the debugger's frames without source | MIT | Copyright 2011-2026 AlphaSierraPapa |
| `ZLinq` | the debugger's queries | MIT | © Cysharp, Inc. |

## Building and testing only

Packages the build or the tests use that no app carries.

| Package | In Passenger | Licence | Copyright |
|---|---|---|---|
| `Avalonia.BuildServices` | Avalonia's build task, whose telemetry `AVALONIA_TELEMETRY_OPTOUT` turns off (the README says how) | MIT | Copyright 2023-2025 © The AvaloniaUI Project |
| `Microsoft.CodeAnalysis.Analyzers` | Roslyn's analyzers, which come with Microsoft.CodeAnalysis.Common | MIT | © Microsoft Corporation. All rights reserved. |
| `Krafs.Publicizer` | the debugger's build | MIT | © Krafs 2024 |
| `Microsoft.Build`, `Microsoft.Build.Framework`, `Microsoft.NET.StringTools`, `System.Configuration.ConfigurationManager`, `System.Diagnostics.EventLog`, `System.Reflection.MetadataLoadContext`, `System.Security.Cryptography.ProtectedData` | the project host's build: MSBuild's API and what it depends on, compiled against and never carried, since the host runs the installed SDK's own MSBuild (`src/Passenger.Host`) | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | the build: the calls Passenger's own code may not make, and the entry points a test may not call | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.NETCore.Platforms`, `System.Buffers`, `System.Collections.Immutable`, `System.Memory`, `System.Numerics.Vectors`, `System.Reflection.Metadata`, `System.Runtime.CompilerServices.Unsafe`, `System.Text.Encoding.CodePages`, `System.Threading.Tasks.Extensions` | the build: what Passenger's own analyzers (src/Passenger.Analyzers), compiled for netstandard2.0 against Roslyn, depend on | MIT | © Microsoft Corporation. All rights reserved. |
| `NETStandard.Library` | the build: netstandard2.0's library, which Passenger's own analyzers are compiled against | MIT | Copyright (c) .NET Foundation and Contributors |
| `xunit.v3`, `xunit.v3.assert`, `xunit.v3.common`, `xunit.v3.core.mtp-v1`, `xunit.v3.extensibility.core`, `xunit.v3.mtp-v1`, `xunit.v3.runner.common`, `xunit.v3.runner.inproc.console`, `xunit.analyzers`, `xunit.runner.visualstudio` | the tests | Apache-2.0 | Copyright (C) .NET Foundation |
| `Avalonia.Headless.XUnit` | the tests on Avalonia's headless platform | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| `Avalonia.Templates` | Avalonia's dotnet new templates, downloaded for the tests' local feed, from which they install them as New Solution does (`AvaloniaApp.Feed`); Passenger never carries them, and installs them only when asked. The package names no licence; its repository's (AvaloniaUI/Avalonia.Templates) is MIT | MIT | Copyright (c) 2023 .NET Foundation and Contributors All Rights Reserved |
| `Aspire.ProjectTemplates` | Official Aspire starter templates, downloaded for the tests' private template hive; not shipped | MIT | © Microsoft Corporation. All rights reserved. |
| `Aspire.Hosting`, `Aspire.Hosting.AppHost`, `Aspire.AppHost.Sdk`, `Aspire.Dashboard.Sdk.linux-x64`, `Aspire.Dashboard.Sdk.linux-arm64`, `Aspire.Dashboard.Sdk.osx-x64`, `Aspire.Dashboard.Sdk.osx-arm64`, `Aspire.Dashboard.Sdk.win-x64`, `Aspire.Dashboard.Sdk.win-arm64`, `Aspire.Hosting.Orchestration.linux-x64`, `Aspire.Hosting.Orchestration.linux-arm64`, `Aspire.Hosting.Orchestration.osx-x64`, `Aspire.Hosting.Orchestration.osx-arm64`, `Aspire.Hosting.Orchestration.win-x64`, `Aspire.Hosting.Orchestration.win-arm64`, `Aspire.Cli.linux-x64`, `Aspire.Cli.linux-arm64`, `Aspire.Cli.osx-x64`, `Aspire.Cli.osx-arm64` | official starters' hosting and build tools, restored for the tests' local feed, and the aspire CLI the tests run in a home of their own, never carried by Passenger | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.AspNetCore.OpenApi`, `Microsoft.Extensions.AmbientMetadata.Application`, `Microsoft.Extensions.Compliance.Abstractions`, `Microsoft.Extensions.DependencyInjection.AutoActivation`, `Microsoft.Extensions.Diagnostics.ExceptionSummarization`, `Microsoft.Extensions.Http.Diagnostics`, `Microsoft.Extensions.Http.Resilience`, `Microsoft.Extensions.Resilience`, `Microsoft.Extensions.ServiceDiscovery`, `Microsoft.Extensions.ServiceDiscovery.Abstractions`, `Microsoft.Extensions.Telemetry`, `Microsoft.Extensions.Telemetry.Abstractions`, `Microsoft.OpenApi`, `Microsoft.Win32.SystemEvents`, `System.IO.Hashing` | official starters' dependencies, restored for the tests' local feed | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Extensions.Caching.Abstractions`, `Microsoft.Extensions.Configuration.CommandLine`, `Microsoft.Extensions.Configuration.EnvironmentVariables`, `Microsoft.Extensions.Configuration.FileExtensions`, `Microsoft.Extensions.Configuration.Json`, `Microsoft.Extensions.Configuration.UserSecrets`, `Microsoft.Extensions.Diagnostics`, `Microsoft.Extensions.Diagnostics.Abstractions`, `Microsoft.Extensions.Diagnostics.HealthChecks`, `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`, `Microsoft.Extensions.FileProviders.Abstractions`, `Microsoft.Extensions.FileProviders.Physical`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Debug`, `Microsoft.Extensions.Logging.EventLog`, `Microsoft.Extensions.Logging.EventSource` | an AppHost's dependencies at the tests' target, which the test project's own restore prunes, restored for the tests' local feed (tests/AspireGraphs) | MIT | © Microsoft Corporation. All rights reserved. |
| `Grpc.AspNetCore`, `Grpc.AspNetCore.Server`, `Grpc.AspNetCore.Server.ClientFactory`, `Grpc.Net.ClientFactory` | Aspire hosting's gRPC server dependencies, restored for the tests' local feed | Apache-2.0 | Copyright 2019 gRPC Authors |
| `Grpc.Tools` | Aspire hosting's build-time protocol compiler, restored for the tests' local feed and never run; Passenger's own protocol C# is checked in (`just aspire-protocol`) | Apache-2.0 | Copyright 2018 The gRPC Authors |
| `ModelContextProtocol` | Aspire hosting's MCP dependency, restored for the tests' local feed | Apache-2.0 | © Model Context Protocol a Series of LF Projects, LLC. |
| `OpenTelemetry`, `OpenTelemetry.Api`, `OpenTelemetry.Api.ProviderBuilderExtensions`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime` | official starters' telemetry dependencies, restored for the tests' local feed | Apache-2.0 | Copyright OpenTelemetry Authors |
| `AspNetCore.HealthChecks.Uris` | Aspire hosting's health checks, restored for the tests' local feed | Apache-2.0 | no copyright line; its authors: Xabaril Contributors |
| `KubernetesClient` | Aspire hosting's DCP client, restored for the tests' local feed | Apache-2.0 | 2017 The Kubernetes Project Authors |
| `Fractions` | Aspire hosting's dependency, restored for the tests' local feed | BSD-2-Clause (the package's license.txt) | Copyright (c) 2013-2022, Daniel Mueller; the nuspec states Copyright 2013-2023 Daniel Mueller |
| `Hex1b` | Aspire hosting's terminal dependency, restored for the tests' local feed | MIT | Copyright (c) 2024 Mitch Denny |
| `Polly.RateLimiting` | starters' resilience dependency, restored for the tests' local feed | BSD-3-Clause | Copyright (c) 2024, App vNext |
| `Polly.Core` | starters' resilience dependency, restored for the tests' local feed | BSD-3-Clause | Copyright (c) 2015-2026, App vNext |
| `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Abstractions`, `Microsoft.EntityFrameworkCore.Analyzers`, `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Sqlite.Core`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.Data.Sqlite.Core`, `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.DependencyModel`, `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.Common`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `System.Composition` | EF Core with SQLite and the Design package dotnet ef loads, and what they depend on: the graph of the projects the Migrations window's tests write, restored for the tests' local feed (tests/EfGraphs); Passenger never carries them | MIT | © Microsoft Corporation. All rights reserved. |
| `dotnet-ef` | EF Core's command-line tool at the tests' EF Core version and the one before, downloaded for the tests' local feed, from which the Migrations window's tests install it as a local tool; Passenger never carries it, and installs it only when asked | MIT | © Microsoft Corporation. All rights reserved. |
| `Mono.TextTemplating` | the EF Core Design package's template engine, restored for the tests' local feed | MIT | no copyright line; its author: Mikayla Hutchinson |
| `SQLitePCLRaw.bundle_e_sqlite3`, `SQLitePCLRaw.lib.e_sqlite3`, `SQLitePCLRaw.provider.e_sqlite3` | SQLite for EF Core's SQLite provider, restored for the tests' local feed | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| `Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql` | EF Core's PostgreSQL provider and the driver under it: the graph of the web app the Migrations window's compose run test writes, restored for the tests' local feed (tests/EfGraphs); Passenger never carries them | PostgreSQL | Copyright 2025 © The Npgsql Development Team |
| `Polly.Extensions` | starters' resilience dependency, restored for the tests' local feed | BSD-3-Clause | Copyright (c) 2024, App vNext |
| `System.Drawing.Common` | Aspire hosting's dependency, restored for the tests' local feed | MIT | © Microsoft Corporation. All rights reserved. |
| `QRCoder` | Aspire hosting's dependency, restored for the tests' local feed | MIT | Copyright © 2013-2025 Raffael Herrmann. Copyright © 2024-2025 Shane Krueger. All rights reserved. |
| `YamlDotNet` | Aspire hosting's dependency, restored for the tests' local feed | MIT | Copyright (c) Antoine Aubry and contributors |
| `Microsoft.WindowsDesktop.App.Ref` | WPF's reference assemblies, downloaded for the tests' local feed, from which the WPF app `WpfXamlTests` make restores off Windows (`WpfApp`); Passenger reads a WPF project's own copy, never carries one | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.NET.Test.Sdk`, `Microsoft.TestPlatform.TestHost`, `Microsoft.CodeCoverage` | the tests' host | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Testing.Platform`, `Microsoft.Testing.Platform.MSBuild`, `Microsoft.Testing.Extensions.Telemetry`, `Microsoft.Testing.Extensions.TrxReport.Abstractions`, `Microsoft.ApplicationInsights` | xunit.v3's test platform | MIT | © Microsoft Corporation. All rights reserved. |
| `Microsoft.Bcl.AsyncInterfaces`, `Microsoft.Win32.Registry` | the test platform's | MIT | © Microsoft Corporation. All rights reserved. |
| `vpk` | Velopack's packer, a .NET tool at the version `dotnet-tools.json` pins, which builds the Windows installer (`tools/package-win.sh`). It carries others' programs under their own terms for what Passenger's installer does not use or carry: Microsoft's signtool and Azure Artifact Signing client, which sign it only when signing is asked for (the Windows SDK's and Microsoft's terms), WiX (MS-RL) for MSI packages, and zstd (BSD-3-Clause or GPL-2.0) | MIT | Copyright © Velopack Ltd. All rights reserved. |

### The tests' own ESLint and Prettier

The npm packages `EslintPrettierTests` put in a test folder's node_modules as a project's own ESLint and Prettier:
`tests/Passenger.Tests/NodePackages/package.json` pins them and `package-lock.json` everything they bring, each with
its tarball's sha512, which the tests' build checks as `tools/Passenger.NodePackages` puts them in the tests' output
(`node-packages/`). No app carries them, and no test fetches them from npm. Each is named by its npm name, with the
licence its package.json states and the copyright line its licence file (else its source) carries, else its author.
`tools/check-notices.py` fails naming any package the lock file pins that is not named here.

| Package | In Passenger | Licence | Copyright |
|---|---|---|---|
| `eslint` | the project's own ESLint, which Passenger's ESLint support runs through the ESLint language server | MIT | Copyright OpenJS Foundation and other contributors, <www.openjsf.org> |
| `prettier`, `prettier2` (Prettier 2.8.8, under npm's alias) | the project's own Prettier, 3 and 2, which Passenger's Prettier runner formats with | MIT | Copyright © James Long and contributors |
| `@eslint/config-array`, `@eslint/core`, `@eslint/object-schema`, `@eslint/plugin-kit`, `@humanfs/core`, `@humanfs/node`, `@humanfs/types` | eslint's configuration, rules and file system | Apache-2.0 | no copyright line; its author: Nicholas C. Zakas |
| `@eslint/config-helpers` | eslint's configuration | Apache-2.0 | no copyright line or author; its repository is eslint/rewrite |
| `@humanwhocodes/module-importer`, `@humanwhocodes/retry` | eslint's | Apache-2.0 | no copyright line; its author: Nicholas C. Zaks (as it is written) |
| `eslint-visitor-keys` (5.0.1, and 3.4.3 under @eslint-community/eslint-utils) | eslint's syntax tree | Apache-2.0 | no copyright line; its author: Toru Nagashima |
| `@eslint-community/eslint-utils`, `@eslint-community/regexpp` | eslint's rules | MIT | Copyright (c) 2018 Toru Nagashima |
| `eslint-scope` | eslint's scopes | BSD-2-Clause | Copyright JS Foundation and other contributors, https://js.foundation |
| `espree` | eslint's parser | BSD-2-Clause | Copyright (c) Open JS Foundation |
| `esrecurse` | eslint-scope's | BSD-2-Clause | Copyright (C) 2014 Yusuke Suzuki <utatane.tea@gmail.com> |
| `estraverse` | eslint-scope's | BSD-2-Clause | Copyright (C) 2012-2013 Yusuke Suzuki <utatane.tea@gmail.com> |
| `esutils` | eslint's | BSD-2-Clause | Copyright (C) 2013-2014 Yusuke Suzuki <utatane.tea@gmail.com> |
| `esquery` | eslint's selectors | BSD-3-Clause | Copyright (c) 2013, Joel Feenstra |
| `acorn` | espree's | MIT | Copyright (C) 2012-2022 by various contributors (see AUTHORS) |
| `acorn-jsx` | espree's | MIT | Copyright (C) 2012-2017 by Ingvar Stepanyan |
| `ajv` | eslint's rule options | MIT | Copyright (c) 2015-2017 Evgeny Poberezkin |
| `fast-deep-equal`, `fast-json-stable-stringify`, `json-schema-traverse` | ajv's | MIT | Copyright (c) 2017 Evgeny Poberezkin |
| `uri-js` | ajv's | BSD-2-Clause | Copyright 2011 Gary Court. All rights reserved. |
| `punycode` | uri-js's | MIT | Copyright Mathias Bynens <https://mathiasbynens.be/> |
| `json-stable-stringify-without-jsonify` | eslint's | MIT | no copyright line; its author: James Halliday |
| `@types/esrecurse`, `@types/estree`, `@types/json-schema` | eslint's types | MIT | Copyright (c) Microsoft Corporation. |
| `debug` | eslint's | MIT | Copyright (c) 2014-2017 TJ Holowaychuk <tj@vision-media.ca> |
| `ms` | debug's | MIT | Copyright (c) 2020 Vercel, Inc. |
| `cross-spawn` | eslint's | MIT | Copyright (c) 2018 Made With MOXY Lda <hello@moxy.studio> |
| `which`, `isexe` | cross-spawn's | ISC | Copyright (c) Isaac Z. Schlueter and Contributors |
| `shebang-command` | cross-spawn's | MIT | Copyright (c) Kevin Mårtensson <kevinmartensson@gmail.com> (github.com/kevva) |
| `shebang-regex`, `path-key`, `path-exists`, `find-up`, `locate-path`, `p-limit`, `p-locate`, `yocto-queue`, `escape-string-regexp` | eslint's | MIT | Copyright (c) Sindre Sorhus <sindresorhus@gmail.com> |
| `file-entry-cache`, `flat-cache`, `cacheable`, `@cacheable/memory`, `@cacheable/utils`, `hookified` (1.15.1, and 2.2.0 under qified), `qified` | eslint's cache | MIT | no copyright line; its author: Jared Wray <me@jaredwray.com> |
| `hashery`, `@keyv/bigmap` | the cache's | MIT | Copyright (c) 2025 Jared Wray; Copyright (c) Jared Wray |
| `keyv`, `@keyv/serialize` | the cache's | MIT | Copyright (c) 2017-2021 Luke Childs |
| `flatted` | flat-cache's | ISC | Copyright (c) 2018-2020, Andrea Giammarchi, @WebReflection |
| `glob-parent` | eslint's | ISC | Copyright (c) 2015, 2019 Elan Shanker, 2021 Blaine Bublitz <blaine.bublitz@gmail.com>, Eric Schoffstall <yo@contra.io> and other contributors |
| `ignore` | eslint's | MIT | Copyright (c) 2013 Kael Zhang <i@kael.me>, contributors |
| `imurmurhash` | eslint's | MIT | no copyright line; its author: Jens Taylor |
| `is-glob`, `is-extglob` | eslint's | MIT | Copyright (c) 2014-2017, Jon Schlinkert. |
| `word-wrap` | optionator's | MIT | Copyright (c) 2014-2016, Jon Schlinkert |
| `minimatch` | eslint's globs | BlueOak-1.0.0 | no copyright line; its author: Isaac Z. Schlueter <i@izs.me> |
| `brace-expansion`, `balanced-match` | minimatch's | MIT | Copyright Julian Gruber <julian@juliangruber.com> |
| `optionator`, `levn`, `prelude-ls`, `type-check` | eslint's options | MIT | Copyright (c) George Zahariev |
| `deep-is` | optionator's | MIT | Copyright (c) 2012, 2013 Thorsten Lorenz <thlorenz@gmx.de> |
| `fast-levenshtein` | optionator's | MIT | Copyright (c) 2013 Ramesh Nair |
| `natural-compare` | eslint's | MIT | no copyright line; its author: Lauri Rooden |

## Licence texts

`tools/Passenger.Notices/Licenses/` holds the texts of the licences packages name by SPDX identifier (`MIT.txt`,
`Apache-2.0.txt`, `ISC.txt`), from the SPDX License List 3.29.0 (https://github.com/spdx/license-list-data), unchanged; its README
says where each came from. The app's notices, `THIRD-PARTY-NOTICES.txt` beside it, are written from them, from the
packages themselves and from this file: Roslyn's server's table above, and everything below; the container tools' carry
the debugger's two sections below.

<!-- The app's notices (THIRD-PARTY-NOTICES.txt) carry everything below this line as it is. -->

## Aspire dashboard protocol

`src/Passenger.App/Aspire/Protocol/dashboard_service.proto` is copied from
https://github.com/microsoft/aspire, tag v13.6.2, commit af07e33,
`src/Aspire.Hosting/Dashboard/proto/dashboard_service.proto`. Only the generated C# namespace is changed. The C# protoc
generates from it, `DashboardService.cs` and `DashboardServiceGrpc.cs` beside it (`just aspire-protocol`), is under its
licence too.

```
MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## SharpDbg and ICorDebugSharp

Passenger's debugger began as a copy of two MIT-licensed projects by Matthew Parker (MattParkerDev), and is
Passenger's own from there:

- `src/Passenger.Debugger` (the engine), `src/Passenger.DebugAdapter` (its adapter), `tests/Debuggees` and the tests
  under `tests/Passenger.Tests/Debugging` started from SharpDbg, https://github.com/MattParkerDev/sharpdbg, at commit
  93b25ac (September 15, 2026).
- `src/Passenger.Debugger.Interop` (the ICorDebug, IMetaDataImport and dbgshim declarations) started from ICorDebugSharp
  0.1.8, whose package declares the MIT licence. Its source is not published, so this copy was recovered from the
  package's assembly with ILSpy, keeping only what its author wrote: the source generators' output was dropped, since
  the build generates it again. It has since been cut to what Passenger's debugger uses: the declarations the engine
  names, and those they name in turn. Its declarations were generated from Microsoft's MIT-licensed `cordebug.h` and
  `cor.h` (https://github.com/dotnet/runtime), and its documentation comments are adapted from the Microsoft .NET
  documentation (https://github.com/dotnet/docs), licensed under the Creative Commons Attribution 4.0 International
  Public License (https://creativecommons.org/licenses/by/4.0/), changed from Markdown into XML documentation comments,
  a reference to a declaration cut since left as plain text.

```
MIT License

Copyright (c) 2026 Matthew Parker

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Code from ILSpy and ClrDebug

Two of the debugger's files came with SharpDbg from projects of their own:

- `src/Passenger.Debugger/Decompilation/PortablePdbWriter2.cs` is ILSpy's `PortablePdbWriter`
  (`ICSharpCode.Decompiler/DebugInfo/PortablePdbWriter.cs`, https://github.com/icsharpcode/ILSpy), changed to decompile
  its files in parallel and return their text: MIT, Copyright (c) 2018 Siegfried Pammer, as the file's header in ILSpy
  says.
- `src/Passenger.Debugger/ClrDebugExtensions.cs` began as ClrDebug's .NET Core sample (`Samples/NetCore/Program.cs` at
  commit 5f46218, https://github.com/lordmilko/ClrDebug): its automatic attach, and its manual one kept in comments.
  MIT, Copyright (c) 2022 lordmilko.

```
Copyright (c) 2018 Siegfried Pammer

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

```
MIT License

Copyright (c) 2022 lordmilko

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## JetBrains Mono

`src/Passenger.Editor/Fonts/` holds JetBrains Mono 2.304 (Regular, Bold, Italic, Bold Italic), the
editor's font, from https://github.com/JetBrains/JetBrainsMono under the SIL Open Font License 1.1,
whose full text is beside the fonts (`OFL.txt`). The fonts are unmodified.

## Fluent UI System Icons

`src/Passenger.App/Theme/Icons.axaml` holds path data from Microsoft's Fluent UI System Icons
(https://github.com/microsoft/fluentui-system-icons), written by `tools/icons.py`, which lists every
icon used.

```
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Codicons

`src/Passenger.App/Theme/Icons.axaml`'s `Icon.Preview` is the path data of the `open-preview` icon from Visual Studio
Code's Codicons (https://github.com/microsoft/vscode-codicons, `src/icons/open-preview.svg` at commit 5a6ad495),
unchanged, written by `tools/icons.py`. Copyright (c) Microsoft Corporation. It is licensed under the Creative Commons
Attribution 4.0 International License (CC BY 4.0, https://creativecommons.org/licenses/by/4.0/).

## File Icons

`src/Passenger.App/Theme/FileIconSet.axaml` holds path data written by `tools/file-icons.py`, which lists every
icon used, from three sets the file-icons package (https://github.com/file-icons) draws with: file-icons' own icons
(https://github.com/file-icons/icons, ISC), MFixx, its fork of Font Mfizz (https://github.com/file-icons/MFixx, MIT),
and GitHub's Octicons (https://github.com/primer/octicons, MIT). Which file takes which icon and colour family
(`src/Passenger.App/Explorer/FileIconSet.cs`) follows the file-icons package's own rules
(https://github.com/file-icons/atom, MIT). The F#, Visual Basic, Rust, Swift, Dart, npm, Racket and Haxe icons are
Passenger's own, and the script draws the sets' solid badges (dotenv, Markdown, HTML, PowerShell and the like) as
outlines.

file-icons' icons:

```
Copyright (c) 2016-2021, John Gardner

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

MFixx:

```
Copyright (c) 2013-2015 Fizzed, Inc.
Copyright (c) 2016-2025 John Gardner

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

Octicons:

```
MIT License

Copyright (c) 2026 GitHub Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The file-icons package:

```
Copyright (c) 2014-2016 Daniel Brooker
Copyright (c) 2016-2026 John Gardner

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Unicode Character Database

`src/Passenger.Terminal/WideCharacters.cs`, the characters the terminal draws two columns wide, is derived from the
Unicode Character Database 16.0.0's `EastAsianWidth.txt` and `emoji/emoji-data.txt`
(https://www.unicode.org/Public/16.0.0/ucd/), written by `tools/unicode-widths.py`.

```
UNICODE LICENSE V3

COPYRIGHT AND PERMISSION NOTICE

Copyright © 1991-2026 Unicode, Inc.

NOTICE TO USER: Carefully read the following legal agreement. BY
DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

Permission is hereby granted, free of charge, to any person obtaining a
copy of data files and any associated documentation (the "Data Files") or
software and any associated documentation (the "Software") to deal in the
Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell
copies of the Data Files or Software, and to permit persons to whom the
Data Files or Software are furnished to do so, provided that either (a)
this copyright and permission notice appear with all copies of the Data
Files or Software, or (b) this copyright and permission notice appear in
associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
THIRD PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall
not be used in advertising or otherwise to promote the sale, use or other
dealings in these Data Files or Software without prior written
authorization of the copyright holder.
```

## XAML Styler

The attribute ordering rule groups Reformat Code puts XAML's attributes in when asked
(`XamlFormatSettings.DefaultAttributeOrder` in `src/Passenger.Xaml/Features/XamlFormatSettings.cs`) are XAML Styler's
defaults, its `AttributeOrderingRuleGroups` in `src/XamlStyler/Options/StylerOptions.cs`
(https://github.com/Xavalon/XamlStyler), as are the names of the options Passenger shares with it (Enable attribute
reordering, Remove ending tag of empty element); (c) Xavalon. All rights reserved., under the Apache License 2.0 below.

## MSBuild's schemas

`src/Passenger.App/MSBuild/Schemas/` holds `Microsoft.Build.Core.xsd` and `Microsoft.Build.CommonTypes.xsd`, MSBuild's own
schemas of its elements, properties, items, metadata and tasks (`src/MSBuild/MSBuild/` of https://github.com/dotnet/msbuild
at commit 7af2a3b, October 14, 2026), unchanged. The app embeds them for the code help in project files, which offers and
describes what they declare; the .NET SDK does not carry them.

```
The MIT License (MIT)

Copyright (c) .NET Foundation and contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## TextMate grammars

`src/Passenger.Languages/TextMate/Grammars/` holds the TextMate grammars that colour the files beside the C#, each a
file of Visual Studio Code's own extensions (https://github.com/microsoft/vscode, MIT, Copyright (c) 2015 - present
Microsoft Corporation), unchanged, from the project its header names:

- `xml.tmLanguage.json` (VS Code 1.142.0, `extensions/xml`): from atom/language-xml
  (https://github.com/atom/language-xml), MIT, Copyright (c) 2014 GitHub Inc., which was converted from the TextMate
  bundle textmate/xml.tmbundle, under the TextMate bundle licence below.
- `JSON.tmLanguage.json` (VS Code 1.142.0, `extensions/json`): from microsoft/vscode-JSON.tmLanguage
  (https://github.com/microsoft/vscode-JSON.tmLanguage), MIT, Copyright (c) 2015 simonzack, zertosh, benvie.
- `yaml.tmLanguage.json` (VS Code 1.85.0, `extensions/yaml`): the YAML syntax of textmate/yaml.tmbundle
  (https://github.com/textmate/yaml.tmbundle), MIT, Copyright (c) 2015 FichteFoll, as its `Syntaxes/YAML-license.txt`
  says. It stays at 1.85.0: VS Code's own since is RedCMD/YAML-Syntax-Highlighter's (a grammar per YAML version),
  which TextMateSharp did not read rightly when these grammars came in; its 2.0.4 is still its newest.
- `markdown.tmLanguage.json` (VS Code 1.142.0, `extensions/markdown-basics`): from microsoft/vscode-markdown-tm-grammar
  (https://github.com/microsoft/vscode-markdown-tm-grammar), MIT, Copyright (c) Microsoft 2018, derived from
  textmate/markdown.tmbundle, under the TextMate bundle licence below.
- `html.tmLanguage.json` (VS Code 1.142.0, `extensions/html`): converted from textmate/html.tmbundle
  (https://github.com/textmate/html.tmbundle), under the TextMate bundle licence below.
- `css.tmLanguage.json` (VS Code 1.142.0, `extensions/css`): from microsoft/vscode-css
  (https://github.com/microsoft/vscode-css), MIT, Copyright (c) Microsoft Corporation, derived from atom/language-css
  and the TextMate bundle textmate/css.tmbundle, under the TextMate bundle licence below.
- `scss.tmLanguage.json` (VS Code 1.142.0, `extensions/scss`): from atom/language-sass
  (https://github.com/atom/language-sass), MIT, Copyright (c) 2014 GitHub Inc., derived from the TextMate bundle
  alexsancho/Sass.tmbundle (https://github.com/alexsancho/Sass.tmbundle), MIT, Copyright (c) 2012 Alex Sancho, as
  atom/language-sass's `LICENSE.md` says.
- `less.tmLanguage.json` (VS Code 1.142.0, `extensions/less`): from radium-v/Better-Less
  (https://github.com/radium-v/Better-Less), MIT, Copyright (c) 2017 John Kreitlow.
- `JavaScript.tmLanguage.json` (VS Code 1.142.0, `extensions/javascript`) and `TypeScript.tmLanguage.json` (VS Code
  1.142.0, `extensions/typescript-basics`): from microsoft/TypeScript-TmLanguage
  (https://github.com/microsoft/TypeScript-TmLanguage), MIT, Copyright (c) Microsoft Corporation; VS Code names
  textmate/javascript.tmbundle, under the TextMate bundle licence below, among the JavaScript grammar's sources too.
- `shell-unix-bash.tmLanguage.json` (VS Code 1.142.0, `extensions/shellscript`): from jeff-hykin/better-shell-syntax
  (https://github.com/jeff-hykin/better-shell-syntax), MIT, Copyright (c) 2019 Jeff Hykin.
- `docker.tmLanguage.json` (VS Code 1.142.0, `extensions/docker`): from moby/moby's
  `contrib/syntax/textmate/Docker.tmbundle` (https://github.com/moby/moby), under the Apache License 2.0 below, with
  Docker's NOTICE.

And two that colour Razor's .razor and .cshtml files before the language server answers, files of Razor's tooling in
dotnet/roslyn (https://github.com/dotnet/roslyn, where Razor's tooling moved from dotnet/razor; MIT, Copyright (c) .NET
Foundation and Contributors) at the commit Roslyn's server is built from (dc1db3e7), unchanged, under
`src/Razor/src/Razor/src/Microsoft.VisualStudio.RazorExtension/EmbeddedGrammars/`:

- `aspnetcorerazor.tmLanguage.json`: Razor's own grammar, which embeds HTML's and C#'s.
- `csharp.tmLanguage.json`: converted from dotnet/csharp-tmLanguage (https://github.com/dotnet/csharp-tmLanguage), MIT,
  Copyright (c) 2016 .NET Foundation, as its header says; it colours a Markdown file's C# code blocks too.

Reading them, Passenger leaves out what VS Code's reader passes over (a capture that is not a rule) and lets a rule
that begins with no end never end, as VS Code does, writes a class of every character past one (`[^\x{00}-\x{7F}]`,
which its regular expressions misread inside another class, as Less's are) as the range it is, and takes YAML's typed
scalars (booleans, numbers, null) out of a block mapping's keys, so a key such as a workflow's `on` is read as a key.

```
MIT License

Copyright (c) 2015 - present Microsoft Corporation
Copyright (c) 2014 GitHub Inc.
Copyright (c) 2015 simonzack, zertosh, benvie
Copyright (c) 2015 FichteFoll <fichtefoll2@googlemail.com>
Copyright (c) Microsoft 2018
Copyright (c) Microsoft Corporation
Copyright (c) 2012 Alex Sancho, http://alexsancho.name/
Copyright (c) 2017 John Kreitlow
Copyright (c) 2019 Jeff Hykin
Copyright (c) .NET Foundation and Contributors
Copyright (c) 2016 .NET Foundation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The TextMate bundles textmate/xml.tmbundle, textmate/markdown.tmbundle, textmate/html.tmbundle,
textmate/css.tmbundle and textmate/javascript.tmbundle, each in its README:

```
If not otherwise specified (see below), files in this repository fall under the following license:

	Permission to copy, use, modify, sell and distribute this
	software is granted. This software is provided "as is" without
	express or implied warranty, and with no claim as to its
	suitability for any purpose.

An exception is made for files in readable text which contain their own license information, or files where an
accompanying file exists (in the same directory) with a "-license" suffix added to the base-name name of the original
file, and an extension of txt, html, or similar. For example "tidy" is accompanied by "tidy-license.txt".
```

moby/moby's NOTICE:

```
Docker
Copyright 2012-2017 Docker, Inc.

This product includes software developed at Docker, Inc. (https://www.docker.com).
```

The Apache License 2.0:

```
Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/

TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

1. Definitions.

"License" shall mean the terms and conditions for use, reproduction, and distribution as defined by Sections 1 through 9 of this document.

"Licensor" shall mean the copyright owner or entity authorized by the copyright owner that is granting the License.

"Legal Entity" shall mean the union of the acting entity and all other entities that control, are controlled by, or are under common control with that entity. For the purposes of this definition, "control" means (i) the power, direct or indirect, to cause the direction or management of such entity, whether by contract or otherwise, or (ii) ownership of fifty percent (50%) or more of the outstanding shares, or (iii) beneficial ownership of such entity.

"You" (or "Your") shall mean an individual or Legal Entity exercising permissions granted by this License.

"Source" form shall mean the preferred form for making modifications, including but not limited to software source code, documentation source, and configuration files.

"Object" form shall mean any form resulting from mechanical transformation or translation of a Source form, including but not limited to compiled object code, generated documentation, and conversions to other media types.

"Work" shall mean the work of authorship, whether in Source or Object form, made available under the License, as indicated by a copyright notice that is included in or attached to the work (an example is provided in the Appendix below).

"Derivative Works" shall mean any work, whether in Source or Object form, that is based on (or derived from) the Work and for which the editorial revisions, annotations, elaborations, or other modifications represent, as a whole, an original work of authorship. For the purposes of this License, Derivative Works shall not include works that remain separable from, or merely link (or bind by name) to the interfaces of, the Work and Derivative Works thereof.

"Contribution" shall mean any work of authorship, including the original version of the Work and any modifications or additions to that Work or Derivative Works thereof, that is intentionally submitted to Licensor for inclusion in the Work by the copyright owner or by an individual or Legal Entity authorized to submit on behalf of the copyright owner. For the purposes of this definition, "submitted" means any form of electronic, verbal, or written communication sent to the Licensor or its representatives, including but not limited to communication on electronic mailing lists, source code control systems, and issue tracking systems that are managed by, or on behalf of, the Licensor for the purpose of discussing and improving the Work, but excluding communication that is conspicuously marked or otherwise designated in writing by the copyright owner as "Not a Contribution."

"Contributor" shall mean Licensor and any individual or Legal Entity on behalf of whom a Contribution has been received by Licensor and subsequently incorporated within the Work.

2. Grant of Copyright License. Subject to the terms and conditions of this License, each Contributor hereby grants to You a perpetual, worldwide, non-exclusive, no-charge, royalty-free, irrevocable copyright license to reproduce, prepare Derivative Works of, publicly display, publicly perform, sublicense, and distribute the Work and such Derivative Works in Source or Object form.

3. Grant of Patent License. Subject to the terms and conditions of this License, each Contributor hereby grants to You a perpetual, worldwide, non-exclusive, no-charge, royalty-free, irrevocable (except as stated in this section) patent license to make, have made, use, offer to sell, sell, import, and otherwise transfer the Work, where such license applies only to those patent claims licensable by such Contributor that are necessarily infringed by their Contribution(s) alone or by combination of their Contribution(s) with the Work to which such Contribution(s) was submitted. If You institute patent litigation against any entity (including a cross-claim or counterclaim in a lawsuit) alleging that the Work or a Contribution incorporated within the Work constitutes direct or contributory patent infringement, then any patent licenses granted to You under this License for that Work shall terminate as of the date such litigation is filed.

4. Redistribution. You may reproduce and distribute copies of the Work or Derivative Works thereof in any medium, with or without modifications, and in Source or Object form, provided that You meet the following conditions:

     (a) You must give any other recipients of the Work or Derivative Works a copy of this License; and

     (b) You must cause any modified files to carry prominent notices stating that You changed the files; and

     (c) You must retain, in the Source form of any Derivative Works that You distribute, all copyright, patent, trademark, and attribution notices from the Source form of the Work, excluding those notices that do not pertain to any part of the Derivative Works; and

     (d) If the Work includes a "NOTICE" text file as part of its distribution, then any Derivative Works that You distribute must include a readable copy of the attribution notices contained within such NOTICE file, excluding those notices that do not pertain to any part of the Derivative Works, in at least one of the following places: within a NOTICE text file distributed as part of the Derivative Works; within the Source form or documentation, if provided along with the Derivative Works; or, within a display generated by the Derivative Works, if and wherever such third-party notices normally appear. The contents of the NOTICE file are for informational purposes only and do not modify the License. You may add Your own attribution notices within Derivative Works that You distribute, alongside or as an addendum to the NOTICE text from the Work, provided that such additional attribution notices cannot be construed as modifying the License.

     You may add Your own copyright statement to Your modifications and may provide additional or different license terms and conditions for use, reproduction, or distribution of Your modifications, or for any such Derivative Works as a whole, provided Your use, reproduction, and distribution of the Work otherwise complies with the conditions stated in this License.

5. Submission of Contributions. Unless You explicitly state otherwise, any Contribution intentionally submitted for inclusion in the Work by You to the Licensor shall be under the terms and conditions of this License, without any additional terms or conditions. Notwithstanding the above, nothing herein shall supersede or modify the terms of any separate license agreement you may have executed with Licensor regarding such Contributions.

6. Trademarks. This License does not grant permission to use the trade names, trademarks, service marks, or product names of the Licensor, except as required for reasonable and customary use in describing the origin of the Work and reproducing the content of the NOTICE file.

7. Disclaimer of Warranty. Unless required by applicable law or agreed to in writing, Licensor provides the Work (and each Contributor provides its Contributions) on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied, including, without limitation, any warranties or conditions of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A PARTICULAR PURPOSE. You are solely responsible for determining the appropriateness of using or redistributing the Work and assume any risks associated with Your exercise of permissions under this License.

8. Limitation of Liability. In no event and under no legal theory, whether in tort (including negligence), contract, or otherwise, unless required by applicable law (such as deliberate and grossly negligent acts) or agreed to in writing, shall any Contributor be liable to You for damages, including any direct, indirect, special, incidental, or consequential damages of any character arising as a result of this License or out of the use or inability to use the Work (including but not limited to damages for loss of goodwill, work stoppage, computer failure or malfunction, or any and all other commercial damages or losses), even if such Contributor has been advised of the possibility of such damages.

9. Accepting Warranty or Additional Liability. While redistributing the Work or Derivative Works thereof, You may choose to offer, and charge a fee for, acceptance of support, warranty, indemnity, or other liability obligations and/or rights consistent with this License. However, in accepting such obligations, You may act only on Your own behalf and on Your sole responsibility, not on behalf of any other Contributor, and only if You agree to indemnify, defend, and hold each Contributor harmless for any liability incurred by, or claims asserted against, such Contributor by reason of your accepting any such warranty or additional liability.

END OF TERMS AND CONDITIONS

APPENDIX: How to apply the Apache License to your work.

To apply the Apache License to your work, attach the following boilerplate notice, with the fields enclosed by brackets "[]" replaced with your own identifying information. (Don't include the brackets!)  The text should be enclosed in the appropriate comment syntax for the file format. We also recommend that a file or class name and description of purpose be included on the same "printed page" as the copyright notice for easier identification within third-party archives.

Copyright [yyyy] [name of copyright owner]

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

## SchemaStore's JSON schemas

`src/Passenger.Lsp/WebServers/schemas/` holds JSON schemas of the JSON Schema Store
(https://github.com/SchemaStore/schemastore, `src/schemas/json/` at commit 5661ee1b), unchanged, carried under
`web/schemas/`: those the JSON language server checks and completes package.json, tsconfig.json, jsconfig.json,
appsettings.json, launchSettings.json and global.json by (`package.json`, `tsconfig.json`, `jsconfig.json`,
`appsettings.json`, `launchsettings.json`, `global.json`), and those they refer to (`eslintrc.json`, `prettierrc.json`,
`stylelintrc.json`, `ava.json`, `semantic-release.json`, `jscpd.json`, `madge.json`, `nodemon.json`, `quikrun.json`,
`partial-eslint-plugins.json`), under the Apache License 2.0 given under TextMate grammars above, with the Schema
Store's NOTICE:

```
JSON Schema Store
Copyright 2015-Current Mads Kristensen and Contributors
```

## The Compose specification's schema

`src/Passenger.Lsp/WebServers/schemas/compose-spec.json` is the Compose specification's JSON schema
(https://github.com/compose-spec/compose-spec, `schema/compose-spec.json` at commit 914ec15d), unchanged, carried under
`web/schemas/`: the YAML language server checks and completes compose files (`compose.yaml`, `docker-compose.yml` and
their variants) by it, under the Apache License 2.0 given under TextMate grammars above, with the Compose
Specification's NOTICE:

```
The Compose Specification
Copyright 2020 The Compose Specification Authors
```
