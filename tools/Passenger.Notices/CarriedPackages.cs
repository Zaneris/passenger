using System.Text.Json;
using Passenger.Core;

namespace Passenger.Notices;

/// <summary>The packages a program carries, as its .deps.json names them: a package is carried where the program takes a
/// file of it (a runtime, native, resource or runtime target's file, the kinds MSBuild copies beside the app), so one that
/// brings only its dependencies (Microsoft.Data.Sqlite) is not, and the notices name it nowhere. The notices and the test
/// of them both read a program's packages through this one rule.</summary>
public static class CarriedPackages
{
    /// <summary>A target's kinds of files a program takes.</summary>
    private static readonly string[] Taken = ["runtime", "native", "resources", "runtimeTargets"];

    /// <summary>Each package, and the runtime pack a self-contained program carries ("runtimepack.Microsoft.NETCore.App.Runtime.
    /// osx-arm64" as Microsoft.NETCore.App.Runtime.osx-arm64), with its folder under the package folder where the file gives
    /// one and the names of the files the program takes of it.</summary>
    public static IReadOnlyList<(string Id, string Version, string? Path, IReadOnlyList<string> Files)> In(string deps)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(deps));
        JsonElement root = document.RootElement;
        JsonElement? target = JsonMembers.Object(JsonMembers.Object(root, "targets"), JsonMembers.Text(JsonMembers.Object(root, "runtimeTarget"), "name") ?? string.Empty);
        List<(string, string, string?, IReadOnlyList<string>)> carried = [];
        foreach (JsonProperty library in JsonMembers.Properties(root, "libraries"))
        {
            string[] name = library.Name.Split('/');
            string? id = JsonMembers.Text(library.Value, "type") switch
            {
                "package" => name[0],
                "runtimepack" => name[0]["runtimepack.".Length..],
                _ => null,
            };
            JsonElement? entry = JsonMembers.Object(target, library.Name);
            string[] files = [.. Taken.SelectMany(kind => JsonMembers.Properties(entry, kind)).Select(file => Path.GetFileName(file.Name))];
            if (id is not null && files.Length > 0)
            {
                carried.Add((id, name[1], JsonMembers.Text(library.Value, "type") == "package" ? JsonMembers.Text(library.Value, "path") : null, files));
            }
        }
        return carried;
    }
}
