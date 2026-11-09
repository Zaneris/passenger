using System.Text.Json;

namespace Passenger.Core;

/// <summary>A build's runtime configuration, as the runtimeconfig.json beside its assembly gives it: the target framework
/// it was built for (net10.0), the shared frameworks it runs on with their versions (its one framework, its several, or
/// those a self-contained build carries), and its configProperties as text, the form the runtime is given them in.
/// Another program's JSON, read leniently (comments and trailing commas taken). Read off the window's thread.</summary>
public sealed record RuntimeConfig(string? TargetFramework, IReadOnlyList<(string Name, string? Version)> Frameworks, IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>The configuration of a built assembly (an app's dll or apphost, the SDK's dotnet.dll); null for one with
    /// none, or one that does not read.</summary>
    public static RuntimeConfig? Read(string assembly)
    {
        try
        {
            using FileStream stream = File.OpenRead(Path.ChangeExtension(assembly, ".runtimeconfig.json"));
            using JsonDocument read = JsonDocument.Parse(stream, LenientJson.Document);
            JsonElement? options = JsonMembers.Object(read.RootElement, "runtimeOptions");
            List<JsonElement> named = [.. JsonMembers.Items(options, "frameworks"), .. JsonMembers.Items(options, "includedFrameworks")];
            if (JsonMembers.Object(options, "framework") is { } one)
            {
                named.Insert(0, one);
            }
            List<(string Name, string? Version)> frameworks = [];
            foreach (JsonElement framework in named)
            {
                if (JsonMembers.Text(framework, "name") is { Length: > 0 } name)
                {
                    frameworks.Add((name, JsonMembers.Text(framework, "version")));
                }
            }
            JsonElement? configured = JsonMembers.Object(options, "configProperties");
            Dictionary<string, string> properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in JsonMembers.Properties(options, "configProperties"))
            {
                if (JsonMembers.Scalar(configured, property.Name) is { } value)
                {
                    properties[property.Name] = value;
                }
            }
            return new RuntimeConfig(JsonMembers.Text(options, "tfm"), frameworks, properties);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether it runs on the Windows desktop's shared framework: a WPF or Windows Forms app's.</summary>
    public bool OnWindowsDesktop => Frameworks.Any(f => FrameworkNames.IsWindowsDesktop(f.Name));

    /// <summary>The version of a shared framework it runs on (Microsoft.NETCore.App's 10.0.0); null for one it does not.</summary>
    public string? VersionOf(string framework) =>
        Frameworks.FirstOrDefault(f => f.Name.Equals(framework, StringComparison.OrdinalIgnoreCase)).Version;

    /// <summary>A switch its configProperties set (System.StartupHookProvider.IsSupported), true or false in any letter
    /// case, as JSON's or as text; null for one they do not set.</summary>
    public bool? Switch(string name) => Properties.TryGetValue(name, out string? value) && bool.TryParse(value, out bool on) ? on : null;
}
