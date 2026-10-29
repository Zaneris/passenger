using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.Tests;

public sealed class AspireContainerRecoveryTests
{
    private const string Context = "aspire-recovery";
    private static readonly string RecordedId = new('a', 64);
    private static readonly string UnrecordedId = new('b', 64);

    [AvaloniaTheory]
    [InlineData("dead-owner")]
    [InlineData("live-owner")]
    [InlineData("renamed")]
    [InlineData("engine-mismatch")]
    [InlineData("unknown-id")]
    [InlineData("persistent")]
    [InlineData("gone")]
    [InlineData("other-state")]
    [InlineData("other-machine")]
    [InlineData("empty")]
    public void Opening_a_window_removes_only_an_exact_record_of_a_dead_owners_container(string scenario)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in container engine uses a Unix shell script");
        string state = TestFolders.Create("aspire-recovery-state-");
        string folder = TestFolders.Create("aspire-recovery-engine-");
        string endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } host
            ? host : "unix:///nowhere/aspire-recovery.sock";
        string cli = StandInEngine(folder, endpoint, scenario);
        string environment = string.Join('\n', new[] { "DOCKER_CONFIG", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH", "DOCKER_TLS" }
            .Select(variable => $"{variable}={Environment.GetEnvironmentVariable(variable)}"));
        string fingerprint = Hash($"{Path.GetFullPath(cli)}\n{Context}\n{endpoint}\n{environment}");
        string deadOwner = ContainerLabels.Of(Environment.ProcessId, 1);
        Assert.True(ContainerLabels.IsGone(deadOwner), "The old process start time must not match this test process");
        var entry = new
        {
            id = RecordedId,
            name = "passenger-session-cache",
            uid = "cache-resource",
            appHost = Hash(Path.Combine(state, "Host.csproj")),
            engine = scenario == "engine-mismatch" ? Hash("another engine") : fingerprint,
            state = ContainerLabels.StateValueOf(scenario == "other-state" ? folder : state),
            machine = scenario == "other-machine" ? new string('0', 16) : ContainerLabels.MachineValue,
            owner = scenario == "live-owner" ? ContainerLabels.ProcessValue : deadOwner,
        };
        string manifest = Path.Combine(Directory.CreateDirectory(Path.Combine(state, "aspire-containers")).FullName, "ownership.json");
        string seeded = JsonSerializer.Serialize(new { version = 0, entries = new[] { entry }.Take(scenario == "empty" ? 0 : 1) });
        File.WriteAllText(manifest, seeded);
        string log = Path.Combine(folder, "commands");
        EngineState? sharedBefore = ContainerEngine.Shared.Known;
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, Engine = new ContainerEngine(cli: cli) };
        try
        {
            Ui.Settle();
            Assert.Empty(File.ReadAllLines(log));
            Assert.Same(sharedBefore, ContainerEngine.Shared.Known);
            Assert.Equal(seeded, File.ReadAllText(manifest));
            Assert.Equal(0, BackgroundWork.Pending);

            window.Show();
            Ui.Settle();
            Assert.Equal(0, BackgroundWork.Pending);
            Assert.Same(sharedBefore, ContainerEngine.Shared.Known);
            string[] commands = File.ReadAllLines(log);
            string inspect = $"--context {Context} ps -a --no-trunc --filter id={RecordedId} --format json";
            string remove = $"--context {Context} rm -f {RecordedId}";
            bool eligible = scenario is "dead-owner" or "renamed" or "engine-mismatch" or "unknown-id" or "persistent" or "gone";
            if (eligible)
            {
                Assert.Contains("version --format json", commands);
                Assert.Contains("context inspect --format json", commands);
                Assert.Contains($"context inspect {Context}", commands);
                Assert.Equal(scenario == "engine-mismatch" ? 0 : 1, commands.Count(command => command == inspect));
                Assert.All(commands, command => Assert.Contains(command, new[]
                {
                    "version --format json", "context inspect --format json", "compose version --format json", "buildx version",
                    "info --format json", $"context inspect {Context}", inspect, remove,
                }));
            }
            else
            {
                Assert.Empty(commands);
            }
            Assert.Equal(scenario == "dead-owner" ? new[] { remove } : [],
                commands.Where(command => command.Contains("rm ", StringComparison.Ordinal)));
            Assert.DoesNotContain(commands, command => command.Contains(UnrecordedId, StringComparison.Ordinal));
            // Removed, or gone from the engine already: either way no longer in the record.
            if (scenario is "dead-owner" or "gone")
            {
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(manifest));
                Assert.Empty(saved.RootElement.GetProperty("entries").EnumerateArray());
            }
            else
            {
                Assert.Equal(seeded, File.ReadAllText(manifest));
            }
        }
        finally
        {
            window.Close();
        }
        Ui.Settle();
        Assert.Equal(0, BackgroundWork.Pending);
    }

    private static string StandInEngine(string folder, string endpoint, string scenario)
    {
        string cli = Path.Combine(folder, "docker");
        File.WriteAllText(Path.Combine(folder, "commands"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "version.json"), JsonSerializer.Serialize(new
        {
            Client = new { Version = "29.0.0", Context },
            Server = new { Version = "29.0.0", Os = "linux" },
        }));
        File.WriteAllText(Path.Combine(folder, "context.json"), JsonSerializer.Serialize(new[]
        {
            new { Name = Context, Endpoints = new { docker = new { Host = endpoint } } },
        }));
        // ps's JSON as Docker writes it, empty for a gone container; a Persistent one carries the orchestrator's label.
        File.WriteAllText(Path.Combine(folder, "inspect.json"), scenario == "gone" ? string.Empty : JsonSerializer.Serialize(new
        {
            ID = scenario == "unknown-id" ? UnrecordedId : RecordedId,
            Names = scenario == "renamed" ? "renamed-by-user" : "passenger-session-cache",
            Labels = scenario == "persistent" ? "com.microsoft.developer.usvc-dev.persistent=true" : "com.microsoft.developer.usvc-dev.persistent=false",
        }) + "\n");
        File.WriteAllText(cli, string.Join('\n',
            "#!/bin/sh",
            "here=$(dirname \"$0\")",
            "printf '%s\\n' \"$*\" >> \"$here/commands\"",
            "case \"$*\" in",
            " 'version --format json') cat \"$here/version.json\" ;;",
            $" 'context inspect --format json'|'context inspect {Context}') cat \"$here/context.json\" ;;",
            " 'compose version --format json'|'buildx version') exit 1 ;;",
            $" '--context {Context} ps -a --no-trunc --filter id={RecordedId} --format json') cat \"$here/inspect.json\" ;;",
            $" '--context {Context} rm -f {RecordedId}') ;;",
            " *) exit 1 ;;",
            "esac",
            string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return cli;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
