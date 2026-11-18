using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Passenger.Core;

/// <summary>A build's runtime configuration, as the runtimeconfig.json beside its assembly gives it: the target framework
/// it was built for (net10.0), the shared frameworks it runs on with their versions (its one framework, its several, or
/// those a self-contained build carries), and its configProperties as text, the form .NET's host gives the runtime them
/// in, which Passenger's helper host gives a helper. Another program's JSON, read leniently (comments and trailing commas
/// taken). Read off the window's thread.</summary>
public sealed record RuntimeConfig(string? TargetFramework, IReadOnlyList<(string Name, string? Version)> Frameworks, IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>Whether the build carries its runtime (its frameworks those it includes), so no installation's is taken.</summary>
    public bool SelfContained { get; init; }

    /// <summary>How the host rolls forward to a later framework than it names (Major, LatestPatch), as written; null for its default.</summary>
    public string? RollForward { get; init; }

    /// <summary>JSON's writing of a value that is no string, number or switch: on one line, its text unescaped.</summary>
    private static readonly JsonWriterOptions OneLine = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
            Dictionary<string, string> properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in JsonMembers.Properties(options, "configProperties"))
            {
                properties[property.Name] = Given(property.Value);
            }
            return new RuntimeConfig(JsonMembers.Text(options, "tfm"), frameworks, properties)
            {
                SelfContained = JsonMembers.Items(options, "includedFrameworks").Count > 0,
                RollForward = JsonMembers.Text(options, "rollForward"),
            };
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A property's value as the host gives it: a string's text, true or false, a number as written, and anything
    /// else (an object, a list, null) as its JSON on one line.</summary>
    private static string Given(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString()!;
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                return value.GetRawText();
            default:
                ArrayBufferWriter<byte> written = new();
                using (Utf8JsonWriter writer = new(written, OneLine))
                {
                    value.WriteTo(writer);
                }
                return Encoding.UTF8.GetString(written.WrittenSpan);
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
