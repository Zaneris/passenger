using System.Text.Json.Serialization;

namespace Passenger.Roslyn.Extension;

/// <summary>A folder's layer rules (its passenger-layers.json, docs/layers.md): the layers its code is put in, and which
/// each may use. Code is in the first layer that holds it, by its project's name, its namespace or its file's folder;
/// code in a layer may use its own layer's code, the layers it names, and code in no layer. Read by the window from the
/// file, sent to Passenger's extension with each check, and matched the same way at both ends.</summary>
public sealed record LayerRules(
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("layers")] IReadOnlyList<Layer> Layers)
{
    /// <summary>The file a folder's rules are written in, at the folder's top.</summary>
    public const string FileName = "passenger-layers.json";

    /// <summary>The layer code is in: the first that holds its project, its namespace or its file; none for code in no layer.</summary>
    public Layer? Of(string project, string space, string? file)
    {
        string? within = file is null ? null : Within(file);
        return Layers.FirstOrDefault(layer => layer.Projects.Any(p => string.Equals(p, project, StringComparison.OrdinalIgnoreCase))
            || layer.Namespaces.Any(n => Matches(n, space, '.'))
            || within is not null && layer.Folders.Any(f => Matches(f.TrimEnd('/'), within, '/')));
    }

    /// <summary>Whether code in one layer using code in another breaks the rules: neither the same layer nor one it may use.</summary>
    public static bool Breaks(Layer from, Layer to) => from != to && !from.MayUse.Contains(to.Name, StringComparer.Ordinal);

    /// <summary>A file's folder from the rules' folder in forward slashes; null for one outside it.</summary>
    private string? Within(string file)
    {
        string folder = Folder.Replace('\\', '/').TrimEnd('/') + "/";
        string path = file.Replace('\\', '/');
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // A file at the folder's top is in its folder's own, named by nothing.
        string rest = path[folder.Length..];
        int end = rest.LastIndexOf('/');
        return end < 0 ? string.Empty : rest[..end];
    }

    /// <summary>Whether a name fits a pattern, part by part between <paramref name="apart"/>: '*' any run of a part's
    /// characters, a part of "**" any number of parts, as is a namespace's last part "*" (Shop.Data.* takes Shop.Data and
    /// every namespace under it); letters' case alike.</summary>
    public static bool Matches(string pattern, string name, char apart)
    {
        string[] wanted = pattern.Split(apart);
        string[] parts = name.Length == 0 ? [] : name.Split(apart);
        return Fits(0, 0);

        bool Fits(int w, int p)
        {
            if (w == wanted.Length)
            {
                return p == parts.Length;
            }
            if (wanted[w] == "**" || apart == '.' && wanted[w] == "*" && w == wanted.Length - 1)
            {
                for (int skip = p; skip <= parts.Length; skip++)
                {
                    if (Fits(w + 1, skip))
                    {
                        return true;
                    }
                }
                return false;
            }
            return p < parts.Length && Part(wanted[w], parts[p]) && Fits(w + 1, p + 1);
        }

        static bool Part(string pattern, string part)
        {
            string[] pieces = pattern.Split('*');
            if (pieces.Length == 1)
            {
                return string.Equals(pattern, part, StringComparison.OrdinalIgnoreCase);
            }
            if (!part.StartsWith(pieces[0], StringComparison.OrdinalIgnoreCase) || !part.EndsWith(pieces[^1], StringComparison.OrdinalIgnoreCase)
                || part.Length < pieces[0].Length + pieces[^1].Length)
            {
                return false;
            }
            int at = pieces[0].Length;
            foreach (string piece in pieces[1..^1])
            {
                int found = part.IndexOf(piece, at, part.Length - pieces[^1].Length - at, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    return false;
                }
                at = found + piece.Length;
            }
            return true;
        }
    }
}

/// <summary>A layer: its name, the code it holds (projects by name, namespaces and folders by pattern), and the layers its
/// code may use beside its own.</summary>
public sealed record Layer(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<string> Namespaces,
    [property: JsonPropertyName("folders")] IReadOnlyList<string> Folders,
    [property: JsonPropertyName("mayUse")] IReadOnlyList<string> MayUse);

/// <summary>A document's layer rules checked (<see cref="LayersBroken"/>): the rules.</summary>
public sealed record LayersAsked([property: JsonPropertyName("rules")] LayerRules Rules);

/// <summary>The uses in a document that break the rules, in its order.</summary>
public sealed record LayersBroken([property: JsonPropertyName("broken")] IReadOnlyList<LayerBreak> Broken);

/// <summary>A use breaking the rules: where it is written, the layers of the code using and the code used, and what it uses
/// as C# shows it.</summary>
public sealed record LayerBreak(
    [property: JsonPropertyName("start")] Place Start,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("used")] string Used);
