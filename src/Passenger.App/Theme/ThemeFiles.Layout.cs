using System.Text.Json;
using Passenger.App.Docking;
using Passenger.Core;

namespace Passenger.App;

/// <summary>A tool window a theme places, by its id (<see cref="ToolWindowIds"/>), and where it docks.</summary>
public sealed record PlacedWindow(string Id, ToolAnchor Anchor);

/// <summary>The default layout a theme gives: where tool windows dock, in its order at each place before the windows it
/// leaves where Passenger puts them; the windows open at the start, or null for Passenger's own; and the areas' sizes, each
/// null where it keeps Passenger's own. A layout a person arranged always wins over it.</summary>
public sealed record ThemeLayout(IReadOnlyList<PlacedWindow> Places, IReadOnlyList<string>? Open, double? LeftWidth, double? RightWidth, double? BottomHeight);

/// <summary>A theme's <c>layout</c> (docs/themes.md).</summary>
public static partial class ThemeFiles
{
    /// <summary>The narrowest a side area, or the lowest the bottom one, a theme may give.</summary>
    public const double LeastArea = 100;

    /// <summary>The widest a side area, or the tallest the bottom one, a theme may give.</summary>
    public const double MostArea = 2000;

    private static readonly (string Word, ToolAnchor Anchor)[] AnchorWords =
    [
        ("leftTop", ToolAnchor.LeftTop), ("leftBottom", ToolAnchor.LeftBottom), ("rightTop", ToolAnchor.RightTop),
        ("rightBottom", ToolAnchor.RightBottom), ("bottomLeft", ToolAnchor.BottomLeft), ("bottomRight", ToolAnchor.BottomRight),
    ];

    private static ThemeLayout? Layout(string file, JsonElement? layout, List<string> problems)
    {
        if (layout is null)
        {
            return null;
        }
        if (layout.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives layout as {Shown(layout.Value)}: layout is {{\"places\": {{…}}, \"open\": […], \"sizes\": {{…}}}}");
        }
        PassOver(file, layout.Value, ["places", "open", "sizes"], "layout", problems);
        List<PlacedWindow> places = [];
        if (Part(layout.Value, "places") is { } placed)
        {
            foreach (JsonProperty place in placed.EnumerateObject())
            {
                int found = Array.FindIndex(AnchorWords, a => a.Word == place.Name);
                if (found < 0)
                {
                    problems.Add($"{file}: {place.Name} in layout's places is no place this Passenger knows ({Listed(AnchorWords.Select(a => a.Word))}), so it was passed over");
                    continue;
                }
                foreach (string id in Windows(file, place.Value, $"layout's places' {place.Name}", problems))
                {
                    if (places.FirstOrDefault(p => p.Id == id) is { } before)
                    {
                        throw new FormatException($"places {id} in layout's {AnchorWords.First(a => a.Anchor == before.Anchor).Word} and in {place.Name}: a tool window docks in one place");
                    }
                    places.Add(new PlacedWindow(id, AnchorWords[found].Anchor));
                }
            }
        }
        List<string>? open = JsonMembers.Value(layout.Value, "open") is { } opened ? [.. Windows(file, opened, "layout's open", problems).Distinct(StringComparer.Ordinal)] : null;
        JsonElement? sizes = Part(layout.Value, "sizes");
        PassOver(file, sizes ?? default, ["left", "right", "bottom"], "layout's sizes", problems);
        return new ThemeLayout(places, open, Area(sizes, "left"), Area(sizes, "right"), Area(sizes, "bottom"));
    }

    private static JsonElement? Part(JsonElement layout, string member)
    {
        JsonElement? value = JsonMembers.Value(layout, member);
        if (value is { } given && given.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives layout's {member} as {Shown(given)}: {member} is an object");
        }
        return value;
    }

    /// <summary>A list of tool windows' ids, each one this Passenger has, the rest passed over and named.</summary>
    private static List<string> Windows(string file, JsonElement list, string where, List<string> problems)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MostEntries)
        {
            throw new FormatException($"gives {where} as {Shown(list)}: it is a list of tool windows' ids (\"explorer\", \"problems\", …)");
        }
        List<string> ids = [];
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new FormatException($"lists {Shown(item)} in {where}: a tool window is named by its id (\"explorer\", \"problems\", …)");
            }
            string id = item.GetString()!;
            if (!ToolWindowIds.All.Contains(id))
            {
                problems.Add($"{file}: {Shown(item)} in {where} is no tool window this Passenger has, so it was passed over");
                continue;
            }
            ids.Add(id);
        }
        return ids;
    }

    private static double? Area(JsonElement? sizes, string side)
    {
        if (JsonMembers.Value(sizes, side) is not { } given)
        {
            return null;
        }
        return Number(given, LeastArea, MostArea)
            ?? throw new FormatException($"gives layout's {side} size as {Shown(given)}: an area's size is a number of pixels, {LeastArea} to {MostArea}");
    }
}
