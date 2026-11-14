using System.Text.Json.Nodes;
using Passenger.App.Aspire;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An Azure Functions app (.NET isolated worker) with one HTTP function, and an AppHost adding it with
/// AddAzureFunctionsProject, restored together from the run's local feed and the package cache tests/AspireGraphs fills. Its
/// HTTP function needs no host storage, so the AppHost leaves the storage emulator Aspire adds for it unstarted, and no
/// container runs. Unavailable where no func, Azure Functions Core Tools', is on the PATH (<see cref="WhyNot"/>).</summary>
public sealed class AspireFunctionsApp : SharedProject
{
    /// <summary>Why a test of the Functions app skips (<see cref="IsUnavailable"/>).</summary>
    public const string FuncSkipped = "Runs Azure Functions Core Tools' func, which is not on the PATH: install Core Tools 4 to run it";

    /// <summary>Why a test of the AppHost skips (<see cref="IsAppHostUnavailable"/>): as <see cref="AspireApp.AppHostSkipped"/>, or no func.</summary>
    public const string AppHostSkipped = "Runs a real AppHost (PassengerAspireRuntime=true, on macOS or Linux) adding a Functions app, which Azure Functions Core Tools' func runs: install Core Tools 4 to run it";

    /// <summary>The line of the function the tests stop at, and what its answer says.</summary>
    public const string Answering = "response.WriteString";

    public const string Answer = "Hello from Passenger's function, 42";

    public AspireFunctionsApp() : base("functions-") => WhyNot = FuncMissing;

    public string? WhyNot { get; }

    public static bool IsUnavailable => FuncMissing is not null;

    public static bool IsAppHostUnavailable => AspireApp.IsUnavailable || IsUnavailable;

    private static string? FuncMissing => FunctionsHost.Func() is null ? FuncSkipped : null;

    // By their real paths, as the IDE names them (macOS's temporary folder is reached through a link).
    public string Host => PathNames.Canonical(PathOf("Host/Host.csproj"));
    public string HostProgram => PathNames.Canonical(PathOf("Host/AppHost.cs"));
    public string Function => PathNames.Canonical(PathOf("Fn/Hello.cs"));
    public string FunctionProject => PathNames.Canonical(PathOf("Fn/Fn.csproj"));

    /// <summary>The port the Functions app's own launch profile gives func, where no AppHost gives it another.</summary>
    public int OwnPort { get; } = AspireApp.FreePort();

    /// <summary>The home func is given, the test folder's, so nothing it keeps reaches the person's.</summary>
    private string FuncHome => PathOf("func-home");

    public override async ValueTask InitializeAsync()
    {
        if (WhyNot is not null)
        {
            return;
        }
        AspireApp.UseFeed(Folder);
        string version = AvaloniaApp.Metadata("AzureFunctionsSdkVersion");
        string sdk = Path.Combine(AvaloniaApp.Metadata("NuGetPackageRoot"), "azure.functions.sdk", version, $"azure.functions.sdk.{version}.nupkg");
        Assert.True(File.Exists(sdk), $"Azure.Functions.Sdk {version} is missing at {sdk}; restore the test project first");
        string fed = Path.Combine(AvaloniaApp.Feed, Path.GetFileName(sdk));
        if (!File.Exists(fed))
        {
            File.Copy(sdk, fed, overwrite: true);
        }
        Directory.CreateDirectory(FuncHome);
        WriteFunctions(version);
        WriteHost();
        File.WriteAllText(PathOf("Functions.slnx"), "<Solution>\n  <Project Path=\"Fn/Fn.csproj\" />\n  <Project Path=\"Host/Host.csproj\" />\n</Solution>\n");
        await PrepareAsync(byRealPath: true);
    }

    private void WriteFunctions(string sdk)
    {
        Directory.CreateDirectory(PathOf("Fn/Properties"));
        File.WriteAllText(PathOf("Fn/Fn.csproj"), $"""
            <Project Sdk="Azure.Functions.Sdk/{sdk}">
              <PropertyGroup>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="{AvaloniaApp.Metadata("AzureFunctionsWorkerVersion")}" />
                <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="{AvaloniaApp.Metadata("AzureFunctionsHttpVersion")}" />
              </ItemGroup>
              <ItemGroup>
                <None Update="local.settings.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="Never" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(PathOf("Fn/Program.cs"), """
            using Microsoft.Azure.Functions.Worker.Builder;
            using Microsoft.Extensions.Hosting;

            FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);
            builder.Build().Run();
            """);
        File.WriteAllText(PathOf("Fn/Hello.cs"), $$"""
            using System.Net;
            using Microsoft.Azure.Functions.Worker;
            using Microsoft.Azure.Functions.Worker.Http;

            namespace Fn;

            public class Hello
            {
                [Function("Hello")]
                public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData request)
                {
                    int answer = 42;
                    HttpResponseData response = request.CreateResponse(HttpStatusCode.OK);
                    {{Answering}}($"Hello from Passenger's function, {answer}");
                    return response;
                }
            }
            """);
        File.WriteAllText(PathOf("Fn/host.json"), "{\n  \"version\": \"2.0\"\n}\n");
        File.WriteAllText(PathOf("Fn/local.settings.json"), "{\n  \"IsEncrypted\": false,\n  \"Values\": {\n    \"FUNCTIONS_WORKER_RUNTIME\": \"dotnet-isolated\"\n  }\n}\n");
        // As the Functions template writes it, func told its port; func keeps its files in the test's home, and sends no telemetry.
        File.WriteAllText(PathOf("Fn/Properties/launchSettings.json"), new JsonObject
        {
            ["profiles"] = new JsonObject
            {
                ["Fn"] = new JsonObject
                {
                    ["commandName"] = "Project",
                    ["commandLineArgs"] = $"--port {OwnPort}",
                    ["launchBrowser"] = false,
                    ["environmentVariables"] = new JsonObject
                    {
                        ["HOME"] = FuncHome,
                        ["FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT"] = "1",
                    },
                },
            },
        }.ToJsonString());
    }

    private void WriteHost()
    {
        Directory.CreateDirectory(PathOf("Host/Properties"));
        File.WriteAllText(PathOf("Host/Host.csproj"), $"""
            <Project Sdk="Aspire.AppHost.Sdk/{AspireApp.TemplatesVersion}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AspireUseCliBundle>false</AspireUseCliBundle>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.Azure.Functions" Version="{AspireApp.TemplatesVersion}" />
                <ProjectReference Include="../Fn/Fn.csproj" />
              </ItemGroup>
            </Project>
            """);
        // Its HTTP function needs no host storage: the storage Aspire adds for it never starts (no emulator's container), and nothing names it.
        File.WriteAllText(PathOf("Host/AppHost.cs"), """
            using Aspire.Hosting;
            using Aspire.Hosting.Azure;

            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);
            builder.AddAzureFunctionsProject<Projects.Fn>("fn")
                .WithEnvironment(context =>
                {
                    foreach (string storage in context.EnvironmentVariables.Keys.Where(k => k.StartsWith("AzureWebJobsStorage", StringComparison.Ordinal)).ToList())
                    {
                        context.EnvironmentVariables.Remove(storage);
                    }
                });
            foreach (AzureStorageResource storage in builder.Resources.OfType<AzureStorageResource>().ToList())
            {
                builder.CreateResourceBuilder(storage).WithExplicitStart();
            }
            builder.Build().Run();
            """);
        // Under obj, so a run leaves the project's own files as written.
        string dashboardData = Directory.CreateDirectory(PathOf("Host/obj/passenger-dashboard")).FullName;
        File.WriteAllText(PathOf("Host/Properties/launchSettings.json"), new JsonObject
        {
            ["profiles"] = new JsonObject
            {
                ["http"] = new JsonObject
                {
                    ["commandName"] = "Project",
                    ["launchBrowser"] = false,
                    ["applicationUrl"] = $"http://localhost:{AspireApp.FreePort()}",
                    ["environmentVariables"] = new JsonObject
                    {
                        [AspNetCoreVariables.AspNetCoreEnvironment] = "Development",
                        [AspNetCoreVariables.DotnetEnvironment] = "Development",
                        [AspireVariables.DashboardOtlpEndpoint] = $"http://localhost:{AspireApp.FreePort()}",
                        [AspireVariables.ResourceServiceEndpoint] = $"http://localhost:{AspireApp.FreePort()}",
                        [AspireVariables.AllowUnsecuredTransport] = "true",
                        ["ASPIRE_VERSION_CHECK_DISABLED"] = "true",
                        // Where the AppHost keeps its record of running AppHosts, the run's own, never the person's ~/.aspire.
                        ["HOME"] = AspireCli.Home,
                        ["ASPIRE_DASHBOARD_DATA_DIRECTORY"] = dashboardData,
                    },
                },
            },
        }.ToJsonString());
    }
}
