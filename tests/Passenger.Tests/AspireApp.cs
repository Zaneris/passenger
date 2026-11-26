using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Two official, container-free starters, restored together and shared by the window tests.</summary>
public sealed class AspireApp : SharedProject
{
    private static readonly Lazy<Task<string>> Hive = new(InstallAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    public static string TemplatesVersion => AvaloniaApp.Metadata("AspireTemplatesVersion");
    public const string Name = "PassengerAspire";
    public const string CompanionName = "CompanionAspire";
    public const string RequestLog = "Passenger request {RequestValue}";
    /// <summary>Why a test that runs a real AppHost or the aspire CLI skips (<see cref="Unavailable"/>): no CLI of the run's
    /// own (<see cref="AspireCli.Program"/>), whose bundle holds the dashboard and orchestrator an AppHost runs with, or
    /// Windows, where an AppHost keeps its record of running AppHosts in the person's profile whatever its environment says,
    /// so no home of the run's own (<see cref="AspireCli.Home"/>) keeps it from theirs; the person's CLI is never run.</summary>
    public const string AppHostSkipped = "Runs a real AppHost or the aspire CLI, which needs the aspire CLI of the run's own, its bundle holding Aspire's dashboard and orchestrator (PassengerAspireRuntime=true), and, to keep out of the person's ~/.aspire, macOS or Linux";

    public AspireApp() : base("aspire-") => WhyNot = Unavailable;

    /// <summary>The two starters made by Aspire's templates and restored together, unless the machine cannot run them.</summary>
    public override async ValueTask InitializeAsync()
    {
        if (WhyNot is not null)
        {
            return;
        }
        AvaloniaApp.UseFeed(Folder);
        await CreateAsync(PathOf("First"), Name);
        await CreateAsync(PathOf("Second"), CompanionName);
        AskedOfTheStarter();
        File.WriteAllText(PathOf("AspireTests.slnx"), "<Solution>\n" + string.Concat(
            new[] { ("First", Name), ("Second", CompanionName) }.SelectMany(app =>
                new[] { "AppHost", "ApiService", "Web", "ServiceDefaults" }.Select(project =>
                    $"  <Project Path=\"{app.Item1}/{app.Item2}.{project}/{app.Item2}.{project}.csproj\" />\n"))) + "</Solution>\n");
        await AspireCli.SetUpAsync();
        await PrepareAsync(byRealPath: true);
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Host)!, "obj", "project.assets.json")));
        Assert.True(assets.RootElement.GetProperty("libraries").TryGetProperty("MessagePack/2.5.307", out _),
            $"The {TemplatesVersion} AppHost must restore the fixed MessagePack dependency.");
    }

    public string? WhyNot { get; }

    public static bool IsUnavailable => Unavailable is not null;

    public static string? Unavailable => !RuntimeDownloaded ? RuntimeMissing
        : OperatingSystem.IsWindows() ? "On Windows an AppHost keeps its record of running AppHosts in the person's profile whatever its environment says, which no test may touch"
        : AspireCli.Program is null ? "The build downloaded no aspire CLI of the run's own for this machine" : null;

    /// <summary>Whether the test project downloaded the aspire CLI, whose bundle running an AppHost needs: only where
    /// PassengerAspireRuntime was true as it restored (Passenger.Tests.csproj).</summary>
    public static bool RuntimeDownloaded => AvaloniaApp.Metadata("AspireRuntime") == "true";

    public const string RuntimeMissing = "Runs an AppHost, whose dashboard and orchestrator come from the aspire CLI's bundle, which the tests download only where PassengerAspireRuntime is true: restore and build them with it set";

    /// <summary>What stands in for the aspire CLI's bundle in a folder's builds where the run has no CLI of its own (<see
    /// cref="AspireCli.Program"/>): an AppHost's build takes its dashboard and orchestrator from these folders, which hold
    /// neither, and never looks for the bundle, so it builds, and nothing runs it.</summary>
    private static string NoBundle => $"""
          <PropertyGroup Condition="'$(DcpDir)' == ''">
            <DcpDir>{Path.Combine(TestRun.Root, "no-aspire-bundle", "dcp")}</DcpDir>
            <AspireDashboardDir>{Path.Combine(TestRun.Root, "no-aspire-bundle", "dashboard")}</AspireDashboardDir>
          </PropertyGroup>

        """;

    /// <summary>A folder's restores from the run's local feed alone (<see cref="AvaloniaApp.UseFeed"/>), with Aspire's AppHost
    /// SDK in it for the AppHosts a test writes with it: the rest of an AppHost's graph is in the package cache the test
    /// project's restore filled, never fetched. Where the run has no CLI of its own, the folder's Directory.Build.targets
    /// gives its AppHosts' builds what stands in for its bundle (<see cref="NoBundle"/>); a folder with a
    /// Directory.Build.targets of its own has it put first.</summary>
    public static void UseFeed(string folder)
    {
        if (AspireCli.Program is null)
        {
            string targets = Path.Combine(folder, "Directory.Build.targets");
            string own = File.Exists(targets) ? File.ReadAllText(targets) : "<Project>\n</Project>\n";
            if (!own.Contains(NoBundle, StringComparison.Ordinal))
            {
                int end = own.LastIndexOf("</Project>", StringComparison.Ordinal);
                Assert.True(end >= 0, $"{targets} is no MSBuild project, so its AppHosts could not be given what stands in for the aspire CLI's bundle");
                File.WriteAllText(targets, own[..end] + NoBundle + own[end..]);
            }
        }
        string version = TemplatesVersion.ToLowerInvariant();
        string sdk = Path.Combine(AvaloniaApp.Metadata("NuGetPackageRoot"), "aspire.apphost.sdk", version, $"aspire.apphost.sdk.{version}.nupkg");
        Assert.True(File.Exists(sdk), $"Aspire.AppHost.Sdk {TemplatesVersion} is missing at {sdk}; restore the test project first");
        string fed = Path.Combine(AvaloniaApp.Feed, Path.GetFileName(sdk));
        if (!File.Exists(fed))
        {
            File.Copy(sdk, fed, overwrite: true);
        }
        AvaloniaApp.UseFeed(folder);
    }

    // By their real paths, as the IDE names them (macOS's temporary folder is reached through a link).
    public string Host => PathNames.Canonical(PathOf($"First/{Name}.AppHost/{Name}.AppHost.csproj"));
    public string CompanionHost => PathNames.Canonical(PathOf($"Second/{CompanionName}.AppHost/{CompanionName}.AppHost.csproj"));
    public string ApiProgram => PathNames.Canonical(PathOf($"First/{Name}.ApiService/Program.cs"));
    public string ApiProject => PathNames.Canonical(PathOf($"First/{Name}.ApiService/{Name}.ApiService.csproj"));
    public string WebProgram => PathNames.Canonical(PathOf($"First/{Name}.Web/Program.cs"));

    /// <summary>Where the API's command's form writes its answer (<see cref="AskedOfTheStarter"/>), under obj as what a run writes is.</summary>
    public string InteractionAnswer => Path.Combine(Path.GetDirectoryName(Host)!, "obj", "passenger-interaction-answer.txt");

    /// <summary>What the file-based service writes if the IDE ever runs it, which it must refuse to.</summary>
    public string FileServiceRan => PathOf("file-service-started.txt");

    /// <summary>The file-based service a DCP request names, which the IDE refuses.</summary>
    public string FileService => PathOf("FileService/worker.cs");

    /// <summary>The window on the first starter's AppHost.cs: with no C# language server for a test that reads no C# (an
    /// AppHost run through the toolbar, its window and the IDE's execution endpoint), as <see cref="SharedProject.Open"/>.</summary>
    public MainWindow OpenHost(bool languageServer = true) => Open(PathOf($"First/{Name}.AppHost/AppHost.cs"), languageServer: languageServer);

    /// <summary>Where the AppHost records the IDE's execution endpoint and token it was given, under its obj: a run that wrote
    /// in the project's own folders would have the next test's window start a cold language server (<see cref="SharedProject.Reset"/>).</summary>
    public static string ConnectionInfoPath(string appHost) => Path.Combine(Path.GetDirectoryName(appHost)!, "obj", "passenger.dcp.json");

    /// <summary>Reads the connection inherited by the real fixture AppHost, never the IDE's internal server.</summary>
    public static (int Port, string Token) ReadConnectionInfo(string appHost)
    {
        using JsonDocument connection = JsonDocument.Parse(File.ReadAllText(ConnectionInfoPath(appHost)));
        string? endpoint = connection.RootElement.GetProperty("endpoint").GetString();
        Assert.True(endpoint?.StartsWith("localhost:", StringComparison.Ordinal) == true, "The fixture AppHost did not inherit a localhost DCP endpoint");
        Assert.True(int.TryParse(endpoint!["localhost:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535, "The fixture AppHost inherited an invalid DCP port");
        string? token = connection.RootElement.GetProperty("token").GetString();
        Assert.True(!string.IsNullOrWhiteSpace(token), "The fixture AppHost did not inherit a DCP bearer token");
        return (port, token!);
    }

    public static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Makes what one of Aspire's templates makes, from a hive of the run's own, without restore or changes to the
    /// person's template hive.</summary>
    public static async Task MakeAsync(string template, string folder, string name, params string[] options)
    {
        string hive = await Hive.Value;
        ChildResult made = await Run(["new", template, "--output", folder, "--name", name, "--framework", TestProjects.Framework, .. options,
            "--no-update-check", "--debug:custom-hive", hive], Path.GetDirectoryName(hive));
        Assert.True(made.Succeeded, $"Aspire {TemplatesVersion}'s {template} did not generate:\n{made.Output}{made.Error}");
    }

    /// <summary>Makes Aspire's starter, an AppHost of Aspire's package alone adding its two services by path, its program kept
    /// to the run (<see cref="KeepToTheRun"/>) and each service started by its HTTP launch profile.</summary>
    public static async Task<string> MakeStarterAsync(string folder, string name)
    {
        await MakeAsync("aspire-starter", folder, name, "--use-redis-cache", "false", "--skipRestore");
        string hostFolder = Path.Combine(folder, name + ".AppHost");
        string project = File.ReadAllText(Path.Combine(hostFolder, name + ".AppHost.csproj"));
        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk\">", project, StringComparison.Ordinal);
        Assert.Contains($"<PackageReference Include=\"{Passenger.Host.AspireHosts.Package}\" Version=\"{TemplatesVersion}\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain(Passenger.Host.AspireHosts.Sdk, project, StringComparison.Ordinal);
        Assert.DoesNotContain(Passenger.Host.AspireHosts.Property, project, StringComparison.Ordinal);
        string hostSource = Path.Combine(hostFolder, "AppHost.cs");
        string hostText = File.ReadAllText(hostSource);
        foreach (string service in new[] { "ApiService", "Web" })
        {
            string added = $"\"../{name}.{service}/{name}.{service}.csproj\")";
            Assert.Contains(added, hostText, StringComparison.Ordinal);
            hostText = hostText.Replace(added, added[..^1] + ", options => options.LaunchProfileName = \"http\")", StringComparison.Ordinal);
        }
        File.WriteAllText(hostSource, hostText);
        KeepToTheRun(hostFolder);
        return hostSource;
    }

    /// <summary>Generates the pinned upstream starter, without restore or changes to the person's template hive, its AppHost
    /// recording the IDE's execution endpoint it is given and its services answering what the tests ask.</summary>
    public static async Task CreateAsync(string folder, string name)
    {
        string hostSource = await MakeStarterAsync(folder, name);
        AvaloniaApp.UseFeed(folder);
        string hostText = File.ReadAllText(hostSource);
        string connectionPath = JsonSerializer.Serialize(ConnectionInfoPath(Path.Combine(Path.GetDirectoryName(hostSource)!, name + ".AppHost.csproj")));
        string recordConnection = $$"""
            var passengerDcpFile = {{connectionPath}};
            var passengerDcpOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                passengerDcpOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var passengerDcpStream = new FileStream(passengerDcpFile + ".tmp", passengerDcpOptions))
            {
                System.Text.Json.JsonSerializer.Serialize(passengerDcpStream, new
                {
                    endpoint = Environment.GetEnvironmentVariable("DEBUG_SESSION_PORT"),
                    token = Environment.GetEnvironmentVariable("DEBUG_SESSION_TOKEN"),
                });
            }
            File.Move(passengerDcpFile + ".tmp", passengerDcpFile, overwrite: true);

            """;
        File.WriteAllText(hostSource, recordConnection + hostText);

        string api = Path.Combine(folder, name + ".ApiService", "Program.cs");
        string apiText = File.ReadAllText(api);
        Assert.Contains("app.MapGet(\"/weatherforecast\", () =>", apiText, StringComparison.Ordinal);
        File.WriteAllText(api, apiText.Replace("app.MapGet(\"/weatherforecast\", () =>\n{", "app.MapGet(\"/weatherforecast\", (ILogger<Program> logger) =>\n{", StringComparison.Ordinal)
            .Replace("app.MapGet(\"/weatherforecast\", () =>\r\n{", "app.MapGet(\"/weatherforecast\", (ILogger<Program> logger) =>\r\n{", StringComparison.Ordinal)
            .Replace("    var forecast =", $"    int requestValue = 42;\n    logger.LogInformation(\"{RequestLog}\", requestValue);\n    var forecast =", StringComparison.Ordinal)
            .Replace("app.Run();", """
                app.MapGet("/passenger-flood", () =>
                {
                    for (int line = 0; line < 4096; line++) Console.WriteLine($"Passenger flood {line} {new string('x', 1024)}");
                    return "flood completed";
                });
                app.Run();
                """, StringComparison.Ordinal));
        string web = Path.Combine(folder, name + ".Web", "Program.cs");
        string webText = File.ReadAllText(web);
        File.WriteAllText(web, webText.Replace("app.Run();", $$"""
            app.MapGet("/passenger-test", async (WeatherApiClient weather, ILogger<Program> logger) =>
            {
                int requestValue = 42;
                logger.LogInformation("{{RequestLog}}", requestValue);
                var forecasts = await weather.GetWeatherAsync();
                return Results.Ok(forecasts.Length);
            });
            app.Run();
            """, StringComparison.Ordinal));
    }

    /// <summary>An AppHost's HTTP launch profile kept to the run: no browser, free ports for it, its telemetry and its resource
    /// service, served over HTTP, no version check, the run's own home and its dashboard's data under obj; its HTTPS one gone.</summary>
    public static void KeepToTheRun(string hostFolder)
    {
        string settings = Path.Combine(hostFolder, "Properties", "launchSettings.json");
        JsonObject launch = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        JsonObject http = launch["profiles"]!["http"]!.AsObject();
        http["launchBrowser"] = false;
        http["applicationUrl"] = $"http://localhost:{FreePort()}";
        http["environmentVariables"]![AspireVariables.DashboardOtlpEndpoint] = $"http://localhost:{FreePort()}";
        http["environmentVariables"]![AspireVariables.ResourceServiceEndpoint] = $"http://localhost:{FreePort()}";
        http["environmentVariables"]![AspireVariables.AllowUnsecuredTransport] = "true";
        http["environmentVariables"]!["ASPIRE_VERSION_CHECK_DISABLED"] = "true";
        // Where the AppHost keeps its record of running AppHosts (aspire ps's), the run's own, never the person's ~/.aspire.
        http["environmentVariables"]!["HOME"] = AspireCli.Home;
        // Under obj, so a run leaves the project's own files as written.
        string dashboardData = Path.Combine(hostFolder, "obj", "passenger-dashboard");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dashboardData);
        }
        else
        {
            Directory.CreateDirectory(dashboardData, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        http["environmentVariables"]!["ASPIRE_DASHBOARD_DATA_DIRECTORY"] = dashboardData;
        launch["profiles"]!.AsObject().Remove("https");
        File.WriteAllText(settings, launch.ToJsonString());
    }

    /// <summary>What the class's tests ask of the first starter, written before it is restored, so no test writes a file of its
    /// own to the project (which would have the next window start a cold language server): an edited method each service
    /// answers at /passenger-edit (Hot Reload, Rerun), the API's command that asks a confirmation and a form the AppHost
    /// validates, a launch profile whose folder is missing, a program that writes where a DCP request says it ran, and a
    /// file-based service, which the IDE refuses.</summary>
    private void AskedOfTheStarter()
    {
        string host = PathOf($"First/{Name}.AppHost/AppHost.cs");
        string answer = JsonSerializer.Serialize(InteractionAnswer);
        string command = $$"""
            .WithCommand("passenger-interaction", "Passenger interaction", async context =>
            {
                var interactions = (Aspire.Hosting.IInteractionService)context.Services.GetService(typeof(Aspire.Hosting.IInteractionService))!;
                var confirmation = await interactions.PromptConfirmationAsync("Restart API?", "Confirm restart", cancellationToken: context.CancellationToken);
                if (!confirmation.Canceled && confirmation.Data)
                {
                    throw new InvalidOperationException("Cancel accepted the confirmation");
                }
                var input = new Aspire.Hosting.InteractionInput { Name = "value", Label = "Value", InputType = Aspire.Hosting.InputType.Text, Value = "before" };
                var result = await interactions.PromptInputAsync("API settings", "Change the value", input,
                    new Aspire.Hosting.InputsDialogInteractionOptions
                    {
                        ValidationCallback = validation =>
                        {
                            if (validation.Inputs["value"].Value != "latest value")
                            {
                                validation.AddValidationError("value", "Choose another value");
                            }
                            return Task.CompletedTask;
                        }
                    }, context.CancellationToken);
                File.WriteAllText({{answer}}, result.Data!.Value);
                return new Aspire.Hosting.ApplicationModel.ExecuteCommandResult { Success = true };
            })
            """;
        string hostText = File.ReadAllText(host);
        const string Health = $"builder.AddDotnetProject(\"apiservice\", \"../{Name}.ApiService/{Name}.ApiService.csproj\", options => options.LaunchProfileName = \"http\")\n    .WithHttpHealthCheck(\"/health\")";
        Assert.Contains(Health, hostText.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        File.WriteAllText(host, hostText.ReplaceLineEndings("\n").Replace(Health, Health + command, StringComparison.Ordinal));

        const string Edited = "\nstatic class PassengerEdit\n{\n    static int count;\n    public static long Value() => 1 + count;\n}\n";
        string api = PathOf($"First/{Name}.ApiService/Program.cs");
        string apiText = File.ReadAllText(api);
        Assert.Contains("app.Run();", apiText, StringComparison.Ordinal);
        File.WriteAllText(api, apiText.Replace("app.Run();", """
            app.MapGet("/passenger-edit", () => PassengerEdit.Value().ToString());
            if (Environment.GetEnvironmentVariable("PASSENGER_CANCELED_DCP_PID") is { Length: > 0 } marker)
            {
                File.WriteAllText(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            app.Run();
            """, StringComparison.Ordinal) + Edited);
        string web = PathOf($"First/{Name}.Web/Program.cs");
        string webText = File.ReadAllText(web);
        Assert.Contains("app.Run();", webText, StringComparison.Ordinal);
        File.WriteAllText(web, webText.Replace("app.Run();", "app.MapGet(\"/passenger-edit\", () => PassengerEdit.Value().ToString());\napp.Run();",
            StringComparison.Ordinal) + Edited);

        string settings = PathOf($"First/{Name}.ApiService/Properties/launchSettings.json");
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        launch["profiles"]!["missing-folder"] = new JsonObject
        {
            ["commandName"] = "Project",
            ["workingDirectory"] = PathOf("missing-working-folder"),
        };
        File.WriteAllText(settings, launch.ToJsonString());

        Directory.CreateDirectory(PathOf("FileService"));
        File.WriteAllText(FileService, $"File.WriteAllText({JsonSerializer.Serialize(FileServiceRan)}, \"executed\");\n");
    }

    /// <summary>A hive of the run's own holding Aspire's templates, its AppHost SDK put in the run's feed for the AppHosts the
    /// tests write with it.</summary>
    private static async Task<string> InstallAsync()
    {
        string folder = Directory.CreateDirectory(Path.Combine(TestRun.Root, "aspire-templates", TemplatesVersion)).FullName;
        string hive = Path.Combine(folder, "hive");
        string lower = TemplatesVersion.ToLowerInvariant();
        string package = Path.Combine(AvaloniaApp.Metadata("NuGetPackageRoot"), "aspire.projecttemplates", lower,
            $"aspire.projecttemplates.{lower}.nupkg");
        Assert.True(File.Exists(package), $"Aspire.ProjectTemplates {TemplatesVersion} is missing at {package}; restore the test project first");
        string localPackage = Path.Combine(folder, Path.GetFileName(package));
        File.Copy(package, localPackage, overwrite: true);
        UseFeed(folder);
        ChildResult installed = await Run(["new", "install", localPackage, "--debug:custom-hive", hive], folder);
        Assert.True(installed.Succeeded, $"Aspire templates did not install into the private hive:\n{installed.Output}{installed.Error}");
        return hive;
    }

    private static Task<ChildResult> Run(IReadOnlyList<string> arguments, string? folder) =>
        ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(arguments, folder), limit: TimeSpan.FromMinutes(3).Scaled());
}

/// <summary>The run's own aspire CLI, and the home it and the tests' AppHosts take in place of the person's, since both
/// keep their record of running AppHosts (which aspire ps and stop read) in $HOME/.aspire/cli/bch whatever ASPIRE_HOME
/// says. On macOS and Linux the run's PATH starts with a wrapper named aspire that runs the CLI the build downloaded
/// (Passenger.Tests.csproj) in that home, or refuses where it downloaded none, so the IDE and the tests never run the
/// person's.</summary>
public static class AspireCli
{
    /// <summary>The CLI the build downloaded for this machine, null where it downloaded none (PassengerAspireRuntime off,
    /// Windows); its bundle is unpacked into the run's Aspire home (<see cref="SetUpAsync"/>).</summary>
    public static string? Program { get; } = typeof(AspireCli).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "AspireCli")?.Value is { } path && File.Exists(path) ? path : null;

    private static readonly Lazy<Task> Unpacked = new(UnpackAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The CLI's bundle, the dashboard and orchestrator every AppHost the tests run takes, unpacked once a run into
    /// the run's Aspire home, as a person's CLI unpacks it in theirs, where an AppHost's build finds it; nothing where the run
    /// has no CLI.</summary>
    public static Task SetUpAsync() => Program is null ? Task.CompletedTask : Unpacked.Value;

    private static async Task UnpackAsync()
    {
        string home = Environment.GetEnvironmentVariable("ASPIRE_HOME") ?? throw new InvalidOperationException("The run has no Aspire home of its own");
        System.Diagnostics.ProcessStartInfo setup = ChildProcesses.Command(Program!, ["setup", "--install-path", home], TestRun.Root);
        setup.Environment["HOME"] = Home;
        setup.Environment[AspireVariables.TelemetryOptOut] = "1";
        ChildResult unpacked = await ChildProcesses.RunAsync(setup, limit: TimeSpan.FromMinutes(3).Scaled());
        Assert.True(unpacked.Succeeded, $"The aspire CLI did not unpack its bundle:\n{unpacked.Output}{unpacked.Error}");
    }

    private const string Prefix = "/tmp/passenger-aspire-";

    /// <summary>The run's home for the CLI and the AppHosts, short as macOS's 104-byte socket paths ask: the record of a running
    /// AppHost is a socket at $HOME/.aspire/cli/bch/ and 20 characters more, its process's id after them. Gone with the run.</summary>
    public static string Home { get; } = Prefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Puts the wrapper first on the run's PATH, before anything starts, and removes the homes of runs whose process
    /// is gone. Windows finds aspire.exe alone, which no script can stand in for, so there nothing is put.</summary>
    internal static void Begin(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        foreach (string left in Directory.EnumerateDirectories("/tmp", "passenger-aspire-*"))
        {
            if (int.TryParse(left[Prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id != Environment.ProcessId
                && !Processes.IsRunning(id))
            {
                TestFolders.Delete(left);
            }
        }
        // .NET takes a HOME that is not there for none at all, and the CLI then refuses to start.
        Directory.CreateDirectory(Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string folder = Directory.CreateDirectory(Path.Combine(root, "aspire-bin")).FullName;
        string wrapper = Path.Combine(folder, "aspire");
        File.WriteAllText(wrapper, Program is { } program
            ? $"#!/bin/sh\nHOME={Quoted(Home)} exec {Quoted(program)} \"$@\"\n"
            : "#!/bin/sh\necho \"The test run has no aspire CLI of its own: build the tests with PassengerAspireRuntime=true on macOS or Linux; the person's is never run.\" >&2\nexit 127\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }

    private static string Quoted(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
